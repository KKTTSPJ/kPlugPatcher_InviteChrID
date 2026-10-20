using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// InviteUIDiagnostics で判明した「KK環境では Core.hStartCtx が Normal ではなく
    /// SpLib1（書斎の特殊体位）に固定され、isValidCtx が恒常的に false になる」問題を
    /// さらに追うための診断パッチ。
    ///
    /// Core.hStartCtx は ParserScene.HStartCtx() の戻り値でのみ更新され、この更新は Core の毎フレーム処理の中で
    /// 「H シーンへ新規に入った瞬間（hCtrl が null から non-null に変わる瞬間）」にしか実行されない。
    ///
    /// つまり ParserScene.HStartCtx() の呼び出し回数そのものが「Hシーンに何回入ったと
    /// kPlugが認識したか」を表す。KK環境でこの回数が実際のHシーン突入回数より少なければ、
    /// 2回目以降のHシーン突入を kPlug 自身が検知できていないことになり、hStartCtx が
    /// 最初に計算された値（誤ってSpLib1になっている値）のまま固まる、という仮説を裏付ける。
    ///
    /// 本パッチは ParserScene.HStartCtx() の Postfix として、呼び出し回数・戻り値・
    /// その判定材料である Core.hAnim（現在再生アニメーションの正規化後の名前）を
    /// 毎回ログに出力する（読み取り専用、値の書き換えは行わない）。
    /// </summary>
    public static class HStartCtxDiagnostics
    {
        private sealed class TypeInfo
        {
            public FieldInfo CoreHAnim;
            public FieldInfo CoreHFlag;
            public int CallCount;
        }

        private static readonly Dictionary<MethodBase, TypeInfo> _infoByMethod = new Dictionary<MethodBase, TypeInfo>();

        internal static void TryApply(Harmony harmony)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.FullName.Contains("kPlug")) continue;

                Type[] types;
                try { types = assembly.GetExportedTypes(); }
                catch (ReflectionTypeLoadException) { continue; }

                var parserSceneType = types.FirstOrDefault(t => t.FullName.EndsWith(".ParserScene"));
                if (parserSceneType == null) continue;

                try
                {
                    var hStartCtxMethod = AccessTools.Method(parserSceneType, "HStartCtx", Type.EmptyTypes);
                    if (hStartCtxMethod == null)
                    {
                        PatcherPlugin.Logger.LogWarning($"[HStartCtxDiagnostics] {parserSceneType.FullName}.HStartCtx() が見つかりませんでした。");
                        continue;
                    }

                    var coreType = types.FirstOrDefault(t => t.Name == "Core");
                    var info = new TypeInfo
                    {
                        CoreHAnim = coreType != null ? AccessTools.Field(coreType, "hAnim") : null,
                        CoreHFlag = coreType != null ? AccessTools.Field(coreType, "hFlag") : null,
                        CallCount = 0,
                    };
                    _infoByMethod[hStartCtxMethod] = info;

                    var postfix = typeof(HStartCtxDiagnostics).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static);
                    harmony.Patch(hStartCtxMethod, postfix: new HarmonyMethod(postfix));
                    PatcherPlugin.Logger.LogInfo($"[HStartCtxDiagnostics] {parserSceneType.FullName}.HStartCtx() に診断パッチを適用しました（呼び出しごとにログ出力）。");
                }
                catch (Exception ex)
                {
                    PatcherPlugin.Logger.LogError($"[HStartCtxDiagnostics] {parserSceneType.FullName} の解析に失敗: {ex.Message}");
                }
            }
        }

        private static void Postfix(MethodBase __originalMethod, object __result)
        {
            try
            {
                TypeInfo info;
                if (!_infoByMethod.TryGetValue(__originalMethod, out info) || info == null) return;

                info.CallCount++;
                string hAnim = info.CoreHAnim != null ? (info.CoreHAnim.GetValue(null) as string) : "?";
                string rawFile;
                string assetPath;
                GetRawAnimPath(info, out rawFile, out assetPath);

                PatcherPlugin.Logger.LogInfo(
                    $"[HStartCtxDiagnostics] HStartCtx() 呼び出し #{info.CallCount}: 結果={__result} "
                    + $"(hAnim=\"{hAnim}\", 生file=\"{rawFile}\", 生assetpath=\"{assetPath}\")");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"[HStartCtxDiagnostics] Postfix Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Core.hFlag.nowAnimationInfo.paramFemale.path.file / .assetpath を辿って取得する。
        /// これらの型 (HFlag/AnimationListInfo/FemaleParameter/AnimPath 等) はゲーム本体側の
        /// H関連クラスであり kPlug の型ではないため、フィールド名だけを頼りに
        /// リフレクションで1段ずつ辿る（存在しない・null ならその場で諦めて "?" を返す）。
        /// </summary>
        private static void GetRawAnimPath(TypeInfo info, out string rawFile, out string assetPath)
        {
            rawFile = "?";
            assetPath = "?";
            try
            {
                object hFlag = info.CoreHFlag?.GetValue(null);
                object nowAnimationInfo = GetMemberValue(hFlag, "nowAnimationInfo");
                object paramFemale = GetMemberValue(nowAnimationInfo, "paramFemale");
                object path = GetMemberValue(paramFemale, "path");
                rawFile = GetMemberValue(path, "file") as string ?? "?";
                assetPath = GetMemberValue(path, "assetpath") as string ?? "?";
            }
            catch
            {
                // 途中のどこかが null / 型不一致の場合はそのまま "?" を返す（診断用のため無視して良い）。
            }
        }

        private static object GetMemberValue(object target, string memberName)
        {
            if (target == null) return null;
            var type = target.GetType();
            var field = AccessTools.Field(type, memberName);
            if (field != null) return field.GetValue(target);
            var prop = AccessTools.Property(type, memberName);
            if (prop != null) return prop.GetValue(target, null);
            return null;
        }
    }
}
