using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// 複数人招待時のフレームレート低下の主因である DynamicBone（ゲーム本体の揺れもの）の負荷を下げる（v1.8.0 D1）。
    ///   5人のとき、フレーム時間の約6割が DynamicBone.LateUpdate（75〜94ms/f）。数が増えるうえに、
    ///   DynamicBone.UpdateDynamicBones は m_UpdateRate（既定60回/秒）に合わせて、フレームが遅いと1フレームで最大3回計算するため、
    ///   フレームレートが下がるほど計算が増える悪循環になっている。
    ///
    /// 制御（どちらも設定で切り替えられる。まず効果が最大になる設定を既定にして測る）:
    ///   1. DisableInvitedDynamicBones：kPlug が招待したキャラ（kPlug の AI_Public がちょうどその型で付いている ChaControl）と
    ///      バディ（AI_Buddy）の DynamicBone / DynamicBone_Ver01 の LateUpdate（揺れの計算と反映）を行わない。
    ///      Update（InitTransforms：ボーンを初期の姿勢に戻す）はそのまま動くので、揺れものは静止した初期姿勢になる。
    ///      主人公・メインヒロイン（AI_Main は AI_Public の派生なので、型が一致しない）は対象外。
    ///      DynamicBone_Ver02（胸）は DisableInvitedBreastDynamicBones で別に切り替える。
    ///   2. LimitDynamicBoneIterations：すべてのキャラの DynamicBone / DynamicBone_Ver01 について、
    ///      1フレームあたりの計算回数を MaxIterationsPerFrame（既定1）までに抑える（悪循環を断つ）。
    ///      フレームレートが m_UpdateRate 以上なら元々1回以下なので、見た目は変わらない。
    ///
    /// v1.16.0: 1 はゲーム中に切り替えられるようにした（kPlug の型が見つかれば、設定が無効でも LateUpdate の Prefix を当て、Prefix の中で設定を見る）。
    ///   招待キャラの揺れものを動かしたまま、DynamicBoneColliderCache でどこまで軽くなるかを比べるため。2 は従来どおり再起動で反映。
    /// </summary>
    internal static class DynamicBoneThrottle
    {
        private const string Tag = "[DynamicBoneThrottle]";

        private static Type _aiPublicType, _aiBuddyType;
        private static readonly Dictionary<int, bool> _skipCache = new Dictionary<int, bool>();
        private static float _cacheClearedAt;
        private const float CacheLifetimeSeconds = 5f;

        private static AccessTools.FieldRef<DynamicBone, float> _dbTime, _dbRate;
        private static AccessTools.FieldRef<DynamicBone_Ver01, float> _db1Time, _db1Rate;

        private static bool _applied;

        /// <summary>PatcherPlugin の本パスから1回呼ぶ。kPlug の型は、動作中のモードのアセンブリから解決する。</summary>
        internal static void TryApply(Harmony harmony)
        {
            if (_applied) return;
            _applied = true;

            bool limit = PatcherPlugin.LimitDynamicBoneIterations.Value;
            bool guestPatch = true;

            try
            {
                // 通常モードで kPlugVR の型に深く触れると Mono がネイティブで落ちるため、動作中のモードのアセンブリだけを見る
                string wanted = BepInEx.Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase) ? "kPlugVR" : "kPlug";
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == wanted);
                if (asm != null)
                {
                    _aiPublicType = asm.GetType(wanted + ".CmpH.AI_Public");
                    _aiBuddyType = asm.GetType(wanted + ".CmpH.AI_Buddy");
                }
                if (_aiPublicType == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {wanted}.CmpH.AI_Public が見つからないため、招待キャラの揺れものの停止は行いません。");
                    guestPatch = false;
                }

                if (guestPatch)
                {
                    var pre = new HarmonyMethod(typeof(DynamicBoneThrottle).GetMethod(nameof(LateUpdatePrefix), BindingFlags.NonPublic | BindingFlags.Static));
                    var preBreast = new HarmonyMethod(typeof(DynamicBoneThrottle).GetMethod(nameof(LateUpdatePrefixBreast), BindingFlags.NonPublic | BindingFlags.Static));
                    harmony.Patch(AccessTools.Method(typeof(DynamicBone), "LateUpdate"), prefix: pre);
                    harmony.Patch(AccessTools.Method(typeof(DynamicBone_Ver01), "LateUpdate"), prefix: pre);
                    harmony.Patch(AccessTools.Method(typeof(DynamicBone_Ver02), "LateUpdate"), prefix: preBreast);
                    PatcherPlugin.DisableInvitedDynamicBones.SettingChanged += (s, e) => LogGuestSetting("設定を変更しました");
                    PatcherPlugin.DisableInvitedBreastDynamicBones.SettingChanged += (s, e) => LogGuestSetting("設定を変更しました");
                }

                if (limit)
                {
                    _dbTime = AccessTools.FieldRefAccess<DynamicBone, float>("m_Time");
                    _dbRate = AccessTools.FieldRefAccess<DynamicBone, float>("m_UpdateRate");
                    _db1Time = AccessTools.FieldRefAccess<DynamicBone_Ver01, float>("m_Time");
                    _db1Rate = AccessTools.FieldRefAccess<DynamicBone_Ver01, float>("m_UpdateRate");
                    harmony.Patch(AccessTools.Method(typeof(DynamicBone), "UpdateDynamicBones", new[] { typeof(float) }),
                        prefix: new HarmonyMethod(typeof(DynamicBoneThrottle).GetMethod(nameof(LimitPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                    harmony.Patch(AccessTools.Method(typeof(DynamicBone_Ver01), "UpdateDynamicBones", new[] { typeof(float) }),
                        prefix: new HarmonyMethod(typeof(DynamicBoneThrottle).GetMethod(nameof(LimitPrefixVer01), BindingFlags.NonPublic | BindingFlags.Static)));
                }

                bool disableInvited = guestPatch && PatcherPlugin.DisableInvitedDynamicBones.Value;
                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} 適用しました: 招待キャラ・バディの揺れものの停止={(disableInvited ? "有効" : "無効")}" +
                    (disableInvited ? $"（胸 Ver02 も停止={PatcherPlugin.DisableInvitedBreastDynamicBones.Value}）" : "") +
                    (guestPatch ? "（ゲーム中に切り替え可）" : "") +
                    $" / 1フレームの計算回数の上限={(limit ? PatcherPlugin.MaxDynamicBoneIterationsPerFrame.Value + "回" : "無効")}");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗: {ex.Message}");
            }
        }

        // ===================== 1. 招待キャラの揺れものを止める =====================

        private static bool LateUpdatePrefix(Component __instance)
        {
            if (!PatcherPlugin.DisableInvitedDynamicBones.Value) return true;
            try
            {
                return !ShouldSkip(__instance);
            }
            catch
            {
                return true;
            }
        }

        private static bool LateUpdatePrefixBreast(Component __instance)
        {
            if (!PatcherPlugin.DisableInvitedDynamicBones.Value || !PatcherPlugin.DisableInvitedBreastDynamicBones.Value) return true;
            try
            {
                return !ShouldSkip(__instance);
            }
            catch
            {
                return true;
            }
        }

        private static void LogGuestSetting(string why)
        {
            PatcherPlugin.Logger.LogInfo($"{Tag} {why}: 招待キャラ・バディの揺れものの停止={(PatcherPlugin.DisableInvitedDynamicBones.Value ? "有効" : "無効")}" +
                                         $"（胸 Ver02 も停止={PatcherPlugin.DisableInvitedBreastDynamicBones.Value}）");
        }

        private static bool ShouldSkip(Component db)
        {
            if (db == null) return false;

            float now = Time.realtimeSinceStartup;
            if (now - _cacheClearedAt > CacheLifetimeSeconds)
            {
                // 招待・退場・交代で持ち主が変わりうるので、定期的に判定し直す
                _skipCache.Clear();
                _cacheClearedAt = now;
            }

            int id = db.GetInstanceID();
            bool skip;
            if (_skipCache.TryGetValue(id, out skip)) return skip;

            skip = false;
            var cha = db.GetComponentInParent<ChaControl>();
            if (cha != null)
            {
                foreach (var mb in cha.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    var t = mb.GetType();
                    // AI_Main / AI_Second は AI_Public の派生。型の完全一致で、招待キャラ（AI_Public）とバディ（AI_Buddy）だけを選ぶ
                    if (t == _aiPublicType || (_aiBuddyType != null && t == _aiBuddyType)) { skip = true; break; }
                }
            }
            _skipCache[id] = skip;
            return skip;
        }

        // ===================== 2. 1フレームの計算回数を抑える =====================

        private static void LimitPrefix(DynamicBone __instance, float t)
        {
            try
            {
                Limit(ref _dbTime(__instance), _dbRate(__instance), t);
            }
            catch { }
        }

        private static void LimitPrefixVer01(DynamicBone_Ver01 __instance, float t)
        {
            try
            {
                Limit(ref _db1Time(__instance), _db1Rate(__instance), t);
            }
            catch { }
        }

        /// <summary>
        /// 元の処理は「m_Time += t; while (m_Time >= 1/rate) { m_Time -= 1/rate; 回数++; 3回で打ち切り }」。
        /// 呼ぶ前に m_Time を「m_Time + t &lt; (上限+1)/rate」になるよう切り詰めておけば、計算は上限の回数以下になる。
        /// </summary>
        private static void Limit(ref float time, float rate, float t)
        {
            if (rate <= 0f) return;
            int max = Math.Max(1, PatcherPlugin.MaxDynamicBoneIterationsPerFrame.Value);
            if (max >= 3) return;
            float dt = 1f / rate;
            float cap = dt * (max + 1) - 1e-5f - t;
            if (time > cap) time = cap;
        }
    }
}
