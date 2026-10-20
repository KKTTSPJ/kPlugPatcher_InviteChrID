using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ActionGame.MapObject;
using H;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// 「KK環境でHシーン開始時の体位が本棚愛撫(id=9, category=1002)になってしまう」問題を
    /// 実機で追うための診断パッチ。
    ///
    /// H.HSceneProc（ゲーム本体、Assembly-CSharp側のクラス。kPlugではない）の
    /// - CreateListAnimationFileName(): categorys（現在アクティブなカテゴリ集合）を元に
    ///   lstAnimInfo を lstUseAnimInfo へ絞り込む。
    /// - LoadAnimationListInfo(): 絞り込み後の lstUseAnimInfo から実際に使う体位を選ぶ
    ///   （appointAction/category指定が無ければ最終的に lstUseAnimInfo[k][0] を返す）。
    /// の2箇所に Postfix を当て、categorys / kindMap(.categoryes) / 絞り込み結果 / 最終選択結果を
    /// ログに出す（読み取り専用、値の書き換えは行わない）。
    ///
    /// HSceneProc.categorys は public フィールドのため直接アクセスできるが、
    /// kindMap / lstUseAnimInfo は private フィールドのためリフレクションで読む。
    /// </summary>
    public static class HSceneCategoryDiagnostics
    {
        private static FieldInfo _kindMapField;
        private static FieldInfo _lstUseAnimInfoField;

        internal static void TryApply(Harmony harmony)
        {
            try
            {
                var type = typeof(HSceneProc);
                _kindMapField = AccessTools.Field(type, "kindMap");
                _lstUseAnimInfoField = AccessTools.Field(type, "lstUseAnimInfo");

                var loadMethod = AccessTools.Method(type, "LoadAnimationListInfo", new[] { typeof(int), typeof(int) });
                if (loadMethod != null)
                {
                    harmony.Patch(loadMethod, postfix: new HarmonyMethod(typeof(HSceneCategoryDiagnostics), nameof(LoadAnimationListInfoPostfix)));
                    PatcherPlugin.Logger.LogInfo("[HSceneCategoryDiagnostics] H.HSceneProc.LoadAnimationListInfo() に診断パッチを適用しました。");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning("[HSceneCategoryDiagnostics] LoadAnimationListInfo が見つかりませんでした。");
                }

                var createMethod = AccessTools.Method(type, "CreateListAnimationFileName", new[] { typeof(bool), typeof(int) });
                if (createMethod != null)
                {
                    harmony.Patch(createMethod, postfix: new HarmonyMethod(typeof(HSceneCategoryDiagnostics), nameof(CreateListAnimationFileNamePostfix)));
                    PatcherPlugin.Logger.LogInfo("[HSceneCategoryDiagnostics] H.HSceneProc.CreateListAnimationFileName() に診断パッチを適用しました。");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning("[HSceneCategoryDiagnostics] CreateListAnimationFileName が見つかりませんでした。");
                }
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"[HSceneCategoryDiagnostics] 適用に失敗: {ex.Message}");
            }
        }

        private static void LoadAnimationListInfoPostfix(HSceneProc __instance, int _category, int _appointAction, HSceneProc.AnimationListInfo __result)
        {
            try
            {
                string categorysStr = __instance.categorys != null ? string.Join(",", __instance.categorys.ConvertAll(x => x.ToString()).ToArray()) : "null";

                string kindInfo = "null";
                if (_kindMapField != null)
                {
                    var kind = _kindMapField.GetValue(__instance) as Kind;
                    if (kind != null)
                    {
                        string catList = kind.categoryes != null ? string.Join(",", Array.ConvertAll(kind.categoryes, x => x.ToString())) : "";
                        kindInfo = $"{kind.gameObject.name}:[{catList}]";
                    }
                }

                string resultInfo = __result != null
                    ? $"id={__result.id}, name={__result.nameAnimation}, mode={__result.mode}"
                    : "null";

                PatcherPlugin.Logger.LogInfo(
                    $"[HSceneCategoryDiagnostics] LoadAnimationListInfo(_category={_category}, _appointAction={_appointAction}) "
                    + $"categorys=[{categorysStr}] kindMap={kindInfo} => 結果: {resultInfo}");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"[HSceneCategoryDiagnostics] LoadAnimationListInfoPostfix Error: {ex.Message}");
            }
        }

        private static void CreateListAnimationFileNamePostfix(HSceneProc __instance, bool _isAnimListCreate, int _list)
        {
            try
            {
                string categorysStr = __instance.categorys != null ? string.Join(",", __instance.categorys.ConvertAll(x => x.ToString()).ToArray()) : "null";

                string useInfo = "?";
                if (_lstUseAnimInfoField != null)
                {
                    var lstUseAnimInfo = _lstUseAnimInfoField.GetValue(__instance) as IList;
                    if (lstUseAnimInfo != null)
                    {
                        var parts = new List<string>();
                        for (int i = 0; i < lstUseAnimInfo.Count; i++)
                        {
                            var list = lstUseAnimInfo[i] as IEnumerable;
                            if (list == null) { parts.Add($"[{i}]=null"); continue; }
                            var ids = new List<string>();
                            foreach (var item in list)
                            {
                                var info = item as HSceneProc.AnimationListInfo;
                                if (info != null) ids.Add(info.id.ToString());
                            }
                            parts.Add(ids.Count > 0 ? $"[{i}]=({ids.Count}){{{string.Join("/", ids.ToArray())}}}" : $"[{i}]=(0)");
                        }
                        useInfo = string.Join(" ", parts.ToArray());
                    }
                }

                PatcherPlugin.Logger.LogInfo(
                    $"[HSceneCategoryDiagnostics] CreateListAnimationFileName(_isAnimListCreate={_isAnimListCreate}, _list={_list}) "
                    + $"categorys=[{categorysStr}] lstUseAnimInfo: {useInfo}");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"[HSceneCategoryDiagnostics] CreateListAnimationFileNamePostfix Error: {ex.Message}");
            }
        }
    }
}
