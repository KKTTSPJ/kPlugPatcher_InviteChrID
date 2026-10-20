using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// 衣装変更(CoordinateChanged)後に kPlug が破棄済み参照を掴み続け、毎フレーム
    /// NullReferenceException を出し続ける問題の回避策。
    ///
    /// 【不具合のメカニズム（2026-08-06 の実機ログで確定済み）】
    ///   キャラクターの衣装が再構築されると、そのキャラのボーン階層および当たり判定用
    ///   GameObject が Unity 上で破棄される。しかし kPlug 側は破棄されたことを検知せず、
    ///   古い参照を保持したまま毎フレーム参照し続けるため NRE が永続化する。
    ///   実機ログでは「CoordinateChanged を起こした本人の参照が壊れる」という
    ///   1対1の対応が確認されている（ゲストの衣装変更→ゲスト分の binder が壊れ、
    ///   メインヒロインの衣装変更→メインヒロインの ColDefsManager / BonesCtrl が壊れる）。
    ///
    ///   | 対象                              | 壊れるもの                                      |
    ///   |-----------------------------------|-------------------------------------------------|
    ///   | ChaTools+ColDefsManager.LateUpdate | gos(List&lt;GameObject&gt;) の要素               |
    ///   | BindersCtrl.Update                 | binders(List&lt;Binder&gt;) の各 Binder.root     |
    ///   | BonesCtrl.LateUpdate               | root/waist/spine1-3/neck/head 等の Transform     |
    ///   | BellyCtrl.LateUpdate→LerpInflation | waist2/waist1/spine1/spine2 の Bone.t（v1.10.7） |
    ///
    ///   BellyCtrl はキャラ交代（AI_Public.IE_SwapGirls の Reload）で壊れる。2026-10-19 の kPlugVR の
    ///   ログで毎フレーム 2,376 件。再取得メソッドが無いため kPlug の ChaTools.FindInCha() で t だけ取り直す。
    ///
    /// 【D-1（SpotManagerBonesFix）との違い】
    ///   D-1 は招待直後の SpotManager.AdaptCaliber 時点での破棄を修復するもので、
    ///   本件はその後の CoordinateChanged で壊れるためタイミング的に間に合わない。
    ///   両者は独立した回避策として併存させる必要がある。
    ///
    /// 【回避策】
    ///   - gos / binders / customs … kPlug 側に破棄済み要素を取り除く経路が無いため、
    ///     Prefix で破棄済み要素をリストから除去する（生きている要素には触らない）。
    ///   - BonesCtrl の Transform 群 … D-1 と同じく kPlug 自身の再構築メソッド
    ///     BonesCtrl.FindBones() を呼び直して取り直す（独自のボーン探索は書かない）。
    ///
    /// 【設計上の注意】
    /// - **null と destroyed を厳密に区別する。** 最初から未設定(null)のフィールドは正常であり
    ///   修復対象ではない。「一度取得したが破棄された(destroyed)」場合のみ修復する。
    /// - **毎フレーム走るメソッドが対象のためコストを抑える。** 破棄検出のうち、実機で実際に
    ///   壊れることが確認された中核フィールドのみ毎フレーム検査し、腕・脚・指などの
    ///   周辺フィールドは <see cref="DetectionIntervalFrames"/> フレームおきに検査する。
    /// - **FindBones() の呼び出しには回数制限とクールダウンを設ける。** FindBones は全 Transform を
    ///   総なめするため重く、破棄が解消しない状況で毎フレーム呼ぶと実質フリーズする。
    ///   クールダウンは直らなかったときだけ効かせ、直ったとき・健全に戻ったときは戻す。復旧の直後にまた壊れたとき
    ///   （短い間隔で続けて着替えたとき）に待つと、その間ずっと NRE が出るため（2026-10-20 のログで 37 件・59 件×5。1.11.1 / 1.11.2）。
    /// - リフレクション情報は型ごとの辞書で持つ（kPlug と kPlugVR で上書きし合う事故を防ぐ）。
    /// - Prefix は例外を外へ出さない。回避策が原因で kPlug 側を壊すことだけは避ける。
    /// </summary>
    internal static class CoordinateChangeStaleRefFix
    {
        /// <summary>周辺フィールド（腕・脚・指など）を検査する間隔。中核フィールドは毎フレーム検査する。</summary>
        private const int DetectionIntervalFrames = 10;

        /// <summary>
        /// 復旧を試みても直らなかったとき、次に FindBones() を呼ぶまでの最小フレーム数。
        /// 直ったとき・健全に戻ったときは待ちを戻す（復旧の後にまた壊れたときは待たない。1.11.1 / 1.11.2）。
        /// </summary>
        private const int RepairCooldownFrames = 60;

        /// <summary>同一インスタンスに対する FindBones() の最大試行回数。超えたら諦めて警告を出す。</summary>
        private const int MaxRepairAttempts = 3;

        // ---------- 参照等価で比較する辞書（Unity の == オーバーロードを避けるため） ----------

        /// <summary>
        /// UnityEngine.Object は == / Equals が「破棄済みなら null と等しい」と振る舞うようオーバーロード
        /// されている。インスタンスをキーにする辞書でそれを使うと破棄済みオブジェクトの扱いが壊れるため、
        /// 参照等価（ReferenceEquals）で比較する。
        /// </summary>
        private sealed class RefComparer : IEqualityComparer<object>
        {
            internal static readonly RefComparer Instance = new RefComparer();
            bool IEqualityComparer<object>.Equals(object x, object y) { return ReferenceEquals(x, y); }
            int IEqualityComparer<object>.GetHashCode(object obj) { return RuntimeHelpers.GetHashCode(obj); }
        }

        // ---------- ColDefsManager ----------

        private sealed class ColDefsInfo
        {
            public FieldInfo Gos;
            public FieldInfo RelatedCha;
        }

        private static readonly Dictionary<Type, ColDefsInfo> _colDefsInfoByType = new Dictionary<Type, ColDefsInfo>();

        // ---------- BindersCtrl ----------

        private sealed class BindersInfo
        {
            public FieldInfo Binders;
            public FieldInfo BinderRoot;
        }

        private static readonly Dictionary<Type, BindersInfo> _bindersInfoByType = new Dictionary<Type, BindersInfo>();

        // ---------- BonesCtrl ----------

        /// <summary>フィールド名とアクセサをまとめたもの（ログに名前を出すため名前を保持する）。</summary>
        private sealed class NamedField
        {
            public string Name;
            public FieldInfo Field;
        }

        private sealed class BonesInfo
        {
            /// <summary>LateUpdate が直接参照する Transform 群。実機で破棄が確認された中核。毎フレーム検査する。</summary>
            public NamedField[] CoreTransforms;

            /// <summary>BonesCtrl 直下のその他 Transform（headTop など）。間引いて検査する。</summary>
            public NamedField[] OtherTransforms;

            /// <summary>arm_L / arm_R / leg_L / leg_R / fingers_L / fingers_R とその中の Transform 群。</summary>
            public NamedField[] LimbHolders;
            public Dictionary<Type, NamedField[]> LimbTransformsByType;

            public FieldInfo Customs;
            public FieldInfo CustomTrs;
            public MethodInfo FindBones;
        }

        /// <summary>インスタンスごとの修復状態。無制限に FindBones を呼ばないための管理。</summary>
        private sealed class BonesState
        {
            public int NextFullCheckFrame;

            /// <summary>
            /// 次に FindBones() を呼んでよいフレーム。0 初期値なので初回は即座に修復できる。
            /// 「最後に修復したフレーム」を持って毎回引き算する方式にすると、初期値を
            /// int.MinValue にした場合に Time.frameCount との差が int を溢れて負になり、
            /// 常にクールダウン中と誤判定されて一度も修復されない（実際に踏んだ不具合）。
            /// </summary>
            public int NextRepairFrame;

            public int Attempts;
            public bool GaveUp;
        }

        private static readonly Dictionary<Type, BonesInfo> _bonesInfoByType = new Dictionary<Type, BonesInfo>();
        private static readonly Dictionary<object, BonesState> _bonesStateByInstance = new Dictionary<object, BonesState>(RefComparer.Instance);

        /// <summary>
        /// 毎フレーム検査する Transform フィールド名。
        /// root〜hipShaper_R は BonesCtrl.LateUpdate が直接デリファレンスするもの
        /// （null を確かめずに使う）。
        /// nose / eyeBridge は BonesCtrl 自身ではなく他コンポーネントが毎フレーム参照する。
        /// FaceCtrl.LateUpdate の `ai.bc.nose.position` と AI_Public.IE_CalcDistFromCam の
        /// `bc.nose.transform.position` がそれで、ここを間引き検査（DetectionIntervalFrames）に
        /// 置くと、破棄されてから復旧するまでの最大10フレームのあいだ毎フレーム NRE が出る
        /// </summary>
        private static readonly string[] CoreTransformNames =
        {
            "root", "waist", "spine1", "spine2", "spine3", "neck", "head", "hipShaper_L", "hipShaper_R",
            "nose", "eyeBridge",
        };

        private static readonly string[] LimbHolderNames =
        {
            "arm_L", "arm_R", "leg_L", "leg_R", "fingers_L", "fingers_R",
        };

        // ---------- BellyCtrl（v1.10.7） ----------

        /// <summary>
        /// BellyCtrl の Bone フィールド名と、kPlug が IE_Init() の GetBone() で探す骨の名前。
        /// kPlug / kPlugVR 3.5.6・3.6.3 で同じ。
        /// </summary>
        private static readonly string[][] BellyBoneNames =
        {
            new[] { "waist2", "cf_s_waist02" },
            new[] { "waist1", "cf_s_waist01" },
            new[] { "spine1", "cf_s_spine01" },
            new[] { "spine2", "cf_s_spine02" },
        };

        private sealed class BellyBoneField
        {
            public string Name;
            public string BoneName;
            public FieldInfo Field;
        }

        private sealed class BellyInfo
        {
            public BellyBoneField[] Bones;
            public FieldInfo BoneT;
            public FieldInfo Cha;
            public MethodInfo FindInCha;
        }

        private static readonly Dictionary<Type, BellyInfo> _bellyInfoByType = new Dictionary<Type, BellyInfo>();

        /// <summary>Bone 型（kPlug / kPlugVR で別の型）ごとの Bone.t。LerpInflation の Prefix で使う。</summary>
        private static readonly Dictionary<Type, FieldInfo> _bellyBoneTByType = new Dictionary<Type, FieldInfo>();

        private static readonly Dictionary<object, BonesState> _bellyStateByInstance = new Dictionary<object, BonesState>(RefComparer.Instance);

        /// <summary>LerpInflation を空振りさせた回数。最初の1回と <see cref="BellySkipLogInterval"/> 回ごとにログを出す。</summary>
        private static int _bellySkipCount;
        private const int BellySkipLogInterval = 1000;

        // ===================== 適用 =====================

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
                if (PatcherPlugin.EnableBellyCtrlStaleBoneFix.Value) TryPatchBellyCtrl(harmony, types);
            }
        }

        /// <summary>
        /// 診断パッチ(CoordinateChangeStaleRefDiagnostics)が「修復前の壊れた状態」を観測できるよう、
        /// 修復用 Prefix は他の Prefix より後に走らせる。
        /// </summary>
        private static HarmonyMethod MakePrefix(string methodName)
        {
            var m = typeof(CoordinateChangeStaleRefFix).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            return new HarmonyMethod(m) { priority = Priority.Last };
        }

        private static void TryPatchColDefsManager(Harmony harmony, Type[] types)
        {
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith("+ColDefsManager"));
            if (type == null) return;

            try
            {
                var info = new ColDefsInfo
                {
                    Gos = AccessTools.Field(type, "gos"),
                    RelatedCha = AccessTools.Field(type, "relatedCha"),
                };
                if (info.Gos == null)
                {
                    PatcherPlugin.Logger.LogWarning($"[StaleRefFix] {type.FullName}: gos が見つからないため回避策を適用できません。");
                    return;
                }

                var method = AccessTools.Method(type, "LateUpdate");
                if (method == null)
                {
                    PatcherPlugin.Logger.LogWarning($"[StaleRefFix] {type.FullName}.LateUpdate() が見つかりませんでした。");
                    return;
                }

                _colDefsInfoByType[type] = info;
                harmony.Patch(method, MakePrefix(nameof(ColDefsManagerLateUpdatePrefix)));
                PatcherPlugin.Logger.LogInfo($"[StaleRefFix] {type.FullName}.LateUpdate() に回避策を適用しました。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.LogPatchFailure("[StaleRefFix]", type, ex);
            }
        }

        private static void TryPatchBindersCtrl(Harmony harmony, Type[] types)
        {
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".BindersCtrl"));
            if (type == null) return;

            try
            {
                var binderType = type.GetNestedType("Binder", BindingFlags.Public | BindingFlags.NonPublic);
                var info = new BindersInfo
                {
                    Binders = AccessTools.Field(type, "binders"),
                    BinderRoot = binderType != null ? AccessTools.Field(binderType, "root") : null,
                };
                if (info.Binders == null || info.BinderRoot == null)
                {
                    PatcherPlugin.Logger.LogWarning($"[StaleRefFix] {type.FullName}: binders / Binder.root が見つからないため回避策を適用できません。");
                    return;
                }

                var method = AccessTools.Method(type, "Update");
                if (method == null)
                {
                    PatcherPlugin.Logger.LogWarning($"[StaleRefFix] {type.FullName}.Update() が見つかりませんでした。");
                    return;
                }

                _bindersInfoByType[type] = info;
                harmony.Patch(method, MakePrefix(nameof(BindersCtrlUpdatePrefix)));
                PatcherPlugin.Logger.LogInfo($"[StaleRefFix] {type.FullName}.Update() に回避策を適用しました。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.LogPatchFailure("[StaleRefFix]", type, ex);
            }
        }

        private static void TryPatchBonesCtrl(Harmony harmony, Type[] types)
        {
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".BonesCtrl"));
            if (type == null) return;

            try
            {
                var info = BuildBonesInfo(type);
                if (info.FindBones == null)
                {
                    PatcherPlugin.Logger.LogWarning(
                        $"[StaleRefFix] {type.FullName}.FindBones() が見つかりません。ボーン参照の復旧はできません。");
                    return;
                }

                var method = AccessTools.Method(type, "LateUpdate");
                if (method == null)
                {
                    PatcherPlugin.Logger.LogWarning($"[StaleRefFix] {type.FullName}.LateUpdate() が見つかりませんでした。");
                    return;
                }

                _bonesInfoByType[type] = info;
                harmony.Patch(method, MakePrefix(nameof(BonesCtrlLateUpdatePrefix)));
                PatcherPlugin.Logger.LogInfo($"[StaleRefFix] {type.FullName}.LateUpdate() に回避策を適用しました。");
            }
            catch (Exception ex)
            {
                // kPlugVR 側は LateUpdate へのパッチで Harmony が IL 生成に失敗することが確認されている。
                // 非VR側だけでも動作させたいので、ここで止めずに記録だけ残す。
                PatcherPlugin.LogPatchFailure("[StaleRefFix]", type, ex);
            }
        }

        private static void TryPatchBellyCtrl(Harmony harmony, Type[] types)
        {
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".CmpChara.BellyCtrl"));
            if (type == null) return;

            try
            {
                var boneType = type.GetNestedType("Bone", BindingFlags.Public | BindingFlags.NonPublic);
                var chaTools = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".Tools.ChaTools"));
                var info = new BellyInfo
                {
                    Bones = BellyBoneNames
                        .Select(p => new BellyBoneField { Name = p[0], BoneName = p[1], Field = AccessTools.Field(type, p[0]) })
                        .ToArray(),
                    BoneT = boneType != null ? AccessTools.Field(boneType, "t") : null,
                    Cha = AccessTools.Field(type, "cha"),
                    FindInCha = chaTools != null ? AccessTools.Method(chaTools, "FindInCha") : null,
                };
                if (info.Bones.Any(b => b.Field == null) || info.BoneT == null || info.Cha == null || info.FindInCha == null)
                {
                    PatcherPlugin.Logger.LogWarning(
                        $"[StaleRefFix] {type.FullName}: Bone フィールド / Bone.t / cha / ChaTools.FindInCha のいずれかが見つからないため回避策を適用できません。");
                    return;
                }

                var lateUpdate = AccessTools.Method(type, "LateUpdate");
                var lerp = AccessTools.Method(type, "LerpInflation");
                if (lateUpdate == null || lerp == null)
                {
                    PatcherPlugin.Logger.LogWarning($"[StaleRefFix] {type.FullName}.LateUpdate() / LerpInflation() が見つかりませんでした。");
                    return;
                }

                _bellyInfoByType[type] = info;
                _bellyBoneTByType[boneType] = info.BoneT;
                harmony.Patch(lateUpdate, MakePrefix(nameof(BellyCtrlLateUpdatePrefix)));
                harmony.Patch(lerp, MakePrefix(nameof(BellyCtrlLerpInflationPrefix)));
                PatcherPlugin.Logger.LogInfo($"[StaleRefFix] {type.FullName}.LateUpdate() / LerpInflation() に回避策を適用しました。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.LogPatchFailure("[StaleRefFix]", type, ex);
            }
        }

        private static BonesInfo BuildBonesInfo(Type bonesCtrlType)
        {
            var info = new BonesInfo();
            var transformType = typeof(Transform);

            var allTransformFields = bonesCtrlType
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => f.FieldType == transformType)
                .Select(f => new NamedField { Name = f.Name, Field = f })
                .ToArray();

            info.CoreTransforms = allTransformFields.Where(nf => CoreTransformNames.Contains(nf.Name)).ToArray();
            info.OtherTransforms = allTransformFields.Where(nf => !CoreTransformNames.Contains(nf.Name)).ToArray();

            // arm_L / leg_R / fingers_L … の入れ物と、その型ごとの Transform フィールド一覧
            info.LimbHolders = LimbHolderNames
                .Select(n => new NamedField { Name = n, Field = AccessTools.Field(bonesCtrlType, n) })
                .Where(nf => nf.Field != null)
                .ToArray();

            info.LimbTransformsByType = new Dictionary<Type, NamedField[]>();
            foreach (var holder in info.LimbHolders)
            {
                var t = holder.Field.FieldType;
                if (info.LimbTransformsByType.ContainsKey(t)) continue;
                info.LimbTransformsByType[t] = t
                    .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(f => f.FieldType == transformType)
                    .Select(f => new NamedField { Name = f.Name, Field = f })
                    .ToArray();
            }

            info.Customs = AccessTools.Field(bonesCtrlType, "customs");
            var customType = bonesCtrlType.GetNestedType("Custom", BindingFlags.Public | BindingFlags.NonPublic);
            info.CustomTrs = customType != null ? AccessTools.Field(customType, "trs") : null;

            info.FindBones = AccessTools.Method(bonesCtrlType, "FindBones");

            return info;
        }

        // ===================== ColDefsManager =====================

        /// <summary>
        /// gos から破棄済み／null の要素を取り除く。
        /// kPlug 側に取り除く経路が無く、積まれる一方であるため、ここで掃除する。
        /// </summary>
        private static void ColDefsManagerLateUpdatePrefix(object __instance)
        {
            if (PatcherPlugin.IsQuitting) return;

            try
            {
                if (!PatcherPlugin.EnableCoordinateChangeStaleRefFix.Value) return;
                if (__instance == null) return;

                ColDefsInfo info;
                if (!_colDefsInfoByType.TryGetValue(__instance.GetType(), out info) || info == null) return;

                var list = SafeGet(info.Gos, __instance) as IList;
                if (list == null) return;

                int removed = RemoveDeadElements(list, null);
                if (removed > 0)
                {
                    string owner = OwnerName(SafeGet(info.RelatedCha, __instance) as Component);
                    PatcherPlugin.Logger.LogInfo(
                        $"[StaleRefFix][ColDefsManager] owner={owner} の破棄済み当たり判定オブジェクトを {removed} 件除去しました（残り {list.Count} 件）。");
                }
            }
            catch (Exception ex)
            {
                SafeLogWarning("ColDefsManager", ex);
            }
        }

        // ===================== BindersCtrl =====================

        /// <summary>
        /// binders から root が破棄済み／null の要素を取り除く。
        /// root が破棄されている Binder は既に実体を失っており、保持し続ける意味が無い。
        /// </summary>
        private static void BindersCtrlUpdatePrefix(object __instance)
        {
            if (PatcherPlugin.IsQuitting) return;

            try
            {
                if (!PatcherPlugin.EnableCoordinateChangeStaleRefFix.Value) return;
                if (__instance == null) return;

                BindersInfo info;
                if (!_bindersInfoByType.TryGetValue(__instance.GetType(), out info) || info == null) return;

                var list = SafeGet(info.Binders, __instance) as IList;
                if (list == null) return;

                int removed = RemoveDeadElements(list, info.BinderRoot);
                if (removed > 0)
                {
                    PatcherPlugin.Logger.LogInfo(
                        $"[StaleRefFix][BindersCtrl] root が破棄済みの Binder を {removed} 件除去しました（残り {list.Count} 件）。");
                }
            }
            catch (Exception ex)
            {
                SafeLogWarning("BindersCtrl", ex);
            }
        }

        // ===================== BonesCtrl =====================

        /// <summary>
        /// 破棄済みボーン参照を検出し、kPlug 自身の FindBones() を呼び直して復旧する。
        /// あわせて customs から破棄済み要素を除去する。
        /// </summary>
        private static void BonesCtrlLateUpdatePrefix(object __instance)
        {
            if (PatcherPlugin.IsQuitting) return;

            try
            {
                if (!PatcherPlugin.EnableCoordinateChangeStaleRefFix.Value) return;
                if (__instance == null) return;

                BonesInfo info;
                if (!_bonesInfoByType.TryGetValue(__instance.GetType(), out info) || info == null) return;

                // customs は FindBones の対象外（kPlug が動的に積むもの）なので個別に掃除する
                if (info.Customs != null && info.CustomTrs != null)
                {
                    var customs = SafeGet(info.Customs, __instance) as IList;
                    if (customs != null)
                    {
                        int removedCustoms = RemoveDeadElements(customs, info.CustomTrs);
                        if (removedCustoms > 0)
                        {
                            PatcherPlugin.Logger.LogInfo(
                                $"[StaleRefFix][BonesCtrl] owner={OwnerName(__instance as Component)} の破棄済み customs を {removedCustoms} 件除去しました（残り {customs.Count} 件）。");
                        }
                    }
                }

                BonesState state;
                if (!_bonesStateByInstance.TryGetValue(__instance, out state))
                {
                    state = new BonesState();
                    _bonesStateByInstance[__instance] = state;
                }

                int frame = Time.frameCount;
                bool fullCheck = frame >= state.NextFullCheckFrame;
                if (fullCheck) state.NextFullCheckFrame = frame + DetectionIntervalFrames;

                var destroyed = CollectDestroyed(info, __instance, fullCheck);

                if (destroyed.Count == 0)
                {
                    // 健全に戻ったら、次に壊れたときのためリセットする。待ち（NextRepairFrame）も戻し、
                    // 復旧の直後にまた壊れたとき（短い間隔で続けて着替えたとき）は待たずに直す（1.11.1）。
                    // 待ちは、FindBones を呼んでも直らないときの連打を防ぐためだけに使う。
                    state.Attempts = 0;
                    state.GaveUp = false;
                    state.NextRepairFrame = 0;
                    return;
                }

                if (state.GaveUp) return;
                if (frame < state.NextRepairFrame) return;

                state.NextRepairFrame = frame + RepairCooldownFrames;
                state.Attempts++;

                string owner = OwnerName(__instance as Component);

                try
                {
                    // kPlug 自身の再構築ロジック。名前が一致した Transform を代入するだけで
                    // 既存値をクリアしないため、破棄済み参照だけが健全な Transform に置き換わる。
                    info.FindBones.Invoke(__instance, null);
                }
                catch (Exception ex)
                {
                    state.GaveUp = true;
                    PatcherPlugin.Logger.LogError(
                        $"[StaleRefFix][BonesCtrl] owner={owner} の FindBones() 呼び出しに失敗したため、このインスタンスの復旧を中止します: {ex.Message}");
                    return;
                }

                var stillDestroyed = CollectDestroyed(info, __instance, fullCheck: true);
                if (stillDestroyed.Count == 0)
                {
                    // 直ったら待ちもここで戻す。復旧した次のフレームでもう次の着替えが走ると、健全な分岐を
                    // 一度も通らずにまた壊れるため、健全な分岐で戻すだけでは 60 フレーム待ってしまう（1.11.2）
                    state.Attempts = 0;
                    state.NextRepairFrame = 0;
                    PatcherPlugin.Logger.LogInfo(
                        $"[StaleRefFix][BonesCtrl] owner={owner} の破棄済みボーンを復旧しました: {string.Join(", ", destroyed.ToArray())}");
                }
                else if (state.Attempts >= MaxRepairAttempts)
                {
                    state.GaveUp = true;
                    PatcherPlugin.Logger.LogWarning(
                        $"[StaleRefFix][BonesCtrl] owner={owner} の復旧に {state.Attempts} 回失敗したため中止します（毎フレームの FindBones 呼び出しを避けるため）。未解決: {string.Join(", ", stillDestroyed.ToArray())}");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning(
                        $"[StaleRefFix][BonesCtrl] owner={owner} の復旧を試みましたが未解決が残っています（{state.Attempts}/{MaxRepairAttempts} 回目）: {string.Join(", ", stillDestroyed.ToArray())}");
                }
            }
            catch (Exception ex)
            {
                SafeLogWarning("BonesCtrl", ex);
            }
        }

        /// <summary>
        /// 破棄済み(destroyed)の Transform を列挙する。**null は対象外**（最初から未設定なだけで正常）。
        /// fullCheck が false のときは中核フィールドのみを見て負荷を抑える。
        /// </summary>
        private static List<string> CollectDestroyed(BonesInfo info, object instance, bool fullCheck)
        {
            var result = new List<string>();

            foreach (var nf in info.CoreTransforms)
            {
                if (IsDestroyed(SafeGet(nf.Field, instance))) result.Add(nf.Name);
            }

            if (!fullCheck) return result;

            foreach (var nf in info.OtherTransforms)
            {
                if (IsDestroyed(SafeGet(nf.Field, instance))) result.Add(nf.Name);
            }

            foreach (var holder in info.LimbHolders)
            {
                object limb = SafeGet(holder.Field, instance);
                if (limb == null) continue;

                NamedField[] fields;
                if (!info.LimbTransformsByType.TryGetValue(holder.Field.FieldType, out fields)) continue;

                foreach (var nf in fields)
                {
                    if (IsDestroyed(SafeGet(nf.Field, limb))) result.Add(holder.Name + "." + nf.Name);
                }
            }

            return result;
        }

        // ===================== BellyCtrl（v1.10.7） =====================

        /// <summary>
        /// BellyCtrl の4本の Bone.t のうち破棄済みのものを、kPlug 自身の ChaTools.FindInCha() で探し直して差し替える。
        ///
        /// kPlug のキャラ交代（AI_Public.IE_SwapGirls）は2人を Reload / ChangeCoordinateTypeAndReload して
        /// 骨の階層を作り直すが、BellyCtrl は IE_Init() で一度だけ骨を取得し、取り直す処理を持たない。
        /// 直後の SwapBellyValues() は基準値（locPos / locRot / locScl）だけを入れ替えるので、
        /// ここでも差し替えるのは t だけにする（基準値は入れ替え後の値のまま残す）。
        /// </summary>
        private static void BellyCtrlLateUpdatePrefix(object __instance)
        {
            if (PatcherPlugin.IsQuitting) return;

            try
            {
                if (!PatcherPlugin.EnableCoordinateChangeStaleRefFix.Value || !PatcherPlugin.EnableBellyCtrlStaleBoneFix.Value) return;
                if (__instance == null) return;

                BellyInfo info;
                if (!_bellyInfoByType.TryGetValue(__instance.GetType(), out info) || info == null) return;

                var destroyed = CollectDestroyedBelly(info, __instance);
                BonesState state;
                if (!_bellyStateByInstance.TryGetValue(__instance, out state))
                {
                    if (destroyed.Count == 0) return; // 健全なインスタンスには状態を作らない
                    state = new BonesState();
                    _bellyStateByInstance[__instance] = state;
                }

                if (destroyed.Count == 0)
                {
                    // BonesCtrl と同じく、待ちも戻して次の破棄は待たずに直す（1.11.1）
                    state.Attempts = 0;
                    state.GaveUp = false;
                    state.NextRepairFrame = 0;
                    return;
                }

                int frame = Time.frameCount;
                if (state.GaveUp) return;
                if (frame < state.NextRepairFrame) return;

                state.NextRepairFrame = frame + RepairCooldownFrames;
                state.Attempts++;

                string owner = OwnerName(__instance as Component);
                object cha = SafeGet(info.Cha, __instance);
                if (IsNullOrDestroyed(cha))
                {
                    state.GaveUp = true;
                    PatcherPlugin.Logger.LogWarning($"[StaleRefFix][BellyCtrl] owner={owner} の cha が無いため骨を探し直せません。復旧を中止します。");
                    return;
                }

                foreach (var bf in info.Bones)
                {
                    object bone = SafeGet(bf.Field, __instance);
                    if (bone == null || !IsDestroyed(SafeGet(info.BoneT, bone))) continue;

                    Transform found;
                    try { found = info.FindInCha.Invoke(null, new object[] { cha, bf.BoneName }) as Transform; }
                    catch (Exception ex)
                    {
                        state.GaveUp = true;
                        PatcherPlugin.Logger.LogError(
                            $"[StaleRefFix][BellyCtrl] owner={owner} の ChaTools.FindInCha() 呼び出しに失敗したため、このインスタンスの復旧を中止します: {ex.Message}");
                        return;
                    }

                    // 見つからない（null）ときは破棄済みのまま残す。null を入れると LerpInflation の
                    // 空振り判定は同じだが、次の試行で「破棄済み」として検出できなくなるため。
                    if (ReferenceEquals(found, null) || found == null) continue;
                    try { info.BoneT.SetValue(bone, found); }
                    catch { }
                }

                var still = CollectDestroyedBelly(info, __instance);
                if (still.Count == 0)
                {
                    // BonesCtrl と同じく、直ったら待ちもここで戻す（1.11.2）
                    state.Attempts = 0;
                    state.NextRepairFrame = 0;
                    PatcherPlugin.Logger.LogInfo(
                        $"[StaleRefFix][BellyCtrl] owner={owner} の破棄済みボーンを復旧しました: {string.Join(", ", destroyed.ToArray())}");
                }
                else if (state.Attempts >= MaxRepairAttempts)
                {
                    state.GaveUp = true;
                    PatcherPlugin.Logger.LogWarning(
                        $"[StaleRefFix][BellyCtrl] owner={owner} の復旧に {state.Attempts} 回失敗したため中止します（該当の骨は LerpInflation を空振りさせます）。未解決: {string.Join(", ", still.ToArray())}");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning(
                        $"[StaleRefFix][BellyCtrl] owner={owner} の復旧を試みましたが未解決が残っています（{state.Attempts}/{MaxRepairAttempts} 回目）: {string.Join(", ", still.ToArray())}");
                }
            }
            catch (Exception ex)
            {
                SafeLogWarning("BellyCtrl", ex);
            }
        }

        /// <summary>
        /// LerpInflation(Bone b, ...) の予備。b.t が null / 破棄済みなら、その骨1本の書き込みだけを飛ばす
        /// （復旧の待ち時間中や、骨が見つからず諦めた後も NRE を出さないため）。
        /// </summary>
        private static bool BellyCtrlLerpInflationPrefix(object b)
        {
            try
            {
                if (PatcherPlugin.IsQuitting) return true;
                if (!PatcherPlugin.EnableCoordinateChangeStaleRefFix.Value || !PatcherPlugin.EnableBellyCtrlStaleBoneFix.Value) return true;
                if (b == null) return true; // kPlug の元の挙動のまま（Bone 自体が null になる経路は無い）

                FieldInfo boneT;
                if (!_bellyBoneTByType.TryGetValue(b.GetType(), out boneT)) return true;
                if (!IsNullOrDestroyed(SafeGet(boneT, b))) return true;

                _bellySkipCount++;
                if (_bellySkipCount == 1 || _bellySkipCount % BellySkipLogInterval == 0)
                {
                    PatcherPlugin.Logger.LogInfo(
                        $"[StaleRefFix][BellyCtrl] 骨が無い（null / 破棄済み）ため LerpInflation を空振りさせました（累計 {_bellySkipCount} 回）。");
                }
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static List<string> CollectDestroyedBelly(BellyInfo info, object instance)
        {
            var result = new List<string>();
            foreach (var bf in info.Bones)
            {
                object bone = SafeGet(bf.Field, instance);
                if (bone != null && IsDestroyed(SafeGet(info.BoneT, bone))) result.Add(bf.Name);
            }
            return result;
        }

        // ===================== 共通ヘルパー =====================

        /// <summary>
        /// リストから「破棄済み／null」の要素を取り除き、除去件数を返す。
        /// memberField が指定された場合は、要素そのものではなくそのフィールド値を判定対象にする
        /// （Binder.root や Custom.trs のように、入れ物は生きていて中身だけ破棄されるケース）。
        /// </summary>
        private static int RemoveDeadElements(IList list, FieldInfo memberField)
        {
            int removed = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                object element;
                try { element = list[i]; }
                catch { continue; }

                object target = (memberField == null) ? element : SafeGet(memberField, element);

                if (IsNullOrDestroyed(target))
                {
                    try { list.RemoveAt(i); removed++; }
                    catch { }
                }
            }
            return removed;
        }

        /// <summary>Unity 上で破棄済み（C# 参照は非 null だがネイティブ側が破棄されている）かどうか。</summary>
        private static bool IsDestroyed(object v)
        {
            if (v == null) return false;
            var uo = v as UnityEngine.Object;
            if (ReferenceEquals(uo, null)) return false;
            return uo == null;
        }

        private static bool IsNullOrDestroyed(object v)
        {
            if (v == null) return true;
            var uo = v as UnityEngine.Object;
            if (ReferenceEquals(uo, null)) return false;
            return uo == null;
        }

        private static object SafeGet(FieldInfo field, object target)
        {
            if (field == null || target == null) return null;
            try { return field.GetValue(target); }
            catch { return null; }
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

        private static void SafeLogWarning(string where, Exception ex)
        {
            try { PatcherPlugin.Logger.LogWarning($"[StaleRefFix][{where}] 処理中に例外（kPlug 側の処理は継続します）: {ex.Message}"); }
            catch { }
        }
    }
}
