using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// 在席中のキャラと同じ名前のカードが招待リストに出てこない問題への対策。
    ///
    /// 【原因】kPlug の <c>InviteUI.IsAlreadyInHarem(ChaFileControl)</c> が**名前の一致だけ**で
    /// 「もう居る」と判定し、一覧の描画時にそのカードを飛ばしている。
    /// VR版はメインヒロイン・2人目・招待済みのキャラの名前の一覧を毎回作って比べ、
    /// 非VR版（名前は <c>IsAlreadyInHaremV2</c>）は <c>PresentNames</c> と比べる。
    /// 呼び出し元は3か所とも一覧の表示・抽選のみ（VRメニューの組み立て、ランダム招待の候補、非VRのカードタブ）。
    ///
    /// 本プラグインは招待の瞬間にカードを複製して改名するため、同名でも別個体として扱える。
    /// 元の判定は「同じカードを2回招待できない」ためのものだが、改名によってその前提は不要になっている。
    /// メインヒロインに選んだキャラと同名のカードが一覧に出ないのは、この判定が残っているせい。
    ///
    /// 【対策】<c>ChaFileControl</c> を受ける方のオーバーロードだけを Prefix で置き換え、常に false を返す。
    /// 元の実装は副作用の無い純粋な判定（リストを作って Contains するだけ）なので、実行しなくても失われる処理は無い。
    /// むしろ VR版は呼ばれるたびに <c>List&lt;string&gt;</c> を作り直しており、非VRでは OnGUI から
    /// カード数×毎フレーム呼ばれるため、飛ばした方が軽い。
    ///
    /// 【<c>Heroine</c> を受ける方は触らない】ストーリー/デート相手のヒロインはセーブデータ由来で、
    /// 本プラグインは**改名しない方針**（セーブデータの氏名を汚さないため）。
    /// こちらを解除すると同名のまま2人並び、kPlug の名前をキーにした管理が壊れる。
    /// </summary>
    internal static class InviteListSameNameUnblock
    {
        private const string Tag = "[InviteListSameName]";

        private static readonly HashSet<string> _appliedKeys = new HashSet<string>();
        private static readonly HashSet<string> _failureLoggedKeys = new HashSet<string>();

        /// <summary>最初に1件だけ「実際に隠されるはずだったカードを出した」ことを記録する。</summary>
        private static bool _loggedFirstUnblock;

        internal static void TryApply(Harmony harmony)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.FullName.Contains("kPlug")) continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".CmpH.InviteUI"));
                if (type == null) continue;

                var key = type.FullName + "|IsAlreadyInHarem";
                if (_appliedKeys.Contains(key)) continue;

                try
                {
                    // VR版は IsAlreadyInHarem、非VR版は IsAlreadyInHaremV2。
                    // 名前を決め打ちせず「ChaFileControl 1個を受け取る bool のメソッド」で探す。
                    var method = type
                        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .FirstOrDefault(m => m.Name.IndexOf("IsAlreadyInHarem", StringComparison.Ordinal) >= 0
                                          && m.ReturnType == typeof(bool)
                                          && MatchesCardOverload(m));

                    _appliedKeys.Add(key);

                    if (method == null)
                    {
                        PatcherPlugin.Logger.LogWarning(
                            $"{Tag} {type.FullName}: ChaFileControl を受け取る IsAlreadyInHarem が見つからないため、" +
                            "同名カードの表示は変更しません。");
                        continue;
                    }

                    harmony.Patch(method, prefix: new HarmonyMethod(
                        typeof(InviteListSameNameUnblock).GetMethod(nameof(IsAlreadyInHaremPrefix), BindingFlags.NonPublic | BindingFlags.Static)));

                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag} {type.FullName}.{method.Name}(ChaFileControl) を「常に居ないものとして扱う」に変更しました" +
                        $"（在席中のキャラと同名のカードも招待リストに出ます。frame={Time.frameCount}）。");
                }
                catch (Exception ex)
                {
                    if (_failureLoggedKeys.Add(key))
                    {
                        PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName} への適用に失敗: {ex.Message}");
                    }
                }
            }
        }

        private static bool MatchesCardOverload(MethodInfo method)
        {
            var parameters = method.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(ChaFileControl);
        }

        /// <summary>
        /// 元の判定を実行せず false を返す。元は副作用の無い純粋な判定なので、失われる処理は無い。
        /// </summary>
        private static bool IsAlreadyInHaremPrefix(ref bool __result, ChaFileControl __0)
        {
            __result = false;

            if (!_loggedFirstUnblock)
            {
                _loggedFirstUnblock = true;
                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} 在席判定を飛ばして招待リストを組み立てています（最初の1件: {DescribeCard(__0)}）。" +
                    "以後この種別のログは出しません。");
            }

            return false;
        }

        private static string DescribeCard(ChaFileControl chaFile)
        {
            try
            {
                if (chaFile == null || chaFile.parameter == null) return "(不明)";
                return (chaFile.parameter.lastname ?? "") + (chaFile.parameter.firstname ?? "");
            }
            catch { return "(不明)"; }
        }
    }
}
