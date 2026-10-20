using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// kPlug / kPlugVR の招待が「クリックしても無反応」になる不具合の回避策。
    ///
    /// 【不具合のメカニズム（実機ログで確定済み）】
    ///   招待（Dealer.InviteCard など）は InProcess が false のときだけ招待のコルーチンを始め、そこで InProcess を true にする。
    ///   Hシーンで最初の招待のときは SpotManager を追加し、その Start() → CreateCaliber() → AdaptCaliber() で NRE が出る。
    ///   例外で Start() が中断し、spotsAreInit を true にするコルーチン（IE_CreateSpots）が始まらない。
    ///   招待のコルーチンは spotsAreInit を待ち続けるので、最後に InProcess を false に戻す処理に届かない。
    ///   InProcess が true のままロックされ、以降の招待が全てログにも出ないまま無視される。
    ///
    /// 【NRE の直接原因（2026-08-03 の実機ログで特定）】
    ///   `Core.hCtrl.bcG`（メインヒロインの BonesCtrl）が保持する `spine3` などの Transform 参照が、
    ///   **Unity 上で破棄済み（destroyed）** になっていた。AdaptCaliber は例えば
    ///   `bcG.headTop.position.y &gt; bcG.spine3.position.y` のように直接 `.position` を読むため、
    ///   破棄済み参照に触れた時点で NRE となる。
    ///
    ///   重要なのは **null（最初から存在しない）ではなく destroyed（一度取得したが後で破棄された）** だった点。
    ///   ボーン自体はキャラカードに存在しており（KKABMX で確認済み）、
    ///   頭部 MOD やボーン注入系プラグイン（Mod Bone Implantor 等）がボーン階層を再構築した結果、
    ///   BonesCtrl が古い Transform 参照を持ったままになったものと考えられる。
    ///
    /// 【回避策】
    ///   AdaptCaliber の直前に破棄済み参照を検出したら、**kPlug 自身の再構築メソッド
    ///   `BonesCtrl.FindBones()` を呼び直す**だけ。`ParserScene.FindBones` は見つかったボーンを
    ///   代入するのみで既存値をクリアしないため、破棄済み参照だけが健全な Transform に置き換わる。
    ///   独自のボーン探索を書かず kPlug のロジックをそのまま使うため、挙動のズレが生じにくい。
    ///
    /// 【設計上の注意】
    /// - 他プラグインの private メソッドに割り込むため、**絶対に例外を投げない**こと。
    ///   Prefix が例外を投げると kPlug 側の招待処理を巻き込んで壊す。全処理を try-catch で囲う。
    /// - kPlug の型名を固定文字列で決め打ちしない。`kPlug` と `kPlugVR` は同名クラスを
    ///   別名前空間に持つ独立実装で、通常モードでも両方ロードされうる
    /// - **リフレクション情報は必ず型ごとの辞書で持つ。**
    ///   単一の static フィールドにキャッシュすると、後から解析した型の FieldInfo が
    ///   先の型の分を上書きし、「Field mainRoot ... is not a field on the target object」で
    ///   実行時に失敗する（KK_AlternativeVoice 側で実際に踏んだ不具合）。
    /// </summary>
    internal static class SpotManagerBonesFix
    {
        /// <summary>1つの SpotManager 型（＝1つの kPlug アセンブリ）に対応するリフレクション情報一式。</summary>
        private sealed class KPlugTypeInfo
        {
            public FieldInfo CoreHCtrl, CoreHAnim, CoreInHresult;
            public FieldInfo HCtrlBcB, HCtrlBcG, HCtrlPosMod;
            public FieldInfo BcHeadTop, BcSpine3, BcLegL, BcLegR, BcArmL, BcArmR;
            public FieldInfo LegShin, LegFoot;
            public FieldInfo ArmHand;
            public MethodInfo BcFindBones;
            public FieldInfo SmMainRoot, SmCalib, SmTryPosRefs;
            public FieldInfo CalibRoot, CalibMsh;
        }

        private static readonly Dictionary<Type, KPlugTypeInfo> _infoByType = new Dictionary<Type, KPlugTypeInfo>();

        /// <summary>
        /// 見つかった全ての SpotManager 型の AdaptCaliber にパッチを当てる。
        /// PatcherPlugin.Update() の Dealer パッチと同じタイミングで呼ばれる。
        /// </summary>
        internal static void TryApply(Harmony harmony)
        {
            var spotManagerTypes = new List<Type>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.FullName.Contains("kPlug")) continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t != null && t.FullName != null && t.FullName.EndsWith(".SpotManager") && !spotManagerTypes.Contains(t))
                        spotManagerTypes.Add(t);
                }
            }

            if (spotManagerTypes.Count == 0)
            {
                PatcherPlugin.Logger.LogWarning($"{PatcherPlugin.PluginName}: SpotManager 型が見つかりませんでした。招待無反応の回避策は無効です。");
                return;
            }

            var prefix = new HarmonyMethod(typeof(SpotManagerBonesFix)
                .GetMethod(nameof(AdaptCaliberPrefix), BindingFlags.NonPublic | BindingFlags.Static));
            var finalizer = new HarmonyMethod(typeof(SpotManagerBonesFix)
                .GetMethod(nameof(AdaptCaliberFinalizer), BindingFlags.NonPublic | BindingFlags.Static));

            foreach (var smType in spotManagerTypes)
            {
                try
                {
                    var method = AccessTools.Method(smType, "AdaptCaliber");
                    if (method == null)
                    {
                        PatcherPlugin.Logger.LogWarning($"{PatcherPlugin.PluginName}: {smType.FullName}.AdaptCaliber が見つかりませんでした。");
                        continue;
                    }

                    // 型ごとに独立して保持する（単一変数に入れると後勝ちで壊れる）
                    _infoByType[smType] = BuildTypeInfo(smType);

                    // 引数を省略すると旧シグネチャ（CS0619）に解決されるため ilmanipulator まで明示する
                    harmony.Patch(method, prefix, null, null, finalizer, null);
                    PatcherPlugin.Logger.LogInfo($"{PatcherPlugin.PluginName}: AdaptCaliber パッチ適用成功 ({smType.FullName})");
                }
                catch (Exception ex)
                {
                    PatcherPlugin.LogPatchFailure($"{PatcherPlugin.PluginName}: AdaptCaliber のパッチ:", smType, ex);
                }
            }
        }

        /// <summary>
        /// 指定された SpotManager 型と、同じアセンブリ内の Core / HCtrl / BonesCtrl から
        /// 必要なリフレクション情報を集める。見つからないものは null のままにし、実行時に「?」として扱う。
        /// </summary>
        private static KPlugTypeInfo BuildTypeInfo(Type spotManagerType)
        {
            var info = new KPlugTypeInfo();

            info.SmMainRoot = AccessTools.Field(spotManagerType, "mainRoot");
            info.SmCalib = AccessTools.Field(spotManagerType, "calib");
            info.SmTryPosRefs = AccessTools.Field(spotManagerType, "tryPosRefs");

            var calibType = spotManagerType.GetNestedType("Caliber", BindingFlags.Public | BindingFlags.NonPublic);
            if (calibType != null)
            {
                info.CalibRoot = AccessTools.Field(calibType, "root");
                info.CalibMsh = AccessTools.Field(calibType, "msh");
            }

            Type[] types;
            try { types = spotManagerType.Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

            var coreType = types.FirstOrDefault(t => t.Name == "Core");
            if (coreType != null)
            {
                info.CoreHCtrl = AccessTools.Field(coreType, "hCtrl");
                info.CoreHAnim = AccessTools.Field(coreType, "hAnim");
                info.CoreInHresult = AccessTools.Field(coreType, "inHresult");
            }

            var hCtrlType = types.FirstOrDefault(t => t.Name == "HCtrl");
            if (hCtrlType != null)
            {
                info.HCtrlBcB = AccessTools.Field(hCtrlType, "bcB");
                info.HCtrlBcG = AccessTools.Field(hCtrlType, "bcG");
                info.HCtrlPosMod = AccessTools.Field(hCtrlType, "posMod");
            }

            var bonesCtrlType = types.FirstOrDefault(t => t.Name == "BonesCtrl");
            if (bonesCtrlType != null)
            {
                info.BcHeadTop = AccessTools.Field(bonesCtrlType, "headTop");
                info.BcSpine3 = AccessTools.Field(bonesCtrlType, "spine3");
                info.BcLegL = AccessTools.Field(bonesCtrlType, "leg_L");
                info.BcLegR = AccessTools.Field(bonesCtrlType, "leg_R");
                info.BcArmL = AccessTools.Field(bonesCtrlType, "arm_L");
                info.BcArmR = AccessTools.Field(bonesCtrlType, "arm_R");

                // 回避策の要。kPlug 自身のボーン再構築メソッド（public void FindBones()）
                info.BcFindBones = AccessTools.Method(bonesCtrlType, "FindBones");
                if (info.BcFindBones == null)
                {
                    PatcherPlugin.Logger.LogWarning(
                        $"{PatcherPlugin.PluginName}: BonesCtrl.FindBones() が見つかりません。破棄済みボーンの復旧はできません（検出とログのみ動作します）。");
                }

                var legType = bonesCtrlType.GetNestedType("Leg", BindingFlags.Public | BindingFlags.NonPublic);
                if (legType != null)
                {
                    info.LegShin = AccessTools.Field(legType, "shin");
                    info.LegFoot = AccessTools.Field(legType, "foot");
                }
                var armType = bonesCtrlType.GetNestedType("Arm", BindingFlags.Public | BindingFlags.NonPublic);
                if (armType != null)
                {
                    info.ArmHand = AccessTools.Field(armType, "hand");
                }
            }

            return info;
        }

        /// <summary>
        /// AdaptCaliber の直前に、参照されるボーンが破棄済みになっていないか調べ、
        /// 破棄済みがあれば BonesCtrl.FindBones() を呼んで復旧する。
        /// </summary>
        private static void AdaptCaliberPrefix(object __instance)
        {
            if (PatcherPlugin.IsQuitting) return;

            try
            {
                if (!PatcherPlugin.EnableSpotManagerBonesFix.Value) return;
                if (__instance == null) return;

                KPlugTypeInfo info;
                if (!_infoByType.TryGetValue(__instance.GetType(), out info) || info == null) return;

                object hCtrl = SafeGetStatic(info.CoreHCtrl);
                if (hCtrl == null) return;

                object bcG = SafeGet(info.HCtrlBcG, hCtrl);
                object bcB = SafeGet(info.HCtrlBcB, hCtrl);

                RepairIfNeeded(info, bcG, "bcG(メインヒロイン)");
                RepairIfNeeded(info, bcB, "bcB(プレイヤー)");

                if (PatcherPlugin.VerboseSpotManagerDiagnostics.Value)
                {
                    LogFullState(info, __instance, hCtrl, bcG, bcB);
                }
            }
            catch (Exception ex)
            {
                // 回避策が原因で kPlug 側を壊すことだけは避ける
                try { PatcherPlugin.Logger.LogWarning($"[SpotManagerBonesFix] 処理中に例外（招待処理は継続します）: {ex.Message}"); }
                catch { }
            }
        }

        /// <summary>
        /// 破棄済みボーンを検出し、あれば FindBones() で復旧する。
        /// 復旧後に再検査し、直りきらなかった場合は Warning を出す（想定外を検知するため）。
        /// </summary>
        private static void RepairIfNeeded(KPlugTypeInfo info, object bc, string label)
        {
            if (bc == null) return;

            var broken = CollectBroken(info, bc);
            if (broken.Count == 0) return;

            string owner = OwnerName(bc);

            if (info.BcFindBones == null)
            {
                PatcherPlugin.Logger.LogWarning(
                    $"[SpotManagerBonesFix] {label} owner={owner} に破棄済みボーンを検出しましたが、FindBones() が無いため復旧できません: {string.Join(", ", broken.ToArray())}");
                return;
            }

            try
            {
                // kPlug 自身の再構築ロジックを呼ぶ。ParserScene.FindBones は
                // 名前が一致した Transform を代入するだけで既存値をクリアしないため、
                // 破棄済み参照だけが健全な Transform に置き換わる。
                info.BcFindBones.Invoke(bc, null);
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"[SpotManagerBonesFix] {label} owner={owner} の FindBones() 呼び出しに失敗: {ex.Message}");
                return;
            }

            var stillBroken = CollectBroken(info, bc);
            if (stillBroken.Count == 0)
            {
                PatcherPlugin.Logger.LogInfo(
                    $"[SpotManagerBonesFix] {label} owner={owner} の破棄済みボーンを復旧しました: {string.Join(", ", broken.ToArray())}");
            }
            else
            {
                PatcherPlugin.Logger.LogWarning(
                    $"[SpotManagerBonesFix] {label} owner={owner} の復旧を試みましたが未解決が残っています: 復旧前={string.Join(", ", broken.ToArray())} / 復旧後={string.Join(", ", stillBroken.ToArray())}");
            }
        }

        /// <summary>AdaptCaliber が実際に触るボーンのうち、null / 破棄済みのものを列挙する。</summary>
        private static List<string> CollectBroken(KPlugTypeInfo info, object bc)
        {
            var broken = new List<string>();

            CheckBone(broken, "headTop", SafeGet(info.BcHeadTop, bc));
            CheckBone(broken, "spine3", SafeGet(info.BcSpine3, bc));
            CheckLimb(broken, "leg_L", SafeGet(info.BcLegL, bc), info.LegShin, info.LegFoot);
            CheckLimb(broken, "leg_R", SafeGet(info.BcLegR, bc), info.LegShin, info.LegFoot);
            CheckHand(broken, "arm_L", SafeGet(info.BcArmL, bc), info.ArmHand);
            CheckHand(broken, "arm_R", SafeGet(info.BcArmR, bc), info.ArmHand);

            return broken;
        }

        private static void CheckBone(List<string> broken, string name, object v)
        {
            string s = Describe(v);
            if (s != "ok" && s != "?") broken.Add($"{name}={s}");
        }

        private static void CheckLimb(List<string> broken, string name, object limb, FieldInfo shin, FieldInfo foot)
        {
            if (limb == null) return;
            if (shin != null) CheckBone(broken, name + ".shin", SafeGet(shin, limb));
            if (foot != null) CheckBone(broken, name + ".foot", SafeGet(foot, limb));
        }

        private static void CheckHand(List<string> broken, string name, object arm, FieldInfo hand)
        {
            if (arm == null || hand == null) return;
            CheckBone(broken, name + ".hand", SafeGet(hand, arm));
        }

        /// <summary>
        /// AdaptCaliber が例外で終わった場合に記録する。**例外は握りつぶさずそのまま返す**。
        /// 回避策で防げなかったケースを取りこぼさないための保険。
        /// </summary>
        private static Exception AdaptCaliberFinalizer(Exception __exception)
        {
            if (__exception != null && !PatcherPlugin.IsQuitting)
            {
                try
                {
                    PatcherPlugin.Logger.LogWarning(
                        $"[SpotManagerBonesFix] AdaptCaliber が例外で終了しました（回避策では防げていません）: {__exception.GetType().Name}: {__exception.Message}");
                }
                catch { }
            }
            return __exception;
        }

        /// <summary>詳細診断。既定では無効で、原因調査が必要なときだけ有効にする。</summary>
        private static void LogFullState(KPlugTypeInfo info, object instance, object hCtrl, object bcG, object bcB)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("inHresult=").Append(SafeToString(SafeGetStatic(info.CoreInHresult)));
                sb.Append(" hAnim=").Append(SafeToString(SafeGetStatic(info.CoreHAnim)));
                sb.Append(" posMod=").Append(SafeToString(SafeGet(info.HCtrlPosMod, hCtrl)));
                sb.Append(" bcG.owner=").Append(OwnerName(bcG));
                sb.Append(" bcB.owner=").Append(OwnerName(bcB));
                sb.Append(" bcG{").Append(string.Join(",", CollectBroken(info, bcG).ToArray())).Append("}");
                sb.Append(" bcB{").Append(string.Join(",", CollectBroken(info, bcB).ToArray())).Append("}");
                sb.Append(" mainRoot=").Append(Describe(SafeGet(info.SmMainRoot, instance)));
                object calib = SafeGet(info.SmCalib, instance);
                sb.Append(" calib.root=").Append(Describe(SafeGet(info.CalibRoot, calib)));
                sb.Append(" calib.msh=").Append(Describe(SafeGet(info.CalibMsh, calib)));
                sb.Append(" tryPosRefs=").Append(CountOf(SafeGet(info.SmTryPosRefs, instance)));

                PatcherPlugin.Logger.LogInfo($"[SpotManagerBonesFix][詳細] {sb}");
            }
            catch { }
        }

        // ---------- ヘルパー ----------

        private static object SafeGet(FieldInfo field, object target)
        {
            if (field == null || target == null) return null;
            try { return field.GetValue(target); }
            catch { return null; }
        }

        private static object SafeGetStatic(FieldInfo field)
        {
            if (field == null) return null;
            try { return field.GetValue(null); }
            catch { return null; }
        }

        /// <summary>
        /// null と「Unity 上で破棄済み(destroyed)」を区別する。
        /// 両者は原因が全く異なる（前者は最初から存在しない／後者は一度存在したが破棄された）ため、
        /// ログ上でも必ず区別する。
        /// </summary>
        private static string Describe(object v)
        {
            if (v == null) return "null";
            var uo = v as UnityEngine.Object;
            if (!ReferenceEquals(uo, null)) return (uo == null) ? "destroyed" : "ok";
            return "ok"; // Leg / Arm / Caliber のような純粋な C# クラス
        }

        private static string SafeToString(object v)
        {
            try { return v == null ? "null" : v.ToString(); }
            catch { return "?"; }
        }

        /// <summary>BonesCtrl がどの GameObject に付いているか（＝どのキャラのボーンか）。</summary>
        private static string OwnerName(object component)
        {
            try
            {
                var c = component as Component;
                if (ReferenceEquals(c, null) || c == null) return "-";
                return c.gameObject.name;
            }
            catch { return "?"; }
        }

        private static string CountOf(object list)
        {
            try
            {
                var c = list as System.Collections.ICollection;
                return c == null ? "?" : c.Count.ToString();
            }
            catch { return "?"; }
        }
    }
}
