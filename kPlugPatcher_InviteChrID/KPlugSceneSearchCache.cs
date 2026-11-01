using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// 通常版 kPlug が毎フレーム行うシーン全体の検索を、覚えておいた対象の確認に置き換える（v1.22.0、通常モードのみ）。
    ///
    /// 【対象】
    ///   1. <c>kPlug.DefAndParse.ParserScene.InH()</c>: シーン全体から HFlag を探して Hシーンかを決める。
    ///      Core.Update から Hシーン中は2回/f、Hシーンの外（行動中）も毎フレーム呼ばれる。女6人で約3.6ms/f。
    ///   2. <c>kPlug.MenuCtrl.CursorIsOverMenus()</c>: シーン全体の MenuCtrl を探し、カーソルの下にあるものがあるかを決める。
    ///      HCtrl.LateUpdate から1回/f。女6人で約1.9ms/f。
    ///   どちらも時間は人数ではなく、シーンにあるオブジェクトの数で増える。kPlugVR の InH は Singleton の確認だけなので対象外。
    ///
    /// 【処理】
    ///   - HFlag は <c>HFlag.Start</c>、MenuCtrl は（派生クラスに共通の Awake が無いので）コンストラクタで覚える。
    ///   - 判定は、覚えた対象のうち破棄されておらず GameObject が activeInHierarchy のものがあるかで行う
    ///     （FindObjectOfType / FindObjectsOfType は非アクティブの GameObject のものを返さない）。
    ///   - 適用直後・シーンの読み込み後・VerifyIntervalFrames ごとに元の全体検索を1回行い、結果を照合する。
    ///     食い違えばログに出し、見つかった対象を覚え直す（その回は元の検索の結果を返す）。
    /// </summary>
    internal static class KPlugSceneSearchCache
    {
        private const string Tag = "[SearchCache]";
        private const int MaxMismatchLogs = 20;

        internal static ConfigEntry<bool> CacheHSceneCheck;
        internal static ConfigEntry<bool> CacheMenuCursorCheck;
        internal static ConfigEntry<int> VerifyIntervalFrames;

        private static bool _applied;
        private static bool _gaveUp;
        private static Type _hFlagType;
        private static Type _menuType;
        private static Func<Component, bool> _isUnderCursor;

        /// <summary>覚えた対象。コンストラクタの時点では InstanceID が決まっていないため、参照で区別する。</summary>
        private static readonly List<Component> _flags = new List<Component>();
        private static readonly List<Component> _menus = new List<Component>();
        private static readonly HashSet<object> _flagSet = new HashSet<object>(RefComparer.Instance);
        private static readonly HashSet<object> _menuSet = new HashSet<object>(RefComparer.Instance);
        private static readonly object _lock = new object();

        private static bool _flagNeedFull = true, _menuNeedFull = true;
        private static int _flagNextVerify, _menuNextVerify;
        private static int _mismatchLogs;
        // シーンごとの集計（シーンの読み込み時にログへ出す）
        private static int _flagCalls, _flagSearches, _menuCalls, _menuSearches, _mismatches;

        /// <summary>まだ適用を試す必要があるか（PatcherPlugin.Update の早い適用の条件）。</summary>
        internal static bool Pending => !_applied && !_gaveUp;

        internal static void BindConfig(ConfigFile config)
        {
            CacheHSceneCheck = config.Bind(
                "KPlugSearchCache", "CacheHSceneCheck", true,
                "通常モードのみ（v1.22.0）。kPlug の Hシーン判定（ParserScene.InH）は毎フレーム、シーン全体から HFlag を探します（Hシーン中は2回/f、女6人で約3.6ms/f。時間はシーンのオブジェクト数で増えます）。" +
                "有効にすると、HFlag が作られたときに覚えておき、それが残っているかの確認に置き換えます。シーンの読み込み後と VerifyIntervalFrames ごとに元の検索で照合します。ゲーム中に切り替えられます。");
            CacheMenuCursorCheck = config.Bind(
                "KPlugSearchCache", "CacheMenuCursorCheck", true,
                "通常モードのみ（v1.22.0）。kPlug の「カーソルがメニューの上か」の判定（MenuCtrl.CursorIsOverMenus、Hシーン中に毎フレーム）は、シーン全体から kPlug のメニューを探します（女6人で約1.9ms/f）。" +
                "有効にすると、kPlug のメニューが作られたときに覚えておき、その中だけを調べます。シーンの読み込み後と VerifyIntervalFrames ごとに元の検索で照合します。ゲーム中に切り替えられます。");
            VerifyIntervalFrames = config.Bind(
                "KPlugSearchCache", "VerifyIntervalFrames", 300,
                new ConfigDescription(
                    "通常モードのみ（v1.22.0）。上の2つが有効なとき、何フレームごとに元の全体検索を1回行って結果を照合するか。食い違いがあればログに出し、見つかった対象を覚え直します。0 にするとシーンの読み込み後だけ照合します。ゲーム中に切り替えられます。",
                    new AcceptableValueRange<int>(0, 100000)));
        }

        /// <summary>型が見つかるまで繰り返し呼んでよい（PatcherPlugin.Update の早い適用から呼ぶ）。</summary>
        internal static void TryApply(Harmony harmony)
        {
            if (_applied || _gaveUp) return;
            if (Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase)) { _gaveUp = true; return; }
            try
            {
                Type parser = AccessTools.TypeByName("kPlug.DefAndParse.ParserScene");
                if (parser == null) return; // kPlug がまだ読まれていない。次の試行で探す
                _gaveUp = true; // 以降は成功・失敗にかかわらず試さない
                _menuType = AccessTools.TypeByName("kPlug.MenuCtrl");
                _hFlagType = AccessTools.TypeByName("HFlag");
                MethodInfo inH = AccessTools.Method(parser, "InH", Type.EmptyTypes);
                MethodInfo cursor = _menuType != null ? AccessTools.Method(_menuType, "CursorIsOverMenus", Type.EmptyTypes) : null;
                MethodInfo hStart = _hFlagType != null ? AccessTools.Method(_hFlagType, "Start", Type.EmptyTypes) : null;
                ConstructorInfo menuCtor = _menuType != null ? AccessTools.Constructor(_menuType, Type.EmptyTypes) : null;
                MethodInfo underGetter = _menuType != null ? AccessTools.PropertyGetter(_menuType, "IsUnderCursor") : null;

                bool flagOk = inH != null && inH.IsStatic && inH.ReturnType == typeof(bool) && hStart != null && typeof(Component).IsAssignableFrom(_hFlagType);
                bool menuOk = cursor != null && cursor.IsStatic && cursor.ReturnType == typeof(bool) && menuCtor != null
                    && underGetter != null && underGetter.ReturnType == typeof(bool) && typeof(Component).IsAssignableFrom(_menuType);
                if (!flagOk)
                    PatcherPlugin.Logger.LogWarning($"{Tag} ParserScene.InH（{inH != null}）/ HFlag.Start（{hStart != null}）が想定の形でないため、Hシーン判定は置き換えません。");
                if (!menuOk)
                    PatcherPlugin.Logger.LogWarning($"{Tag} MenuCtrl.CursorIsOverMenus（{cursor != null}）/ コンストラクタ（{menuCtor != null}）/ IsUnderCursor（{underGetter != null}）が想定の形でないため、メニューの判定は置き換えません。");
                if (!flagOk && !menuOk) return;

                if (flagOk)
                {
                    harmony.Patch(hStart, postfix: new HarmonyMethod(AccessTools.Method(typeof(KPlugSceneSearchCache), nameof(HFlagStartPostfix))));
                    harmony.Patch(inH, prefix: new HarmonyMethod(AccessTools.Method(typeof(KPlugSceneSearchCache), nameof(InHPrefix))));
                }
                if (menuOk)
                {
                    _isUnderCursor = BuildGetter(_menuType, underGetter);
                    harmony.Patch(menuCtor, postfix: new HarmonyMethod(AccessTools.Method(typeof(KPlugSceneSearchCache), nameof(MenuCtorPostfix))));
                    harmony.Patch(cursor, prefix: new HarmonyMethod(AccessTools.Method(typeof(KPlugSceneSearchCache), nameof(CursorIsOverMenusPrefix))));
                }
                SceneManager.sceneLoaded += OnSceneLoaded;
                _applied = true;
                PatcherPlugin.Logger.LogInfo($"{Tag} 適用しました（Hシーン判定={(flagOk ? CacheHSceneCheck.Value.ToString() : "適用なし")} / " +
                    $"メニューの判定={(menuOk ? CacheMenuCursorCheck.Value.ToString() : "適用なし")} / 照合 {VerifyIntervalFrames.Value} フレームごと。ゲーム中に切り替えられます）。");
            }
            catch (Exception ex)
            {
                _gaveUp = true;
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗しました: {ex.Message}");
            }
        }

        /// <summary>IsUnderCursor の呼び出しを、箱詰め・配列の確保なしで行う。</summary>
        private static Func<Component, bool> BuildGetter(Type type, MethodInfo getter)
        {
            var dm = new DynamicMethod("KPlugSearchCache_IsUnderCursor", typeof(bool), new[] { typeof(Component) }, typeof(KPlugSceneSearchCache), true);
            ILGenerator il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, type);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            return (Func<Component, bool>)dm.CreateDelegate(typeof(Func<Component, bool>));
        }

        // ===================== 対象を覚える =====================

        private static void HFlagStartPostfix(Component __instance) { Remember(__instance, _flags, _flagSet); }

        private static void MenuCtorPostfix(Component __instance) { Remember(__instance, _menus, _menuSet); }

        private static void Remember(Component c, List<Component> list, HashSet<object> set)
        {
            if (ReferenceEquals(c, null)) return;
            lock (_lock)
            {
                if (set.Add(c)) list.Add(c);
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _flagNeedFull = true;
            _menuNeedFull = true;
            if (_flagCalls > 0 || _menuCalls > 0)
            {
                PatcherPlugin.Logger.LogInfo($"{Tag} 前回のシーン読み込みからの集計: Hシーン判定 {_flagCalls}回（うち元の検索 {_flagSearches}回）/ " +
                    $"メニューの判定 {_menuCalls}回（うち元の検索 {_menuSearches}回）/ 食い違い {_mismatches}回（読み込んだシーン={scene.name}・{mode}）");
            }
            _flagCalls = _flagSearches = _menuCalls = _menuSearches = _mismatches = 0;
        }

        /// <summary>破棄された対象を除き、GameObject が activeInHierarchy の対象があるか（pred があれば、それも満たすもの）。</summary>
        private static bool AnyActive(List<Component> list, Func<Component, bool> pred)
        {
            bool found = false;
            lock (_lock)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    Component c = list[i];
                    if (c == null)
                    {
                        // 破棄済み（Unity の == null）。参照の集合からも外す
                        (ReferenceEquals(list, _flags) ? _flagSet : _menuSet).Remove(c);
                        list.RemoveAt(i);
                        continue;
                    }
                    if (!found && c.gameObject.activeInHierarchy && (pred == null || pred(c))) found = true;
                }
            }
            return found;
        }

        private static bool VerifyDue(ref bool needFull, ref int next)
        {
            int frame = Time.frameCount;
            int interval = VerifyIntervalFrames.Value;
            if (!needFull && (interval <= 0 || frame < next)) return false;
            needFull = false;
            next = frame + Math.Max(1, interval);
            return true;
        }

        private static void LogMismatch(string what, string detail)
        {
            _mismatches++;
            if (_mismatchLogs >= MaxMismatchLogs) return;
            _mismatchLogs++;
            PatcherPlugin.Logger.LogWarning($"{Tag} {what}が元の検索と食い違いました（{detail}。見つかった対象を覚え直し、この回は元の検索の結果を使います。{_mismatchLogs}/{MaxMismatchLogs}）。" +
                $"シーン={SceneManager.GetActiveScene().name}");
        }

        // ===================== Hシーン判定（ParserScene.InH） =====================

        private static bool InHPrefix(ref bool __result)
        {
            if (!CacheHSceneCheck.Value || PatcherPlugin.IsQuitting) return true;
            try
            {
                _flagCalls++;
                if (VerifyDue(ref _flagNeedFull, ref _flagNextVerify))
                {
                    _flagSearches++;
                    UnityEngine.Object found = UnityEngine.Object.FindObjectOfType(_hFlagType);
                    bool cached = AnyActive(_flags, null);
                    bool real = found != null;
                    if (real) Remember(found as Component, _flags, _flagSet);
                    if (cached != real) LogMismatch("Hシーン判定", $"覚えた対象={cached}・元の検索={real}");
                    __result = real;
                    return false;
                }
                __result = AnyActive(_flags, null);
                return false;
            }
            catch (Exception ex)
            {
                CacheHSceneCheck.Value = false;
                PatcherPlugin.Logger.LogError($"{Tag} Hシーン判定の置き換えで例外が起きたため、CacheHSceneCheck を false にして元の処理に戻します: {ex}");
                return true;
            }
        }

        // ===================== メニューの判定（MenuCtrl.CursorIsOverMenus） =====================

        private static bool CursorIsOverMenusPrefix(ref bool __result)
        {
            if (!CacheMenuCursorCheck.Value || PatcherPlugin.IsQuitting) return true;
            try
            {
                _menuCalls++;
                if (VerifyDue(ref _menuNeedFull, ref _menuNextVerify))
                {
                    _menuSearches++;
                    UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(_menuType);
                    int known = 0, missing = 0;
                    var realSet = new HashSet<object>(RefComparer.Instance);
                    bool real = false;
                    for (int i = 0; i < all.Length; i++)
                    {
                        var c = all[i] as Component;
                        if (c == null) continue;
                        realSet.Add(c);
                        lock (_lock) { if (_menuSet.Contains(c)) known++; else missing++; }
                        Remember(c, _menus, _menuSet);
                        if (!real && _isUnderCursor(c)) real = true;
                    }
                    int extra = CountActiveNotIn(_menus, realSet);
                    if (missing > 0 || extra > 0) LogMismatch("メニューの一覧", $"元の検索 {all.Length}個・覚えていなかった {missing}個・覚えた中で元の検索に無い有効なもの {extra}個");
                    __result = real;
                    return false;
                }
                __result = AnyActive(_menus, _isUnderCursor);
                return false;
            }
            catch (Exception ex)
            {
                CacheMenuCursorCheck.Value = false;
                PatcherPlugin.Logger.LogError($"{Tag} メニューの判定の置き換えで例外が起きたため、CacheMenuCursorCheck を false にして元の処理に戻します: {ex}");
                return true;
            }
        }

        private static int CountActiveNotIn(List<Component> list, HashSet<object> realSet)
        {
            int n = 0;
            lock (_lock)
            {
                foreach (Component c in list)
                {
                    if (c != null && c.gameObject.activeInHierarchy && !realSet.Contains(c)) n++;
                }
            }
            return n;
        }

        /// <summary>UnityEngine.Object の Equals は InstanceID で比べるため（コンストラクタの時点では未確定）、参照で比べる。</summary>
        private sealed class RefComparer : IEqualityComparer<object>
        {
            internal static readonly RefComparer Instance = new RefComparer();
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object obj) { return RuntimeHelpers.GetHashCode(obj); }
        }
    }
}
