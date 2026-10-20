using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// kPlugVR で、招待したキャラ複数と同時にキスできるようにする（v1.9.0）。
    ///
    /// 【kPlug の作り】<c>HCtrl.ParseTouchables</c>（毎フレーム）は、キスの範囲に入っている招待キャラのうち
    /// <c>haremAI</c> の並びで最初の1人だけを <c>kissable_ai</c> に選び、その1人に <c>KissByPlayer()</c> を呼ぶ。
    /// <c>IE_Kiss(kissedAi)</c> は <c>kissable_ai</c> が null になるまで待ってから <c>kissedAi.FinishKiss()</c> を呼ぶ。
    /// キスの状態（<c>AI_Public.inKiss</c>・表情と声・舌・興奮）はキャラごとに持っているので、複数同時でも壊れない。
    ///
    /// 【このパッチ】<c>ParseTouchables</c> の Postfix で、kPlug が選んだ1人に加えて、2人目以降のキャラを管理する。
    /// - 誰か（ほかの招待キャラ）がキス中のときだけ、<c>StartDistance</c> / <c>StartAngle</c> の範囲に入ったキャラの
    ///   キスを始める（1人だけのキスは kPlug 本来の範囲のまま変えない）。
    /// - 管理中のキャラは <c>KeepDistance</c> / <c>KeepAngle</c> の範囲にいる間 <c>KissByPlayer()</c> を呼び続け、
    ///   範囲を出たら <c>FinishKiss()</c> を呼ぶ。
    /// - 2人目以降がいる間は、kPlug が選んだキャラも管理に入れる。kPlug の範囲（既定 12〜14cm・25°）を出ても、
    ///   こちらの範囲にいる間は kPlug の <c>FinishKiss()</c> を止めて（Prefix）キスを続ける。
    ///   止めないと、kPlug が終わらせた次のフレームにこちらがまた始めて、キスの声と笑い声が繰り返される。
    /// - ストリップ中・初期位置へ移動中・キャラ交代中・<c>freezeKissing</c>（交代を頼まれた）は kPlug と同じく対象外。
    ///
    /// 【kPlug の既存の問題の後始末（<c>EnableStuckGuestKissFix</c>）】キスの相手が A から B に同じフレームで直接替わると、
    /// <c>IE_Kiss(A)</c> は <c>kissable_ai == null</c> を待ち続け、A の <c>FinishKiss()</c> が呼ばれない。
    /// A は離れても <c>inKiss</c> のまま残り（KK_CyuVR では口と舌が動き続ける）、その後 B が離れると
    /// A だけが終わって今度は B が残る。kPlug が選んでおらず、このパッチも管理していないのに <c>inKiss</c> が
    /// 残っているキャラには <c>FinishKiss()</c> を呼ぶ。
    ///
    /// kPlug のメンバーはリフレクションで引く（kPlugVR 3.56 / 3.63、KK / KKP で同じ名前）。
    /// 非VR版 kPlug の HCtrl には ParseTouchables が無いので対象外。
    /// </summary>
    internal static class MultiGuestKiss
    {
        private const string Tag = "[MultiGuestKiss]";

        private static readonly HashSet<string> _appliedKeys = new HashSet<string>();
        private static readonly HashSet<string> _failureLoggedKeys = new HashSet<string>();

        // ---- kPlug のメンバー（HCtrl の型ごとに1回だけ解決する。KK と KKP、3.56 と 3.63 で名前は同じ） ----
        private static FieldInfo _kissableAi;          // HCtrl.kissable_ai
        private static FieldInfo _coreDealer;          // Core.dealer (static)
        private static FieldInfo _haremAI;             // Dealer.haremAI (List<AI_Public>)
        private static MethodInfo _inStripTease;       // AI_Public.inStripTease (getter)
        private static FieldInfo _spotCtrl;            // AI_Public.spotCtrl
        private static MethodInfo _inInitPos;          // SpotCtrl.InInitPos (getter)
        private static FieldInfo _inSwap;              // AI_Public.InSwap
        private static FieldInfo _freezeKissing;       // AI_Public.freezeKissing
        private static MethodInfo _inKiss;             // AI_Public.inKiss (getter)
        private static FieldInfo _distFromCam;         // AI_Public.DistFromCam
        private static FieldInfo _angleFromCam;        // AI_Public.AngleFromCam
        private static MethodInfo _kissByPlayer;       // AI_Public.KissByPlayer()
        private static MethodInfo _finishKiss;         // AI_Public.FinishKiss()
        private static FieldInfo _charaName;           // AI.CharaName（AI_Public の基底クラスのフィールド、ログ用。無くてもよい）

        // ---- 状態 ----
        /// <summary>このパッチが管理しているキャラ（2人目以降と、その間の kPlug が選んだキャラ）。</summary>
        private static readonly List<object> _managed = new List<object>();
        private static object _lastHCtrl;
        /// <summary>このパッチ自身が FinishKiss を呼んでいる間 true（Prefix で止めないため）。</summary>
        private static bool _ownFinish;
        private static int _suppressedFinishCount;
        private static int _stuckFixCount;
        /// <summary>
        /// kPlug のキスの相手でも管理中でもないのに inKiss が残っているキャラと、最初にそう見えた時刻。
        /// kPlug が1人とのキスを普通に終えるときも、kissable_ai が null になってから IE_Kiss が FinishKiss を呼ぶまで
        /// 1フレームほどこの状態になるため、StuckSeconds 続いたときだけ後始末する。
        /// </summary>
        private static readonly Dictionary<object, float> _stuckSince = new Dictionary<object, float>();
        private const float StuckSeconds = 0.5f;
        private static bool _runtimeErrorLogged;

        internal static void TryApply(Harmony harmony)
        {
            bool wantMulti = PatcherPlugin.EnableMultiGuestKiss.Value;
            bool wantStuckFix = PatcherPlugin.EnableStuckGuestKissFix.Value;
            if (!wantMulti && !wantStuckFix) return;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.FullName.Contains("kPlug")) continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                var hctrl = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".CmpH.HCtrl"));
                if (hctrl != null) TryPatch(harmony, hctrl);
            }
        }

        private static void TryPatch(Harmony harmony, Type hctrlType)
        {
            var key = hctrlType.FullName;
            if (_appliedKeys.Contains(key)) return;

            try
            {
                var parse = AccessTools.DeclaredMethod(hctrlType, "ParseTouchables", Type.EmptyTypes);
                var kissable = AccessTools.Field(hctrlType, "kissable_ai");
                if (parse == null || kissable == null)
                {
                    // 非VR版 kPlug の HCtrl（招待キャラへのキスの処理が無い）。対象外。
                    _appliedKeys.Add(key);
                    return;
                }

                if (!Resolve(hctrlType, kissable, out string missing))
                {
                    _appliedKeys.Add(key);
                    PatcherPlugin.Logger.LogWarning($"{Tag} kPlug のメンバー {missing} が見つからないため、招待キャラ複数との同時キスは適用しません（kPlug のバージョン違いの可能性）。");
                    return;
                }

                harmony.Patch(parse, postfix: new HarmonyMethod(typeof(MultiGuestKiss)
                    .GetMethod(nameof(ParseTouchablesPostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                harmony.Patch(_finishKiss, prefix: new HarmonyMethod(typeof(MultiGuestKiss)
                    .GetMethod(nameof(FinishKissPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                _appliedKeys.Add(key);

                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} {hctrlType.FullName}.ParseTouchables() に適用しました" +
                    $"（招待キャラ複数との同時キス: {(PatcherPlugin.EnableMultiGuestKiss.Value ? "有効" : "無効")}、" +
                    $"残ったキス状態の後始末: {(PatcherPlugin.EnableStuckGuestKissFix.Value ? "有効" : "無効")}、frame={Time.frameCount}）。");
            }
            catch (Exception ex)
            {
                if (_failureLoggedKeys.Add(key))
                {
                    if (IsAssemblyUnavailable(ex))
                        PatcherPlugin.Logger.LogInfo($"{Tag} {key} は対象外です（このモードでは kPlugVR のクラスをロードできません。実害はありません）: {ex.Message}");
                    else
                        PatcherPlugin.Logger.LogError($"{Tag} {key} への適用に失敗: {ex.Message}");
                }
            }
        }

        private static bool Resolve(Type hctrlType, FieldInfo kissable, out string missing)
        {
            missing = null;
            var aiType = kissable.FieldType;
            var coreType = ResolveCoreType(hctrlType);

            _kissableAi = kissable;
            _coreDealer = coreType != null ? AccessTools.Field(coreType, "dealer") : null;
            _haremAI = _coreDealer != null ? AccessTools.Field(_coreDealer.FieldType, "haremAI") : null;
            _inStripTease = AccessTools.PropertyGetter(aiType, "inStripTease");
            _spotCtrl = AccessTools.Field(aiType, "spotCtrl");
            _inInitPos = _spotCtrl != null ? AccessTools.PropertyGetter(_spotCtrl.FieldType, "InInitPos") : null;
            _inSwap = AccessTools.Field(aiType, "InSwap");
            _freezeKissing = AccessTools.Field(aiType, "freezeKissing");
            _inKiss = AccessTools.PropertyGetter(aiType, "inKiss");
            _distFromCam = AccessTools.Field(aiType, "DistFromCam");
            _angleFromCam = AccessTools.Field(aiType, "AngleFromCam");
            _kissByPlayer = AccessTools.Method(aiType, "KissByPlayer", Type.EmptyTypes);
            _finishKiss = AccessTools.Method(aiType, "FinishKiss", Type.EmptyTypes);
            _charaName = AccessTools.Field(aiType, "CharaName");

            // net35 には ValueTuple が無いので名前と値を別の配列にする
            var names = new[]
            {
                "Core.dealer", "Dealer.haremAI", "AI_Public.inStripTease", "AI_Public.spotCtrl", "SpotCtrl.InInitPos",
                "AI_Public.InSwap", "AI_Public.freezeKissing", "AI_Public.inKiss", "AI_Public.DistFromCam",
                "AI_Public.AngleFromCam", "AI_Public.KissByPlayer()", "AI_Public.FinishKiss()",
            };
            var members = new object[]
            {
                _coreDealer, _haremAI, _inStripTease, _spotCtrl, _inInitPos,
                _inSwap, _freezeKissing, _inKiss, _distFromCam,
                _angleFromCam, _kissByPlayer, _finishKiss,
            };
            for (int i = 0; i < names.Length; i++)
            {
                if (members[i] == null) { missing = names[i]; return false; }
            }
            return true;
        }

        /// <summary>kPlugVR.CmpH.HCtrl → kPlugVR.Core（非VR版 kPlug.Core と取り違えないよう同一アセンブリから引く）。</summary>
        private static Type ResolveCoreType(Type memberType)
        {
            var ns = memberType.Namespace ?? string.Empty;
            int dot = ns.IndexOf('.');
            var root = dot >= 0 ? ns.Substring(0, dot) : ns;
            return memberType.Assembly.GetType(root + ".Core");
        }

        private static bool IsAssemblyUnavailable(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is TypeLoadException || e is ReflectionTypeLoadException
                    || e is BadImageFormatException || e is System.IO.FileNotFoundException) return true;
            }
            return false;
        }

        // ---- 毎フレームの処理 ----

        private static void ParseTouchablesPostfix(object __instance)
        {
            try
            {
                if (!ReferenceEquals(__instance, _lastHCtrl))
                {
                    // Hシーンが替わった。前のシーンのキャラは破棄済みなので、何も呼ばずに忘れる。
                    _lastHCtrl = __instance;
                    _managed.Clear();
                    _stuckSince.Clear();
                }

                var dealer = _coreDealer.GetValue(null);
                var harem = dealer != null ? _haremAI.GetValue(dealer) as IList : null;
                if (harem == null) return;

                object primary = _kissableAi.GetValue(__instance);
                if (IsDestroyed(primary)) primary = null;

                // 招待キャラから外れた（退場・破棄）キャラは管理から外す。生きていればキスを終わらせる。
                for (int i = _managed.Count - 1; i >= 0; i--)
                {
                    var ai = _managed[i];
                    if (IsDestroyed(ai)) { _managed.RemoveAt(i); continue; }
                    if (!harem.Contains(ai)) { _managed.RemoveAt(i); Finish(ai, "招待キャラの一覧から外れた"); }
                }

                bool multi = PatcherPlugin.EnableMultiGuestKiss.Value;
                float startDist = PatcherPlugin.MultiKissStartDistance.Value;
                float startAngle = PatcherPlugin.MultiKissStartAngle.Value;
                float keepDist = Mathf.Max(startDist, PatcherPlugin.MultiKissKeepDistance.Value);
                float keepAngle = Mathf.Max(startAngle, PatcherPlugin.MultiKissKeepAngle.Value);

                foreach (var ai in harem)
                {
                    if (ai == null || IsDestroyed(ai)) continue;
                    bool isPrimary = ReferenceEquals(ai, primary);
                    bool managed = _managed.Contains(ai);

                    if (IsExcluded(ai))
                    {
                        // kPlug と同じ対象外の条件（ストリップ・初期位置へ移動・交代中・交代を頼まれた）
                        if (managed) { _managed.Remove(ai); Finish(ai, "キスの対象外になった（交代・ストリップなど）"); }
                        continue;
                    }

                    if (managed)
                    {
                        if (InRange(ai, keepDist, keepAngle))
                        {
                            // kPlug が選んだキャラには kPlug が KissByPlayer を呼んでいる（2回呼ぶと興奮が2倍入る）
                            if (!isPrimary) _kissByPlayer.Invoke(ai, null);
                        }
                        else
                        {
                            _managed.Remove(ai);
                            // kPlug が選んでいるキャラは kPlug に任せる（kPlug の範囲にいる）
                            if (!isPrimary) Finish(ai, "範囲を出た");
                        }
                        continue;
                    }

                    if (!multi) continue;

                    if (isPrimary)
                    {
                        // 2人目以降がいる間は、kPlug が選んだキャラも管理に入れる（kPlug の範囲を出ても、こちらの範囲なら続ける）
                        if (_managed.Count > 0 && InRange(ai, keepDist, keepAngle)) _managed.Add(ai);
                        continue;
                    }

                    // 2人目以降: ほかの招待キャラがキス中のときだけ、こちらの範囲で始める
                    if (AnyOtherKissing(harem, ai, primary) && InRange(ai, startDist, startAngle))
                    {
                        _managed.Add(ai);
                        _kissByPlayer.Invoke(ai, null);
                        if (primary != null && !_managed.Contains(primary) && InRange(primary, keepDist, keepAngle))
                            _managed.Add(primary);
                        PatcherPlugin.Logger.LogInfo(
                            $"{Tag} {NameOf(ai)} とのキスを始めました（同時にキス中: {DescribeKissing(harem, ai)}。" +
                            $"距離 {GetFloat(_distFromCam, ai):F3}m・角度 {GetFloat(_angleFromCam, ai):F0}°、範囲 {startDist:F2}m・{startAngle:F0}°）。");
                    }
                }

                if (PatcherPlugin.EnableStuckGuestKissFix.Value) FixStuckKisses(harem, primary);
            }
            catch (Exception ex)
            {
                if (!_runtimeErrorLogged)
                {
                    _runtimeErrorLogged = true;
                    PatcherPlugin.Logger.LogWarning($"{Tag} 処理中に例外が出ました（以後このログは出しません）: {ex}");
                }
            }
        }

        /// <summary>
        /// kPlug が選んでおらず、このパッチも管理していないのに inKiss が残っているキャラのキスを終わらせる
        /// （キスの相手が同じフレームで別のキャラに直接替わったとき、kPlug が FinishKiss を呼ばない問題）。
        /// </summary>
        private static void FixStuckKisses(IList harem, object primary)
        {
            // 今は残っていないキャラ（キスが終わった・一覧から外れた）の記録を消す
            if (_stuckSince.Count > 0)
            {
                foreach (var key in _stuckSince.Keys.ToList())
                {
                    if (IsDestroyed(key) || !harem.Contains(key) || ReferenceEquals(key, primary) || _managed.Contains(key)
                        || !GetBool(_inKiss, key))
                        _stuckSince.Remove(key);
                }
            }

            float now = Time.unscaledTime;
            foreach (var ai in harem)
            {
                if (ai == null || IsDestroyed(ai) || ReferenceEquals(ai, primary) || _managed.Contains(ai)) continue;
                if (!GetBool(_inKiss, ai)) continue;
                if (!_stuckSince.TryGetValue(ai, out float since)) { _stuckSince[ai] = now; continue; }
                if (now - since < StuckSeconds) continue;
                _stuckSince.Remove(ai);
                _stuckFixCount++;
                if (_stuckFixCount <= 5 || _stuckFixCount % 50 == 0)
                {
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag} {NameOf(ai)} は kPlug のキスの相手ではないのに {StuckSeconds}秒以上キス状態のまま残っていたため、" +
                        $"キスを終わらせました（{_stuckFixCount}回目、距離 {GetFloat(_distFromCam, ai):F3}m）。");
                }
                Finish(ai, null);
            }
        }

        /// <summary>
        /// kPlug の <c>IE_Kiss</c>（kPlug の範囲を出た）や <c>AskForSwap</c> からの FinishKiss を、
        /// このパッチが管理していてこちらの範囲にいるキャラについては止める。
        /// 交代を頼まれた場合は直後に freezeKissing が立つので、次のフレームにこちらで終わらせる。
        /// </summary>
        private static bool FinishKissPrefix(object __instance)
        {
            if (_ownFinish || !_managed.Contains(__instance)) return true;
            _suppressedFinishCount++;
            if (_suppressedFinishCount <= 5 || _suppressedFinishCount % 50 == 0)
            {
                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} {NameOf(__instance)} は同時キスの範囲にいるため、kPlug のキス終了を止めてキスを続けます" +
                    $"（{_suppressedFinishCount}回目、距離 {GetFloat(_distFromCam, __instance):F3}m）。");
            }
            return false;
        }

        private static void Finish(object ai, string reason)
        {
            _ownFinish = true;
            try { _finishKiss.Invoke(ai, null); }
            finally { _ownFinish = false; }
            if (reason != null)
                PatcherPlugin.Logger.LogInfo($"{Tag} {NameOf(ai)} とのキスを終えました（{reason}、距離 {GetFloat(_distFromCam, ai):F3}m・角度 {GetFloat(_angleFromCam, ai):F0}°）。");
        }

        private static bool IsExcluded(object ai)
        {
            if (GetBool(_inStripTease, ai) || GetBool(_inSwap, ai) || GetBool(_freezeKissing, ai)) return true;
            var spot = _spotCtrl.GetValue(ai);
            return spot != null && !IsDestroyed(spot) && GetBool(_inInitPos, spot);
        }

        private static bool InRange(object ai, float distance, float angle)
        {
            return GetFloat(_distFromCam, ai) < distance && GetFloat(_angleFromCam, ai) < angle;
        }

        /// <summary>自分以外の招待キャラがキス中か（kPlug が選んだキャラ、またはこのパッチが管理しているキャラ）。</summary>
        private static bool AnyOtherKissing(IList harem, object self, object primary)
        {
            if (primary != null && !ReferenceEquals(primary, self) && GetBool(_inKiss, primary)) return true;
            foreach (var ai in _managed)
            {
                if (!ReferenceEquals(ai, self)) return true;
            }
            return false;
        }

        private static string DescribeKissing(IList harem, object self)
        {
            var names = new List<string>();
            foreach (var ai in harem)
            {
                if (ai == null || ReferenceEquals(ai, self) || IsDestroyed(ai)) continue;
                if (GetBool(_inKiss, ai)) names.Add(NameOf(ai));
            }
            return names.Count > 0 ? string.Join(" / ", names.ToArray()) : "(なし)";
        }

        private static bool IsDestroyed(object obj)
        {
            var uo = obj as UnityEngine.Object;
            return obj == null || uo == null;
        }

        private static bool GetBool(MemberInfo member, object target)
        {
            object v = member is FieldInfo f ? f.GetValue(target) : ((MethodInfo)member).Invoke(target, null);
            return v is bool b && b;
        }

        private static float GetFloat(FieldInfo field, object target)
        {
            try { return field.GetValue(target) is float f ? f : float.NaN; }
            catch { return float.NaN; }
        }

        private static string NameOf(object ai)
        {
            try
            {
                if (_charaName != null && _charaName.GetValue(ai) is string s && s.Length > 0) return s;
            }
            catch { }
            return (ai as Component) != null ? ((Component)ai).name : "(不明)";
        }
    }
}
