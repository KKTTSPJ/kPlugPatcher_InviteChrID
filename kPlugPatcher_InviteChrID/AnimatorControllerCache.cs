using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// Hシーンの間、ChaControl.LoadAnimation で読んだモーションのコントローラーを覚えて使い回す（v1.24.0）。
    /// ksPlugPatcher_NRE 0.14.0 の AnimatorControllerCache を KK・KKP に移したもの（KKS で確認済み: 11分で Unity確保 ±2MB、それまでは +約600MB/分）。
    ///
    /// 【原因】
    ///   kPlug のモーションの読み込みは、ほぼ全部 AnimLoader.LoadAnimation → ゲームの ChaControl.LoadAnimation を通る
    ///   （招待キャラの場所のポーズ・ダンス、絡み、AI の体位の変更など）。KK 3.6.3 と KKP 3.5.6 で同じ。
    ///   LoadAnimation は CommonLib.LoadAsset でコントローラーを読み、すぐに AssetBundleManager.UnloadAssetBundle(isUnloadForceRefCount: true) でバンドルを閉じる
    ///   （読んだ物は残す）。そのため同じモーションでも読むたびに、コントローラーとクリップの新しいコピーができ、前のコピーは使われなくなる。
    ///   使われなくなったコピーは次の UnloadUnusedAssets まで残り、H の間は着替えるか H を出るまで溜まり続ける
    ///   （KK・kPlug 3.6.3 で招待6人のダンスを何度も変えると、読み込み1回あたり約22MB、約2分で +2.6GB）。
    ///
    /// 【処理】
    ///   H の間だけ、LoadAnimation に Prefix を当て、(マニフェスト, バンドル, アセット名) ごとに最初に読んだコントローラーを覚える。
    ///   2回目からは覚えた物を animBody に入れて返し、読み込みとバンドルの開け閉めを飛ばす（バンドルが開いたままなら Unity が返すのと同じ物なので、動きは変わらない）。
    ///   覚えた物は H を出たとき（と設定を切ったとき）に忘れる。覚えた分（種類ごとに 1つ）は H の間残る。
    ///
    /// 【v1.24.1: kPlug が CommonLib.LoadAsset で直接読む経路】
    ///   主の女性のダンスなど（MainActionOver.ReplaceAnim → AnimLoader.LoadAnimator と T_Stand の基本のコントローラー）は LoadAnimation を通らず、
    ///   ダンスを1回変えるたびにコントローラー2つのコピー（約55MB）が残った（KK・kPlug 3.6.3、30回で +1.66GB）。
    ///   kPlug の MainActionOver.ReplaceAnim・DestroyOverride、AnimLoader.LoadAnimator・LoadAnimation（上書きのモーションの後半）の中の
    ///   CommonLib.LoadAsset&lt;RuntimeAnimatorController&gt;(string, string, bool, string) の呼び出しを、Transpiler で LoadControllerShared に差し替える
    ///   （命令の operand だけを書き換える。Clone はしない）。LoadControllerShared は H の間だけ上と同じ表で使い回し、それ以外は元の関数を呼ぶ。
    ///   後に続く AssetBundleManager.UnloadAssetBundle は、使い回したときはバンドルが読まれていないので何もしない。
    ///   kPlug 3.6.3・3.5.6（通常・VR）で、これらのメソッドの中身は同じ。毎回作られる AnimatorOverrideController は対象外。
    /// </summary>
    internal static class AnimatorControllerCache
    {
        private const string Tag = "[AnimatorControllerCache]";

        internal static ConfigEntry<bool> Enabled;

        private static readonly Dictionary<string, RuntimeAnimatorController> Cache = new Dictionary<string, RuntimeAnimatorController>();
        private static int _hits, _loads;
        private static bool _failed;
        private static float _nextCheck, _nextSummary;
        private static bool _served;   // Prefix で覚えた物を返した（Postfix で覚え直さない）
        private static bool _kPlugApplied, _kPlugGaveUp;
        private static int _replaced;

        /// <summary>kPlug の経路（v1.24.1）をまだ当てていない（PatcherPlugin.Update の早い適用から TryApplyKPlug を呼ぶ）。</summary>
        internal static bool KPlugPending => !_kPlugApplied && !_kPlugGaveUp;

        internal static void BindConfig(ConfigFile config)
        {
            Enabled = config.Bind(
                "AnimatorControllerCache", "EnableInH", true,
                "Hシーンの間、キャラのモーションのコントローラー（ChaControl.LoadAnimation で読む物）を種類ごとに覚えて使い回します（v1.24.0）。" +
                "kPlug の招待キャラのポーズ・ダンス・体位を変えるたびに同じコントローラーの新しいコピー（1回 約22MB）ができ、H を出るか着替えるまで溜まり続けるのを防ぎます。" +
                "覚えた分は H を出たときに忘れます。ゲーム中に切り替えられます（切ると、その時点で忘れます）。");
            Enabled.SettingChanged += (s, e) => { if (!Enabled.Value) Forget("設定を切った"); };
        }

        internal static void TryApply(Harmony harmony)
        {
            try
            {
                MethodInfo load = AccessTools.Method(typeof(ChaControl), "LoadAnimation", new[] { typeof(string), typeof(string), typeof(string) });
                if (load == null || load.ReturnType != typeof(RuntimeAnimatorController))
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} ChaControl.LoadAnimation(string, string, string) が見つからないため適用しません。");
                    return;
                }
                harmony.Patch(load,
                    prefix: new HarmonyMethod(typeof(AnimatorControllerCache), nameof(LoadAnimationPrefix)),
                    postfix: new HarmonyMethod(typeof(AnimatorControllerCache), nameof(LoadAnimationPostfix)));
                PatcherPlugin.Logger.LogInfo($"{Tag} ChaControl.LoadAnimation に適用しました（有効={Enabled.Value}、H の間だけ使い回す）。");
            }
            catch (Exception ex)
            {
                _failed = true;
                PatcherPlugin.Logger.LogWarning($"{Tag} 適用に失敗しました。元のまま動きます: {ex}");
            }
        }

        private static bool InHScene()
        {
            return SceneManager.GetSceneByName("H").isLoaded || SceneManager.GetSceneByName("VRHScene").isLoaded;
        }

        private static string Key(string assetBundleName, string assetName, string manifestName)
        {
            return (manifestName ?? "") + "|" + (assetBundleName ?? "") + "|" + (assetName ?? "");
        }

        private static bool Active { get { return !_failed && Enabled.Value && InHScene(); } }

        // 2回目からは覚えた物を入れて、元の処理（読み込み・バンドルを閉じる）を飛ばす
        private static bool LoadAnimationPrefix(ChaControl __instance, string assetBundleName, string assetName, string manifestName, ref RuntimeAnimatorController __result)
        {
            try
            {
                if (!Active) return true;
                Animator anim = __instance.animBody;
                if (anim == null) return true;
                if (!Cache.TryGetValue(Key(assetBundleName, assetName, manifestName), out RuntimeAnimatorController cached)) return true;
                if (cached == null) return true;   // 何かに破棄された（読み直して覚え直す）
                anim.runtimeAnimatorController = cached;
                __result = cached;
                _hits++;
                _served = true;
                return false;
            }
            catch (Exception ex)
            {
                _failed = true;
                PatcherPlugin.Logger.LogWarning($"{Tag} 失敗したので止めます（元の処理で続けます）: {ex}");
                return true;
            }
        }

        // 元の処理で読んだ物を覚える
        private static void LoadAnimationPostfix(string assetBundleName, string assetName, string manifestName, RuntimeAnimatorController __result)
        {
            if (_served) { _served = false; return; }
            if (__result == null) return;
            try
            {
                if (!Active) return;
                Cache[Key(assetBundleName, assetName, manifestName)] = __result;
                _loads++;
            }
            catch (Exception ex)
            {
                _failed = true;
                PatcherPlugin.Logger.LogWarning($"{Tag} 失敗したので止めます（元の処理で続けます）: {ex}");
            }
        }

        // ===================== v1.24.1: kPlug が CommonLib.LoadAsset で直接読む経路 =====================

        /// <summary>差し替える (型の名前, メソッドの名前)。名前空間は通常版 kPlug・VR 版 kPlugVR で変える。</summary>
        private static readonly string[][] KPlugTargets =
        {
            new[] { "CmpH.MainActionOver", "ReplaceAnim" },
            new[] { "CmpH.MainActionOver", "DestroyOverride" },
            new[] { "DefAndParse.AnimLoader", "LoadAnimator" },
            new[] { "DefAndParse.AnimLoader", "LoadAnimation" },
        };

        /// <summary>kPlug の型が見つかるまで繰り返し呼んでよい（PatcherPlugin.Update の早い適用から呼ぶ）。</summary>
        internal static void TryApplyKPlug(Harmony harmony)
        {
            if (_kPlugApplied || _kPlugGaveUp || _failed) return;
            try
            {
                string asmName = Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase) ? "kPlugVR" : "kPlug";
                Assembly kPlug = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    if (asm.GetName().Name == asmName) { kPlug = asm; break; }
                if (kPlug == null) return;   // kPlug がまだ読まれていない
                _kPlugGaveUp = true;   // 以降は成功・失敗にかかわらず試さない

                var transpiler = new HarmonyMethod(AccessTools.Method(typeof(AnimatorControllerCache), nameof(Transpiler)));
                var done = new List<string>();
                var missing = new List<string>();
                foreach (string[] target in KPlugTargets)
                {
                    Type t = kPlug.GetType(asmName + "." + target[0]);
                    var methods = new List<MethodInfo>();
                    if (t != null)
                        foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                            if (m.Name == target[1] && !m.IsAbstract) methods.Add(m);
                    if (methods.Count == 0) { missing.Add(target[0] + "." + target[1]); continue; }
                    foreach (MethodInfo m in methods)
                    {
                        int before = _replaced;
                        harmony.Patch(m, transpiler: transpiler);
                        if (_replaced > before) done.Add($"{target[0]}.{target[1]}（{_replaced - before}か所）");
                    }
                }
                _kPlugApplied = done.Count > 0;
                PatcherPlugin.Logger.LogInfo($"{Tag} {asmName} の CommonLib.LoadAsset<RuntimeAnimatorController> の呼び出しを差し替えました: {(done.Count == 0 ? "なし" : string.Join("、", done.ToArray()))}" +
                                             (missing.Count > 0 ? $"（見つからない: {string.Join("、", missing.ToArray())}）" : "") + "。");
            }
            catch (Exception ex)
            {
                _kPlugGaveUp = true;
                PatcherPlugin.Logger.LogWarning($"{Tag} kPlug の経路の適用に失敗しました（LoadAnimation の使い回しは続けます）: {ex}");
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo shared = AccessTools.Method(typeof(AnimatorControllerCache), nameof(LoadControllerShared));
            foreach (CodeInstruction code in instructions)
            {
                if ((code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) && code.operand is MethodInfo mi && IsLoadController(mi))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = shared;   // ラベルとブロックはそのまま（Clone しない）
                    _replaced++;
                }
                yield return code;
            }
        }

        private static bool IsLoadController(MethodInfo mi)
        {
            if (mi.Name != "LoadAsset" || !mi.IsGenericMethod || mi.DeclaringType != typeof(CommonLib)) return false;
            Type[] args = mi.GetGenericArguments();
            if (args.Length != 1 || args[0] != typeof(RuntimeAnimatorController)) return false;
            ParameterInfo[] ps = mi.GetParameters();
            return ps.Length == 4 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string)
                && ps[2].ParameterType == typeof(bool) && ps[3].ParameterType == typeof(string);
        }

        /// <summary>CommonLib.LoadAsset&lt;RuntimeAnimatorController&gt; と同じ引数。H の間は LoadAnimation と同じ表で使い回す。</summary>
        internal static RuntimeAnimatorController LoadControllerShared(string assetBundleName, string assetName, bool clone, string manifestName)
        {
            bool active;
            try { active = !clone && Active; }
            catch { active = false; }
            if (!active) return CommonLib.LoadAsset<RuntimeAnimatorController>(assetBundleName, assetName, clone, manifestName);
            string key = Key(assetBundleName, assetName, manifestName);
            if (Cache.TryGetValue(key, out RuntimeAnimatorController cached) && cached != null)
            {
                _hits++;
                return cached;
            }
            RuntimeAnimatorController loaded = CommonLib.LoadAsset<RuntimeAnimatorController>(assetBundleName, assetName, clone, manifestName);
            if (loaded != null)
            {
                Cache[key] = loaded;
                _loads++;
            }
            return loaded;
        }

        /// <summary>PatcherPlugin.Update から毎フレーム呼ぶ（1秒に1回、H を出たかを見る）。</summary>
        internal static void OnUpdate()
        {
            if (Cache.Count == 0 && _hits == 0) return;
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 1f;
            if (!InHScene()) { Forget("Hシーンを出た"); return; }
            // 確かめるための記録（5分に1回、使い回したときだけ）
            if (_hits > 0 && Time.unscaledTime >= _nextSummary)
            {
                if (_nextSummary > 0f)
                    PatcherPlugin.Logger.LogInfo($"{Tag} H の間: 覚えたコントローラー {Cache.Count}種・読んだ {_loads}回・使い回した {_hits}回。");
                _nextSummary = Time.unscaledTime + 300f;
            }
        }

        private static void Forget(string reason)
        {
            if (Cache.Count == 0 && _hits == 0 && _loads == 0) return;
            PatcherPlugin.Logger.LogInfo($"{Tag} {reason}ので、覚えたコントローラー {Cache.Count}種を忘れます（この H で読んだ {_loads}回・使い回した {_hits}回）。");
            Cache.Clear();
            _hits = _loads = 0;
            _nextSummary = 0f;
        }
    }
}
