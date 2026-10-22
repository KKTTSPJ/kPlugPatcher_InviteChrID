using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// kPlugVR 3.599 以降の VR のメニューが、招待のあとで断続的にちらつく（顔画像の一部と文字が消えたり戻ったりする）件を切り分けるための診断（v1.14.0、読み取り専用）。
    /// 経緯:
    ///   報告された症状: 招待のタイミングによって、招待メニューの顔画像の一定範囲が欠け、kPlug の他のメニューも文字だけがちらつく
    ///   （背景の箱は崩れない）。スクロールのときに多い。キャラ選択に戻るか、次の招待で直ることがある。kPlug 3.5.6 では体感したことがない。
    ///
    /// 推測（どれも未確認）と、それぞれを見分けるために記録するもの:
    ///   1. 招待メニューのタブの作り直し（IE_RebuildTab → TabContent）が重なり、ボタンが2組できている
    ///      → 作り直しの要求と実行をログに出し、実行の前にボタンが残っていないか、後で同じ位置のボタンが重なっていないかを数える。
    ///   2. 文字のメッシュが空になっている（TextMeshPro の作り直しの問題）
    ///      → 毎フレームの終わりに、文字のある TextMeshPro のうち頂点が0のものを数える。
    ///   3. Canvas が描いていない（Graphic.depth が -1。カリング・並べ替えから外れた）
    ///      → 毎フレームの終わりに数える。canvasRenderer.cull も数える。
    ///   4. ステンシル（Mask）の状態の崩れ
    ///      → 描画に使うマテリアル（materialForRendering）のステンシルの値の分布と、uGUI / TextMeshPro のステンシル用マテリアルの数を出す。
    ///   2・3 に何も出ず、4 の分布が変わらないのにちらつくなら、CPU 側の状態ではなく GPU 側の描画（並び順・ステンシルの残り）を疑う。
    ///
    /// 記録の出し方:
    ///   - kPlugVR のメニュー（BaseMenu の frame.menuGo）のどれかが表示されている間だけ、毎フレームの終わりに数える。
    ///   - 5秒ごとに、異常があったとき・メニューの顔ぶれやステンシルの分布が変わったときだけ、まとめを出す（何も無くても60秒に1回）。
    ///   - 異常を初めて見たときは、その部品を最大20個まで詳しく出す（30秒に1回まで）。
    ///   - SnapshotKey（既定 Ctrl+F9）で、今の状態を詳しく出す（ちらつきを見たときに押す）。
    ///     v1.14.1: VR ではコントローラーに触れているだけで KeyboardShortcut.IsDown() が反応しなかったので、主キーと修飾キーだけを見るようにした。
    ///   - v1.14.1: メニューの表示中は30秒ごとと詳細のときに「描画の状況」（カメラとメニューの間にかかるレンダラー、カメラ・Canvas の設定）を出す。
    ///     ちらつきの最中も CPU 側の状態に異常が無かったため。
    /// VR のプロセスの kPlugVR だけが対象（通常モードでは kPlugVR の型を調べると落ちる）。
    /// 毎フレームの数えは、メニューを表示している間だけで、部品数百個分の軽い読み取り（0.1〜0.3ms 程度の見込み）。
    /// </summary>
    internal static class VRMenuFlickerDiagnostics
    {
        internal const string Tag = "[VRMenuDiag]";

        internal static ConfigEntry<bool> Enable;
        internal static ConfigEntry<KeyboardShortcut> SnapshotKey;
        internal static ConfigEntry<bool> DrawMenusOnTop;
        internal static ConfigEntry<DrawOrderMethod> Method;
        internal static ConfigEntry<int> MenuSortingOrder;
        internal static ConfigEntry<int> CharacterSortingOrder;

        /// <summary>メニューをキャラより後に描かせる方法（v1.14.3）。</summary>
        internal enum DrawOrderMethod
        {
            /// <summary>キャラの透過の部品の sortingOrder を下げて、キャラを先に描く（メニューとキャラ以外の順番は kPlugVR の元のまま）。</summary>
            CharactersFirst,
            /// <summary>メニューの Canvas の sortingOrder を上げて、メニューを最後に描く（v1.14.2 の方法）。</summary>
            MenusLast,
        }

        internal static void BindConfig(ConfigFile config)
        {
            DrawMenusOnTop = config.Bind(
                "VRMenuDrawOrder", "DrawMenusOnTop", true,
                "kPlugVR（3.599 以降）の VR のメニュー（Canvas）を、キャラより後に描かせる（v1.14.2）。メニューは深度を書かないため、" +
                "描く順番がメニューより後になるキャラの部品（透過の服・髪など。描画の順番の値 renderQueue が 3000 以上のもの）は、メニューの奥にあってもメニューの上に描かれる" +
                "（透過の服で確認）。順番が同じ 3000 のものは距離で前後が入れ替わり、招待メニューのちらつきの原因になる（有効で消えることを確認済み）。" +
                "方法は Method で選ぶ。ゲーム中に切り替えられる（無効にすると元の値に戻す）。VRモードのみ対象。");
            Method = config.Bind(
                "VRMenuDrawOrder", "Method", DrawOrderMethod.CharactersFirst,
                "DrawMenusOnTop の方法（v1.14.3）。ゲーム中に切り替えられる。\n" +
                "CharactersFirst: キャラの透過の部品（renderQueue 2500 以上のマテリアルを持つレンダラー。男のシルエットは除く）の sortingOrder を CharacterSortingOrder に下げ、キャラをメニューより先に描く。" +
                "レイ・手のシルエットなど、キャラ以外の順番は kPlugVR の元のまま。キャラの半透明の部品と、マップの透過の物（窓ガラスなど）の前後が変わることがある。\n" +
                "MenusLast: メニューの Canvas の sortingOrder を MenuSortingOrder に上げ、メニューを全部の最後に描く（v1.14.2）。手前のレイがメニューに隠れる・招待メニューの枠の外のカードが空中に見えることがある。");
            MenuSortingOrder = config.Bind(
                "VRMenuDrawOrder", "MenuSortingOrder", 30000,
                new ConfigDescription("Method = MenusLast のときの、メニューの Canvas の sortingOrder（kPlugVR の既定は 0）。", new AcceptableValueRange<int>(1, 32767)));
            CharacterSortingOrder = config.Bind(
                "VRMenuDrawOrder", "CharacterSortingOrder", -1,
                new ConfigDescription("Method = CharactersFirst のときの、キャラの透過の部品の sortingOrder（ゲームの既定は 0。メニューは 0）。", new AcceptableValueRange<int>(-32768, -1)));
            Enable = config.Bind(
                "VRMenuFlickerDiagnostics", "Enable", false,
                "kPlugVR（3.599 以降）の VR のメニューが、招待のあとで断続的にちらつく件を切り分けるための診断（v1.14.0、読み取り専用）。招待メニューのタブの作り直しの重なり、文字のメッシュが空になっていないか、Canvas が描いていない部品が無いか、ステンシル（Mask）の状態をログ出力します。メニューを表示している間だけ毎フレーム数えます（わずかな負荷）。VRモードのみ対象。原因が分かったら無効化して構いません。変更はゲーム再起動後に反映されます。");
            SnapshotKey = config.Bind(
                "VRMenuFlickerDiagnostics", "SnapshotKey", new KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl),
                "ちらつきを見たときに押すと、今のメニューの状態（異常のある部品、ステンシルの分布、招待メニューのボタンの数）を詳しくログ出力します。");
        }

        // ===================== kPlugVR の型（リフレクション。kPlugVR は参照しない） =====================

        private static Type _baseMenuType, _menuInviteType;
        private static FieldInfo _frameField, _menuGoField;            // BaseMenu.frame / VRUI.MenuFrame.menuGo
        private static FieldInfo _vrTabsField;                         // MenuInvite.VRTabs（List<VRUI.VRtab>）
        private static FieldInfo _tabButtonListField, _tabContentGoField, _tabMetaStringField;   // VRUI.VRtab
        private static FieldInfo _butGoField;                          // VRUI.VRButton.butGo
        private static bool _applied;

        /// <summary>診断か描く順番の変更のどちらかが有効なら true（どちらも TryApply で当てる）。</summary>
        internal static bool EnableOrDrawOrder => Enable != null && (Enable.Value || DrawMenusOnTop.Value);

        internal static void TryApply(Harmony harmony)
        {
            if (_applied || !EnableOrDrawOrder) return;
            if (!BepInEx.Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase)) { _applied = true; return; }

            Assembly asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name.Equals("kPlugVR", StringComparison.OrdinalIgnoreCase));
            if (asm == null) return;
            _baseMenuType = asm.GetType("kPlugVR.Menu.BaseMenu");
            _menuInviteType = asm.GetType("kPlugVR.Menu.MenuInvite");
            var vrui = asm.GetType("kPlugVR.Menu.VRUI");
            if (_baseMenuType == null || _menuInviteType == null || vrui == null) return;   // 3.599 より前の kPlugVR（型が無い）
            _applied = true;

            try
            {
                const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var frameType = vrui.GetNestedType("MenuFrame", BindingFlags.Public | BindingFlags.NonPublic);
                var tabType = vrui.GetNestedType("VRtab", BindingFlags.Public | BindingFlags.NonPublic);
                var butType = vrui.GetNestedType("VRButton", BindingFlags.Public | BindingFlags.NonPublic);
                _frameField = _baseMenuType.GetField("frame", I);
                _menuGoField = frameType != null ? frameType.GetField("menuGo", I) : null;
                _vrTabsField = _menuInviteType.GetField("VRTabs", I);
                _tabButtonListField = tabType != null ? tabType.GetField("buttonList", I) : null;
                _tabContentGoField = tabType != null ? tabType.GetField("contentGo", I) : null;
                _tabMetaStringField = tabType != null ? tabType.GetField("metaString", I) : null;
                _butGoField = butType != null ? butType.GetField("butGo", I) : null;
                if (_frameField == null || _menuGoField == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} kPlugVR のメニューの構造が想定と違うため、診断は行いません（BaseMenu.frame={_frameField != null} / MenuFrame.menuGo={_menuGoField != null}）。");
                    return;
                }

                var names = new List<string>();
                if (Enable.Value)
                {
                    PatchIf(harmony, AccessTools.DeclaredMethod(_menuInviteType, "IE_RebuildTab"), nameof(RebuildRequestPrefix), null, names);
                    PatchIf(harmony, AccessTools.DeclaredMethod(_menuInviteType, "TabContent"), nameof(TabContentPrefix), nameof(TabContentPostfix), names);
                    PatchIf(harmony, AccessTools.DeclaredMethod(_menuInviteType, "CleanCreatedFileInfo"), nameof(CleanCreatedPrefix), null, names);
                    PatchIf(harmony, AccessTools.DeclaredMethod(_menuInviteType, "InviteSomeone"), null, nameof(InviteSomeonePostfix), names);
                }
                // メニューを作ったときに、描く順番の変更もすぐ当てるため（MarkDirty）、こちらは診断が無効でも当てる
                PatchIf(harmony, AccessTools.DeclaredMethod(_menuInviteType, "Start"), null, nameof(MenuInviteStartPostfix), names);

                var go = new GameObject("kPlugPatcher_VRMenuDiag");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<VRMenuFlickerProbe>();

                if (Enable.Value)
                    PatcherPlugin.Logger.LogInfo($"{Tag} VR のメニューのちらつきの診断を始めました（MenuInvite の {string.Join(" / ", names.ToArray())} に記録を適用。" +
                                                 $"メニューの表示中は毎フレーム数えます。{SnapshotKey.Value} で詳しく出します）。");
                PatcherPlugin.Logger.LogInfo($"{Tag} メニューの描く順番: DrawMenusOnTop={DrawMenusOnTop.Value} / Method={Method.Value}" +
                                             $"（MenusLast はメニューを sortingOrder {MenuSortingOrder.Value}、CharactersFirst はキャラの透過の部品を {CharacterSortingOrder.Value}。ゲーム中に切り替えられます）。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 診断を始められませんでした: {ex.Message}");
            }
        }

        private static void PatchIf(Harmony harmony, MethodInfo target, string prefix, string postfix, List<string> names)
        {
            if (target == null) return;
            harmony.Patch(target,
                prefix: prefix != null ? new HarmonyMethod(typeof(VRMenuFlickerDiagnostics).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)) : null,
                postfix: postfix != null ? new HarmonyMethod(typeof(VRMenuFlickerDiagnostics).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static)) : null);
            names.Add(target.Name);
        }

        // ===================== 招待メニューのタブの作り直し =====================

        private static int _pendingRebuilds;
        private static int _rebuildSeq;

        private static void RebuildRequestPrefix()
        {
            _pendingRebuilds++;
            _rebuildSeq++;
            string overlap = _pendingRebuilds > 1 ? $"【重なり】前の作り直しがまだ終わっていない（待ち {_pendingRebuilds} 件）" : "";
            PatcherPlugin.Logger.LogInfo($"{Tag} 招待メニューのタブの作り直しの要求 #{_rebuildSeq}（frame={Time.frameCount}）{overlap}");
            VRMenuFlickerProbe.MarkEvent($"作り直しの要求#{_rebuildSeq}");
        }

        private static void TabContentPrefix(object __0, out int __state)
        {
            __state = -1;
            try
            {
                var list = _tabButtonListField != null ? _tabButtonListField.GetValue(__0) as IList : null;
                __state = list != null ? list.Count : -1;
            }
            catch { }
        }

        private static void TabContentPostfix(object __0, object __1, int __state)
        {
            bool fromRebuild = _pendingRebuilds > 0;
            if (fromRebuild) _pendingRebuilds--;
            try
            {
                var list = _tabButtonListField != null ? _tabButtonListField.GetValue(__0) as IList : null;
                var content = _tabContentGoField != null ? _tabContentGoField.GetValue(__0) as GameObject : null;
                string tab = _tabMetaStringField != null ? _tabMetaStringField.GetValue(__0) as string : "?";
                int entries = (__1 as IList) != null ? ((IList)__1).Count : -1;
                int children = content != null ? content.transform.childCount : -1;
                int dupPos = CountDuplicatePositions(content);
                string warn = "";
                if (__state > 0) warn += $" 【前のボタンが {__state} 個残ったまま作った】";
                if (dupPos > 0) warn += $" 【同じ位置に重なったボタン {dupPos} 組】";
                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} 招待メニューのタブ「{tab}」を{(fromRebuild ? "作り直しました" : "作りました")}（frame={Time.frameCount}）: " +
                    $"一覧 {entries} 件 → ボタン {(list != null ? list.Count : -1)} 個（作る前 {__state} 個）/ タブの中の子 {children} 個（破棄待ちを含む）{warn}");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} タブの記録に失敗: {ex.Message}");
            }
            VRMenuFlickerProbe.MarkDirty();
        }

        /// <summary>タブの中の、有効で破棄待ちでない子のうち、ほぼ同じ位置にあるものの組の数。</summary>
        internal static int CountDuplicatePositions(GameObject content)
        {
            if (content == null) return -1;
            var seen = new Dictionary<long, int>();
            int dup = 0;
            foreach (Transform child in content.transform)
            {
                if (child == null || !child.gameObject.activeSelf) continue;
                var p = child.localPosition;
                long key = ((long)Mathf.RoundToInt(p.x * 10f) << 32) ^ (uint)Mathf.RoundToInt(p.y * 10f);
                int n;
                seen.TryGetValue(key, out n);
                if (n == 1) dup++;
                seen[key] = n + 1;
            }
            return dup;
        }

        private static void CleanCreatedPrefix(int __0, bool __2)
        {
            PatcherPlugin.Logger.LogInfo($"{Tag} 招待キャラが抜けた（CleanCreatedFileInfo id={__0}{(__2 ? " 男" : "")}、frame={Time.frameCount}）。次の LateUpdate でタブを作り直します。");
            VRMenuFlickerProbe.MarkEvent($"退場id={__0}");
        }

        private static void InviteSomeonePostfix()
        {
            VRMenuFlickerProbe.MarkEvent("招待");
        }

        private static void MenuInviteStartPostfix()
        {
            PatcherPlugin.Logger.LogInfo($"{Tag} 招待メニューを作りました（MenuInvite.Start、frame={Time.frameCount}）。");
            _pendingRebuilds = 0;
            VRMenuFlickerProbe.MarkDirty();
        }

        // ===================== メニューの部品の取得（VRMenuFlickerProbe から使う） =====================

        /// <summary>今あるメニュー（BaseMenu）の型名と frame.menuGo。</summary>
        internal static List<KeyValuePair<string, GameObject>> FindMenuRoots()
        {
            var result = new List<KeyValuePair<string, GameObject>>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(_baseMenuType))
            {
                var mb = o as MonoBehaviour;
                if (mb == null) continue;
                var frame = _frameField.GetValue(mb);
                var go = frame != null ? _menuGoField.GetValue(frame) as GameObject : null;
                if (go != null) result.Add(new KeyValuePair<string, GameObject>(mb.GetType().Name, go));
            }
            return result;
        }

        /// <summary>招待メニューのタブごとのボタンの数・子の数・重なり（無ければ空文字）。</summary>
        internal static string DescribeInviteTabs()
        {
            if (_vrTabsField == null) return "";
            var parts = new List<string>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(_menuInviteType))
            {
                var tabs = _vrTabsField.GetValue(o) as IList;
                if (tabs == null) continue;
                foreach (var tab in tabs)
                {
                    if (tab == null) continue;
                    var list = _tabButtonListField != null ? _tabButtonListField.GetValue(tab) as IList : null;
                    var content = _tabContentGoField != null ? _tabContentGoField.GetValue(tab) as GameObject : null;
                    string name = _tabMetaStringField != null ? _tabMetaStringField.GetValue(tab) as string : "?";
                    int activeChildren = 0, alive = 0;
                    if (content != null)
                        foreach (Transform c in content.transform) { alive++; if (c.gameObject.activeSelf) activeChildren++; }
                    int destroyedButtons = 0;
                    if (list != null && _butGoField != null)
                        foreach (var b in list) if (b == null || (_butGoField.GetValue(b) as GameObject) == null) destroyedButtons++;
                    parts.Add($"「{name}」ボタン {(list != null ? list.Count : -1)}（破棄済み {destroyedButtons}）/ 子 {alive}（有効 {activeChildren}）/ 重なり {CountDuplicatePositions(content)} 組" +
                              $"{(content != null && !content.activeInHierarchy ? " / 非表示" : "")}");
                }
            }
            return parts.Count == 0 ? "" : "招待メニューのタブ: " + string.Join(", ", parts.ToArray());
        }
    }

    /// <summary>
    /// VRMenuFlickerDiagnostics の毎フレームの数え（専用の GameObject で動く）。
    /// </summary>
    internal sealed class VRMenuFlickerProbe : MonoBehaviour
    {
        private const string Tag = VRMenuFlickerDiagnostics.Tag;
        private const float RefreshSeconds = 2f, IdleRefreshSeconds = 10f, WindowSeconds = 5f, HeartbeatSeconds = 60f, DetailCooldown = 30f;

        private sealed class Entry
        {
            public Graphic G;
            public TMPro.TMP_Text Text;   // TextMeshPro のときだけ
            public string Menu;
        }

        private static bool _dirty = true;
        private static bool _charaScanRequested = true;   // キャラの部品の描く順番をすぐ付け直す（v1.14.3）
        private static readonly List<string> _events = new List<string>();

        internal static void MarkDirty() { _dirty = true; _charaScanRequested = true; }
        internal static void MarkEvent(string e) { _events.Add($"{e}@{Time.frameCount}"); _dirty = true; _charaScanRequested = true; }

        private readonly List<Entry> _entries = new List<Entry>();
        private List<KeyValuePair<string, GameObject>> _roots = new List<KeyValuePair<string, GameObject>>();
        private float _nextRefresh, _windowStart = -1f, _lastLog = -100f, _lastDetail = -100f;
        private string _lastSignature = "";
        private bool _wasVisible;

        // 窓の数え
        private int _frames, _fEmptyText, _fNotDrawn, _fCulled, _fNoTex, _maxEmptyText, _maxNotDrawn, _maxCulled, _maxNoTex, _changes, _prevAnomaly;
        private readonly HashSet<string> _visibleMenus = new HashSet<string>();
        private bool _detailRequested;

        private readonly WaitForEndOfFrame _eof = new WaitForEndOfFrame();

        private void Start()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
            StartCoroutine(Loop());
        }

        private void OnDestroy()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            _dirty = true;
            _charaScanRequested = true;
        }

        private void Update()
        {
            try
            {
                // KeyboardShortcut.IsDown() は、指定外のキーが1つでも押されていると反応しない（マウスのボタンだけ除外）。
                // VR では、コントローラーのボタン・スティックに指が触れているだけで「ジョイスティックのボタン」が押された扱いになるため、
                // VR では Ctrl+F9 を何度押しても反応しなかった。主キーと指定の修飾キーだけを見る
                var key = VRMenuFlickerDiagnostics.SnapshotKey.Value;
                if (key.MainKey != KeyCode.None && Input.GetKeyDown(key.MainKey) && key.Modifiers.All(m => Input.GetKey(m) || IsOtherSideHeld(m)))
                {
                    _detailRequested = true;
                    _dirty = true;
                    PatcherPlugin.Logger.LogInfo($"{Tag} {key} を受け付けました（frame={Time.frameCount}）。");
                }
            }
            catch { }
        }

        /// <summary>左右のある修飾キーは、反対側でもよい（右 Ctrl で押しても反応させる）。</summary>
        private static bool IsOtherSideHeld(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.LeftControl: return Input.GetKey(KeyCode.RightControl);
                case KeyCode.RightControl: return Input.GetKey(KeyCode.LeftControl);
                case KeyCode.LeftShift: return Input.GetKey(KeyCode.RightShift);
                case KeyCode.RightShift: return Input.GetKey(KeyCode.LeftShift);
                case KeyCode.LeftAlt: return Input.GetKey(KeyCode.RightAlt);
                case KeyCode.RightAlt: return Input.GetKey(KeyCode.LeftAlt);
                default: return false;
            }
        }

        private IEnumerator Loop()
        {
            while (true)
            {
                yield return _eof;
                if (PatcherPlugin.IsQuitting) continue;
                try { Sample(); }
                catch (Exception ex)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} 数えている途中で例外（診断を止めます）: {ex.Message}");
                    enabled = false;
                    yield break;
                }
            }
        }

        private void Refresh()
        {
            _roots = VRMenuFlickerDiagnostics.FindMenuRoots();
            _entries.Clear();
            foreach (var kv in _roots)
            {
                foreach (var g in kv.Value.GetComponentsInChildren<Graphic>(true))
                {
                    if (g == null) continue;
                    _entries.Add(new Entry { G = g, Text = g as TMPro.TMP_Text, Menu = kv.Key });
                }
            }
            _dirty = false;
            // FindObjectsOfType は重いので、メニューを表示していない間は間隔を空ける（作り直し・シーン変化のときは MarkDirty ですぐ取り直す）
            _nextRefresh = Time.realtimeSinceStartup + (_wasVisible ? RefreshSeconds : IdleRefreshSeconds);
        }

        private void Sample()
        {
            float now = Time.realtimeSinceStartup;
            if (_dirty || now >= _nextRefresh) Refresh();
            ApplyDrawOrder();
            if (!VRMenuFlickerDiagnostics.Enable.Value) return;

            bool visible = false;
            foreach (var kv in _roots)
                if (kv.Value != null && kv.Value.activeInHierarchy) { visible = true; _visibleMenus.Add(kv.Key); }

            if (!visible)
            {
                if (_wasVisible) Flush(now, "メニューを閉じた");
                _wasVisible = false;
                if (_detailRequested)
                {
                    _detailRequested = false;
                    LogDetail($"{VRMenuFlickerDiagnostics.SnapshotKey.Value} が押された（メニューは非表示）");
                }
                return;
            }
            if (!_wasVisible) { StartWindow(now); _wasVisible = true; }

            int emptyText = 0, notDrawn = 0, culled = 0, noTex = 0;
            foreach (var e in _entries)
            {
                var g = e.G;
                if (g == null || !g.isActiveAndEnabled) continue;
                if (g.canvasRenderer.cull) { culled++; continue; }
                if (g.depth < 0) notDrawn++;
                if (e.Text != null)
                {
                    if (!string.IsNullOrEmpty(e.Text.text))
                    {
                        var m = e.Text.mesh;
                        if (m == null || m.vertexCount == 0) emptyText++;
                    }
                }
                else
                {
                    var mat = g.material;
                    if (mat != null && mat != g.defaultMaterial && mat.HasProperty("_MainTex") && mat.mainTexture == null) noTex++;
                }
            }
            _frames++;
            if (emptyText > 0) _fEmptyText++;
            if (notDrawn > 0) _fNotDrawn++;
            if (culled > 0) _fCulled++;
            if (noTex > 0) _fNoTex++;
            _maxEmptyText = Math.Max(_maxEmptyText, emptyText);
            _maxNotDrawn = Math.Max(_maxNotDrawn, notDrawn);
            _maxCulled = Math.Max(_maxCulled, culled);
            _maxNoTex = Math.Max(_maxNoTex, noTex);
            int anomaly = emptyText + notDrawn + noTex;
            if (_frames > 1 && anomaly != _prevAnomaly) _changes++;
            _prevAnomaly = anomaly;

            bool anomalyNow = anomaly > 0;
            if (_detailRequested || (anomalyNow && now - _lastDetail >= DetailCooldown))
            {
                LogDetail(_detailRequested ? $"{VRMenuFlickerDiagnostics.SnapshotKey.Value} が押された" : "異常のある部品を見つけた");
                _lastDetail = now;
                _detailRequested = false;
            }

            MaybeLogContext(now, false);
            if (now - _windowStart >= WindowSeconds) Flush(now, null);
        }

        private void StartWindow(float now)
        {
            _windowStart = now;
            _frames = _fEmptyText = _fNotDrawn = _fCulled = _fNoTex = 0;
            _maxEmptyText = _maxNotDrawn = _maxCulled = _maxNoTex = 0;
            _changes = 0;
            _prevAnomaly = 0;
            _visibleMenus.Clear();
            _events.Clear();
        }

        /// <summary>窓のまとめ。異常・顔ぶれやステンシルの分布の変化・出来事があったとき、または60秒ぶりのときだけ出す。</summary>
        private void Flush(float now, string reason)
        {
            if (_windowStart < 0f) { StartWindow(now); return; }
            string stencil = DescribeStencil();
            string tabs = VRMenuFlickerDiagnostics.DescribeInviteTabs();
            string menus = string.Join(",", _visibleMenus.OrderBy(s => s).ToArray());
            string signature = menus + "|" + stencil + "|" + tabs;
            bool anomaly = _fEmptyText + _fNotDrawn + _fNoTex > 0;
            bool force = reason != null || _events.Count > 0;
            if (anomaly || force || signature != _lastSignature || now - _lastLog >= HeartbeatSeconds)
            {
                var sb = new StringBuilder();
                sb.Append($"{Tag} 直近 {now - _windowStart:F0}秒{(reason != null ? "（" + reason + "）" : "")}: 表示したメニュー {menus} / {_frames} フレーム");
                sb.Append($" | 文字のメッシュが空 {_fEmptyText}f（最大 {_maxEmptyText}個）/ Canvas が描いていない {_fNotDrawn}f（最大 {_maxNotDrawn}個）/ " +
                          $"テクスチャが無い {_fNoTex}f（最大 {_maxNoTex}個）/ カリング中 {_fCulled}f（最大 {_maxCulled}個）/ 異常の数が変わった {_changes}回");
                if (_events.Count > 0) sb.Append($" | 出来事: {string.Join(", ", _events.ToArray())}");
                sb.Append($" | {stencil}");
                if (tabs.Length > 0) sb.Append($" | {tabs}");
                PatcherPlugin.Logger.LogInfo(sb.ToString());
                _lastLog = now;
                _lastSignature = signature;
            }
            StartWindow(now);
        }

        /// <summary>表示中の部品の、描画に使うマテリアルのステンシルの値の分布と、ステンシル用マテリアルの数。</summary>
        private string DescribeStencil()
        {
            var hist = new Dictionary<string, int>();
            var mats = new HashSet<int>();
            int active = 0, texts = 0, masks = 0, maskGraphicOff = 0;
            foreach (var e in _entries)
            {
                var g = e.G;
                if (g == null || !g.isActiveAndEnabled) continue;
                active++;
                if (e.Text != null) texts++;
                var mask = g.GetComponent<Mask>();
                if (mask != null && mask.isActiveAndEnabled) { masks++; if (!mask.showMaskGraphic) maskGraphicOff++; }
                var m = g.materialForRendering;
                if (m == null) { Inc(hist, "マテリアルなし"); continue; }
                mats.Add(m.GetInstanceID());
                Inc(hist, StencilKey(m) + (e.Text != null ? "（文字）" : ""));
            }
            string top = string.Join(", ", hist.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} ×{kv.Value}").ToArray());
            return $"表示中の部品 {active}（文字 {texts}）/ Mask {masks}（枠を描かない {maskGraphicOff}）/ 描画のマテリアル {mats.Count}種 / " +
                   $"ステンシル用マテリアル uGUI {CountStatic("UnityEngine.UI.StencilMaterial, UnityEngine.UI", "m_List")}・TMP {CountStatic("TMPro.TMP_MaterialManager, TextMeshPro-1.0.55.56.0b12", "m_materialList")} / " +
                   $"ステンシルの分布: {top}";
        }

        private static string StencilKey(Material m)
        {
            if (!m.HasProperty("_Stencil")) return "ステンシルなし";
            return $"ref{m.GetFloat("_Stencil"):F0}/comp{(m.HasProperty("_StencilComp") ? m.GetFloat("_StencilComp") : -1):F0}" +
                   $"/op{(m.HasProperty("_StencilOp") ? m.GetFloat("_StencilOp") : -1):F0}/rm{(m.HasProperty("_StencilReadMask") ? m.GetFloat("_StencilReadMask") : -1):F0}" +
                   $"/cm{(m.HasProperty("_ColorMask") ? m.GetFloat("_ColorMask") : -1):F0}";
        }

        private static void Inc(Dictionary<string, int> d, string k)
        {
            int n;
            d.TryGetValue(k, out n);
            d[k] = n + 1;
        }

        private static readonly Dictionary<string, FieldInfo> _staticFields = new Dictionary<string, FieldInfo>();

        private static string CountStatic(string typeName, string field)
        {
            try
            {
                FieldInfo f;
                if (!_staticFields.TryGetValue(typeName, out f))
                {
                    var t = Type.GetType(typeName);
                    f = t != null ? t.GetField(field, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public) : null;
                    _staticFields[typeName] = f;
                }
                var c = f != null ? f.GetValue(null) as ICollection : null;
                return c != null ? c.Count.ToString() : "?";
            }
            catch { return "?"; }
        }

        private static string PathOf(Transform t, int depth)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < depth; i++, t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        /// <summary>異常のある部品（無ければ表示中の文字の先頭）を最大20個、詳しく出す。</summary>
        private void LogDetail(string reason)
        {
            var sb = new StringBuilder();
            sb.Append($"{Tag}[詳細] {reason}（frame={Time.frameCount}）: {VRMenuFlickerDiagnostics.DescribeInviteTabs()}");
            int n = 0;
            foreach (var e in _entries)
            {
                var g = e.G;
                if (g == null || !g.isActiveAndEnabled) continue;
                int verts = -1;
                bool empty = false;
                if (e.Text != null && !string.IsNullOrEmpty(e.Text.text))
                {
                    var m = e.Text.mesh;
                    verts = m != null ? m.vertexCount : -1;
                    empty = verts <= 0;
                }
                var mat = g.materialForRendering;
                bool noTex = e.Text == null && g.material != null && g.material != g.defaultMaterial && g.material.HasProperty("_MainTex") && g.material.mainTexture == null;
                bool bad = !g.canvasRenderer.cull && (empty || g.depth < 0 || noTex);
                if (!bad && n >= 5) continue;   // 異常が無いときも、比べるために先頭の5個は出す
                sb.Append($"\n  - {(bad ? "【異常】" : "")}{e.Menu}: {PathOf(g.transform, 4)} / {g.GetType().Name} / depth={g.depth} / cull={g.canvasRenderer.cull}" +
                          $"{(e.Text != null ? $" / 文字「{Short(e.Text.text)}」頂点 {verts}" : "")} / マテリアル {(mat != null ? mat.name + " " + StencilKey(mat) : "なし")}" +
                          $"{(e.Text == null && g.mainTexture != null ? $" / テクスチャ {g.mainTexture.width}x{g.mainTexture.height}" : "")}{(noTex ? " / テクスチャ破棄済み" : "")}");
                if (++n >= 20) break;
            }
            sb.Append($"\n  {DescribeStencil()}");
            // v1.14.2: メニュー自身とキャラのマテリアルの描く順番の値（renderQueue）。メニューより後に描かれる部品があるかを見る
            var menuQueues = new HashSet<int>();
            foreach (var e in _entries)
                if (e.G != null && e.G.isActiveAndEnabled && e.G.materialForRendering != null) menuQueues.Add(e.G.materialForRendering.renderQueue);
            var canvasOrders = _roots.Where(kv => kv.Value != null && kv.Value.activeInHierarchy && kv.Value.GetComponent<Canvas>() != null)
                .Select(kv => $"{kv.Key} {kv.Value.GetComponent<Canvas>().sortingOrder}").ToArray();
            sb.Append($"\n  メニューの renderQueue: {string.Join(", ", menuQueues.OrderBy(q => q).Select(q => q.ToString()).ToArray())} / " +
                      $"Canvas の sortingOrder: {string.Join(", ", canvasOrders)} / キャラのマテリアルの renderQueue:");
            try { sb.Append(DescribeCharaQueues(UnityEngine.Object.FindObjectsOfType<ChaControl>())); }
            catch (Exception ex) { sb.Append(" 取得失敗 " + ex.Message); }
            // v1.14.3: 描く順番の方式と、キャラ以外の透過の物・kPlug 以外の Canvas（空中のカードの一覧・レイが隠れる件の手がかり）
            sb.Append($"\n  {DescribeDrawOrderState()}");
            try { sb.Append(DescribeOtherTransparents()); }
            catch (Exception ex) { sb.Append("\n  キャラ以外の透過の物: 取得失敗 " + ex.Message); }
            try { sb.Append(DescribeOtherCanvases()); }
            catch (Exception ex) { sb.Append("\n  kPlug 以外の Canvas: 取得失敗 " + ex.Message); }
            PatcherPlugin.Logger.LogInfo(sb.ToString());
            MaybeLogContext(Time.realtimeSinceStartup, true);
        }

        // ===================== メニューの描く順番（v1.14.2） =====================
        // VR で、招待キャラの透過の服がメニューの奥にあるのにメニューの上に描かれた（他のキャラ・同じキャラの髪や顔は正しい）。
        // uGUI はワールド空間でも深度を書かない（UI/Default は ZWrite Off）。透過の描画は、描く順番の値（renderQueue）・sortingLayer / sortingOrder・距離で並べられ、
        // メニュー（UI/Default と TextMeshPro は renderQueue 3000、sortingOrder 0）より後に描かれる部品は、奥にあっても上に重なる。
        // 透過の範囲では sortingLayer → sortingOrder → renderQueue → 距離 の順に効く（12:40 の VR で、renderQueue 3100 の服にも sortingOrder が効いた）。
        //
        // v1.14.2（MenusLast）: メニューの Canvas の sortingOrder を上げて、メニューを全部の最後に描く。ちらつき・服の重なりは消えたが（12:40）、
        //   手前のレイ（VRTK のポインター）がメニューに隠れ、招待メニューの枠の外のカード（Mask で隠しているだけで、ワールド空間では空中にある）が
        //   ときどき空中に見えた（メニューより前に描かれるようになった何かがステンシルを残している見込み）。
        // v1.14.3（CharactersFirst、既定）: メニューは 0 のまま、キャラの透過の部品の sortingOrder を下げてキャラを先に描く。
        //   メニューとキャラの関係は MenusLast と同じで、キャラ以外の物の順番は kPlugVR の元のまま。
        //   キャラのレンダラーは招待・着替え・アクセサリの変更で増えるので、表示中は2秒ごと（それ以外は5秒ごと、招待・シーン変化のときはすぐ）に付け直す。
        // どちらもゲーム中に切り替えられ、無効にしたり方法を変えたりすると元の値に戻す。

        private const float CharaScanSeconds = 2f, IdleCharaScanSeconds = 5f;
        private const string SilhouetteShader = "Custom/Silhouette";   // 男のシルエット（4900）。元どおりメニューより後に描く

        private readonly Dictionary<int, KeyValuePair<Canvas, int>> _originalOrder = new Dictionary<int, KeyValuePair<Canvas, int>>();
        private readonly Dictionary<int, KeyValuePair<Renderer, int>> _originalCharaOrder = new Dictionary<int, KeyValuePair<Renderer, int>>();
        private bool _drawStateLogged, _lastDrawOnTop;
        private VRMenuFlickerDiagnostics.DrawOrderMethod _lastMethod;
        private int _lastOrder, _lastCharaOrder;
        private float _nextCharaScan;

        private void ApplyDrawOrder()
        {
            bool on = VRMenuFlickerDiagnostics.DrawMenusOnTop.Value;
            var method = VRMenuFlickerDiagnostics.Method.Value;
            int order = VRMenuFlickerDiagnostics.MenuSortingOrder.Value;
            int charaOrder = VRMenuFlickerDiagnostics.CharacterSortingOrder.Value;
            if (!_drawStateLogged || on != _lastDrawOnTop || method != _lastMethod || order != _lastOrder || charaOrder != _lastCharaOrder)
            {
                string how = !on ? "元のまま"
                    : method == VRMenuFlickerDiagnostics.DrawOrderMethod.MenusLast ? $"メニューを最後に（メニューの sortingOrder {order}）"
                    : $"キャラを先に（キャラの透過の部品の sortingOrder {charaOrder}）";
                PatcherPlugin.Logger.LogInfo($"{Tag} メニューの描く順番を{how}にします（frame={Time.frameCount}）。");
                _drawStateLogged = true;
                _lastDrawOnTop = on;
                _lastMethod = method;
                _lastOrder = order;
                _lastCharaOrder = charaOrder;
                _charaScanRequested = true;
            }

            bool menusLast = on && method == VRMenuFlickerDiagnostics.DrawOrderMethod.MenusLast;
            // kPlugVR のメニューがあるとき（H）だけ。それ以外の VR のシーンのキャラの順番は変えない
            bool charasFirst = on && method == VRMenuFlickerDiagnostics.DrawOrderMethod.CharactersFirst && _roots.Count > 0;

            if (menusLast)
            {
                foreach (var kv in _roots)
                {
                    var canvas = kv.Value != null ? kv.Value.GetComponent<Canvas>() : null;
                    if (canvas == null || canvas.sortingOrder == order) continue;
                    int id = canvas.GetInstanceID();
                    if (!_originalOrder.ContainsKey(id)) _originalOrder[id] = new KeyValuePair<Canvas, int>(canvas, canvas.sortingOrder);
                    canvas.sortingOrder = order;
                }
            }
            else if (_originalOrder.Count > 0)
            {
                foreach (var kv in _originalOrder.Values)
                    if (kv.Key != null) kv.Key.sortingOrder = kv.Value;
                _originalOrder.Clear();
            }

            if (charasFirst)
            {
                float now = Time.realtimeSinceStartup;
                if (_charaScanRequested || now >= _nextCharaScan)
                {
                    _charaScanRequested = false;
                    bool visible = _roots.Any(kv => kv.Value != null && kv.Value.activeInHierarchy);
                    _nextCharaScan = now + (visible ? CharaScanSeconds : IdleCharaScanSeconds);
                    LowerCharacters(charaOrder);
                }
            }
            else if (_originalCharaOrder.Count > 0)
            {
                int restored = 0;
                foreach (var kv in _originalCharaOrder.Values)
                    if (kv.Key != null) { kv.Key.sortingOrder = kv.Value; restored++; }
                _originalCharaOrder.Clear();
                PatcherPlugin.Logger.LogInfo($"{Tag} キャラの部品 {restored} 個の描く順番を元に戻しました（frame={Time.frameCount}）。");
            }
        }

        /// <summary>キャラ（ChaControl）の下の、透過の範囲のマテリアルを持つレンダラーの sortingOrder を下げる。新しく下げたものがあればログに出す。</summary>
        private void LowerCharacters(int charaOrder)
        {
            // 破棄されたレンダラーを捨てる
            if (_originalCharaOrder.Count > 0)
            {
                var dead = _originalCharaOrder.Where(kv => kv.Value.Key == null).Select(kv => kv.Key).ToList();
                foreach (var id in dead) _originalCharaOrder.Remove(id);
            }
            int lowered = 0, charas = 0;
            foreach (var cha in UnityEngine.Object.FindObjectsOfType<ChaControl>())
            {
                if (cha == null) continue;
                charas++;
                foreach (var r in cha.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || r.sortingOrder == charaOrder || !NeedsLowering(r)) continue;
                    int id = r.GetInstanceID();
                    if (!_originalCharaOrder.ContainsKey(id)) _originalCharaOrder[id] = new KeyValuePair<Renderer, int>(r, r.sortingOrder);
                    r.sortingOrder = charaOrder;
                    lowered++;
                }
            }
            if (lowered > 0)
                PatcherPlugin.Logger.LogInfo($"{Tag} キャラの透過の部品 {lowered} 個の描く順番を {charaOrder} にしました（下げている部品は計 {_originalCharaOrder.Count} 個・キャラ {charas} 人、frame={Time.frameCount}）。");
        }

        private static bool NeedsLowering(Renderer r)
        {
            bool transparent = false;
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) continue;
                if (m.shader != null && m.shader.name == SilhouetteShader) return false;
                if (m.renderQueue >= 2500) transparent = true;
            }
            return transparent;
        }

        /// <summary>詳細に出す、今の描く順番の方式と、下げているキャラの部品の数。</summary>
        private string DescribeDrawOrderState()
        {
            if (!VRMenuFlickerDiagnostics.DrawMenusOnTop.Value) return "描く順番: 元のまま（DrawMenusOnTop=False）";
            return $"描く順番: {VRMenuFlickerDiagnostics.Method.Value} / メニューの sortingOrder を変えている Canvas {_originalOrder.Count} 個 / " +
                   $"sortingOrder を下げているキャラの部品 {_originalCharaOrder.Count} 個";
        }

        /// <summary>メニューの近くのキャラの、描く順番の値（renderQueue）が透過の範囲（2500 以上）のマテリアル。詳細を出すときに使う。</summary>
        private string DescribeCharaQueues(IEnumerable<ChaControl> charas)
        {
            var sb = new StringBuilder();
            foreach (var cha in charas)
            {
                if (cha == null) continue;
                int opaque = 0, trans = 0, q3000 = 0, over = 0;
                var examples = new List<string>();
                foreach (var r in cha.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || !r.enabled) continue;
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        int q = m.renderQueue;
                        if (q < 2500) { opaque++; continue; }
                        if (q < 3000) trans++;
                        else if (q == 3000) q3000++;
                        else over++;
                        if (q > 3000 && examples.Count < 6)
                            examples.Add($"{r.name}/{m.name}（{(m.shader != null ? m.shader.name : "?")}・{q}）");
                    }
                }
                string name;
                try { name = cha.fileParam.fullname; } catch { name = cha.name; }
                sb.Append($"\n    - 「{name}」: 2500未満 {opaque} / 2500〜2999 {trans} / 3000 {q3000} / 3001以上 {over}" +
                          (examples.Count > 0 ? $"（3001以上の例: {string.Join(", ", examples.ToArray())}）" : ""));
            }
            return sb.ToString();
        }

        private bool IsUnderMenu(Transform t)
        {
            foreach (var kv in _roots)
                if (kv.Value != null && t.IsChildOf(kv.Value.transform)) return true;
            return false;
        }

        /// <summary>キャラ・メニュー以外で、カメラに映っている透過の範囲（renderQueue 2500 以上）のレンダラー（最大20個、renderQueue の大きい順）。</summary>
        private string DescribeOtherTransparents()
        {
            var rows = new List<KeyValuePair<int, string>>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled || !r.isVisible || r.GetComponentInParent<ChaControl>() != null || IsUnderMenu(r.transform)) continue;
                int maxQ = -1;
                string shader = "";
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.renderQueue < 2500 || m.renderQueue <= maxQ) continue;
                    maxQ = m.renderQueue;
                    shader = m.shader != null ? m.shader.name : "?";
                }
                if (maxQ < 0) continue;
                rows.Add(new KeyValuePair<int, string>(maxQ, $"{PathOf(r.transform, 3)}（{r.GetType().Name}・{shader}・{maxQ}・順{r.sortingOrder}）"));
            }
            return $"\n  キャラ以外の透過の物（映っているもの {rows.Count} 個、上位20）: " +
                   (rows.Count == 0 ? "なし" : string.Join(", ", rows.OrderByDescending(kv => kv.Key).Take(20).Select(kv => kv.Value).ToArray()));
        }

        /// <summary>kPlugVR のメニュー以外の、表示中のルートの Canvas（最大15個）。Mask を持つものはステンシルを書く。</summary>
        private string DescribeOtherCanvases()
        {
            var rows = new List<string>();
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (c == null || !c.isActiveAndEnabled || !c.isRootCanvas || IsUnderMenu(c.transform)) continue;
                int masks = c.GetComponentsInChildren<Mask>(false).Count(m => m.isActiveAndEnabled);
                int rectMasks = c.GetComponentsInChildren<RectMask2D>(false).Length;
                rows.Add($"{PathOf(c.transform, 3)}（{c.renderMode}・順{c.sortingOrder}・Mask {masks}・RectMask2D {rectMasks}）");
            }
            return $"\n  kPlug 以外の Canvas（表示中 {rows.Count} 個、上位15）: " + (rows.Count == 0 ? "なし" : string.Join(", ", rows.Take(15).ToArray()));
        }

        // ===================== 描画の状況（v1.14.1） =====================
        // 診断のログでは、ちらつきが見えていた時間も CPU 側の状態（文字のメッシュ・Canvas の描画・ボタンの重なり・
        // ステンシルの設定）に異常が無かった。そこで、GPU 側で隠れる理由になりうるものを、メニューの表示中に30秒ごとと詳細の出力のときに出す。
        //   - カメラからメニューまでの間に、境界ボックスがかかっているレンダラー（キャラの体・髪など。深度で隠れる候補）
        //   - メニューの中心を境界ボックスに含むレンダラー
        //   - カメラ（VR の目のカメラ）の設定と、描画先の深度バッファのビット数（ステンシルは 24 以上で使える）
        //   - メニューの Canvas の並び順（sortingOrder）とカメラからの距離

        private float _lastContext = -100f;
        private const float ContextSeconds = 30f;

        private void MaybeLogContext(float now, bool force)
        {
            if (!force && now - _lastContext < ContextSeconds) return;
            _lastContext = now;
            try { PatcherPlugin.Logger.LogInfo($"{Tag}[描画の状況] {DescribeRenderContext()}"); }
            catch (Exception ex) { PatcherPlugin.Logger.LogWarning($"{Tag} 描画の状況の記録に失敗: {ex.Message}"); }
        }

        private static Camera FindEyeCamera()
        {
            var main = Camera.main;
            if (main != null && main.isActiveAndEnabled) return main;
            return Camera.allCameras.FirstOrDefault(c => c.stereoEnabled) ?? Camera.allCameras.OrderByDescending(c => c.depth).FirstOrDefault();
        }

        private string DescribeRenderContext()
        {
            var sb = new StringBuilder();
            var cam = FindEyeCamera();
            if (cam == null) return "カメラが見つかりません";
            var rt = cam.targetTexture;
            sb.Append($"カメラ {PathOf(cam.transform, 3)} / stereo={cam.stereoEnabled}（{cam.stereoTargetEye}）/ 描画={cam.actualRenderingPath} / " +
                      $"出力={(rt != null ? $"RT {rt.width}x{rt.height} 深度{rt.depth}bit" : "画面")} / 深度テクスチャ={cam.depthTextureMode} / MSAA={cam.allowMSAA}・{QualitySettings.antiAliasing}");

            Renderer[] renderers = null;
            foreach (var kv in _roots)
            {
                var go = kv.Value;
                if (go == null || !go.activeInHierarchy) continue;
                var canvas = go.GetComponent<Canvas>();
                var rect = go.GetComponent<RectTransform>();
                Vector3 center = rect != null ? rect.TransformPoint(rect.rect.center) : go.transform.position;
                Vector3 from = cam.transform.position;
                float dist = Vector3.Distance(from, center);
                sb.Append($" | {kv.Key}: 距離 {dist:F2}m / Canvas {(canvas != null ? $"{canvas.renderMode} 順{canvas.sortingOrder} 層{canvas.sortingLayerName}" : "なし")}");

                if (renderers == null) renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
                var ray = new Ray(from, (center - from).normalized);
                var between = new Dictionary<string, int>();
                var containing = new Dictionary<string, int>();
                foreach (var r in renderers)
                {
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r is ParticleSystemRenderer) continue;
                    if (r.transform.IsChildOf(go.transform)) continue;   // メニュー自身
                    var b = r.bounds;
                    float d;
                    if (b.Contains(center)) Inc(containing, OwnerOf(r));
                    else if (b.IntersectRay(ray, out d) && d < dist) Inc(between, OwnerOf(r));
                }
                sb.Append($" / 間にかかるもの: {Top(between)} / 中心を含むもの: {Top(containing)}");
            }
            return sb.ToString();
        }

        private static string OwnerOf(Renderer r)
        {
            var cha = r.GetComponentInParent<ChaControl>();
            if (cha != null)
            {
                try { return "キャラ「" + cha.fileParam.fullname + "」"; }
                catch { return "キャラ " + cha.name; }
            }
            return PathOf(r.transform, 2);
        }

        private static string Top(Dictionary<string, int> d)
        {
            if (d.Count == 0) return "なし";
            return string.Join(", ", d.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} ×{kv.Value}").ToArray()) +
                   (d.Count > 6 ? $" ほか{d.Count - 6}" : "");
        }

        private static string Short(string s)
        {
            if (s == null) return "";
            s = s.Replace("\n", " ");
            return s.Length > 16 ? s.Substring(0, 16) + "…" : s;
        }
    }
}
