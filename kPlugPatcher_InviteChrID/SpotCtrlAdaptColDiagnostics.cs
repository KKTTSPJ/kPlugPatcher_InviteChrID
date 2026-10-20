using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// 【D】<c>SpotCtrl.AdaptColSclToPose()</c> が出す NRE の原因を特定するための診断。
    ///
    /// このメソッドは <c>spotCol.transform</c> の大きさと位置を読み、<c>ai.InActionBind</c> で分岐し
    /// （片方の分岐では <c>infos.standTyp</c>、もう片方では <c>ai.actionBinder.targetAnim</c> を読む）、
    /// 最後に大きさと位置を書き戻す。<c>ai</c> と <c>ai.actionBinder</c> は null を確かめていない。
    ///
    /// 実機のスタックトレースには Unity ネイティブ側の wrapper フレームが無く、
    /// <c>AdaptColSclToPose</c> 自身が最上位だった。したがって <c>spotCol.transform</c>
    /// （extern 呼び出し）ではなく、**managed なフィールド・プロパティ参照が null** である。
    /// <c>infos</c> は宣言時に <c>new ToolSpot.Infos()</c> で初期化されるため null になり得ず、
    /// <c>ai</c> が有力だったため、**例外が出たときだけ**各参照の状態を記録する（実機の記録では毎回 <c>ai</c> が null だった）。
    ///
    /// 【設計】
    /// - Finalizer を使い、**例外はそのまま外へ出す**（抑止しない）。原型の挙動は一切変えない。
    /// - 記録は 1 セッション <see cref="MaxLogs"/> 件まで。毎フレーム出続けてもログを埋めない。
    /// - 読み取りのみで、値の書き換えは行わない。
    /// </summary>
    internal static class SpotCtrlAdaptColDiagnostics
    {
        private const string Tag = "[SpotCtrlAdaptColDiag]";

        /// <summary>1 セッションあたりの記録件数の上限。</summary>
        private const int MaxLogs = 5;

        private sealed class SpotCtrlInfo
        {
            public FieldInfo Ai;
            public FieldInfo Infos;
            public PropertyInfo SpotCol;
            public FieldInfo AiActionBinder;
            public PropertyInfo AiInActionBind;
        }

        private static readonly Dictionary<Type, SpotCtrlInfo> _infoByType = new Dictionary<Type, SpotCtrlInfo>();
        private static int _logged;

        internal static void TryApply(Harmony harmony)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.FullName.Contains("kPlug")) continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                TryPatch(harmony, types);
            }
        }

        private static void TryPatch(Harmony harmony, Type[] types)
        {
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".CmpH.SpotCtrl"));
            if (type == null) return;
            if (_infoByType.ContainsKey(type)) return; // 適用済み

            try
            {
                var info = new SpotCtrlInfo
                {
                    Ai = AccessTools.Field(type, "ai"),
                    Infos = AccessTools.Field(type, "infos"),
                    SpotCol = AccessTools.Property(type, "spotCol"),
                };
                if (info.Ai != null)
                {
                    info.AiActionBinder = AccessTools.Field(info.Ai.FieldType, "actionBinder");
                    info.AiInActionBind = AccessTools.Property(info.Ai.FieldType, "InActionBind");
                }

                var method = AccessTools.DeclaredMethod(type, "AdaptColSclToPose", Type.EmptyTypes);
                if (method == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}.AdaptColSclToPose() が見つかりませんでした。");
                    return;
                }

                _infoByType[type] = info;
                harmony.Patch(method, finalizer: new HarmonyMethod(typeof(SpotCtrlAdaptColDiagnostics)
                    .GetMethod(nameof(AdaptColSclToPoseFinalizer), BindingFlags.NonPublic | BindingFlags.Static)));
                PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName}.AdaptColSclToPose() に診断パッチを適用しました（例外発生時のみログ出力・抑止はしません）。");
            }
            catch (Exception ex)
            {
                // 通常モードでは kPlugVR のクラスをロードできず失敗するのが正常（実害なし）。
                var message = ex.Message ?? string.Empty;
                if (ex is TypeLoadException || ex is ReflectionTypeLoadException
                    || message.Contains("Could not load type") || message.Contains("IL Compile Error")
                    || message.Contains("cannot be loaded"))
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName} は対象外です（このモードでは kPlugVR のクラスをロードできません）: {message}");
                }
                else
                {
                    PatcherPlugin.Logger.LogError($"{Tag} {type.FullName} への適用に失敗: {message}");
                }
            }
        }

        /// <summary>例外はそのまま外へ出し、状態の記録だけを行う。</summary>
        private static Exception AdaptColSclToPoseFinalizer(Exception __exception, object __instance)
        {
            if (__exception == null) return null;

            try
            {
                if (_logged >= MaxLogs || __instance == null) return __exception;

                SpotCtrlInfo info;
                if (!_infoByType.TryGetValue(__instance.GetType(), out info)) return __exception;

                _logged++;

                object ai = SafeGet(info.Ai, __instance);
                string aiState = RefState(ai);
                string binderState = "?";
                string inActionBind = "?";
                if (!ReferenceEquals(ai, null))
                {
                    binderState = info.AiActionBinder != null ? RefState(SafeGet(info.AiActionBinder, ai)) : "?";
                    try { inActionBind = info.AiInActionBind != null ? info.AiInActionBind.GetValue(ai, null).ToString() : "?"; }
                    catch { inActionBind = "取得時に例外"; }
                }

                string spotColState = "?";
                try { if (info.SpotCol != null) spotColState = RefState(info.SpotCol.GetValue(__instance, null)); }
                catch { spotColState = "取得時に例外"; }

                PatcherPlugin.Logger.LogWarning(
                    $"{Tag} {Describe(__instance)}: {__exception.GetType().Name} 発生時の状態 " +
                    $"(記録 {_logged}/{MaxLogs} 件目) frame={Time.frameCount}, " +
                    $"ai={aiState}, ai.actionBinder={binderState}, ai.InActionBind={inActionBind}, " +
                    $"infos={RefState(SafeGet(info.Infos, __instance))}, spotCol={spotColState}");
            }
            catch { }

            return __exception;
        }

        private static object SafeGet(FieldInfo field, object instance)
        {
            try { return field == null ? null : field.GetValue(instance); }
            catch { return null; }
        }

        private static string RefState(object value)
        {
            if (ReferenceEquals(value, null)) return "null";
            var uo = value as UnityEngine.Object;
            if (!ReferenceEquals(uo, null) && uo == null) return "破棄済み";
            return "ok";
        }

        private static string Describe(object obj)
        {
            try
            {
                var c = obj as Component;
                if (ReferenceEquals(c, null)) return "owner=-";
                if (c == null) return "owner=(破棄済み)";
                return $"owner={c.gameObject.name}(id={c.GetInstanceID()})";
            }
            catch { return "owner=?"; }
        }
    }
}
