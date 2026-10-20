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
    /// kPlug の招待キャラの表情のコルーチン <c>FaceCtrl.IE_SetFace</c> が出す NullReferenceException の原因を特定するための診断（読み取り専用）。
    ///
    /// 【現象】実機ログ（通常モード）で 16件。うち 11件がゲーム本体の性格の招待キャラ1人に集中。
    /// スタックは <c>FaceCtrl+&lt;IE_SetFace&gt;d__NN.MoveNext</c> だけで、その下にゲーム本体のメソッドが無い。
    /// つまり null の参照はコルーチンの本体で直接起きている。コンソールの <c>IE_SetFace &lt;種類&gt;</c> の直後に出るので、
    /// 最初の <c>yield</c>（WaitForEndOfFrame）より後。
    ///
    /// 【候補】本体で直接たどっている参照（3.56 非VR 119443〜）:
    /// <c>ai</c> / <c>ai.Cha</c> / <c>Core.config</c> / <c>Core.hProc.voice.faceLists[0]</c> / <c>ai.Cha.mouthCtrl</c> /
    /// アヘ顔のときだけ <c>ai.Cha.fileCustom.face.baseMakeup</c> と <c>ai.Cha.customMatFace</c>（<c>SetColor</c>）。
    /// 最後のものは 【L】（<c>AI_Main.Ahegao</c> の <c>customMatFace</c>）と同じ形なので最有力だが、ログからはアヘ顔だったか分からない。
    ///
    /// 【診断】<c>MoveNext</c> に Finalizer を当て、NRE が出たときだけ、上の候補それぞれが null かどうかと、
    /// キャラ名・カードの性格・表情の種類・アヘ顔の状態を1行に出す。例外は握りつぶさない（原型と同じく呼び出し元へ返す）。
    /// Harmony が <c>MoveNext</c> を置き換えるので、以後の例外のスタックには IL オフセット（<c>&lt;IL 0x…&gt;</c>）も出る。
    /// その位置を IL と照合すれば、どの命令で落ちたかが確定する。
    ///
    /// 【設計上の注意】
    /// - 動作中のモードのアセンブリ（<c>kPlug</c> / <c>kPlugVR</c>）だけから型を引く（<see cref="DynamicBoneThrottle"/> と同じ）。
    /// - コルーチンのクラス名（<c>d__83</c> など）は版で変わるので、<c>&lt;IE_SetFace&gt;</c> を含む入れ子の型を探す。
    /// - 最初の <see cref="DetailLimit"/> 件は1件ずつ、以降は <see cref="SummaryEvery"/> 件ごとにキャラ別の件数だけ出す。
    /// </summary>
    internal static class FaceCtrlSetFaceDiagnostics
    {
        private const string Tag = "[SetFaceDiag]";
        private const int DetailLimit = 30;
        private const int SummaryEvery = 50;

        private static bool _applied;
        private static int _count;
        private static readonly Dictionary<string, int> _countByChara = new Dictionary<string, int>();

        private static FieldInfo _thisField;      // <>4__this（FaceCtrl）
        private static FieldInfo _stateField;     // <>1__state
        private static FieldInfo _kindField;      // kind
        private static FieldInfo _forceMouthField;// forceMouthOpen
        private static FieldInfo _faceCtrlAi;     // FaceCtrl.ai
        private static MemberInfo _aiCha, _aiName, _aiInAhegao, _aiGender, _aiIsTouch, _aiVirtualEx;
        private static MemberInfo _coreConfig, _coreHProc, _coreHCtrl;
        private static MemberInfo _cfgEnableAhegao;

        internal static void TryApply(Harmony harmony)
        {
            if (_applied) return;
            _applied = true;

            try
            {
                string root = BepInEx.Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase) ? "kPlugVR" : "kPlug";
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == root);
                var faceCtrlType = asm?.GetType(root + ".CmpChara.FaceCtrl");
                var coreType = asm?.GetType(root + ".Core");
                if (faceCtrlType == null || coreType == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {root} の FaceCtrl / Core が見つからないため、診断は行いません。");
                    return;
                }

                var iterType = faceCtrlType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(t => t.Name.Contains("<IE_SetFace>"));
                var moveNext = iterType != null ? AccessTools.DeclaredMethod(iterType, "MoveNext") : null;
                if (moveNext == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} FaceCtrl.IE_SetFace のコルーチンのクラスが見つからないため、診断は行いません。");
                    return;
                }

                _thisField = AccessTools.Field(iterType, "<>4__this");
                _stateField = AccessTools.Field(iterType, "<>1__state");
                _kindField = AccessTools.Field(iterType, "kind");
                _forceMouthField = AccessTools.Field(iterType, "forceMouthOpen");
                _faceCtrlAi = AccessTools.Field(faceCtrlType, "ai");
                var aiType = _faceCtrlAi?.FieldType;
                if (aiType != null)
                {
                    _aiCha = FieldOrProperty(aiType, "Cha");
                    _aiName = FieldOrProperty(aiType, "CharaName");
                    _aiInAhegao = FieldOrProperty(aiType, "InAhegao");
                    _aiGender = FieldOrProperty(aiType, "gender");
                    _aiIsTouch = FieldOrProperty(aiType, "IsTouch");
                    _aiVirtualEx = FieldOrProperty(aiType, "virtualEx");
                }
                _coreConfig = FieldOrProperty(coreType, "config");
                _coreHProc = FieldOrProperty(coreType, "hProc");
                _coreHCtrl = FieldOrProperty(coreType, "hCtrl");
                var cfgType = MemberType(_coreConfig);
                if (cfgType != null) _cfgEnableAhegao = FieldOrProperty(cfgType, "enableAhegao");

                harmony.Patch(moveNext, finalizer: new HarmonyMethod(typeof(FaceCtrlSetFaceDiagnostics)
                    .GetMethod(nameof(MoveNextFinalizer), BindingFlags.NonPublic | BindingFlags.Static)));

                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} {iterType.FullName}.MoveNext に診断を適用しました" +
                    "（NullReferenceException が出たときだけ、候補の参照の状態を記録します。例外は抑止しません）。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗: {ex.Message}");
            }
        }

        /// <summary>void を返すので、元の例外はそのまま呼び出し元へ投げ直される。</summary>
        private static void MoveNextFinalizer(object __instance, Exception __exception)
        {
            if (!(__exception is NullReferenceException)) return;

            try
            {
                _count++;
                object faceCtrl = _thisField?.GetValue(__instance);
                object ai = (faceCtrl != null && _faceCtrlAi != null) ? _faceCtrlAi.GetValue(faceCtrl) : null;
                string name = (ai != null ? Get(_aiName, ai) as string : null) ?? "(不明)";

                int n;
                _countByChara.TryGetValue(name, out n);
                _countByChara[name] = n + 1;

                if (_count <= DetailLimit)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} #{_count} {name}: {Describe(__instance, faceCtrl, ai)}");
                    if (_count == DetailLimit)
                        PatcherPlugin.Logger.LogInfo($"{Tag} 詳細は {DetailLimit} 件までです。以降は {SummaryEvery} 件ごとにキャラ別の件数だけ出します。");
                }
                else if (_count % SummaryEvery == 0)
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} 累計 {_count} 件（キャラ別: {string.Join(", ", _countByChara.Select(kv => kv.Key + "=" + kv.Value).ToArray())}）");
                }
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 状態の記録に失敗: {ex.Message}");
            }
        }

        private static string Describe(object iter, object faceCtrl, object ai)
        {
            var sb = new StringBuilder();
            sb.Append("kind=").Append(Safe(() => _kindField?.GetValue(iter)));
            sb.Append(" state=").Append(Safe(() => _stateField?.GetValue(iter)));
            sb.Append(" forceMouthOpen=").Append(Safe(() => _forceMouthField?.GetValue(iter)));
            sb.Append(" frame=").Append(Time.frameCount);

            sb.Append(" | faceCtrl=").Append(NullState(faceCtrl));
            sb.Append(" ai=").Append(NullState(ai));
            if (ai != null)
            {
                sb.Append(" gender=").Append(Safe(() => Get(_aiGender, ai)));
                sb.Append(" InAhegao=").Append(Safe(() => Get(_aiInAhegao, ai)));
                sb.Append(" IsTouch=").Append(Safe(() => Get(_aiIsTouch, ai)));
                sb.Append(" virtualEx=").Append(Safe(() => Get(_aiVirtualEx, ai)));
            }

            var cha = ai != null ? Get(_aiCha, ai) as ChaControl : null;
            sb.Append(" | ai.Cha=").Append(ai == null ? "-" : NullState(Get(_aiCha, ai)));
            if ((object)cha != null && cha != null)
            {
                sb.Append(" personality=").Append(Safe(() => cha.fileParam.personality));
                sb.Append(" mouthCtrl=").Append(NullState(Safe(() => (object)cha.mouthCtrl)));
                sb.Append(" fileCustom.face.baseMakeup=").Append(NullState(Safe(() => (object)cha.fileCustom?.face?.baseMakeup)));
                sb.Append(" customMatFace=").Append(NullState(Safe(() => (object)cha.customMatFace)));
            }

            var config = Get(_coreConfig, null);
            sb.Append(" | Core.config=").Append(NullState(config));
            if (config != null) sb.Append(" enableAhegao=").Append(Safe(() => Get(_cfgEnableAhegao, config)));
            sb.Append(" Core.hCtrl=").Append(NullState(Safe(() => Get(_coreHCtrl, null))));

            var hProc = Get(_coreHProc, null);
            sb.Append(" Core.hProc=").Append(NullState(hProc));
            if (hProc != null && !IsUnityDead(hProc))
            {
                var voice = Safe(() => Traverse.Create(hProc).Field("voice").GetValue()) as HVoiceCtrl;
                sb.Append(" hProc.voice=").Append(NullState(voice));
                if ((object)voice != null && voice != null)
                {
                    var lists = voice.faceLists;
                    sb.Append(" faceLists[0]=").Append(lists == null ? "配列がnull" : lists.Length == 0 ? "空" : NullState(lists[0]));
                }
            }
            return sb.ToString();
        }

        private static string NullState(object o)
        {
            if (o == null) return "null";
            if (o is string s) return s;   // Safe() が返した「取得失敗」の文字列
            return IsUnityDead(o) ? "破棄済み" : "OK";
        }

        private static bool IsUnityDead(object o)
        {
            var uo = o as UnityEngine.Object;
            return (object)uo != null && uo == null;
        }

        private static object Safe(Func<object> f)
        {
            try { return f(); }
            catch (Exception ex) { return "(取得失敗: " + ex.GetType().Name + ")"; }
        }

        private static MemberInfo FieldOrProperty(Type type, string name)
        {
            return (MemberInfo)AccessTools.Field(type, name) ?? AccessTools.Property(type, name);
        }

        private static Type MemberType(MemberInfo m)
        {
            return (m as FieldInfo)?.FieldType ?? (m as PropertyInfo)?.PropertyType;
        }

        private static object Get(MemberInfo m, object instance)
        {
            if (m is FieldInfo f) return f.GetValue(instance);
            if (m is PropertyInfo p) return p.GetValue(instance, null);
            return null;
        }
    }
}
