using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RootMotion.FinalIK;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// 「IK の目標が null になり、絶頂が繰り返される（絶頂ループ）」の回避策（v1.10.6）。
    ///
    /// 【不具合のメカニズム（2026-10-19 の kPlugVR の実機ログで確認）】
    ///   kPlug / kPlugVR の追加アニメーション（ExtraAnimManager）は、アニメーションの切り替えのたびに
    ///   <c>HActionBase.SetPlay</c> の Postfix（<c>Core.HActionBase_PostSetPlay</c>）から
    ///   <c>ToolExtrAnimIK.SetIkTargets(cha, ...)</c> を呼び、手足の IK の目標を付け直す。
    ///
    ///   手足ごとに、<c>ChaTools.FindInCha(cha, 名前)</c>（cha.GetComponentsInChildren から名前で探す）で目標を探し、
    ///   結果を null かどうか確かめずに <c>iKEffector.target</c> に入れる（★見つからなければ null が入る）。
    ///   続けて同じように探したフック先 pv を <c>IkHook.Create(pv, …)</c> に渡す（★pv が null だと pv.gameObject で NRE）。
    ///
    ///   キャラの骨格が一時的にキャラの外にあると（例: KK_SetParentVR が男の位置の操作中に
    ///   p_cf_body_bone をコントローラーや女の子の骨格の下へ付け替える）、骨が見つからず、
    ///   ゲーム本体と共有している IK の目標（IKEffector.target）が null になる。
    ///
    ///   以後、ゲーム本体の <c>MotionIK.LinkIK</c> が <c>effector.target.GetComponent&lt;BaseData&gt;()</c> で
    ///   NullReferenceException を起こす。これは <c>HActionBase.SetPlay</c> → <c>MotionIK.Calc</c> の中なので、
    ///   <c>HSonyu.Proc</c> の SetPlay の後ろの処理が飛ぶ:
    ///
    ///   ・IN_A からの再開: <c>SetPlay("WLoop")</c> の後の <c>flags.finish = none</c> が飛び、finish が残る。
    ///   ・<c>SetPlay("WS_IN_A")</c> の後の <c>gaugeMale = gaugeFemale = 0</c> が飛び、ゲージが残る。
    ///
    ///   finish が残ったまま LoopProc に入ると、すぐ OLoop に入る。これを繰り返すのが絶頂ループ
    ///   （ParentObjectCtrlStaleRefFix と同じく「SetPlay の途中の例外で終了処理が飛ぶ」型）。
    ///   さらに例外は SetPlay の本体で起きるため、目標を付け直す kPlug の Postfix まで届かず、恒久化する。
    ///
    /// 【回避策（2層）】
    ///   1. kPlug 側（<c>EnableExtraAnimIkTargetGuard</c>）:
    ///      - <c>ToolExtrAnimIK.SetIkTargets</c>: 呼ぶ前のキャラの手足の IK の目標を覚えておき、終わった後に
    ///        null（破棄済みを含む）にされたものだけを元に戻す。kPlug が見つけた目標を入れた場合は何もしない。
    ///      - <c>ToolExtrAnimIK.IkHook.Create</c>: pv（フック先）か eff（IK の目標）が null なら作らずに null を返す
    ///        （原型は pv.gameObject で例外。eff が null のフックは LateUpdate で毎フレーム例外になる）。
    ///        戻り値を使う呼び出し元は無い。
    ///   2. ゲーム本体側（<c>EnableMotionIKNullTargetGuard</c>、安全網）:
    ///      - <c>MotionIK.LinkIK</c>: その手足の IK の目標（effector.target）か曲げの目標（bend.bendGoal）が null なら、
    ///        その手足だけを飛ばす。原型は例外になり、その手足に何も設定しないうえ、残りの手足と SetPlay の後ろが飛ぶ。
    ///        飛ばせば残りの手足と SetPlay の後ろ（絶頂の終了処理）が実行される。原因が kPlug 以外でも効く。
    ///
    /// 【設計上の注意】
    /// - 2 は ParentObjectCtrlStaleRefFix と同じく kPlug ではなく**ゲーム本体(Assembly-CSharp)**へのパッチ。個別に無効化できる。
    /// - 例外を外へ出さない。回避策側で失敗した場合は原型の実行に委ねる。
    /// - どのキャラ・どの手足で起きたかをログに出す（kPlug の作者に報告できるように）。
    /// </summary>
    internal static class MotionIKNullTargetGuard
    {
        private const string Tag = "[MotionIKNullTargetGuard]";

        /// <summary>同じ内容のログを出し続けないための、キーごとの回数。最初の1回と50回ごとに出す。</summary>
        private static readonly Dictionary<string, int> _counts = new Dictionary<string, int>();
        private const int ReportInterval = 50;

        private static readonly string[] LimbNames = { "左手", "右手", "左足", "右足" };

        // ===================== 適用 =====================

        /// <summary>ゲーム本体の MotionIK.LinkIK に安全網を当てる。</summary>
        internal static void TryApplyGame(Harmony harmony)
        {
            try
            {
                var method = AccessTools.Method(typeof(global::MotionIK), "LinkIK",
                    new[] { typeof(int), typeof(MotionIKData.State), typeof(global::MotionIK.IKTargetPair) });
                if (method == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} MotionIK.LinkIK(int, MotionIKData.State, IKTargetPair) が見つからないため、ゲーム本体側の安全網は適用しません。");
                    return;
                }
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(MotionIKNullTargetGuard)
                    .GetMethod(nameof(LinkIKPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                PatcherPlugin.Logger.LogInfo($"{Tag} MotionIK.LinkIK に IK の目標の null ガードを適用しました。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} MotionIK.LinkIK への適用に失敗: {ex.Message}");
            }
        }

        /// <summary>kPlug / kPlugVR の ToolExtrAnimIK（動作中のモードのもの）に回避策を当てる。</summary>
        internal static void TryApplyKPlug(Harmony harmony)
        {
            // 通常モードで kPlugVR の型に深く触れないよう、動作中のモードの型だけを FullName の文字列で選ぶ（KPlugVRNullGuards【N】【E】と同じ）。
            string root = BepInEx.Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase) ? "kPlugVR." : "kPlug.";
            Type tool = null;
            Type hook = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.FullName.Contains("kPlug")) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                tool = tool ?? types.FirstOrDefault(t => t != null && t.FullName == root + "Tools.ToolExtrAnimIK");
                hook = hook ?? types.FirstOrDefault(t => t != null && t.FullName == root + "Tools.ToolExtrAnimIK+IkHook");
            }
            if (tool == null)
            {
                PatcherPlugin.Logger.LogInfo($"{Tag} {root}Tools.ToolExtrAnimIK が見つからないため、kPlug 側の回避策は適用しません。");
                return;
            }

            try
            {
                var setIkTargets = AccessTools.Method(tool, "SetIkTargets",
                    new[] { typeof(ChaControl), typeof(string), typeof(string), typeof(bool) });
                if (setIkTargets != null)
                {
                    harmony.Patch(setIkTargets,
                        prefix: new HarmonyMethod(typeof(MotionIKNullTargetGuard).GetMethod(nameof(SetIkTargetsPrefix), BindingFlags.NonPublic | BindingFlags.Static)),
                        finalizer: new HarmonyMethod(typeof(MotionIKNullTargetGuard).GetMethod(nameof(SetIkTargetsFinalizer), BindingFlags.NonPublic | BindingFlags.Static)));
                    PatcherPlugin.Logger.LogInfo($"{Tag} {tool.FullName}.SetIkTargets に、IK の目標を null にしないガードを適用しました。");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {tool.FullName}.SetIkTargets(ChaControl, string, string, bool) が見つかりませんでした。");
                }

                var create = hook != null ? AccessTools.Method(hook, "Create", new[] { typeof(Transform), typeof(Transform), typeof(Transform) }) : null;
                if (create != null && create.ReturnType == hook)
                {
                    harmony.Patch(create, prefix: new HarmonyMethod(typeof(MotionIKNullTargetGuard)
                        .GetMethod(nameof(IkHookCreatePrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                    PatcherPlugin.Logger.LogInfo($"{Tag} {hook.FullName}.Create に null ガードを適用しました。");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {root}Tools.ToolExtrAnimIK+IkHook.Create(Transform, Transform, Transform) が想定の形で見つかりませんでした。");
                }
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} kPlug 側への適用に失敗: {ex.Message}");
            }
        }

        // ===================== ゲーム本体: MotionIK.LinkIK =====================

        /// <summary>
        /// IK の目標か曲げの目標が null（破棄済みを含む）の手足だけを飛ばす。
        /// 原型は effector.target.GetComponent で例外になり、その手足に何も設定しない。
        /// 曲げの目標だけが null の場合、原型は手足の目標の設定をした後で例外になるが、ここではその手足を丸ごと飛ばす。
        /// </summary>
        private static bool LinkIKPrefix(global::MotionIK __instance, int index, global::MotionIK.IKTargetPair pair)
        {
            if (PatcherPlugin.IsQuitting) return true;
            try
            {
                if (!PatcherPlugin.EnableMotionIKNullTargetGuard.Value) return true;

                string missing = null;
                if (pair == null) missing = "IKTargetPair";
                else if (pair.effector == null) missing = "effector";
                else if (pair.effector.target == null) missing = "IK の目標(effector.target)";
                else if (pair.bend == null) missing = "bend";
                else if (pair.bend.bendGoal == null) missing = "曲げの目標(bend.bendGoal)";
                if (missing == null) return true;

                string name = CharaName(__instance != null ? __instance.info : null);
                string limb = index >= 0 && index < LimbNames.Length ? LimbNames[index] : index.ToString();
                Report($"LinkIK|{name}|{index}|{missing}",
                    n => $"{Tag} MotionIK.LinkIK: {name} の{limb}の{missing}が null のため、この手足の IK の設定を飛ばしました" +
                         $"（{n}回目。原型では例外で SetPlay の後ろ（絶頂の終了処理など）も飛んでいた）。");
                return false;
            }
            catch (Exception ex)
            {
                SafeWarn("LinkIK", ex);
                return true;
            }
        }

        // ===================== kPlug: ToolExtrAnimIK.SetIkTargets / IkHook.Create =====================

        /// <summary>呼ぶ前の、キャラの手足の IK の目標（左手・右手・左足・右足）。</summary>
        private static void SetIkTargetsPrefix(ChaControl cha, out Transform[] __state)
        {
            __state = null;
            try
            {
                if (PatcherPlugin.IsQuitting || !PatcherPlugin.EnableExtraAnimIkTargetGuard.Value) return;
                var effectors = LimbEffectors(cha);
                if (effectors == null) return;
                __state = effectors.Select(e => e != null ? e.target : null).ToArray();
            }
            catch (Exception ex)
            {
                SafeWarn("SetIkTargets(前)", ex);
            }
        }

        /// <summary>
        /// 終わった後（例外の場合も）、null にされた目標だけを元に戻す。例外はそのまま通す。
        /// </summary>
        private static Exception SetIkTargetsFinalizer(Exception __exception, ChaControl cha, string animAsset, string state, Transform[] __state)
        {
            try
            {
                if (__state == null) return __exception;
                var effectors = LimbEffectors(cha);
                if (effectors == null) return __exception;
                for (int i = 0; i < effectors.Length && i < __state.Length; i++)
                {
                    var e = effectors[i];
                    if (e == null || e.target != null || __state[i] == null) continue;
                    e.target = __state[i];
                    string name = CharaName(cha);
                    string limb = LimbNames[i];
                    string restored = __state[i].name;
                    Report($"SetIkTargets|{name}|{i}",
                        n => $"{Tag} {name} の{limb}の IK の目標を kPlug の ToolExtrAnimIK.SetIkTargets（{animAsset} / {state}）が null にしたため、" +
                             $"元の目標 {restored} に戻しました（{n}回目。キャラの中に目標の骨が見つからなかった）。");
                }
            }
            catch (Exception ex)
            {
                SafeWarn("SetIkTargets(後)", ex);
            }
            return __exception;
        }

        /// <summary>pv（フック先）か eff（IK の目標）が null ならフックを作らない。</summary>
        private static bool IkHookCreatePrefix(Transform pv, Transform eff)
        {
            try
            {
                if (PatcherPlugin.IsQuitting || !PatcherPlugin.EnableExtraAnimIkTargetGuard.Value) return true;
                if (pv != null && eff != null) return true;
                string what = pv == null ? "フック先(pv)" : "IK の目標(eff)";
                Report($"IkHook|{what}",
                    n => $"{Tag} kPlug の ToolExtrAnimIK.IkHook.Create: {what}が null のため、IK のフックを作りませんでした（{n}回目。原型では例外）。");
                return false; // 戻り値は既定の null（IkHook の型を参照しないため __result は使わない）
            }
            catch (Exception ex)
            {
                SafeWarn("IkHook.Create", ex);
                return true;
            }
        }

        // ===================== 共通 =====================

        /// <summary>キャラの手足の IKEffector（左手・右手・左足・右足）。MotionIK と同じく objAnim の FullBodyBipedIK を使う。</summary>
        private static IKEffector[] LimbEffectors(ChaControl cha)
        {
            if (cha == null || cha.objAnim == null) return null;
            var ik = cha.objAnim.GetComponent<FullBodyBipedIK>();
            if (ik == null || ik.solver == null) return null;
            var s = ik.solver;
            return new[] { s.leftHandEffector, s.rightHandEffector, s.leftFootEffector, s.rightFootEffector };
        }

        private static string CharaName(ChaControl cha)
        {
            try
            {
                if (cha == null) return "(キャラ不明)";
                string full = cha.fileParam != null ? cha.fileParam.fullname : null;
                return string.IsNullOrEmpty(full) ? cha.name : $"{full}({cha.name})";
            }
            catch { return "(キャラ不明)"; }
        }

        private static void Report(string key, Func<int, string> message)
        {
            int n;
            _counts.TryGetValue(key, out n);
            n++;
            _counts[key] = n;
            if (n == 1 || n % ReportInterval == 0) PatcherPlugin.Logger.LogWarning(message(n));
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
