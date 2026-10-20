using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// 調査用診断パッチ。
    ///
    /// 特定のキャラクターカードで衣装変更(CoordinateChanged)が起きた後、kPlugが保持している
    /// GameObject/Transform参照が破棄済み(destroyed)になり、以下3箇所が毎フレームNREを出し続ける。
    ///   - kPlug.Tools.ChaTools+ColDefsManager.LateUpdate()  … gos(List&lt;GameObject&gt;)
    ///   - kPlug.CmpH.BindersCtrl.Update()                    … binders(List&lt;Binder&gt;) の root
    ///   - kPlug.CmpChara.BonesCtrl.LateUpdate()               … root/waist/spine1-3/neck/head/
    ///                                                           hipShaper_L/R/headTop/arm_L/R/leg_L/R/customs
    ///
    /// D-1(SpotManagerBonesFix)と同じ「破棄済み参照」パターンだが発生タイミングが異なり
    /// (AdaptCaliber時点ではまだ健全で、その後のCoordinateChangedで壊れる)、D-1の回避策では救えない。
    ///
    /// 本パッチは診断専用（読み取り専用、値の書き換え・修復は一切行わない）。
    /// 目的は「壊れているのはメインヒロイン側かゲスト側か」「spine3以外にも破棄されているフィールドが
    /// あるか」「gos/binders/customsにも破棄済み要素が溜まっているか」を実機で確認すること。
    ///
    /// 【設計上の注意】
    /// - 3箇所ともUpdate/LateUpdateで毎フレーム呼ばれるため、無条件ログ出力は
    ///   output_log.txtを破裂させる。InviteUIDiagnosticsと同じ「インスタンスごとに直前の
    ///   スナップショットと比較し、変化した時だけ出力する」方式を採る。
    /// - リフレクション情報は型ごとの辞書で持つ（kPlug/kPlugVRの上書き事故を避けるため）。
    /// - 例外は握り潰し、対象プラグインの処理を巻き込んで壊さない。
    /// </summary>
    public static class CoordinateChangeStaleRefDiagnostics
    {
        // ---------- ColDefsManager ----------

        private sealed class ColDefsManagerInfo
        {
            public FieldInfo RelatedCha;
            public FieldInfo Gos;
        }

        private static readonly Dictionary<Type, ColDefsManagerInfo> _colDefsInfoByType = new Dictionary<Type, ColDefsManagerInfo>();
        private static readonly Dictionary<object, string> _colDefsLastSnapshot = new Dictionary<object, string>();

        // ---------- BindersCtrl ----------

        private sealed class BindersCtrlInfo
        {
            public FieldInfo Binders;      // List<Binder>
            public FieldInfo BinderRoot;   // Binder.root (GameObject)
            public FieldInfo BinderRnd;    // Binder.rnd (Renderer)
            public FieldInfo BinderBindSpot; // Binder.bindSpot
        }

        private static readonly Dictionary<Type, BindersCtrlInfo> _bindersInfoByType = new Dictionary<Type, BindersCtrlInfo>();
        private static readonly Dictionary<object, string> _bindersLastSnapshot = new Dictionary<object, string>();

        // ---------- BonesCtrl ----------

        private sealed class BonesCtrlInfo
        {
            public FieldInfo Root, Waist, Spine1, Spine2, Spine3, Neck, Head, HeadTop;
            public FieldInfo HipShaperL, HipShaperR;
            public FieldInfo ArmL, ArmR, LegL, LegR;
            public FieldInfo ArmHand;
            public FieldInfo LegShin, LegFoot;
            public FieldInfo Customs;   // List<Custom>
            public FieldInfo CustomTrs; // Custom.trs (Transform)
        }

        private static readonly Dictionary<Type, BonesCtrlInfo> _bonesInfoByType = new Dictionary<Type, BonesCtrlInfo>();
        private static readonly Dictionary<object, string> _bonesLastSnapshot = new Dictionary<object, string>();

        /// <summary>
        /// root ボーンの「直前の親の名前」をインスタンスごとに記録する。
        /// 衣装ボーンが異常な位置に配置される不具合(2026-08-06 報告、招待キャラで確認)の
        /// 手がかり用。毎フレームの位置変化そのものはログしない（アニメーション中は常に動くため）。
        /// 親が変わった瞬間（＝再ペアレントが起きた瞬間）だけ、その時のローカル座標とあわせて記録する。
        /// </summary>
        private static readonly Dictionary<object, string> _bonesLastRootParent = new Dictionary<object, string>();

        internal static void TryApply(Harmony harmony)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.FullName.Contains("kPlug")) continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                TryPatchColDefsManager(harmony, types);
                TryPatchBindersCtrl(harmony, types);
                TryPatchBonesCtrl(harmony, types);
            }
        }

        // ===================== ColDefsManager =====================

        private static void TryPatchColDefsManager(Harmony harmony, Type[] types)
        {
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith("+ColDefsManager"));
            if (type == null) return;

            try
            {
                var info = new ColDefsManagerInfo
                {
                    RelatedCha = AccessTools.Field(type, "relatedCha"),
                    Gos = AccessTools.Field(type, "gos"),
                };
                if (info.Gos == null)
                {
                    PatcherPlugin.Logger.LogWarning($"[CoordinateChangeStaleRefDiagnostics] {type.FullName}: gos フィールドが見つかりませんでした。");
                    return;
                }
                _colDefsInfoByType[type] = info;

                var method = AccessTools.Method(type, "LateUpdate");
                if (method == null)
                {
                    PatcherPlugin.Logger.LogWarning($"[CoordinateChangeStaleRefDiagnostics] {type.FullName}.LateUpdate() が見つかりませんでした。");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(CoordinateChangeStaleRefDiagnostics).GetMethod(nameof(ColDefsManagerLateUpdatePrefix), BindingFlags.NonPublic | BindingFlags.Static));
                harmony.Patch(method, prefix);
                PatcherPlugin.Logger.LogInfo($"[CoordinateChangeStaleRefDiagnostics] {type.FullName}.LateUpdate() に診断パッチを適用しました。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.LogPatchFailure("[CoordinateChangeStaleRefDiagnostics]", type, ex);
            }
        }

        private static void ColDefsManagerLateUpdatePrefix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                ColDefsManagerInfo info;
                if (!_colDefsInfoByType.TryGetValue(__instance.GetType(), out info) || info == null) return;

                object relatedCha = SafeGet(info.RelatedCha, __instance);
                var gos = SafeGet(info.Gos, __instance) as IEnumerable;
                if (gos == null) return;

                int total = 0, broken = 0;
                foreach (var go in gos)
                {
                    total++;
                    if (Describe(go) == "destroyed") broken++;
                }

                string snapshot = $"owner={OwnerName(relatedCha as Component)}|relatedCha={Describe(relatedCha)}|gos.total={total}|gos.destroyed={broken}";
                LogIfChanged(_colDefsLastSnapshot, __instance, "ColDefsManager", snapshot);
            }
            catch (Exception ex)
            {
                SafeLogError("ColDefsManagerLateUpdatePrefix", ex);
            }
        }

        // ===================== BindersCtrl =====================

        private static void TryPatchBindersCtrl(Harmony harmony, Type[] types)
        {
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".BindersCtrl"));
            if (type == null) return;

            try
            {
                var binderType = type.GetNestedType("Binder", BindingFlags.Public | BindingFlags.NonPublic);
                var info = new BindersCtrlInfo
                {
                    Binders = AccessTools.Field(type, "binders"),
                    BinderRoot = binderType != null ? AccessTools.Field(binderType, "root") : null,
                    BinderRnd = binderType != null ? AccessTools.Field(binderType, "rnd") : null,
                    BinderBindSpot = binderType != null ? AccessTools.Field(binderType, "bindSpot") : null,
                };
                if (info.Binders == null || info.BinderRoot == null)
                {
                    PatcherPlugin.Logger.LogWarning($"[CoordinateChangeStaleRefDiagnostics] {type.FullName}: binders/Binder.root が見つかりませんでした。");
                    return;
                }
                _bindersInfoByType[type] = info;

                var method = AccessTools.Method(type, "Update");
                if (method == null)
                {
                    PatcherPlugin.Logger.LogWarning($"[CoordinateChangeStaleRefDiagnostics] {type.FullName}.Update() が見つかりませんでした。");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(CoordinateChangeStaleRefDiagnostics).GetMethod(nameof(BindersCtrlUpdatePrefix), BindingFlags.NonPublic | BindingFlags.Static));
                harmony.Patch(method, prefix);
                PatcherPlugin.Logger.LogInfo($"[CoordinateChangeStaleRefDiagnostics] {type.FullName}.Update() に診断パッチを適用しました。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.LogPatchFailure("[CoordinateChangeStaleRefDiagnostics]", type, ex);
            }
        }

        private static void BindersCtrlUpdatePrefix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                BindersCtrlInfo info;
                if (!_bindersInfoByType.TryGetValue(__instance.GetType(), out info) || info == null) return;

                var binders = SafeGet(info.Binders, __instance) as IEnumerable;
                if (binders == null) return;

                int total = 0;
                var brokenRoots = new List<string>();
                foreach (var binder in binders)
                {
                    total++;
                    object root = SafeGet(info.BinderRoot, binder);
                    string rootState = Describe(root);
                    if (rootState == "destroyed")
                    {
                        object bindSpot = SafeGet(info.BinderBindSpot, binder);
                        brokenRoots.Add($"root=destroyed(bindSpot={Describe(bindSpot)})");
                    }
                }

                string snapshot = $"binders.total={total}|broken={brokenRoots.Count}" + (brokenRoots.Count > 0 ? $"|{string.Join(",", brokenRoots.ToArray())}" : "");
                LogIfChanged(_bindersLastSnapshot, __instance, "BindersCtrl", snapshot);
            }
            catch (Exception ex)
            {
                SafeLogError("BindersCtrlUpdatePrefix", ex);
            }
        }

        // ===================== BonesCtrl =====================

        private static void TryPatchBonesCtrl(Harmony harmony, Type[] types)
        {
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".BonesCtrl"));
            if (type == null) return;

            try
            {
                var legType = type.GetNestedType("Leg", BindingFlags.Public | BindingFlags.NonPublic);
                var armType = type.GetNestedType("Arm", BindingFlags.Public | BindingFlags.NonPublic);
                var customType = type.GetNestedType("Custom", BindingFlags.Public | BindingFlags.NonPublic);

                var info = new BonesCtrlInfo
                {
                    Root = AccessTools.Field(type, "root"),
                    Waist = AccessTools.Field(type, "waist"),
                    Spine1 = AccessTools.Field(type, "spine1"),
                    Spine2 = AccessTools.Field(type, "spine2"),
                    Spine3 = AccessTools.Field(type, "spine3"),
                    Neck = AccessTools.Field(type, "neck"),
                    Head = AccessTools.Field(type, "head"),
                    HeadTop = AccessTools.Field(type, "headTop"),
                    HipShaperL = AccessTools.Field(type, "hipShaper_L"),
                    HipShaperR = AccessTools.Field(type, "hipShaper_R"),
                    ArmL = AccessTools.Field(type, "arm_L"),
                    ArmR = AccessTools.Field(type, "arm_R"),
                    LegL = AccessTools.Field(type, "leg_L"),
                    LegR = AccessTools.Field(type, "leg_R"),
                    ArmHand = armType != null ? AccessTools.Field(armType, "hand") : null,
                    LegShin = legType != null ? AccessTools.Field(legType, "shin") : null,
                    LegFoot = legType != null ? AccessTools.Field(legType, "foot") : null,
                    Customs = AccessTools.Field(type, "customs"),
                    CustomTrs = customType != null ? AccessTools.Field(customType, "trs") : null,
                };
                _bonesInfoByType[type] = info;

                var method = AccessTools.Method(type, "LateUpdate");
                if (method == null)
                {
                    PatcherPlugin.Logger.LogWarning($"[CoordinateChangeStaleRefDiagnostics] {type.FullName}.LateUpdate() が見つかりませんでした。");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(CoordinateChangeStaleRefDiagnostics).GetMethod(nameof(BonesCtrlLateUpdatePrefix), BindingFlags.NonPublic | BindingFlags.Static));
                harmony.Patch(method, prefix);
                PatcherPlugin.Logger.LogInfo($"[CoordinateChangeStaleRefDiagnostics] {type.FullName}.LateUpdate() に診断パッチを適用しました。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.LogPatchFailure("[CoordinateChangeStaleRefDiagnostics]", type, ex);
            }
        }

        private static void BonesCtrlLateUpdatePrefix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                BonesCtrlInfo info;
                if (!_bonesInfoByType.TryGetValue(__instance.GetType(), out info) || info == null) return;

                var broken = new List<string>();
                CheckSimple(broken, info, "root", info.Root, __instance);
                CheckSimple(broken, info, "waist", info.Waist, __instance);
                CheckSimple(broken, info, "spine1", info.Spine1, __instance);
                CheckSimple(broken, info, "spine2", info.Spine2, __instance);
                CheckSimple(broken, info, "spine3", info.Spine3, __instance);
                CheckSimple(broken, info, "neck", info.Neck, __instance);
                CheckSimple(broken, info, "head", info.Head, __instance);
                CheckSimple(broken, info, "headTop", info.HeadTop, __instance);
                CheckSimple(broken, info, "hipShaper_L", info.HipShaperL, __instance);
                CheckSimple(broken, info, "hipShaper_R", info.HipShaperR, __instance);
                CheckLimb(broken, "leg_L", SafeGet(info.LegL, __instance), info.LegShin, info.LegFoot);
                CheckLimb(broken, "leg_R", SafeGet(info.LegR, __instance), info.LegShin, info.LegFoot);
                CheckHand(broken, "arm_L", SafeGet(info.ArmL, __instance), info.ArmHand);
                CheckHand(broken, "arm_R", SafeGet(info.ArmR, __instance), info.ArmHand);

                int customsTotal = 0, customsBroken = 0;
                var customs = SafeGet(info.Customs, __instance) as IEnumerable;
                if (customs != null && info.CustomTrs != null)
                {
                    foreach (var custom in customs)
                    {
                        customsTotal++;
                        object trs = SafeGet(info.CustomTrs, custom);
                        if (Describe(trs) == "destroyed") customsBroken++;
                    }
                }

                string owner = OwnerName(__instance as Component);
                string snapshot = $"owner={owner}|{string.Join(",", broken.ToArray())}|customs.total={customsTotal}|customs.destroyed={customsBroken}";
                LogIfChanged(_bonesLastSnapshot, __instance, "BonesCtrl", snapshot);

                CheckRootReparent(info, __instance, owner);
            }
            catch (Exception ex)
            {
                SafeLogError("BonesCtrlLateUpdatePrefix", ex);
            }
        }

        private static void CheckSimple(List<string> broken, BonesCtrlInfo info, string name, FieldInfo field, object instance)
        {
            if (field == null) return;
            string s = Describe(SafeGet(field, instance));
            if (s != "ok") broken.Add($"{name}={s}");
        }

        private static void CheckLimb(List<string> broken, string name, object limb, FieldInfo shin, FieldInfo foot)
        {
            if (limb == null) return;
            if (shin != null)
            {
                string s = Describe(SafeGet(shin, limb));
                if (s != "ok") broken.Add($"{name}.shin={s}");
            }
            if (foot != null)
            {
                string s = Describe(SafeGet(foot, limb));
                if (s != "ok") broken.Add($"{name}.foot={s}");
            }
        }

        /// <summary>
        /// root ボーンの親（Transform.parent）が前回検査時から変わっていれば、
        /// 変化後の親の名前・ローカル座標・ワールド座標をログに残す。
        /// 「衣装ボーンが異常な位置に配置される」不具合の手がかり用（読み取り専用）。
        /// </summary>
        private static void CheckRootReparent(BonesCtrlInfo info, object instance, string owner)
        {
            if (info.Root == null) return;
            object rootObj = SafeGet(info.Root, instance);
            var root = rootObj as Transform;
            if (root == null) return; // null または破棄済み（破棄済みは既に broken 側で報告済み）

            Transform parent = root.parent;
            string parentName = parent == null ? "(なし=ルート直下)" : parent.name;

            string last;
            _bonesLastRootParent.TryGetValue(instance, out last);
            if (parentName == last) return;
            _bonesLastRootParent[instance] = parentName;

            if (last == null) return; // 初回検査時は「変化」ではないので記録のみでログは出さない

            string localPos = SafeVector3(root.localPosition);
            string worldPos = SafeVector3(root.position);
            PatcherPlugin.Logger.LogInfo(
                $"[CoordinateChangeStaleRefDiagnostics][BonesCtrl.root再ペアレント] owner={owner} 親: {last} -> {parentName}"
                + $" | localPosition={localPos} position={worldPos}");
        }

        private static string SafeVector3(Vector3 v)
        {
            try { return $"({v.x:F3},{v.y:F3},{v.z:F3})"; }
            catch { return "?"; }
        }

        private static void CheckHand(List<string> broken, string name, object arm, FieldInfo hand)
        {
            if (arm == null || hand == null) return;
            string s = Describe(SafeGet(hand, arm));
            if (s != "ok") broken.Add($"{name}.hand={s}");
        }

        // ===================== 共通ヘルパー =====================

        /// <summary>直前のスナップショットと比較し、変化があった時だけログ出力する（インスタンスごと）。</summary>
        private static void LogIfChanged(Dictionary<object, string> lastByInstance, object instance, string label, string snapshot)
        {
            string last;
            lastByInstance.TryGetValue(instance, out last);
            if (snapshot == last) return;
            lastByInstance[instance] = snapshot;
            PatcherPlugin.Logger.LogInfo($"[CoordinateChangeStaleRefDiagnostics][{label}] 状態変化: {snapshot}");
        }

        private static object SafeGet(FieldInfo field, object target)
        {
            if (field == null || target == null) return null;
            try { return field.GetValue(target); }
            catch { return null; }
        }

        /// <summary>null と Unity上で破棄済み(destroyed)を区別する。SpotManagerBonesFixと同じ方式。</summary>
        private static string Describe(object v)
        {
            if (v == null) return "null";
            var uo = v as UnityEngine.Object;
            if (!ReferenceEquals(uo, null)) return (uo == null) ? "destroyed" : "ok";
            return "ok";
        }

        private static string OwnerName(Component component)
        {
            try
            {
                if (ReferenceEquals(component, null) || component == null) return "-";
                return component.gameObject.name;
            }
            catch { return "?"; }
        }

        private static void SafeLogError(string where, Exception ex)
        {
            try { PatcherPlugin.Logger.LogError($"[CoordinateChangeStaleRefDiagnostics] {where} Error: {ex.Message}"); }
            catch { }
        }
    }
}
