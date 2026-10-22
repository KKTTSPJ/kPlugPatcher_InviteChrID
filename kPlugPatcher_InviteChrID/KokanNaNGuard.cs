using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// kPlug / kPlugVR の KokanBehavior が、挿入中に女の子の cf_j_kokan の大きさ（localScale）を壊す問題の回避策（v1.15.0）。
    ///
    /// 【現象（VR のログ）】
    ///   メインのキャラの <c>…/cf_d_kokan/cf_j_kokan/Kokan_root/Kokan</c>（kPlugVR の ChaTools.AddColliders が付ける球のコライダー）で、
    ///   Unity の警告「Infinity or NaN floating point numbers appear when calculating the transform matrix for a Collider」が
    ///   挿入中（後背位 khs_f_81 の WLoop〜IN_A）に毎フレーム出る（1回のプレイで 29260回、ログの大半）。
    ///
    /// 【仕組み（見込み。どの値で壊れるかは未確認 → このガードのログで確かめる）】
    ///   KokanBehavior は毎フレーム cf_j_kokan の大きさを待機の値に戻し、挿入中は CalculateOverlap で、男の腰の1フレームの移動量（pistonMag）を
    ///   Time.deltaTime で割った値から押しつぶしの量を求めて足す。そのため deltaTime が 0 のときは無限大・NaN に、1フレームの移動量が大きいとき
    ///   （VR で頭を大きく動かすなど）は大きさが 0 や負になる。その値が cf_j_kokan の localScale に入り、子のコライダーの大きさも壊れて、物理の計算で上の警告になる。
    ///   OverlapDeformer の ApplyOverlap（唇の骨の位置）も同じ量を使う。非VR の kPlug も同じ。
    ///
    /// 【回避策】
    ///   1. CalculateOverlap / ApplyOverlap: deltaTime が 0 以下、pistonMag が NaN・無限大、または1フレームの移動量が MaxPistonStep を超えるときは、
    ///      その呼び出しを飛ばす（押しつぶしを足さない。次のフレームの通常の移動量では原型どおり）。
    ///   2. LateUpdate の後: cf_j_kokan の位置・大きさ・回転に NaN・無限大があるか、大きさが MinScale〜MaxScale の外なら、直す
    ///      （NaN は待機の値、範囲外は範囲に切り詰める）。KokanBehavior の S_kokan / P_kokan も同じ値にする。
    ///   どちらも、最初の1回は値を詳しく、その後は30秒ごとに回数をまとめてログに出す。親の骨（cf_d_kokan）やコライダー自体の異常は記録だけする。
    /// 動作中のモード（VR は kPlugVR、通常は kPlug）の型だけに当てる（通常モードで kPlugVR の型に触れないため）。
    /// </summary>
    internal static class KokanNaNGuard
    {
        private const string Tag = "[KokanNaNGuard]";

        /// <summary>1フレームの男の腰の移動量の上限 [m]。普通のピストンは1フレーム 数cm。これを超えるのは頭の移動・位置の付け替えなど。</summary>
        private const float MaxPistonStep = 0.15f;
        /// <summary>cf_j_kokan の大きさの範囲。kPlug の値は待機 1 前後、挿入 (1.32, 0.95, 1.38)。</summary>
        private const float MinScale = 0.05f, MaxScale = 5f;
        private const float SummarySeconds = 30f;

        private static bool _applied;

        internal static void TryApply(Harmony harmony)
        {
            if (_applied) return;
            _applied = true;
            string root = BepInEx.Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase) ? "kPlugVR." : "kPlug.";
            Type kb = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.FullName.Contains("kPlug")) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                kb = kb ?? types.FirstOrDefault(t => t != null && t.FullName == root + "CmpChara.KokanBehavior");
            }
            if (kb == null)
            {
                PatcherPlugin.Logger.LogInfo($"{Tag} {root}CmpChara.KokanBehavior が見つからないため、適用しません。");
                return;
            }

            try
            {
                var names = new List<string>();
                var overlapPrefix = new HarmonyMethod(typeof(KokanNaNGuard).GetMethod(nameof(OverlapPrefix), BindingFlags.NonPublic | BindingFlags.Static));
                foreach (var name in new[] { "CalculateOverlap", "ApplyOverlap" })
                {
                    var m = AccessTools.DeclaredMethod(kb, name);
                    if (m == null) continue;
                    harmony.Patch(m, prefix: overlapPrefix);
                    names.Add(name);
                }
                var late = AccessTools.DeclaredMethod(kb, "LateUpdate");
                if (late != null && AccessTools.Field(kb, "kokan") != null && AccessTools.Field(kb, "S_kokan") != null)
                {
                    harmony.Patch(late, postfix: new HarmonyMethod(typeof(KokanNaNGuard).GetMethod(nameof(LateUpdatePostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                    names.Add("LateUpdate");
                }
                if (names.Count == 0)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {kb.FullName} に想定のメソッドが無いため、適用しません。");
                    return;
                }
                PatcherPlugin.Logger.LogInfo($"{Tag} {kb.FullName} の {string.Join(" / ", names.ToArray())} に、cf_j_kokan の大きさが壊れないようにするガードを適用しました" +
                                             $"（1フレームの移動量の上限 {MaxPistonStep}m・大きさの範囲 {MinScale}〜{MaxScale}）。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗: {ex.Message}");
            }
        }

        // ===================== 1. CalculateOverlap / ApplyOverlap =====================

        private static int _skipDt, _skipNonFinite, _skipBigStep;
        private static float _maxStep;
        private static bool _firstSkipLogged;

        /// <summary>押しつぶしの計算を飛ばすかどうか。false を返すと原型を実行しない（値を変えない）。</summary>
        private static bool OverlapPrefix(float ___pistonMag)
        {
            try
            {
                if (PatcherPlugin.IsQuitting || !PatcherPlugin.EnableKokanNaNGuard.Value) return true;
                float dt = Time.deltaTime;
                string reason = null;
                if (dt <= 0f) { _skipDt++; reason = $"Time.deltaTime が {dt}"; }
                else if (!IsFinite(___pistonMag)) { _skipNonFinite++; reason = $"移動量 pistonMag が {___pistonMag}"; }
                else if (Mathf.Abs(___pistonMag) > MaxPistonStep)
                {
                    _skipBigStep++;
                    _maxStep = Mathf.Max(_maxStep, Mathf.Abs(___pistonMag));
                    reason = $"1フレームの移動量 pistonMag が {___pistonMag:F3}m（上限 {MaxPistonStep}m）";
                }
                if (reason == null) return true;
                if (!_firstSkipLogged)
                {
                    _firstSkipLogged = true;
                    PatcherPlugin.Logger.LogWarning($"{Tag} kPlug の押しつぶしの計算（CalculateOverlap / ApplyOverlap）を飛ばしました: {reason}・deltaTime {dt:F4}s" +
                                                    $"（原型では cf_j_kokan の大きさ・唇の骨の位置が無限大・NaN・0・負になる。以後は {SummarySeconds:F0}秒ごとにまとめて出します）。");
                }
                MaybeSummary();
                return false;
            }
            catch (Exception ex)
            {
                SafeWarn("Overlap", ex);
                return true;
            }
        }

        // ===================== 2. LateUpdate の後: cf_j_kokan の値の点検 =====================

        private static int _repairs;
        private static bool _firstRepairLogged;
        private static readonly HashSet<string> _loggedOther = new HashSet<string>();

        private static void LateUpdatePostfix(MonoBehaviour __instance, Transform ___kokan, ref Vector3 ___S_kokan, ref Vector3 ___P_kokan,
                                              Vector3 ___idl_S_kokan, Vector3 ___idl_P_kokan, float ___pistonMag)
        {
            try
            {
                if (PatcherPlugin.IsQuitting || ___kokan == null || !PatcherPlugin.EnableKokanNaNGuard.Value) return;
                var t = ___kokan;
                Vector3 pos = t.localPosition, scl = t.localScale;
                Quaternion rot = t.localRotation;
                bool posBad = !IsFinite(pos);
                bool rotBad = !IsFinite(rot);
                bool sclBad = !IsFinite(scl) || OutOfRange(scl);
                if (posBad || rotBad || sclBad)
                {
                    Vector3 newPos = posBad ? (IsFinite(___idl_P_kokan) ? ___idl_P_kokan : Vector3.zero) : pos;
                    Vector3 newScl = FixScale(scl, ___idl_S_kokan);
                    if (!_firstRepairLogged)
                    {
                        _firstRepairLogged = true;
                        PatcherPlugin.Logger.LogWarning($"{Tag} {CharaName(__instance)} の cf_j_kokan の値が壊れていたので直しました: " +
                                                        $"位置 {V(pos)} → {V(newPos)} / 大きさ {V(scl)} → {V(newScl)}{(rotBad ? " / 回転が NaN → 単位回転" : "")} | " +
                                                        $"S_kokan {V(___S_kokan)} / P_kokan {V(___P_kokan)} / 待機の大きさ {V(___idl_S_kokan)} / pistonMag {___pistonMag:F4} / deltaTime {Time.deltaTime:F4} / " +
                                                        $"{HState()}（以後は {SummarySeconds:F0}秒ごとにまとめて出します）。");
                    }
                    t.localPosition = newPos;
                    t.localScale = newScl;
                    if (rotBad) t.localRotation = Quaternion.identity;
                    ___S_kokan = newScl;
                    ___P_kokan = newPos;
                    _repairs++;
                }
                CheckOthers(__instance, t);
                MaybeSummary();
            }
            catch (Exception ex)
            {
                SafeWarn("LateUpdate", ex);
            }
        }

        /// <summary>親の骨（cf_d_kokan）と、kPlugVR のコライダー（Kokan_root/Kokan）の異常を記録する（直さない。種類ごとに1回）。</summary>
        private static void CheckOthers(MonoBehaviour kb, Transform kokan)
        {
            var parent = kokan.parent;
            if (parent != null && (!IsFinite(parent.position) || !IsFinite(parent.lossyScale)))
                LogOther(kb, "親", $"{parent.name} の位置 {V(parent.position)}・大きさ {V(parent.lossyScale)} に NaN・無限大（cf_j_kokan より上の骨。kPlug の KokanBehavior 以外が原因）");
            var colRoot = kokan.Find("Kokan_root");
            var col = colRoot != null ? colRoot.Find("Kokan") : null;
            if (col != null)
            {
                Vector3 ls = col.lossyScale;
                if (!IsFinite(ls) || Mathf.Min(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z)) < 1e-4f)
                    LogOther(kb, "コライダー", $"Kokan_root/Kokan の大きさ {V(ls)}（Kokan_root の大きさ {V(colRoot.localScale)}・Kokan の大きさ {V(col.localScale)}）");
            }
        }

        private static void LogOther(MonoBehaviour kb, string kind, string detail)
        {
            if (!_loggedOther.Add(kind)) return;
            PatcherPlugin.Logger.LogWarning($"{Tag} {CharaName(kb)}: {detail} / {HState()}（記録のみ。この種類は1回だけ出します）。");
        }

        private static float _windowStart = -1f;

        private static void MaybeSummary()
        {
            float now = Time.realtimeSinceStartup;
            if (_windowStart < 0f) { _windowStart = now; return; }
            if (now - _windowStart < SummarySeconds) return;
            if (_repairs + _skipDt + _skipNonFinite + _skipBigStep > 0)
                PatcherPlugin.Logger.LogInfo($"{Tag} 直近 {now - _windowStart:F0}秒: cf_j_kokan を直した {_repairs} フレーム / 押しつぶしの計算を飛ばした: " +
                                             $"deltaTime が0 {_skipDt}回・移動量が NaN {_skipNonFinite}回・移動量が上限超え {_skipBigStep}回（最大 {_maxStep:F3}m） / {HState()}");
            _windowStart = now;
            _repairs = _skipDt = _skipNonFinite = _skipBigStep = 0;
            _maxStep = 0f;
        }

        // ===================== 共通 =====================

        private static Vector3 FixScale(Vector3 s, Vector3 idle)
        {
            return new Vector3(FixAxis(s.x, idle.x), FixAxis(s.y, idle.y), FixAxis(s.z, idle.z));
        }

        private static float FixAxis(float v, float idle)
        {
            if (IsFinite(v)) return Mathf.Clamp(v, MinScale, MaxScale);
            return IsFinite(idle) && idle >= MinScale && idle <= MaxScale ? idle : 1f;
        }

        private static bool OutOfRange(Vector3 s)
        {
            return s.x < MinScale || s.y < MinScale || s.z < MinScale || s.x > MaxScale || s.y > MaxScale || s.z > MaxScale;
        }

        private static bool IsFinite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }
        private static bool IsFinite(Vector3 v) { return IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z); }
        private static bool IsFinite(Quaternion q) { return IsFinite(q.x) && IsFinite(q.y) && IsFinite(q.z) && IsFinite(q.w); }

        private static string V(Vector3 v) { return $"({v.x:F3}, {v.y:F3}, {v.z:F3})"; }

        private static string HState()
        {
            try
            {
                var flags = UnityEngine.Object.FindObjectOfType<HFlag>();
                return flags != null ? $"状態 {flags.nowAnimStateName}・モード {flags.mode}" : "状態 不明";
            }
            catch { return "状態 不明"; }
        }

        private static string CharaName(MonoBehaviour kb)
        {
            try
            {
                var cha = kb != null ? kb.GetComponentInParent<ChaControl>() : null;
                if (cha == null) return "(キャラ不明)";
                string full = cha.fileParam != null ? cha.fileParam.fullname : null;
                return string.IsNullOrEmpty(full) ? cha.name : $"{full}({cha.name})";
            }
            catch { return "(キャラ不明)"; }
        }

        private static readonly HashSet<string> _warned = new HashSet<string>();

        private static void SafeWarn(string where, Exception ex)
        {
            try
            {
                if (_warned.Add(where)) PatcherPlugin.Logger.LogWarning($"{Tag} {where} の回避策で例外（原型に委ねます）: {ex.Message}");
            }
            catch { }
        }
    }
}
