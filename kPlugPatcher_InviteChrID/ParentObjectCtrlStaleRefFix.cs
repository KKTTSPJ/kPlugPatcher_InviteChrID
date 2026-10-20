using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// 「招待・キャラ交代の後、ゲージが上がっていないのに絶頂が繰り返される（絶頂ループ）」の回避策。
    ///
    /// 【不具合のメカニズム（2026-08-06 の実機ログで確認）】
    ///   衣装変更(CoordinateChanged)でボーン階層が再構築されると、そのボーンに親子付けされていた
    ///   H用の小物 GameObject も一緒に破棄される。しかしゲーム本体の <c>ParentObjectCtrl</c> は
    ///   破棄済みの GameObject を <c>dicObject</c> に保持したままであり、以下で NRE を起こす。
    ///
    ///   <c>Visible(bool)</c> は、状態が変わるときに <c>dicObject</c> のすべての GameObject に SetActive を呼び、
    ///   最後に <c>isActive</c> を更新する。破棄済みの GameObject で SetActive が NRE になると、
    ///   <c>isActive</c> が更新されないため、次回も同じ条件で再発する（恒久化）。
    ///
    ///   同じクラスの <c>ReleaseObject()</c> は <c>item.Value == null</c> を確認しているのに対し、
    ///   <c>Visible()</c> と <c>Proc()</c> にはその確認が無い。ゲーム本体側の実装漏れである。
    ///
    /// 【絶頂ループにつながる経路】
    ///   HSceneProc.Update → HSonyu.Proc → HActionBase.SetPlay → ParentObjectCtrl.Proc → Visible
    ///   で例外が送出されると、呼び出し元の <c>HSonyu.Proc</c> が中断される。
    ///   絶頂アニメーション終了処理は次の形をしており、
    ///
    ///   <c>SetPlay("WS_IN_A")</c>（アナルなら <c>"A_WS_IN_A"</c>）を呼んでから <c>gaugeMale</c> と <c>gaugeFemale</c> を 0 に戻す。
    ///   SetPlay の中で例外が出ると、ゲージを 0 に戻す処理が実行されない。
    ///
    ///   <c>flags.gaugeFemale = 0f</c> に到達しないためゲージが満タンのまま維持され、
    ///   次フレームで絶頂条件が再成立して絶頂が繰り返される。
    ///   「ゲージが上昇していないのに絶頂が連続する」という報告された症状と一致する。
    ///
    /// 【回避策】
    ///   <c>Visible(bool)</c> と <c>Proc(string)</c> を、**破棄済み要素を飛ばすだけの等価な実装**に
    ///   置き換える（Prefix で原型をスキップ）。重要なのは、要素を飛ばした場合でも
    ///   <c>isActive</c> の更新と後続処理を必ず完了させることで、例外による中断を無くす点にある。
    ///
    ///   <c>dicObject</c> から破棄済み要素を削除する案は採らない。<c>Proc()</c> が
    ///   <c>dicObject[item]</c> でキー参照するため、削除すると KeyNotFoundException を誘発するため。
    ///
    /// 【設計上の注意】
    /// - 本クラスは kPlug ではなく**ゲーム本体(Assembly-CSharp)**にパッチを当てる。
    ///   本プラグインの「kPlug へのパッチを集約する」方針の例外であり、
    ///   設定 <c>EnableParentObjectCtrlStaleRefFix</c> で個別に無効化できるようにしてある。
    /// - 原型の挙動を変えないこと。破棄済み／未登録キーを飛ばす以外は原型と同じ順序・同じ戻り値にする。
    /// - 例外を外へ出さない。回避策側で失敗した場合は原型の実行に委ねる（return true）。
    /// </summary>
    internal static class ParentObjectCtrlStaleRefFix
    {
        private static FieldInfo _dicObjectField;
        private static FieldInfo _dicObjectCtrlField;
        private static FieldInfo _isAllVisibleField;
        private static FieldInfo _isActiveField;

        /// <summary>インスタンスごとに「前回ログに出した破棄済み件数」。同じ内容を出し続けないため。</summary>
        private static readonly Dictionary<object, int> _lastSkippedByInstance = new Dictionary<object, int>();

        internal static void TryApply(Harmony harmony)
        {
            try
            {
                var type = typeof(ParentObjectCtrl);

                _dicObjectField = AccessTools.Field(type, "dicObject");
                _dicObjectCtrlField = AccessTools.Field(type, "dicObjectCtrl");
                _isAllVisibleField = AccessTools.Field(type, "isAllVisible");
                _isActiveField = AccessTools.Field(type, "isActive");

                if (_dicObjectField == null || _isActiveField == null)
                {
                    PatcherPlugin.Logger.LogWarning(
                        "[ParentObjectCtrlFix] dicObject / isActive が見つからないため回避策を適用できません。");
                    return;
                }

                var visible = AccessTools.Method(type, "Visible", new[] { typeof(bool) });
                if (visible != null)
                {
                    harmony.Patch(visible, new HarmonyMethod(typeof(ParentObjectCtrlStaleRefFix)
                        .GetMethod(nameof(VisiblePrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                    PatcherPlugin.Logger.LogInfo("[ParentObjectCtrlFix] ParentObjectCtrl.Visible(bool) に回避策を適用しました。");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning("[ParentObjectCtrlFix] ParentObjectCtrl.Visible(bool) が見つかりませんでした。");
                }

                // Proc は AnimatorStateInfo を取るオーバーロードもあるため引数型を明示する
                var proc = AccessTools.Method(type, "Proc", new[] { typeof(string) });
                if (proc != null && _dicObjectCtrlField != null && _isAllVisibleField != null)
                {
                    harmony.Patch(proc, new HarmonyMethod(typeof(ParentObjectCtrlStaleRefFix)
                        .GetMethod(nameof(ProcPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                    PatcherPlugin.Logger.LogInfo("[ParentObjectCtrlFix] ParentObjectCtrl.Proc(string) に回避策を適用しました。");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning("[ParentObjectCtrlFix] ParentObjectCtrl.Proc(string) に回避策を適用できませんでした。");
                }
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"[ParentObjectCtrlFix] 適用に失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// 原型 Visible(bool) の等価実装。破棄済み／null の GameObject を飛ばす点だけが異なる。
        /// 戻り値は原型と同じく常に false。
        /// </summary>
        private static bool VisiblePrefix(ParentObjectCtrl __instance, bool _visible, ref bool __result)
        {
            if (PatcherPlugin.IsQuitting) return true;

            try
            {
                if (!PatcherPlugin.EnableParentObjectCtrlStaleRefFix.Value) return true;

                // __instance は UnityEngine.Object 派生であり、この == 判定は「破棄済み(destroyed)」も
                // 検出する。ParentObjectCtrl 自体が破棄され、呼び出し元(HActionBase)が古い参照を
                // 持ち続けているケースが実機で確認された。この場合 return true で原型に委ねると
                // 原型がそのまま破棄済みインスタンスに対して実行され NRE を起こす
                // （実際にこの不具合で回避策が機能しなかった実例: 2026-08-07）。
                // 破棄済みなら「表示更新すべき実体が無い」だけなので、安全に何もせず終える。
                if (__instance == null) { __result = false; return false; }

                if (!SafeVisible(__instance, _visible)) return true; // dicObject 自体が無い等、想定外のみ原型に委ねる

                __result = false;
                return false; // 原型はスキップ
            }
            catch (Exception ex)
            {
                SafeLogWarning("Visible", ex);
                return true;
            }
        }

        /// <summary>
        /// 原型 Proc(string) の等価実装。
        /// 破棄済み GameObject と、dicObject に存在しないキーを飛ばす点だけが異なる
        /// （原型は dicObject[item] で直接参照するため、キーが無いと KeyNotFoundException になる）。
        /// </summary>
        private static bool ProcPrefix(ParentObjectCtrl __instance, string _nameNextAnimation, ref bool __result)
        {
            if (PatcherPlugin.IsQuitting) return true;

            try
            {
                if (!PatcherPlugin.EnableParentObjectCtrlStaleRefFix.Value) return true;

                // VisiblePrefix と同じ理由（ParentObjectCtrl 自体が破棄済みのケース）で、
                // 原型に委ねず安全に no-op で終える。
                if (__instance == null) { __result = false; return false; }

                bool isAllVisible = (bool)_isAllVisibleField.GetValue(__instance);

                if (!SafeVisible(__instance, isAllVisible)) return true; // dicObject 自体が無い等、想定外のみ原型に委ねる

                var dicObjectCtrl = _dicObjectCtrlField.GetValue(__instance) as IDictionary;
                if (dicObjectCtrl == null || !dicObjectCtrl.Contains(_nameNextAnimation))
                {
                    __result = false;
                    return false;
                }

                _isActiveField.SetValue(__instance, !isAllVisible);

                var dicObject = _dicObjectField.GetValue(__instance) as IDictionary;
                var keys = dicObjectCtrl[_nameNextAnimation] as IEnumerable;
                int skipped = 0;

                if (keys != null)
                {
                    foreach (object keyObj in keys)
                    {
                        string key = keyObj as string;
                        object value = (key != null && dicObject != null && dicObject.Contains(key))
                            ? dicObject[key]
                            : null;

                        var go = value as GameObject;
                        if (go == null) { skipped++; continue; }  // null / 破棄済み / 未登録キー
                        go.SetActive(!isAllVisible);
                    }
                }

                if (skipped > 0)
                {
                    LogSkippedIfChanged(__instance, skipped,
                        $"[ParentObjectCtrlFix][Proc] anim={_nameNextAnimation} で破棄済み／未登録の小物 {skipped} 件を読み飛ばしました。");
                }

                __result = true;
                return false; // 原型はスキップ
            }
            catch (Exception ex)
            {
                SafeLogWarning("Proc", ex);
                return true;
            }
        }

        /// <summary>
        /// 原型 Visible(bool) と同じ処理を、破棄済み要素を飛ばして行う。
        /// 途中で失敗した場合は false を返し、呼び出し側が原型に委ねられるようにする。
        /// </summary>
        private static bool SafeVisible(ParentObjectCtrl instance, bool visible)
        {
            bool isActive = (bool)_isActiveField.GetValue(instance);
            if (isActive == visible) return true; // 原型と同じ早期 return（状態変化なし）

            var dicObject = _dicObjectField.GetValue(instance) as IDictionary;
            if (dicObject == null) return false;

            int skipped = 0;
            foreach (DictionaryEntry entry in dicObject)
            {
                var go = entry.Value as GameObject;
                if (go == null) { skipped++; continue; }  // Unity の == で破棄済みも弾かれる
                go.SetActive(visible);
            }

            // 原型ではここに到達できず isActive が更新されないため、同じ状態で例外が再発し続けていた。
            _isActiveField.SetValue(instance, visible);

            if (skipped > 0)
            {
                LogSkippedIfChanged(instance, skipped,
                    $"[ParentObjectCtrlFix][Visible] 破棄済みの小物 {skipped} 件を読み飛ばして表示状態を更新しました（visible={visible}）。");
            }

            return true;
        }

        /// <summary>同じ件数のまま何度も出力しないよう、前回と件数が変わったときだけログに出す。</summary>
        private static void LogSkippedIfChanged(object instance, int skipped, string message)
        {
            try
            {
                int last;
                if (_lastSkippedByInstance.TryGetValue(instance, out last) && last == skipped) return;
                _lastSkippedByInstance[instance] = skipped;
                PatcherPlugin.Logger.LogInfo(message);
            }
            catch { }
        }

        private static void SafeLogWarning(string where, Exception ex)
        {
            try
            {
                PatcherPlugin.Logger.LogWarning(
                    $"[ParentObjectCtrlFix][{where}] 処理中に例外（原型の処理に委ねます）: {ex.Message}");
            }
            catch { }
        }
    }
}
