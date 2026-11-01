using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// kPlugVR のメニューが、タイトル画面の UI のキャンバスの範囲（レイが緑になる範囲）に重なるとクリックできない問題の回避策（v1.21.0、VR のみ）。
    /// ksPlugPatcher_NRE 0.9.1 の FixRayForKsPlugMenus（VRMenuRaycastPriority）を kPlugVR 向けに移したもの。
    ///
    /// 【原因（KK・KKS の VRTK は同じ処理）】
    ///   VRTK の <c>VRTK_VRInputModule.CheckRaycasts</c> は、レイの元と向きを <c>pointerEventData.pointerCurrentRaycast</c> の worldPosition / worldNormal に入れて
    ///   <c>EventSystem.RaycastAll</c> を呼び、各キャンバスの <c>VRTK_UIGraphicRaycaster.Raycast</c> はそこからレイを作る。
    ///   ところが Raycast は、当たりがあると <c>SetNearestRaycast</c> で pointerCurrentRaycast をその当たり（worldPosition = 当たった点、worldNormal = 0）で上書きする。
    ///   そのため、先に調べたキャンバスに当たると、後に調べるキャンバスは「当たった点から向き 0」のレイになり、必ず外れる。
    ///   kPlugVR のメニューは表示のたびに <c>menuGo.SetActive(true)</c> され、VRTK_UICanvas.OnEnable が VRTK_UIGraphicRaycaster を付け直すので、
    ///   調べる順番は常にタイトルのキャンバス（範囲全体に透明な板 VRTK_UICANVAS_DRAGGABLE_PANEL がある）より後になる。
    ///
    /// 【処理】
    ///   1. CheckRaycasts の中で kPlugVR のメニューのキャンバスを調べるときだけ、pointerCurrentRaycast を元のレイ（最初のキャンバスが受け取った元と向き）に戻す。
    ///      ゲームのキャンバスどうしの調べ方は元のまま。
    ///   2. （v1.21.1）結果の先頭が kPlugVR のメニュー以外で、kPlugVR のメニューの要素の方がレイの元から近いときだけ、kPlugVR の要素を（順番を保って）先頭に移す。
    ///      EventSystem は結果を sortingOrder の大きい順に並べてから距離で並べる。KK のタイトルのキャンバスは sortingOrder 50、kPlugVR のメニューは 0 なので、
    ///      1 だけでは奥のタイトルの UI が先頭のまま残る（KKS はどちらも 0 で、1 だけで直る）。
    ///   3. （v1.21.2）2 の「手前か」を比べるとき、他のキャンバスの透明な板（VRTK_UICANVAS_DRAGGABLE_PANEL）は数えない。
    ///      KK のタイトルのキャンバスは大きく斜めに置かれていて、中央付近では kPlugVR のメニューがその面より奥になる。
    ///      見た目は kPlugVR のメニューが前に描かれるが、手前には見えない板しかない。ゲーム本体（VRViveController）も名前が "VRTK_" の要素は UI に当たっていない扱いにしている。
    ///   4. （v1.21.2）2 で kPlugVR のメニューを先頭にしたとき、UI のレイのビーム（VRTK_StraightPointerRenderer）をメニューの当たった点まで伸ばす。
    ///      ビームの長さは物理レイキャストで決まり、タイトルのキャンバスの BoxCollider（VRTK_UICanvas が付ける、範囲全体）で止まって見えるため。
    ///   1・3・4 は FixRayForKPlugMenus でまとめて切り替える（v1.21.3。v1.21.2 の IgnoreOtherDraggablePanels・ExtendBeamToKPlugMenu はこれに統合した）。
    ///   2 は PreferKPlugMenu で切り替える（KK では 2 がないと直らないため残している）。
    ///   kPlugVR のメニューのキャンバスは <c>VRUI.CreateVRmenu</c> の戻り値（MenuFrame.menuGo）で覚える。
    /// </summary>
    internal static class VRMenuRayFix
    {
        private const string Tag = "[VRMenuRay]";
        private const int MaxLogsPerScene = 80;

        internal enum PreferMode { Off, WhenNearer, Always }

        internal static ConfigEntry<bool> FixRay;
        internal static ConfigEntry<PreferMode> Prefer;
        internal static ConfigEntry<bool> LogHits;

        private const string DraggablePanelName = "VRTK_UICANVAS_DRAGGABLE_PANEL";

        private static bool _applied;
        private static bool _gaveUp;
        private static FieldInfo _menuGo;
        private static MethodInfo _getOrigin;
        /// <summary>kPlugVR が作ったメニューのキャンバスの InstanceID。</summary>
        private static readonly HashSet<int> _menuCanvasIds = new HashSet<int>();
        /// <summary>要素のいちばん近いキャンバスの InstanceID → kPlugVR のメニューの中か。</summary>
        private static readonly Dictionary<int, bool> _isMenuCache = new Dictionary<int, bool>();
        private static readonly HashSet<string> _loggedOnce = new HashSet<string>();

        /// <summary>まだ適用を試す必要があるか（PatcherPlugin.Update の早い適用の条件）。</summary>
        internal static bool Pending => !_applied && !_gaveUp;

        internal static void BindConfig(ConfigFile config)
        {
            FixRay = config.Bind(
                "VRMenuRaycast", "FixRayForKPlugMenus", true,
                "VR のみ（v1.21.0〜v1.21.3）。タイトル画面で、kPlugVR のメニューがタイトルの UI の範囲（レイが緑になる範囲）に重なるとクリックできない問題の回避策。次の3つをまとめて切り替えます。" +
                "(1) VRTK はキャンバスを1つずつ調べるとき、当たったキャンバスがレイを書き換えるため、後に調べるキャンバスは必ず外れます（kPlugVR のメニューは表示のたびに調べる順番が最後になる）。" +
                "kPlugVR のメニューを調べるときだけ元のレイに戻します（ゲームのキャンバスどうしの調べ方は変えません）。" +
                "(2) PreferKPlugMenu = WhenNearer で手前か比べるとき、他のキャンバスの透明な板（VRTK_UICANVAS_DRAGGABLE_PANEL）を数えません。KK のタイトルの UI は大きく斜めに置かれていて、中央付近では kPlugVR のメニューがその面より奥になるためです。" +
                "(3) kPlugVR のメニューを優先したとき、手から出る UI のレイ（ビーム）をメニューまで伸ばします（見た目だけ）。ゲーム中に切り替えられます。");
            Prefer = config.Bind(
                "VRMenuRaycast", "PreferKPlugMenu", PreferMode.WhenNearer,
                "VR のみ（v1.21.1）。レイが kPlugVR のメニューに当たっているのに、他のキャンバスが先に選ばれるときに、kPlugVR のメニューを優先するかどうか。" +
                "KK のタイトルの UI は sortingOrder 50（kPlugVR のメニューは 0）で、uGUI は距離より先に sortingOrder で選ぶため、kPlugVR のメニューが手前にあってもタイトルの UI が選ばれます。" +
                "Off: 何もしない（ゲームの元の順番）。WhenNearer: kPlugVR のメニューの方が手前にあるときだけ優先する。Always: kPlugVR のメニューに当たっていれば常に優先する。" +
                "kPlugVR のメニューに当たっていないときは何もしません。FixRayForKPlugMenus と一緒に使います（KK のタイトル画面ではどちらも要ります）。ゲーム中に切り替えられます。");
            LogHits = config.Bind(
                "VRMenuRaycast", "LogRaycastHits", false,
                "VR のみ（v1.21.0）。確かめる用。レイの当たり先の先頭が変わったときに、上位4つ（キャンバス・sortingLayer・sortingOrder・距離・kPlugVR のメニューか）と、" +
                "元のレイに戻した数をログに出すかどうか。シーンごとに80行まで。ゲーム中に切り替えられます。");
        }

        /// <summary>
        /// 型が見つかるまで繰り返し呼んでよい（PatcherPlugin.Update の早い適用から呼ぶ）。
        /// タイトルのメニューが作られる前に当てる必要があるため、300 フレーム待ちには入れない。
        /// </summary>
        internal static void TryApply(Harmony harmony)
        {
            if (_applied || _gaveUp) return;
            if (!Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase)) { _gaveUp = true; return; }
            try
            {
                Type vrui = AccessTools.TypeByName("kPlugVR.Menu.VRUI");
                if (vrui == null) return; // kPlugVR がまだ読まれていない。次の試行で探す
                Type frame = AccessTools.TypeByName("kPlugVR.Menu.VRUI+MenuFrame");
                MethodInfo create = AccessTools.Method(vrui, "CreateVRmenu");
                _menuGo = frame != null ? AccessTools.Field(frame, "menuGo") : null;
                Type module = AccessTools.TypeByName("VRTK.VRTK_VRInputModule");
                MethodInfo check = module != null ? AccessTools.Method(module, "CheckRaycasts") : null;
                Type raycaster = AccessTools.TypeByName("VRTK.VRTK_UIGraphicRaycaster");
                MethodInfo raycast = raycaster != null ? AccessTools.Method(raycaster, "Raycast", new[] { typeof(PointerEventData), typeof(List<RaycastResult>) }) : null;
                _gaveUp = true; // 以降は成功・失敗にかかわらず試さない
                if (create == null || create.ReturnType != frame || _menuGo == null || check == null || check.ReturnType != typeof(List<RaycastResult>) || raycast == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} kPlugVR.Menu.VRUI.CreateVRmenu（{create != null}）/ MenuFrame.menuGo（{_menuGo != null}）/ " +
                        $"VRTK_VRInputModule.CheckRaycasts（{check != null}）/ VRTK_UIGraphicRaycaster.Raycast（{raycast != null}）が想定の形でないため、kPlugVR のメニューのレイは直しません。");
                    return;
                }
                harmony.Patch(create, postfix: new HarmonyMethod(AccessTools.Method(typeof(VRMenuRayFix), nameof(CreateVRmenuPostfix))));
                harmony.Patch(check,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(VRMenuRayFix), nameof(CheckRaycastsPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(VRMenuRayFix), nameof(CheckRaycastsPostfix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(VRMenuRayFix), nameof(CheckRaycastsFinalizer))));
                harmony.Patch(raycast, prefix: new HarmonyMethod(AccessTools.Method(typeof(VRMenuRayFix), nameof(RaycastPrefix))));
                _applied = true;

                // ビームを伸ばす処理（v1.21.2）は見た目だけなので、見つからなくても他は当てたままにする
                Type straight = AccessTools.TypeByName("VRTK.VRTK_StraightPointerRenderer");
                MethodInfo cast = straight != null ? AccessTools.Method(straight, "CastRayForward") : null;
                Type baseRenderer = AccessTools.TypeByName("VRTK.VRTK_BasePointerRenderer");
                _getOrigin = baseRenderer != null ? AccessTools.Method(baseRenderer, "GetOrigin", new[] { typeof(bool) }) : null;
                bool beam = cast != null && cast.ReturnType == typeof(float) && _getOrigin != null && _getOrigin.ReturnType == typeof(Transform);
                if (beam) harmony.Patch(cast, postfix: new HarmonyMethod(AccessTools.Method(typeof(VRMenuRayFix), nameof(CastRayForwardPostfix))));
                else PatcherPlugin.Logger.LogWarning($"{Tag} VRTK_StraightPointerRenderer.CastRayForward（{cast != null}）/ GetOrigin（{_getOrigin != null}）が想定の形でないため、ビームは伸ばしません（クリックの直しは当てます）。");

                PatcherPlugin.Logger.LogInfo($"{Tag} VRTK_VRInputModule.CheckRaycasts・VRTK_UIGraphicRaycaster.Raycast と kPlugVR.Menu.VRUI.CreateVRmenu に適用しました" +
                    $"（元のレイに戻す・透明な板を数えない・ビームを伸ばす={FixRay.Value}{(beam ? "" : "（ビームは適用なし）")} / " +
                    $"kPlugVR のメニューを優先={Prefer.Value} / 当たり先のログ={LogHits.Value}。ゲーム中に切り替えられます）。");
            }
            catch (Exception ex)
            {
                _gaveUp = true;
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗しました: {ex.Message}");
            }
        }

        // ===================== kPlugVR のメニューのキャンバスを覚える =====================

        private static void CreateVRmenuPostfix(object __result)
        {
            try
            {
                var go = __result != null ? _menuGo.GetValue(__result) as GameObject : null;
                var canvas = go != null ? go.GetComponent<Canvas>() : null;
                if (canvas == null) return;
                _menuCanvasIds.Add(canvas.GetInstanceID());
                _isMenuCache.Clear(); // 新しいメニューの中の入れ子のキャンバスを判定し直す
            }
            catch { }
        }

        private static bool IsKPlugMenu(GameObject go)
        {
            if (go == null) return false;
            Canvas nearest = go.GetComponentInParent<Canvas>();
            if (nearest == null) return false;
            int id = nearest.GetInstanceID();
            if (_isMenuCache.TryGetValue(id, out bool v)) return v;
            v = false;
            for (Transform t = nearest.transform; t != null; t = t.parent)
            {
                Canvas c = t.GetComponent<Canvas>();
                if (c != null && _menuCanvasIds.Contains(c.GetInstanceID())) { v = true; break; }
            }
            if (_isMenuCache.Count > 4096) _isMenuCache.Clear();
            _isMenuCache[id] = v;
            return v;
        }

        // ===================== 元のレイに戻す =====================

        /// <summary>今の CheckRaycasts の pointerEventData と、最初のキャンバスが受け取った元のレイ。</summary>
        private static PointerEventData _checking;
        private static bool _captured;
        private static Vector3 _origin, _direction;
        private static int _restoredThisCall, _restoredTotal;

        private static void CheckRaycastsPrefix()
        {
            _checking = null;
            _captured = false;
            _restoredThisCall = 0;
        }

        private static Exception CheckRaycastsFinalizer(Exception __exception)
        {
            _checking = null;
            _captured = false;
            return __exception;
        }

        private static void RaycastPrefix(Component __instance, PointerEventData eventData)
        {
            try
            {
                if (eventData == null || !FixRay.Value) return;
                if (!_captured)
                {
                    // CheckRaycasts の中の最初のキャンバス: まだ上書きされていない元のレイを覚える
                    _checking = eventData;
                    _origin = eventData.pointerCurrentRaycast.worldPosition;
                    _direction = eventData.pointerCurrentRaycast.worldNormal;
                    _captured = true;
                    return;
                }
                if (!ReferenceEquals(eventData, _checking) || __instance == null || !IsKPlugMenu(__instance.gameObject)) return;
                RaycastResult r = eventData.pointerCurrentRaycast;
                if (r.worldPosition == _origin && r.worldNormal == _direction) return; // 上書きされていない
                eventData.pointerCurrentRaycast = new RaycastResult { worldPosition = _origin, worldNormal = _direction };
                _restoredThisCall++;
                _restoredTotal++;
            }
            catch (Exception ex)
            {
                if (_loggedOnce.Add("restore:" + ex.GetType().Name))
                    PatcherPlugin.Logger.LogWarning($"{Tag} レイを元に戻す処理で例外が起きました（戻さずに続けます。同じ種類は以降出しません）: {ex}");
            }
        }

        // ===================== kPlugVR のメニューを先頭に（v1.21.1）・確かめる用のログ =====================

        private static readonly List<RaycastResult> _menu = new List<RaycastResult>();
        private static readonly List<RaycastResult> _others = new List<RaycastResult>();
        private static int _moved;
        private static string _lastKey;
        private static string _logScene;
        private static int _logCount;

        private static void CheckRaycastsPostfix(List<RaycastResult> __result)
        {
            try
            {
                if (__result == null) return;
                bool log = LogHits.Value;
                PreferMode mode = Prefer.Value;
                if (!log && mode == PreferMode.Off) return;
                string before = log ? Describe(__result) : null;

                bool moved = false;
                if (mode != PreferMode.Off && __result.Count > 1 && !IsKPlugMenu(__result[0].gameObject))
                {
                    int first = -1;
                    for (int i = 1; i < __result.Count; i++)
                    {
                        if (IsKPlugMenu(__result[i].gameObject)) { first = i; break; }
                    }
                    if (first > 0 && (mode == PreferMode.Always || !BlockedByNearer(__result, first)))
                    {
                        _menu.Clear();
                        _others.Clear();
                        for (int i = 0; i < __result.Count; i++)
                        {
                            if (IsKPlugMenu(__result[i].gameObject)) _menu.Add(__result[i]);
                            else _others.Add(__result[i]);
                        }
                        __result.Clear();
                        __result.AddRange(_menu);
                        __result.AddRange(_others);
                        moved = true;
                        _moved++;
                        RememberBeamTarget(__result[0].worldPosition);
                    }
                }
                if (log) LogIfChanged(before, __result, moved);
            }
            catch (Exception ex)
            {
                if (_loggedOnce.Add("post:" + ex.GetType().Name))
                    PatcherPlugin.Logger.LogWarning($"{Tag} 当たり先の並べ替え・ログで例外が起きました（並べ替えずに続けます。同じ種類は以降出しません）: {ex}");
            }
        }

        /// <summary>
        /// kPlugVR のメニューの最初の要素（first）より前に、それより手前にある他のキャンバスの要素があるか。
        /// FixRayForKPlugMenus が true なら、他のキャンバスの透明な板は数えない（v1.21.2）。
        /// 距離はどちらも元のレイの元から測った値（kPlugVR のメニューは元に戻したレイで測っている）。
        /// </summary>
        private static bool BlockedByNearer(List<RaycastResult> results, int first)
        {
            float menuDistance = results[first].distance;
            bool ignorePanels = FixRay.Value;
            for (int i = 0; i < first; i++)
            {
                RaycastResult r = results[i];
                if (r.distance > menuDistance + 1e-4f) continue;
                if (ignorePanels && r.gameObject != null && r.gameObject.name == DraggablePanelName) continue;
                return true;
            }
            return false;
        }

        // ===================== ビームを kPlugVR のメニューまで伸ばす（v1.21.2） =====================

        /// <summary>kPlugVR のメニューを先頭にしたときの当たった点と、そのフレーム。両手の分があり得るので少しだけ持つ。</summary>
        private static readonly List<KeyValuePair<Vector3, int>> _beamTargets = new List<KeyValuePair<Vector3, int>>();
        private const int BeamFrames = 2;
        /// <summary>ビームの線から当たった点までの許す距離（m）。UI のレイとビームは同じコントローラーから出るが、原点の取り方が少し違うことがある。</summary>
        private const float BeamTolerance = 0.05f;
        private static int _extended;
        private static int _lastBeamFrame = -100;

        private static void RememberBeamTarget(Vector3 point)
        {
            int now = Time.frameCount;
            _beamTargets.RemoveAll(t => now - t.Value > BeamFrames);
            if (_beamTargets.Count < 8) _beamTargets.Add(new KeyValuePair<Vector3, int>(point, now));
            _lastBeamFrame = now;
        }

        private static void CastRayForwardPostfix(Component __instance, ref float __result)
        {
            try
            {
                int now = Time.frameCount;
                if (now - _lastBeamFrame > BeamFrames || !FixRay.Value || Prefer.Value == PreferMode.Off) return;
                Transform origin = _getOrigin.Invoke(__instance, new object[] { true }) as Transform;
                if (origin == null) return;
                float best = -1f, nearestMiss = float.MaxValue;
                for (int i = 0; i < _beamTargets.Count; i++)
                {
                    if (now - _beamTargets[i].Value > BeamFrames) continue;
                    Vector3 v = _beamTargets[i].Key - origin.position;
                    float along = Vector3.Dot(v, origin.forward);
                    if (along <= 0f) continue;
                    float off = (v - origin.forward * along).magnitude;
                    if (off > BeamTolerance) { nearestMiss = Math.Min(nearestMiss, off); continue; }
                    if (along > best) best = along;
                }
                if (best > __result + 1e-3f)
                {
                    __result = best;
                    if (_extended++ == 0)
                        PatcherPlugin.Logger.LogInfo($"{Tag} UI のレイのビームを kPlugVR のメニューまで伸ばしました（初回。以降は出しません）。");
                }
                else if (best < 0f && nearestMiss < float.MaxValue && LogHits.Value && _loggedOnce.Add("beam-miss"))
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} ビームの線と kPlugVR のメニューの当たった点が {nearestMiss:F3} m ずれていたため、ビームは伸ばしませんでした（初回だけ出します）。");
                }
            }
            catch (Exception ex)
            {
                if (_loggedOnce.Add("beam:" + ex.GetType().Name))
                    PatcherPlugin.Logger.LogWarning($"{Tag} ビームを伸ばす処理で例外が起きました（伸ばさずに続けます。同じ種類は以降出しません）: {ex}");
            }
        }

        /// <summary>当たり先の先頭が変わったときだけ、並べ替える前の上位4つを出す。シーンごとに上限まで。</summary>
        private static void LogIfChanged(string before, List<RaycastResult> after, bool moved)
        {
            string key = (after.Count > 0 && after[0].gameObject != null ? after[0].gameObject.GetInstanceID().ToString() : "-") + (moved ? "m" : "");
            if (key == _lastKey) return;
            _lastKey = key;
            if (after.Count == 0) return; // 何にも当たらなくなったときは出さない（次に当たったときに出る）

            string scene = SceneManager.GetActiveScene().name;
            if (scene != _logScene) { _logScene = scene; _logCount = 0; }
            if (_logCount >= MaxLogsPerScene) return;
            _logCount++;
            PatcherPlugin.Logger.LogInfo($"{Tag} レイの当たり先（シーン={scene}、{_logCount}/{MaxLogsPerScene}）: {before}" +
                (_restoredThisCall > 0 ? $" ／ kPlugVR のメニューのレイを元に戻しました（{_restoredThisCall}個、累計 {_restoredTotal}回）" : "") +
                (moved ? $" → kPlugVR のメニューを先頭に移しました（累計 {_moved}回）" : ""));
        }

        private static string Describe(List<RaycastResult> results)
        {
            if (results.Count == 0) return "なし";
            var sb = new StringBuilder();
            int n = Math.Min(4, results.Count);
            for (int i = 0; i < n; i++)
            {
                RaycastResult r = results[i];
                if (i > 0) sb.Append(" ／ ");
                GameObject go = r.gameObject;
                Canvas c = go != null ? go.GetComponentInParent<Canvas>() : null;
                Canvas root = c != null ? c.rootCanvas : null;
                sb.Append($"{i + 1}. {(go != null ? go.name : "?")}（キャンバス {(root != null ? root.name : "?")}" +
                    $"・layer {SortingLayer.IDToName(r.sortingLayer)}・order {r.sortingOrder}・距離 {r.distance:F3}" +
                    $"{(IsKPlugMenu(go) ? "・kPlugVR のメニュー" : "")}）");
            }
            if (results.Count > n) sb.Append($" ほか {results.Count - n}個");
            return sb.ToString();
        }
    }
}
