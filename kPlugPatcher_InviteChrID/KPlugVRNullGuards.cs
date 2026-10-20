using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// kPlug / kPlugVR 本体の null チェック漏れによる NRE を、原因箇所に限定して防ぐガード。
    /// 各件の症状・原因・kPlug の中での直し方の案は docs/KPLUG_ISSUES.md（英語）にまとめてある。
    ///
    /// ※ログ文言には "NullReferenceException" を書かず "NRE" と書く。
    ///   NullReferenceException でログを検索して件数を数えるため、文言に含めると件数が狂う。
    ///
    /// 【G】VRCtrl.SetMenuVisibility()（kPlugVR 3.5.6。3.599 以降は VRCtrl が無いので対象外）
    ///   ほかの5つのメニューは null を確かめてから menuGo.SetActive を呼ぶが、VrRootMenu だけ確かめていない。
    ///   VrRootMenu.menuGo が null／破棄済みのときだけ SetMenuVisibility() 全体をスキップする。
    ///   原型もこの状態では最初の文で例外になり何も更新できないため、スキップしても失われる表示更新は無い。
    ///   呼び出し元 ParseAllInputs() の後続（メニュー表示中のスクロール・ポイント移動）は VrRootMenu に触れない。
    ///
    /// 【H】VRCtrl.LateUpdate()（kPlugVR 3.5.6）
    ///   タイトル画面で作られた VRCtrl は、Hシーンへ移る最初のフレームで自分を Destroy する。
    ///   Destroy はフレームの終わりまで遅延されるため、その 1 フレームだけ後続（Core.dealer などを読む処理）が実行されて NRE になる。
    ///   【G】と同じフレームで起きていることを実機ログで確認済み。
    ///   原型の挙動を一切変えたくないので Prefix ではなく Finalizer を使い、
    ///   **この自壊フレームで発生した NRE だけ**を握り潰す。
    ///   例外より後ろは原型でもどのみち実行されないため、失われる処理は無い。
    ///
    /// 【I】HinputParser.Chain()（static、kPlugVR 3.5.6）
    ///   Core.hCtrl・Core.dealer・Core.mainAi と、hCtrl.VRCtrl・VRCtrl.leads を null を確かめずに読む。
    ///   chainInputDelays が正の間はタイマーを減らすだけで VRCtrl / leads に触れない。
    ///   Hシーン開始時に毎回 1 件 NRE を出す。診断の記録では VRCtrl.leads がまだ代入されていなかった。
    ///   次の条件のときだけ Chain() 全体をスキップする。いずれも原型は
    ///   「何もせず return する」か「副作用を起こす前に NRE になる」ため、失われる処理は無い。
    ///     - Core.hCtrl / Core.dealer / Core.mainAi のいずれかが null
    ///     - chainInputDelays ≦ 0 かつ、hCtrl.VRCtrl または VRCtrl.leads が null
    ///   Core.combo の null はスキップ条件に入れない（combo を触る前に副作用のある分岐があるため）。
    ///   その代わり、ガード条件の外で NRE が出た場合は Finalizer で参照の状態を記録する（例外はそのまま通す）。
    ///   呼び出し元 HComboCtrl.Update() の後続は統計値の読み取りのみで、実行されても問題ない。
    ///   非VR版 kPlug の HCtrl には VRCtrl が無く、この NRE も観測されていないため適用しない。
    ///
    /// 【C】AI_Public.LateUpdate()（kPlugVR）
    ///   kPlugVR は bc.kb.min_exitation を null を確かめずに読む（非VR版 kPlug は bc と bc.kb を確かめている）。
    ///   AI_Public.Start() が AddComponent&lt;BonesCtrl&gt;() した直後は BonesCtrl.Start() が未実行で kb が null。
    ///   Prefix で LateUpdate 全体を止めると残りの処理まで飛ぶため、Transpiler で
    ///   「bc.kb.min_exitation」の読み取りだけを差し替え、kb が null のときは -∞ を返して
    ///   下限補正の if を不成立にする。非VR版と同じ結果になり、LateUpdate の残りは実行される。
    ///   原型で例外になる「kb が本当に null」の場合だけ介入し、破棄済み(fake null)は原型どおり値を読む。
    ///   既に null チェックがある型（非VR版）は、IL を解析して適用対象から外す。
    ///
    /// 【L】AI_Main.Ahegao()（kPlug / kPlugVR とも）
    ///   アヘ顔の頬の色を、Core.heroineCha.customMatFace の Material.SetColor で毎フレーム上書きする。マテリアルの null を確かめていない。
    ///   customMatFace は顔のマテリアルで、顔の素材を指定していない頭 MOD のキャラでは null になる
    ///   （VR の実機ログで1セッション 9,130 回。IL の位置が SetColor の呼び出しと一致することを確認済み）。
    ///   Ahegao() は LateUpdate() の先頭で呼ばれるため、例外が出ると
    ///     ・Ahegao() 末尾の状態記録（_isFemOrg / _isAfterCum / _isKissAct）
    ///     ・LateUpdate() の残り（ChangeEyesBlinkFlag、ぶっかけ処理）
    ///   がまとめて飛ぶ。ガードすると、むしろ原型が意図した処理が実行されるようになる。
    ///   Transpiler で SetColor の呼び出しだけを差し替え、マテリアルが null／破棄済みのときは
    ///   頬の色の上書きだけをスキップする。それ以外は原型と同じ。
    ///   kPlug 設定の EnableAhegao が無効なら Ahegao() は先頭で return するので、この NRE も起きない。
    ///
    /// 【O】AI_Second.Ahegao()（3P の2人目、kPlug / kPlugVR とも）
    ///   【L】の2人目版。対象が Core.heroine2Cha になっただけで、IL の並び（get_customMatFace → ldstr → ldloc → SetColor）も同じ。
    ///   3P で2人目の customMatFace が null（頭 MOD のリストで顔の素材が 0）だと、アヘ顔の表示中に毎フレーム NRE になる
    ///   （VR の実機ログで1セッション 18,047 件）。
    ///   例外で飛ぶのは【L】と同じく Ahegao() 末尾の状態記録と、LateUpdate() の残り（2人目のまばたき設定、ぶっかけのマスク作成）。
    ///   【L】と同じ Transpiler（get_customMatFace の直後の SetColor を探す。heroineCha の名前には頼らない）で SetColor だけを差し替える。
    ///
    /// 【N】FaceCtrl.IE_SetFace()（招待キャラの表情のコルーチン、kPlug / kPlugVR とも）
    ///   アヘ顔中は ai.Cha.customMatFace.SetColor で頬の色を上書きしてから、最後に今の表情（expression）を記録する。
    ///   【L】と同じ null チェック漏れの招待キャラ版。アヘ顔中の招待キャラで customMatFace が null だと、表情を変えるたびに NRE になる
    ///   （通常モードの実機ログで 86件。診断 FaceCtrlSetFaceDiagnostics の 30件すべてが InAhegao=True・customMatFace=null で、
    ///   ほかの参照はすべて有効。IL の位置も SetColor の呼び出しと整合）。
    ///   例外で飛ぶのは頬の色の上書きと、最後の今の表情の記録。
    ///   コルーチンの MoveNext に【L】と同じ Transpiler を当て、SetColor の呼び出しだけを差し替える。
    ///   マテリアルが null／破棄済みのときは頬の色の上書きだけを飛ばし、表情の記録まで実行する。
    ///   どのキャラで起きたかを記録するため、MoveNext の Prefix で「今動いているコルーチン」を覚えておく（読むのはスキップ時だけ）。
    ///
    /// 【M】HCtrl.ParseTouchables()（kPlugVR 3.5.6）
    ///   招待したキャラへのキスの範囲から外れたフレームで kissable_ai が null になるが、キス中の目印 inAiKiss を false に戻すのは
    ///   キスのコルーチンの再開（次のフレーム）なので、その間の ParseTouchables（LateUpdate）が最後の文で
    ///   kissable_ai.KissByPlayer() を呼んで NRE になる。招待したキャラへのキスが終わるたびに1回前後出る（VR の実機ログで1セッション 29件）。
    ///   kPlug 3.6.3 の VR では作者が kissable_ai の null チェックを足している。
    ///   Transpiler で KissByPlayer の呼び出しの前に null チェックを差し込み、null のときはその呼び出しだけを飛ばす。
    ///   原型ではこの文で例外になり、しかもメソッドの最後の文なので、飛ばしても失われる処理は無い。
    ///   IL を読んで、呼び出しの前に kissable_ai の null チェックがある版（3.6.3 以降）には適用しない。
    ///
    /// 【E】【F】ChaTools.CopyLiquidMapToMat(Material mat, bool isFace, ...)（kPlug / kPlugVR とも）
    ///   体・顔の液体（汁）の模様を、メインヒロインのマテリアル（isFace に応じて customMatFace / customMatBody）から
    ///   別のマテリアル mat へ写す。写し元も写し先も null を確かめていない。
    ///   顔の素材を指定していない頭 MOD のキャラ（customMatFace が null）が関わると、ループの1回目で NRE になる。
    ///   呼び出し元: DickBehavior.SetLiquidOnBodyMat（Start から。cha.customMatFace を写し先にする）、TongueCtrl、DickBehavior の竿の模様。
    ///   Hシーン開始時に1〜4件。例外で DickBehavior.Start の残りも飛んでいた。
    ///   Prefix で写し元・写し先のどちらかが null／破棄済みなら原型を実行せず false（「写さなかった」。heroineCha が無いときと同じ戻り値）を返す。
    ///   どちらも null でなければ原型をそのまま実行する。原型は1回目の SetTexture より前に例外になるので、飛ばして失われる処理は無い。
    ///
    /// ログは「スキップ開始」と「解消」の状態変化時だけ出す（毎フレーム出さない）。
    /// </summary>
    internal static class KPlugVRNullGuards
    {
        private const string Tag = "[KPlugVRNullGuard]";

        private sealed class RefComparer : IEqualityComparer<object>
        {
            internal static readonly RefComparer Instance = new RefComparer();
            bool IEqualityComparer<object>.Equals(object x, object y) { return ReferenceEquals(x, y); }
            int IEqualityComparer<object>.GetHashCode(object obj) { return RuntimeHelpers.GetHashCode(obj); }
        }

        private sealed class SkipState
        {
            public int StartFrame;
            public int Count;
        }

        // ===================== 【G】【H】VRCtrl =====================

        private sealed class VRCtrlInfo
        {
            public FieldInfo VrRootMenu;
            public FieldInfo MenuGo;
            public FieldInfo CreatedOnTitle;
            public FieldInfo IsInitOnTitle;
            public FieldInfo ShowMenu;
            public FieldInfo CurMenu;
            public FieldInfo CoreOnTitle;
            public FieldInfo CoreInH;
        }

        private static readonly Dictionary<Type, VRCtrlInfo> _vrCtrlInfoByType = new Dictionary<Type, VRCtrlInfo>();
        private static readonly Dictionary<object, SkipState> _menuSkipByInstance = new Dictionary<object, SkipState>(RefComparer.Instance);
        private static readonly Dictionary<object, SkipState> _lateUpdateSwallowByInstance = new Dictionary<object, SkipState>(RefComparer.Instance);

        /// <summary>自壊フレームは 1〜2 フレームで終わるはず。これを超えて続くなら想定外なので警告する。</summary>
        private const int UnexpectedSwallowCount = 10;

        // ===================== 【I】HinputParser.Chain =====================

        private sealed class ChainInfo
        {
            public FieldInfo CoreHCtrl;
            public FieldInfo CoreDealer;
            public FieldInfo CoreMainAi;
            public FieldInfo CoreCombo;
            public FieldInfo CoreInH;
            public PropertyInfo HCtrlVRCtrl;
            public FieldInfo VRCtrlLeads;
            public FieldInfo ChainInputDelays;
        }

        private static ChainInfo _chainInfo;
        private static SkipState _chainSkip;
        private static string _chainSkipReason;
        private static int _chainUnguardedNreLogged;

        /// <summary>ガード条件の外で出た NRE の記録回数の上限（毎フレーム出る場合にログを埋めないため）。</summary>
        private const int MaxChainUnguardedNreLogs = 3;

        // ===================== 【C】AI_Public.LateUpdate =====================

        private sealed class KbFields
        {
            public FieldInfo Kb;
            public FieldInfo MinExitation;
        }

        private static readonly Dictionary<Type, KbFields> _kbFieldsByBonesType = new Dictionary<Type, KbFields>();
        private static readonly Dictionary<object, SkipState> _kbSkipByInstance = new Dictionary<object, SkipState>(RefComparer.Instance);
        private static bool _loggedBcNull;
        private static bool _loggedKbFieldsMissing;
        private static int _lastTranspileReplaced;

        // ===================== 【L】AI_Main.Ahegao =====================

        /// <summary>Core.heroineCha は1体なので、インスタンス別ではなく1つの状態で足りる。</summary>
        private static SkipState _faceColorSkip;

        /// <summary>スキップが続いているときに途中経過を出す間隔（回）。60fps で約5分。【O】も同じ間隔。</summary>
        private const int FaceColorSkipReportInterval = 18000;

        // ===================== 【O】AI_Second.Ahegao =====================

        /// <summary>Core.heroine2Cha も1体なので、【L】と同じく1つの状態で足りる。</summary>
        private static SkipState _secondFaceColorSkip;

        // ===================== 【N】FaceCtrl.IE_SetFace =====================

        /// <summary>今 MoveNext を実行中の IE_SetFace のコルーチン（スキップしたキャラの名前を引くため）。メインスレッドのみ。</summary>
        private static object _currentSetFaceIter;

        /// <summary>招待キャラごとのスキップ回数。IE_SetFace は表情を変えるたびに1回なので、毎フレームではない。</summary>
        private static readonly Dictionary<string, int> _guestFaceColorSkips = new Dictionary<string, int>();
        private static int _guestFaceColorSkipTotal;

        /// <summary>スキップの途中経過を出す間隔（回、全キャラ合計）。</summary>
        private const int GuestFaceColorSkipReportInterval = 50;

        // ===================== 【M】HCtrl.ParseTouchables =====================

        /// <summary>kissable_ai が null で KissByPlayer を飛ばした回数（ログ用）。</summary>
        private static int _kissByPlayerSkipCount;

        /// <summary>飛ばした回数の途中経過を出す間隔（回）。招待したキャラへのキスが終わるたびに1回前後なので小さめ。</summary>
        private const int KissByPlayerSkipReportInterval = 50;

        // ===================== 【E】【F】ChaTools.CopyLiquidMapToMat =====================

        /// <summary>Core.heroineCha の getter（kPlug / kPlugVR のうち、パッチした ChaTools と同じアセンブリの Core）。</summary>
        private static MethodInfo _liquidHeroineChaGetter;

        /// <summary>飛ばした理由ごとの回数（"写し先 mat" / "写し元 customMatFace" など）。Hシーン開始時に数回なので、最初の1回と50回ごとに出す。</summary>
        private static readonly Dictionary<string, int> _liquidCopySkips = new Dictionary<string, int>();
        private static int _liquidCopySkipTotal;
        private const int LiquidCopySkipReportInterval = 50;

        // ===================== 適用 =====================

        /// <summary>
        /// 適用済みの対象（型＋メソッド）。
        /// 【G】【H】は「タイトル画面で作られた VRCtrl が自壊するフレーム」で起きるため、
        /// PatcherPlugin 側の 300 フレーム待ちを過ぎてからでは間に合わないことがある。
        /// そのため TryApply は型が見つかるまで繰り返し呼ばれる。二重適用をこのキーで防ぐ。
        /// </summary>
        private static readonly HashSet<string> _appliedKeys = new HashSet<string>();

        /// <summary>適用失敗のログを対象ごとに 1 回だけ出すためのキー（失敗しても再試行はする）。</summary>
        private static readonly HashSet<string> _failureLoggedKeys = new HashSet<string>();

        private static bool AlreadyApplied(string key) { return _appliedKeys.Contains(key); }
        private static void MarkApplied(string key) { _appliedKeys.Add(key); }

        private static void LogApplyFailureOnce(string key, string target, Exception ex)
        {
            if (_failureLoggedKeys.Add(key)) LogApplyFailure(target, ex);
        }

        internal static void TryApply(Harmony harmony)
        {
            bool wantVRCtrl = PatcherPlugin.EnableVRRootMenuVisibilityGuard.Value
                           || PatcherPlugin.EnableVRCtrlLateUpdateGuard.Value;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.FullName.Contains("kPlug")) continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                if (wantVRCtrl) TryPatchVRCtrl(harmony, types);
                if (PatcherPlugin.EnableHinputParserChainGuard.Value) TryPatchHinputParser(harmony, types);
                if (PatcherPlugin.EnableAIPublicKbGuard.Value) TryPatchAIPublic(harmony, types);
                if (PatcherPlugin.EnableAIMainFaceColorGuard.Value) TryPatchAhegao(harmony, types, ".CmpH.AI_Main", nameof(AIMainAhegaoTranspiler));
                if (PatcherPlugin.EnableAISecondFaceColorGuard.Value) TryPatchAhegao(harmony, types, ".CmpH.AI_Second", nameof(AISecondAhegaoTranspiler));
                if (PatcherPlugin.EnableGuestFaceColorGuard.Value) TryPatchFaceCtrlSetFace(harmony, types);
                if (PatcherPlugin.EnableHCtrlKissGuard.Value) TryPatchHCtrlParseTouchables(harmony, types);
                if (PatcherPlugin.EnableCopyLiquidMapGuard.Value) TryPatchCopyLiquidMapToMat(harmony, types);
            }
        }

        private static HarmonyMethod GuardMethod(string name)
        {
            return new HarmonyMethod(typeof(KPlugVRNullGuards).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
        }

        private static void TryPatchVRCtrl(Harmony harmony, Type[] types)
        {
            // 非VRモードでは kPlugVR のクラスをロードできず、この型は列挙結果に現れない（実機ログで確認済み）。
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".CmpH.VRCtrl"));
            if (type == null)
            {
                // kPlug 3.599 以降の VR では VRCtrl 自体が廃止された（kPlugVR.Menu.* に再設計）。
                // 「対象が無いので黙ってスキップ」だと次回ログで理由が分からないため、一度だけ記録する。
                bool isNewVrAssembly = types.Any(t => t != null && t.FullName != null
                    && (t.FullName.EndsWith(".Menu.MenuInvite") || t.FullName.EndsWith(".Inputs.VR")));
                if (isNewVrAssembly && _failureLoggedKeys.Add("VRCtrl|missing"))
                {
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag} VRCtrl が見つかりません。kPlug 3.599 以降の VR では VRCtrl が廃止されたため、" +
                        "SetMenuVisibility / LateUpdate のガードは対象外です（新実装で同じ不具合が起きるかは未調査）。");
                }
                return;
            }

            try
            {
                var info = new VRCtrlInfo
                {
                    VrRootMenu = AccessTools.Field(type, "VrRootMenu"),
                    CreatedOnTitle = AccessTools.Field(type, "createdOnTitle"),
                    IsInitOnTitle = AccessTools.Field(type, "isInitOnTitle"),
                    ShowMenu = AccessTools.Field(type, "ShowMenu"),
                    CurMenu = AccessTools.Field(type, "curMenu"),
                };
                if (info.VrRootMenu != null) info.MenuGo = AccessTools.Field(info.VrRootMenu.FieldType, "menuGo");

                var coreType = ResolveCoreType(type);
                if (coreType != null)
                {
                    info.CoreOnTitle = AccessTools.Field(coreType, "onTitle");
                    info.CoreInH = AccessTools.Field(coreType, "inH");
                }

                _vrCtrlInfoByType[type] = info;

                // 【G】SetMenuVisibility
                var menuKey = type.FullName + "|SetMenuVisibility";
                if (PatcherPlugin.EnableVRRootMenuVisibilityGuard.Value && !AlreadyApplied(menuKey))
                {
                    if (info.VrRootMenu == null || info.MenuGo == null)
                    {
                        MarkApplied(menuKey);
                        PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}: VrRootMenu / menuGo が見つからないため SetMenuVisibility のガードを適用できません。");
                    }
                    else
                    {
                        var method = AccessTools.DeclaredMethod(type, "SetMenuVisibility", Type.EmptyTypes);
                        if (method == null)
                        {
                            MarkApplied(menuKey);
                            PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}.SetMenuVisibility() が見つかりませんでした。");
                        }
                        else
                        {
                            harmony.Patch(method, GuardMethod(nameof(SetMenuVisibilityPrefix)));
                            MarkApplied(menuKey);
                            PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName}.SetMenuVisibility() に VrRootMenu.menuGo の null ガードを適用しました（frame={Time.frameCount}）。");
                        }
                    }
                }

                // 【H】LateUpdate（自壊フレームの NRE 抑止）
                var lateUpdateKey = type.FullName + "|LateUpdate";
                if (PatcherPlugin.EnableVRCtrlLateUpdateGuard.Value && !AlreadyApplied(lateUpdateKey))
                {
                    if (info.CreatedOnTitle == null || info.CoreOnTitle == null)
                    {
                        MarkApplied(lateUpdateKey);
                        PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}: createdOnTitle / Core.onTitle が見つからないため LateUpdate のガードを適用できません。");
                    }
                    else
                    {
                        var lateUpdate = AccessTools.DeclaredMethod(type, "LateUpdate", Type.EmptyTypes);
                        if (lateUpdate == null)
                        {
                            MarkApplied(lateUpdateKey);
                            PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}.LateUpdate() が見つかりませんでした。");
                        }
                        else
                        {
                            harmony.Patch(lateUpdate, finalizer: GuardMethod(nameof(VRCtrlLateUpdateFinalizer)));
                            MarkApplied(lateUpdateKey);
                            PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName}.LateUpdate() に自壊フレームの NRE 抑止を適用しました（frame={Time.frameCount}）。");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogApplyFailureOnce(type.FullName + "|VRCtrl", type.FullName, ex);
            }
        }

        private static void TryPatchHinputParser(Harmony harmony, Type[] types)
        {
            // 3.599 以降の VR では HinputParser が廃止され kPlugVR.Inputs.VR.Chain(VRCtrls.Ctrl) になった。
            // どちらが居るかで対象を切り替える（バージョン判定はしない）。
            bool isNewApi = false;
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".Inputs.HinputParser"));
            if (type == null)
            {
                type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".Inputs.VR"));
                isNewApi = type != null;
            }
            if (type == null) return;

            var chainKey = type.FullName + "|Chain";
            if (AlreadyApplied(chainKey)) return;

            try
            {
                var info = new ChainInfo { ChainInputDelays = AccessTools.Field(type, "chainInputDelays") };

                var coreType = ResolveCoreType(type);
                if (coreType != null)
                {
                    info.CoreHCtrl = AccessTools.Field(coreType, "hCtrl");
                    info.CoreDealer = AccessTools.Field(coreType, "dealer");
                    info.CoreMainAi = AccessTools.Field(coreType, "mainAi");
                    info.CoreCombo = AccessTools.Field(coreType, "combo");
                    info.CoreInH = AccessTools.Field(coreType, "inH");
                }
                if (info.CoreHCtrl != null) info.HCtrlVRCtrl = AccessTools.Property(info.CoreHCtrl.FieldType, "VRCtrl");

                // 旧API(HinputParser)は VR と非VR の両方に存在するため、HCtrl.VRCtrl の有無で VR 版を判別する。
                // 新API(Inputs.VR)は VR 版にしか存在しないので、この判別は不要。
                if (!isNewApi && info.HCtrlVRCtrl == null)
                {
                    MarkApplied(chainKey);
                    PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName}.Chain() は HCtrl.VRCtrl を持たない（非VR版の）ため、ガードは適用しません。");
                    return;
                }
                if (info.CoreDealer == null || info.CoreMainAi == null || info.CoreHCtrl == null)
                {
                    MarkApplied(chainKey);
                    PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}: Core.hCtrl / Core.dealer / Core.mainAi が見つからないため Chain のガードを適用できません。");
                    return;
                }

                // 旧API では VRCtrl.leads の未代入が実際の原因だった（実機の診断ログで確認）。
                // 新API では引数 ctrl.inputs に変わり、kPlug 自身が ctrl == null を確認しているため対象外。
                if (!isNewApi && info.HCtrlVRCtrl != null)
                {
                    info.VRCtrlLeads = AccessTools.Field(info.HCtrlVRCtrl.PropertyType, "leads");
                }

                // 旧API: Chain()（引数なし） / 新API: Chain(VRCtrls.Ctrl)
                var method = isNewApi
                    ? AccessTools.DeclaredMethod(type, "Chain")
                    : AccessTools.DeclaredMethod(type, "Chain", Type.EmptyTypes);
                if (method == null || !method.IsStatic)
                {
                    MarkApplied(chainKey);
                    PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}.Chain()（static）が見つかりませんでした。");
                    return;
                }

                _chainInfo = info;
                harmony.Patch(method,
                    prefix: GuardMethod(nameof(HinputParserChainPrefix)),
                    finalizer: GuardMethod(nameof(HinputParserChainFinalizer)));
                MarkApplied(chainKey);
                PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName}.Chain() に参照未確定時の null ガードを適用しました（frame={Time.frameCount}）。");
            }
            catch (Exception ex)
            {
                LogApplyFailureOnce(chainKey, $"{type.FullName}.Chain()", ex);
            }
        }

        private static void TryPatchAIPublic(Harmony harmony, Type[] types)
        {
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".CmpH.AI_Public"));
            if (type == null) return;

            var aiKey = type.FullName + "|LateUpdate";
            if (AlreadyApplied(aiKey)) return;

            try
            {
                var bcField = AccessTools.Field(type, "bc");
                KbFields fields = bcField != null ? GetKbFields(bcField.FieldType) : null;
                if (fields == null)
                {
                    MarkApplied(aiKey);
                    PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}: bc / kb / min_exitation が見つからないため LateUpdate のガードを適用できません。");
                    return;
                }

                var method = AccessTools.DeclaredMethod(type, "LateUpdate", Type.EmptyTypes);
                if (method == null)
                {
                    MarkApplied(aiKey);
                    PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}.LateUpdate() が見つかりませんでした。");
                    return;
                }

                // 適用前に IL を解析し、「kb の読み取りがすべて min_exitation の読み取りに直結している」
                // （= null チェックが無い）場合だけ適用する。非VR版は作者が null チェックを入れ済み。
                var original = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
                int kbLoads = 0, pairs = 0;
                for (int i = 0; i < original.Count; i++)
                {
                    if (!original[i].LoadsField(fields.Kb)) continue;
                    kbLoads++;
                    if (i + 1 < original.Count && original[i + 1].LoadsField(fields.MinExitation)) pairs++;
                }

                if (pairs == 0)
                {
                    MarkApplied(aiKey);
                    PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName}.LateUpdate() に bc.kb.min_exitation の参照が無いため、ガードは適用しません。");
                    return;
                }
                if (kbLoads != pairs)
                {
                    MarkApplied(aiKey);
                    PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName}.LateUpdate() には既に bc.kb の null チェックがあるため、ガードは適用しません（kb 参照 {kbLoads} 箇所 / うち min_exitation 直結 {pairs} 箇所）。");
                    return;
                }

                _lastTranspileReplaced = 0;
                harmony.Patch(method, transpiler: GuardMethod(nameof(AIPublicLateUpdateTranspiler)));
                MarkApplied(aiKey);

                if (_lastTranspileReplaced == pairs)
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName}.LateUpdate() の bc.kb.min_exitation 参照 {pairs} 箇所に null ガードを適用しました（frame={Time.frameCount}）。");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}.LateUpdate(): 置換数が想定と異なります（想定 {pairs} / 実際 {_lastTranspileReplaced}）。");
                }
            }
            catch (Exception ex)
            {
                LogApplyFailureOnce(aiKey, $"{type.FullName}.LateUpdate()", ex);
            }
        }

        /// <summary>【L】AI_Main・【O】AI_Second の Ahegao() に、customMatFace.SetColor の null ガードを当てる（処理は共通）。</summary>
        private static void TryPatchAhegao(Harmony harmony, Type[] types, string typeSuffix, string transpilerName)
        {
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(typeSuffix));
            if (type == null) return;

            var key = type.FullName + "|Ahegao";
            if (AlreadyApplied(key)) return;

            try
            {
                var method = AccessTools.DeclaredMethod(type, "Ahegao", Type.EmptyTypes);
                if (method == null)
                {
                    MarkApplied(key);
                    PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}.Ahegao() が見つかりませんでした。");
                    return;
                }

                // 適用前に IL を読み、「customMatFace の直後に SetColor」という原型の並びがあるか確かめる。
                // 本体側で null チェックが入った版では並びが変わるため、その場合は適用しない。
                var original = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
                int targets = CountFaceColorSetCalls(original);
                if (targets == 0)
                {
                    MarkApplied(key);
                    PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName}.Ahegao() に customMatFace.SetColor の並びが無いため、ガードは適用しません。");
                    return;
                }

                _lastTranspileReplaced = 0;
                harmony.Patch(method, transpiler: GuardMethod(transpilerName));
                MarkApplied(key);

                if (_lastTranspileReplaced == targets)
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName}.Ahegao() の customMatFace.SetColor {targets} 箇所に null ガードを適用しました（frame={Time.frameCount}）。");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}.Ahegao(): 置換数が想定と異なります（想定 {targets} / 実際 {_lastTranspileReplaced}）。");
                }
            }
            catch (Exception ex)
            {
                LogApplyFailureOnce(key, $"{type.FullName}.Ahegao()", ex);
            }
        }

        private static void TryPatchFaceCtrlSetFace(Harmony harmony, Type[] types)
        {
            // コルーチンのクラス名（d__83 など）は版で変わるので、FullName が「….CmpChara.FaceCtrl+<IE_SetFace>…」のものを探す。
            // 通常モードで kPlugVR の型に深く触れると Mono がネイティブで落ちるため、動作中のモードの型だけを対象にする
            // （FullName の文字列だけで判定し、DeclaringType などはたどらない）。
            string root = BepInEx.Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase) ? "kPlugVR." : "kPlug.";
            const string marker = ".CmpChara.FaceCtrl+<IE_SetFace>";
            var type = types.FirstOrDefault(t => t != null && t.FullName != null
                && t.FullName.StartsWith(root, StringComparison.Ordinal) && t.FullName.Contains(marker));
            if (type == null) return;

            var key = type.FullName + "|MoveNext";
            if (AlreadyApplied(key)) return;
            var label = type.FullName.Substring(0, type.FullName.IndexOf('+')) + ".IE_SetFace";

            try
            {
                var method = AccessTools.DeclaredMethod(type, "MoveNext", Type.EmptyTypes);
                if (method == null)
                {
                    MarkApplied(key);
                    PatcherPlugin.Logger.LogWarning($"{Tag} {label} のコルーチンの MoveNext が見つかりませんでした。");
                    return;
                }

                // 【L】と同じく、適用前に「customMatFace の直後に SetColor」という原型の並びがあるか確かめる。
                var original = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
                int targets = CountFaceColorSetCalls(original);
                if (targets == 0)
                {
                    MarkApplied(key);
                    PatcherPlugin.Logger.LogInfo($"{Tag} {label} に customMatFace.SetColor の並びが無いため、ガードは適用しません。");
                    return;
                }

                _lastTranspileReplaced = 0;
                harmony.Patch(method, prefix: GuardMethod(nameof(SetFaceMoveNextPrefix)), transpiler: GuardMethod(nameof(FaceCtrlSetFaceTranspiler)));
                MarkApplied(key);

                if (_lastTranspileReplaced == targets)
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} {label} の customMatFace.SetColor {targets} 箇所に null ガードを適用しました（frame={Time.frameCount}）。");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {label}: 置換数が想定と異なります（想定 {targets} / 実際 {_lastTranspileReplaced}）。");
                }
            }
            catch (Exception ex)
            {
                LogApplyFailureOnce(key, label, ex);
            }
        }

        private static void TryPatchHCtrlParseTouchables(Harmony harmony, Type[] types)
        {
            // HCtrl は非VR版 kPlug にもあるが、ParseTouchables（VR の手・キスの判定）は kPlugVR だけにある。
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".CmpH.HCtrl"));
            if (type == null) return;

            var key = type.FullName + "|ParseTouchables";
            if (AlreadyApplied(key)) return;

            try
            {
                var method = AccessTools.DeclaredMethod(type, "ParseTouchables", Type.EmptyTypes);
                var kissable = AccessTools.Field(type, "kissable_ai");
                if (method == null || kissable == null)
                {
                    // 非VR版 kPlug の HCtrl。対象外なので黙ってスキップする（型が見つかるたびに呼ばれるため記録済みにする）。
                    MarkApplied(key);
                    return;
                }

                var original = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
                int unguarded = 0, guarded = 0;
                for (int i = 1; i < original.Count; i++)
                {
                    if (!IsKissByPlayerCall(original[i]) || !original[i - 1].LoadsField(kissable)) continue;
                    if (HasKissableNullCheckBefore(original, i, kissable)) guarded++;
                    else unguarded++;
                }

                if (unguarded == 0)
                {
                    MarkApplied(key);
                    PatcherPlugin.Logger.LogInfo(guarded > 0
                        ? $"{Tag} {type.FullName}.ParseTouchables() には既に kissable_ai の null チェックがあるため、ガードは適用しません（kPlug 3.63 以降の VR は作者修正済み）。"
                        : $"{Tag} {type.FullName}.ParseTouchables() に kissable_ai.KissByPlayer() の呼び出しが無いため、ガードは適用しません。");
                    return;
                }

                _lastTranspileReplaced = 0;
                harmony.Patch(method, transpiler: GuardMethod(nameof(HCtrlParseTouchablesTranspiler)));
                MarkApplied(key);

                if (_lastTranspileReplaced == unguarded)
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName}.ParseTouchables() の kissable_ai.KissByPlayer() {unguarded} 箇所に null ガードを適用しました（frame={Time.frameCount}）。");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}.ParseTouchables(): 置換数が想定と異なります（想定 {unguarded} / 実際 {_lastTranspileReplaced}）。");
                }
            }
            catch (Exception ex)
            {
                LogApplyFailureOnce(key, $"{type.FullName}.ParseTouchables()", ex);
            }
        }

        private static void TryPatchCopyLiquidMapToMat(Harmony harmony, Type[] types)
        {
            // 【N】と同じく、動作中のモードの型だけを FullName の文字列で選ぶ（通常モードで kPlugVR の型に深く触れない）。
            string root = BepInEx.Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase) ? "kPlugVR." : "kPlug.";
            var type = types.FirstOrDefault(t => t != null && t.FullName == root + "Tools.ChaTools");
            if (type == null) return;

            var key = type.FullName + "|CopyLiquidMapToMat";
            if (AlreadyApplied(key)) return;

            try
            {
                var method = AccessTools.DeclaredMethod(type, "CopyLiquidMapToMat",
                    new[] { typeof(Material), typeof(bool), typeof(Vector2), typeof(Vector2), typeof(bool) });
                var core = types.FirstOrDefault(t => t != null && t.FullName == root + "Core");
                var getter = core != null ? AccessTools.PropertyGetter(core, "heroineCha") : null;
                if (method == null || method.ReturnType != typeof(bool) || getter == null || getter.ReturnType != typeof(ChaControl))
                {
                    MarkApplied(key);
                    PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}.CopyLiquidMapToMat または {root}Core.heroineCha が想定の形で見つからないため、ガードは適用しません。");
                    return;
                }

                _liquidHeroineChaGetter = getter;
                harmony.Patch(method, prefix: GuardMethod(nameof(CopyLiquidMapToMatPrefix)));
                MarkApplied(key);
                PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName}.CopyLiquidMapToMat() にマテリアルの null ガードを適用しました（frame={Time.frameCount}）。");
            }
            catch (Exception ex)
            {
                LogApplyFailureOnce(key, $"{type.FullName}.CopyLiquidMapToMat()", ex);
            }
        }

        // ===================== 【G】Prefix =====================

        private static bool SetMenuVisibilityPrefix(object __instance)
        {
            try
            {
                VRCtrlInfo info;
                if (__instance == null || !_vrCtrlInfoByType.TryGetValue(__instance.GetType(), out info)) return true;

                string reason = null;
                object menu = info.VrRootMenu.GetValue(__instance);
                if (ReferenceEquals(menu, null))
                {
                    reason = "VrRootMenu が null";
                }
                else
                {
                    object go = info.MenuGo.GetValue(menu);
                    var goObj = go as UnityEngine.Object;
                    if (ReferenceEquals(go, null)) reason = "VrRootMenu.menuGo が未生成(null)";
                    else if (goObj == null) reason = "VrRootMenu.menuGo が破棄済み";
                }

                int frame = Time.frameCount;
                SkipState state;

                if (reason == null)
                {
                    if (_menuSkipByInstance.Count > 0 && _menuSkipByInstance.TryGetValue(__instance, out state))
                    {
                        _menuSkipByInstance.Remove(__instance);
                        PatcherPlugin.Logger.LogInfo(
                            $"{Tag}[VRCtrl.SetMenuVisibility] {DescribeComponent(__instance)} の VrRootMenu.menuGo が有効になりました。" +
                            $"スキップ {state.Count} 回（frame {state.StartFrame}→{frame}）。");
                    }
                    return true;
                }

                if (!_menuSkipByInstance.TryGetValue(__instance, out state))
                {
                    PurgeDestroyedKeys(_menuSkipByInstance);
                    state = new SkipState { StartFrame = frame };
                    _menuSkipByInstance[__instance] = state;
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag}[VRCtrl.SetMenuVisibility] {DescribeComponent(__instance)}: {reason} のため SetMenuVisibility をスキップしました。" +
                        $"frame={frame}, createdOnTitle={SafeValue(info.CreatedOnTitle, __instance)}, isInitOnTitle={SafeValue(info.IsInitOnTitle, __instance)}, " +
                        $"Core.onTitle={SafeValue(info.CoreOnTitle, null)}, Core.inH={SafeValue(info.CoreInH, null)}, " +
                        $"ShowMenu={SafeValue(info.ShowMenu, __instance)}, curMenu={SafeValue(info.CurMenu, __instance)}, " +
                        $"VRCtrl生存数={CountAlive(__instance.GetType())}");
                }
                state.Count++;
                return false;
            }
            catch (Exception ex)
            {
                SafeLogWarning("VRCtrl.SetMenuVisibility", ex);
                return true;
            }
        }

        // ===================== 【H】Finalizer =====================

        /// <summary>
        /// VRCtrl.LateUpdate() が「タイトル画面で作られた VRCtrl が自分を破棄するフレーム」で出す
        /// NRE だけを握り潰す。例外より後ろの処理は原型でも実行されないため、
        /// 握り潰しても失われる処理は無い。それ以外の例外・状況はそのまま外へ出す。
        /// </summary>
        private static Exception VRCtrlLateUpdateFinalizer(Exception __exception, object __instance)
        {
            if (__exception == null) return null;

            try
            {
                if (!(__exception is NullReferenceException)) return __exception;

                VRCtrlInfo info;
                if (__instance == null || !_vrCtrlInfoByType.TryGetValue(__instance.GetType(), out info)) return __exception;
                if (info.CreatedOnTitle == null || info.CoreOnTitle == null) return __exception;

                bool createdOnTitle = (bool)info.CreatedOnTitle.GetValue(__instance);
                bool onTitle = (bool)info.CoreOnTitle.GetValue(null);
                if (!createdOnTitle || onTitle) return __exception; // 自壊フレーム以外は原型どおり外へ出す

                int frame = Time.frameCount;
                SkipState state;
                if (!_lateUpdateSwallowByInstance.TryGetValue(__instance, out state))
                {
                    PurgeDestroyedKeys(_lateUpdateSwallowByInstance);
                    state = new SkipState { StartFrame = frame };
                    _lateUpdateSwallowByInstance[__instance] = state;
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag}[VRCtrl.LateUpdate] {DescribeComponent(__instance)}: タイトル画面で作られた VRCtrl が自分を破棄するフレームの " +
                        $"NRE を抑止しました（createdOnTitle=True, Core.onTitle=False, frame={frame}）。");
                }
                state.Count++;

                if (state.Count == UnexpectedSwallowCount)
                {
                    PatcherPlugin.Logger.LogWarning(
                        $"{Tag}[VRCtrl.LateUpdate] {DescribeComponent(__instance)}: 抑止が {state.Count} 回に達しました。" +
                        $"自壊フレームは 1〜2 フレームで終わるはずで、想定外です（frame {state.StartFrame}→{frame}）。");
                }

                return null;
            }
            catch (Exception ex)
            {
                SafeLogWarning("VRCtrl.LateUpdate", ex);
                return __exception;
            }
        }

        // ===================== 【I】Prefix / Finalizer =====================

        private static bool HinputParserChainPrefix()
        {
            var info = _chainInfo;
            if (info == null) return true;

            try
            {
                string reason = GetChainSkipReason(info);
                int frame = Time.frameCount;

                if (reason == null)
                {
                    if (_chainSkip != null)
                    {
                        PatcherPlugin.Logger.LogInfo(
                            $"{Tag}[HinputParser.Chain] 参照がそろいました（直前の理由: {_chainSkipReason}）。" +
                            $"スキップ {_chainSkip.Count} 回（frame {_chainSkip.StartFrame}→{frame}）。");
                        _chainSkip = null;
                        _chainSkipReason = null;
                    }
                    return true;
                }

                if (_chainSkip == null)
                {
                    _chainSkip = new SkipState { StartFrame = frame };
                    _chainSkipReason = reason;
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag}[HinputParser.Chain] {reason} のため Chain をスキップしました" +
                        $"（原型もこの状態では何もせず終わるか、副作用の前に NRE になるため、失われる処理はありません）。{ChainSnapshot(info)}");
                }
                else if (_chainSkipReason != reason)
                {
                    _chainSkipReason = reason;
                    PatcherPlugin.Logger.LogInfo($"{Tag}[HinputParser.Chain] スキップ理由が変化しました: {reason}。{ChainSnapshot(info)}");
                }
                _chainSkip.Count++;
                return false;
            }
            catch (Exception ex)
            {
                SafeLogWarning("HinputParser.Chain", ex);
                return true;
            }
        }

        /// <summary>
        /// 原型の Chain() が「何もしない」か「副作用の前に NRE になる」ことが確実な状態なら理由を返す。
        /// 原型は true-null でのみ例外になる（破棄済み MonoBehaviour のフィールド・プロパティ読み取りは例外にならない）ので、
        /// ReferenceEquals で判定する。
        /// </summary>
        private static string GetChainSkipReason(ChainInfo info)
        {
            object hCtrl = info.CoreHCtrl.GetValue(null);
            if (ReferenceEquals(hCtrl, null)) return "Core.hCtrl が null";
            if (ReferenceEquals(info.CoreDealer.GetValue(null), null)) return "Core.dealer が null";
            if (ReferenceEquals(info.CoreMainAi.GetValue(null), null)) return "Core.mainAi が null";

            // ここから先は旧API(HinputParser)だけの判定。
            // 新API(Inputs.VR)は引数 ctrl を kPlug 自身が null チェックしており、VRCtrl / leads も存在しない。
            if (info.HCtrlVRCtrl == null) return null;

            // chainInputDelays > 0 の間、原型はタイマーを減らすだけで VRCtrl / leads に触れない。
            // その減算を止めないよう、この間はスキップしない。
            if (info.ChainInputDelays != null)
            {
                var delays = info.ChainInputDelays.GetValue(null);
                if (delays is float && (float)delays > 0f) return null;
            }

            object vrCtrl = info.HCtrlVRCtrl.GetValue(hCtrl, null);
            if (ReferenceEquals(vrCtrl, null)) return "Core.hCtrl.VRCtrl が null";
            if (info.VRCtrlLeads != null && ReferenceEquals(info.VRCtrlLeads.GetValue(vrCtrl), null)) return "VRCtrl.leads が null";
            return null;
        }

        /// <summary>
        /// ガード条件の外で NRE が出た場合に、原因切り分け用の状態を記録する。例外はそのまま外へ出す（抑止しない）。
        /// </summary>
        private static Exception HinputParserChainFinalizer(Exception __exception)
        {
            if (__exception == null) return null;

            try
            {
                if (__exception is NullReferenceException && _chainInfo != null && _chainUnguardedNreLogged < MaxChainUnguardedNreLogs)
                {
                    _chainUnguardedNreLogged++;
                    PatcherPlugin.Logger.LogWarning(
                        $"{Tag}[HinputParser.Chain] ガード条件の外で NRE が発生しました（例外はそのまま通します。" +
                        $"記録 {_chainUnguardedNreLogged}/{MaxChainUnguardedNreLogs} 回目）。{ChainSnapshot(_chainInfo)}");
                }
            }
            catch { }

            return __exception;
        }

        private static string ChainSnapshot(ChainInfo info)
        {
            object hCtrl = SafeGetStatic(info.CoreHCtrl);
            object vrCtrl = null;
            object leads = null;
            try { if (!ReferenceEquals(hCtrl, null)) vrCtrl = info.HCtrlVRCtrl.GetValue(hCtrl, null); } catch { }
            try { if (!ReferenceEquals(vrCtrl, null) && info.VRCtrlLeads != null) leads = info.VRCtrlLeads.GetValue(vrCtrl); } catch { }

            return $"frame={Time.frameCount}, Core.hCtrl={RefState(hCtrl)}, Core.dealer={RefStateOf(info.CoreDealer)}, " +
                   $"Core.mainAi={RefStateOf(info.CoreMainAi)}, hCtrl.VRCtrl={(ReferenceEquals(hCtrl, null) ? "?" : RefState(vrCtrl))}, " +
                   $"VRCtrl.leads={(ReferenceEquals(vrCtrl, null) || info.VRCtrlLeads == null ? "?" : RefState(leads))}, " +
                   $"Core.combo={RefStateOf(info.CoreCombo)}, chainInputDelays={SafeValue(info.ChainInputDelays, null)}, " +
                   $"Core.inH={SafeValue(info.CoreInH, null)}";
        }

        // ===================== 【C】Transpiler と差し替え先 =====================

        private static IEnumerable<CodeInstruction> AIPublicLateUpdateTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var list = instructions.ToList();
            var bcField = original != null ? AccessTools.Field(original.DeclaringType, "bc") : null;
            var fields = bcField != null ? GetKbFields(bcField.FieldType) : null;
            if (fields == null) return list;

            var helper = AccessTools.Method(typeof(KPlugVRNullGuards), nameof(GetMinExitationOrNegativeInfinity));
            int replaced = 0;

            // 「ldfld BonesCtrl::kb ; ldfld KokanBehavior::min_exitation」を「call GetMinExitationOrNegativeInfinity(object)」へ。
            // スタック上は [bc] → [float] で前後の型が一致する。
            for (int i = 0; i < list.Count - 1; i++)
            {
                if (!list[i].LoadsField(fields.Kb) || !list[i + 1].LoadsField(fields.MinExitation)) continue;

                var call = new CodeInstruction(OpCodes.Call, helper);
                list[i].MoveLabelsTo(call);
                list[i].MoveBlocksTo(call);
                list[i + 1].MoveLabelsTo(call);
                list[i + 1].MoveBlocksTo(call);
                list[i] = call;
                list.RemoveAt(i + 1);
                replaced++;
            }

            _lastTranspileReplaced = replaced;
            return list;
        }

        /// <summary>
        /// 原型の <c>bc.kb.min_exitation</c> の代わりに呼ばれる。kb が本当に null（原型で NRE になる状態）の
        /// ときだけ -∞ を返し、<c>virtualEx &lt; ...</c> を不成立にする。それ以外は原型と同じ値を返す。
        /// </summary>
        private static float GetMinExitationOrNegativeInfinity(object bc)
        {
            if (ReferenceEquals(bc, null))
            {
                if (!_loggedBcNull)
                {
                    _loggedBcNull = true;
                    PatcherPlugin.Logger.LogInfo($"{Tag}[AI_Public.LateUpdate] bc が null のため min_exitation による下限補正をスキップしました（以後この種別のログは出しません）。frame={Time.frameCount}");
                }
                return float.NegativeInfinity;
            }

            var fields = GetKbFields(bc.GetType());
            if (fields == null)
            {
                if (!_loggedKbFieldsMissing)
                {
                    _loggedKbFieldsMissing = true;
                    PatcherPlugin.Logger.LogWarning($"{Tag}[AI_Public.LateUpdate] {bc.GetType().FullName} の kb / min_exitation を解決できませんでした。");
                }
                return float.NegativeInfinity;
            }

            object kb = fields.Kb.GetValue(bc);
            int frame = Time.frameCount;
            SkipState state;

            if (ReferenceEquals(kb, null))
            {
                if (!_kbSkipByInstance.TryGetValue(bc, out state))
                {
                    PurgeDestroyedKeys(_kbSkipByInstance);
                    state = new SkipState { StartFrame = frame };
                    _kbSkipByInstance[bc] = state;
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag}[AI_Public.LateUpdate] {DescribeComponent(bc)}: bc.kb が未初期化(null)のため、min_exitation による下限補正のみスキップしました（LateUpdate の残りは実行）。frame={frame}");
                }
                state.Count++;
                return float.NegativeInfinity;
            }

            if (_kbSkipByInstance.Count > 0 && _kbSkipByInstance.TryGetValue(bc, out state))
            {
                _kbSkipByInstance.Remove(bc);
                PatcherPlugin.Logger.LogInfo(
                    $"{Tag}[AI_Public.LateUpdate] {DescribeComponent(bc)}: bc.kb が利用可能になりました。スキップ {state.Count} 回（frame {state.StartFrame}→{frame}）。");
            }

            return (float)fields.MinExitation.GetValue(kb);
        }

        // ===================== 【L】Transpiler と差し替え先 =====================

        /// <summary>
        /// 原型の「<c>callvirt ChaInfo::get_customMatFace()</c> … <c>callvirt Material::SetColor(string, Color)</c>」
        /// の並びを数える。間に挟まるのは "_overcolor2" の ldstr と色の ldloc だけなので、
        /// SetColor から数命令さかのぼって customMatFace の読み取りがあるかを見る。
        /// </summary>
        private static int CountFaceColorSetCalls(List<CodeInstruction> list)
        {
            int count = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (!IsMaterialSetColor(list[i])) continue;
                for (int j = i - 1; j >= 0 && j >= i - 4; j--)
                {
                    if (!IsCustomMatFaceGetter(list[j])) continue;
                    count++;
                    break;
                }
            }
            return count;
        }

        private static bool IsMaterialSetColor(CodeInstruction instruction)
        {
            var method = instruction.operand as MethodInfo;
            if (method == null || method.Name != "SetColor" || method.DeclaringType != typeof(Material)) return false;
            var parameters = method.GetParameters();
            return parameters.Length == 2 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(Color);
        }

        private static bool IsCustomMatFaceGetter(CodeInstruction instruction)
        {
            var method = instruction.operand as MethodInfo;
            return method != null && method.Name == "get_customMatFace";
        }

        private static IEnumerable<CodeInstruction> AIMainAhegaoTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceFaceColorSetCalls(instructions, nameof(SetFaceColorIfAlive));
        }

        private static IEnumerable<CodeInstruction> AISecondAhegaoTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceFaceColorSetCalls(instructions, nameof(SetSecondFaceColorIfAlive));
        }

        private static IEnumerable<CodeInstruction> FaceCtrlSetFaceTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceFaceColorSetCalls(instructions, nameof(SetGuestFaceColorIfAlive));
        }

        private static IEnumerable<CodeInstruction> ReplaceFaceColorSetCalls(IEnumerable<CodeInstruction> instructions, string helperName)
        {
            var list = instructions.ToList();
            var helper = AccessTools.Method(typeof(KPlugVRNullGuards), helperName);
            int replaced = 0;

            // 「callvirt Material::SetColor(string, Color)」を「call SetFaceColorIfAlive(Material, string, Color)」へ。
            // スタックは [Material][string][Color] のままなので前後の型が一致する。
            // 命令そのものを書き換えるため、付いているラベル・例外ブロックはそのまま残る。
            for (int i = 0; i < list.Count; i++)
            {
                if (!IsMaterialSetColor(list[i])) continue;

                bool fromCustomMatFace = false;
                for (int j = i - 1; j >= 0 && j >= i - 4; j--)
                {
                    if (!IsCustomMatFaceGetter(list[j])) continue;
                    fromCustomMatFace = true;
                    break;
                }
                if (!fromCustomMatFace) continue;

                list[i].opcode = OpCodes.Call;
                list[i].operand = helper;
                replaced++;
            }

            _lastTranspileReplaced = replaced;
            return list;
        }

        /// <summary>
        /// 【L】原型の <c>Core.heroineCha.customMatFace.SetColor(...)</c> の代わりに呼ばれる。
        /// マテリアルが null（原型で NRE になる状態）／破棄済みのときだけ、頬の色の上書きをスキップする。
        /// それ以外は原型と同じく SetColor を呼ぶ。
        /// </summary>
        private static void SetFaceColorIfAlive(Material material, string propertyName, Color color)
        {
            SetAhegaoFaceColorIfAlive(ref _faceColorSkip, "AI_Main.Ahegao", "Core.heroineCha", material, propertyName, color);
        }

        /// <summary>【O】原型の <c>Core.heroine2Cha.customMatFace.SetColor(...)</c>（3P の2人目）の代わりに呼ばれる。動きは【L】と同じ。</summary>
        private static void SetSecondFaceColorIfAlive(Material material, string propertyName, Color color)
        {
            SetAhegaoFaceColorIfAlive(ref _secondFaceColorSkip, "AI_Second.Ahegao", "Core.heroine2Cha", material, propertyName, color);
        }

        /// <summary>【L】【O】の共通処理。毎フレーム呼ばれるため、ログはスキップの開始・解消と一定回数ごとだけ。</summary>
        private static void SetAhegaoFaceColorIfAlive(ref SkipState skip, string label, string chaName,
            Material material, string propertyName, Color color)
        {
            // Unity の == は破棄済みも null 扱いにする。破棄済みで SetColor を呼ぶと原型でも例外になるため、まとめて防ぐ。
            if (material == null)
            {
                int frame = Time.frameCount;
                if (skip == null)
                {
                    skip = new SkipState { StartFrame = frame };
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag}[{label}] {chaName}.customMatFace が {(ReferenceEquals(material, null) ? "null" : "破棄済み")} のため、" +
                        $"頬の色の上書きだけをスキップしました（Ahegao と LateUpdate の残りは実行します）。frame={frame}");
                }
                skip.Count++;

                // 長く続く場合に「まだ続いている」ことが分かるよう、一定回数ごとに1行だけ出す（毎フレームは出さない）。
                if (skip.Count % FaceColorSkipReportInterval == 0)
                {
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag}[{label}] customMatFace が使えない状態が続いています。スキップ {skip.Count} 回（frame {skip.StartFrame}→{frame}）。");
                }
                return;
            }

            if (skip != null)
            {
                var state = skip;
                skip = null;
                PatcherPlugin.Logger.LogInfo(
                    $"{Tag}[{label}] customMatFace が利用可能になりました。スキップ {state.Count} 回（frame {state.StartFrame}→{Time.frameCount}）。");
            }

            material.SetColor(propertyName, color);
        }

        // ===================== 【N】差し替え先 =====================

        /// <summary>今動いているコルーチンを覚えるだけ（例外は出さない）。</summary>
        private static void SetFaceMoveNextPrefix(object __instance)
        {
            _currentSetFaceIter = __instance;
        }

        /// <summary>
        /// 原型の <c>ai.Cha.customMatFace.SetColor(...)</c>（IE_SetFace のアヘ顔の処理）の代わりに呼ばれる。
        /// マテリアルが null／破棄済みのときだけ頬の色の上書きを飛ばす。それ以外は原型と同じく SetColor を呼ぶ。
        /// </summary>
        private static void SetGuestFaceColorIfAlive(Material material, string propertyName, Color color)
        {
            if (material != null)
            {
                material.SetColor(propertyName, color);
                return;
            }

            string name = GuestNameOfCurrentSetFace();
            int count;
            _guestFaceColorSkips.TryGetValue(name, out count);
            _guestFaceColorSkips[name] = ++count;
            _guestFaceColorSkipTotal++;

            // キャラごとに最初の1回と、全体で一定回数ごとに出す。
            if (count == 1)
            {
                PatcherPlugin.Logger.LogInfo(
                    $"{Tag}[FaceCtrl.IE_SetFace] {name} の customMatFace が {(ReferenceEquals(material, null) ? "null" : "破棄済み")} のため、" +
                    $"アヘ顔の頬の色の上書きだけをスキップしました（表情の記録までは実行します）。frame={Time.frameCount}");
            }
            if (_guestFaceColorSkipTotal % GuestFaceColorSkipReportInterval == 0)
            {
                PatcherPlugin.Logger.LogInfo(
                    $"{Tag}[FaceCtrl.IE_SetFace] スキップ累計 {_guestFaceColorSkipTotal} 回（キャラ別: " +
                    string.Join(", ", _guestFaceColorSkips.Select(kv => kv.Key + "=" + kv.Value).ToArray()) + "）");
            }
        }

        /// <summary>今動いている IE_SetFace のキャラ名（<c>&lt;&gt;4__this.ai.CharaName</c>）。取れなければ "(不明)"。</summary>
        private static string GuestNameOfCurrentSetFace()
        {
            try
            {
                var iter = _currentSetFaceIter;
                if (iter == null) return "(不明)";
                var faceCtrl = Traverse.Create(iter).Field("<>4__this").GetValue();
                var ai = faceCtrl != null ? Traverse.Create(faceCtrl).Field("ai").GetValue() : null;
                var name = ai != null ? Traverse.Create(ai).Field("CharaName").GetValue() as string : null;
                return string.IsNullOrEmpty(name) ? "(不明)" : name;
            }
            catch
            {
                return "(不明)";
            }
        }

        // ===================== 【M】Transpiler と差し替え先 =====================

        /// <summary>引数なし・戻り値なしの KissByPlayer() の呼び出しか。</summary>
        private static bool IsKissByPlayerCall(CodeInstruction instruction)
        {
            if (instruction.opcode != OpCodes.Callvirt && instruction.opcode != OpCodes.Call) return false;
            var method = instruction.operand as MethodInfo;
            return method != null && method.Name == "KissByPlayer" && method.GetParameters().Length == 0
                && method.ReturnType == typeof(void);
        }

        /// <summary>
        /// KissByPlayer の呼び出し（index）の手前に「kissable_ai の読み取り → null 比較」があるか。
        /// 3.6.3 は KissByPlayer の前に kissable_ai の null チェックがあり、その IL は
        /// 「ldfld kissable_ai; ldnull; call Object::op_Inequality; brfalse; ldarg.0; ldfld kissable_ai; callvirt KissByPlayer」になる。
        /// </summary>
        private static bool HasKissableNullCheckBefore(List<CodeInstruction> list, int index, FieldInfo kissable)
        {
            for (int j = index - 2; j >= 0 && j >= index - 10; j--)
            {
                if (!list[j].LoadsField(kissable)) continue;
                for (int k = j + 1; k < index && k <= j + 3; k++)
                {
                    var method = list[k].operand as MethodInfo;
                    if (method != null && (method.Name == "op_Inequality" || method.Name == "op_Implicit" || method.Name == "op_Equality")) return true;
                    if (list[k].opcode == OpCodes.Brtrue || list[k].opcode == OpCodes.Brtrue_S
                        || list[k].opcode == OpCodes.Brfalse || list[k].opcode == OpCodes.Brfalse_S) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 「ldfld kissable_ai ; callvirt KissByPlayer()」の間に null チェックを差し込む。
        ///   ldfld kissable_ai ; dup ; brtrue CALL ; pop ; call OnKissableAiNull ; br END ; CALL: callvirt KissByPlayer ; END: （次の命令）
        /// スタックはどちらの経路でも END で空になる（KissByPlayer は void）。
        /// brtrue は参照が本当に null のときだけ飛ばない。破棄済み(fake null)は原型どおり呼ぶ。
        /// </summary>
        private static IEnumerable<CodeInstruction> HCtrlParseTouchablesTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original, ILGenerator generator)
        {
            var list = instructions.ToList();
            var kissable = original != null ? AccessTools.Field(original.DeclaringType, "kissable_ai") : null;
            if (kissable == null) return list;

            var onNull = AccessTools.Method(typeof(KPlugVRNullGuards), nameof(OnKissableAiNull));
            int replaced = 0;

            for (int i = 1; i < list.Count - 1; i++)
            {
                if (!IsKissByPlayerCall(list[i]) || !list[i - 1].LoadsField(kissable)) continue;
                if (HasKissableNullCheckBefore(list, i, kissable)) continue;

                Label callLabel = generator.DefineLabel();
                Label endLabel = generator.DefineLabel();
                list[i].labels.Add(callLabel);
                list[i + 1].labels.Add(endLabel);
                list.InsertRange(i, new[]
                {
                    new CodeInstruction(OpCodes.Dup),
                    new CodeInstruction(OpCodes.Brtrue, callLabel),
                    new CodeInstruction(OpCodes.Pop),
                    new CodeInstruction(OpCodes.Call, onNull),
                    new CodeInstruction(OpCodes.Br, endLabel),
                });
                i += 5;
                replaced++;
            }

            _lastTranspileReplaced = replaced;
            return list;
        }

        /// <summary>kissable_ai が null で KissByPlayer を飛ばしたときに呼ばれる。最初の1回と、一定回数ごとにだけログを出す。</summary>
        private static void OnKissableAiNull()
        {
            try
            {
                _kissByPlayerSkipCount++;
                if (_kissByPlayerSkipCount == 1)
                {
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag}[HCtrl.ParseTouchables] キスの相手（kissable_ai）が null になったフレームのため、KissByPlayer をスキップしました" +
                        $"（招待したキャラへのキスが終わる瞬間。原型ではここで NRE になり、メソッドの最後の文なので失われる処理はありません）。" +
                        $"以後は {KissByPlayerSkipReportInterval} 回ごとに回数だけ出します。frame={Time.frameCount}");
                }
                else if (_kissByPlayerSkipCount % KissByPlayerSkipReportInterval == 0)
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag}[HCtrl.ParseTouchables] KissByPlayer のスキップが {_kissByPlayerSkipCount} 回になりました。frame={Time.frameCount}");
                }
            }
            catch { }
        }

        // ===================== 【E】【F】Prefix =====================

        /// <summary>
        /// 写し先 <paramref name="mat"/> か写し元（heroineCha の customMatFace / customMatBody）が null／破棄済みなら、
        /// 原型を実行せず false を返す。heroineCha が無いときは原型に任せる（原型が先頭で false を返す）。
        /// </summary>
        private static bool CopyLiquidMapToMatPrefix(Material mat, bool isFace, ref bool __result)
        {
            try
            {
                var heroine = _liquidHeroineChaGetter != null ? _liquidHeroineChaGetter.Invoke(null, null) as ChaControl : null;
                if (heroine == null) return true;

                string reason = null;
                if (mat == null) reason = isFace ? "写し先（顔）" : "写し先（体）";
                else if ((isFace ? heroine.customMatFace : heroine.customMatBody) == null)
                    reason = isFace ? "写し元 Core.heroineCha.customMatFace" : "写し元 Core.heroineCha.customMatBody";
                if (reason == null) return true;

                int count;
                _liquidCopySkips.TryGetValue(reason, out count);
                _liquidCopySkips[reason] = ++count;
                _liquidCopySkipTotal++;
                if (count == 1)
                {
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag}[ChaTools.CopyLiquidMapToMat] {reason} のマテリアルが null のため、液体の模様のコピーをスキップしました" +
                        $"（メイン: {heroine.fileParam?.fullname}）。frame={Time.frameCount}");
                }
                if (_liquidCopySkipTotal % LiquidCopySkipReportInterval == 0)
                {
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag}[ChaTools.CopyLiquidMapToMat] スキップ累計 {_liquidCopySkipTotal} 回（" +
                        string.Join(", ", _liquidCopySkips.Select(kv => kv.Key + "=" + kv.Value).ToArray()) + "）");
                }

                __result = false;
                return false;
            }
            catch
            {
                return true;   // 判定に失敗したら原型どおり
            }
        }

        // ===================== 補助 =====================

        private static KbFields GetKbFields(Type bonesType)
        {
            KbFields fields;
            if (_kbFieldsByBonesType.TryGetValue(bonesType, out fields)) return fields;

            var kb = AccessTools.Field(bonesType, "kb");
            var minEx = kb != null ? AccessTools.Field(kb.FieldType, "min_exitation") : null;
            fields = (kb != null && minEx != null && minEx.FieldType == typeof(float))
                ? new KbFields { Kb = kb, MinExitation = minEx }
                : null;
            _kbFieldsByBonesType[bonesType] = fields;
            return fields;
        }

        /// <summary>kPlugVR.CmpH.VRCtrl → kPlugVR.Core（非VR版 kPlug.Core と取り違えないよう同一アセンブリから引く）。</summary>
        private static Type ResolveCoreType(Type memberType)
        {
            var ns = memberType.Namespace ?? string.Empty;
            int dot = ns.IndexOf('.');
            var root = dot >= 0 ? ns.Substring(0, dot) : ns;
            return memberType.Assembly.GetType(root + ".Core");
        }

        /// <summary>
        /// 通常モードでは kPlugVR アセンブリのクラスをロードできず、型解決や IL 読み取りが必ず失敗する。
        /// kPlug 本体も同じ TypeLoadException を出しており（"The script behaviour 'kPlugVR.Core' could not be
        /// instantiated!"）、そのモードでは kPlugVR のコード自体が動かないため実害は無い。
        /// 紛らわしいので Error ではなく Info で記録する。
        /// </summary>
        private static void LogApplyFailure(string target, Exception ex)
        {
            var message = ex != null ? ex.Message : "(不明)";
            if (IsAssemblyUnavailable(ex))
            {
                PatcherPlugin.Logger.LogInfo($"{Tag} {target} は対象外です（このモードでは kPlugVR のクラスをロードできません。実害はありません）: {message}");
            }
            else
            {
                PatcherPlugin.Logger.LogError($"{Tag} {target} への適用に失敗: {message}");
            }
        }

        private static bool IsAssemblyUnavailable(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is TypeLoadException || e is ReflectionTypeLoadException
                    || e is BadImageFormatException || e is FileNotFoundException) return true;

                var m = e.Message ?? string.Empty;
                if (m.Contains("Could not load type") || m.Contains("IL Compile Error")
                    || m.Contains("cannot be loaded")) return true;
            }
            return false;
        }

        private static void PurgeDestroyedKeys(Dictionary<object, SkipState> dict)
        {
            if (dict.Count == 0) return;
            var dead = dict.Keys.Where(k => { var uo = k as UnityEngine.Object; return !ReferenceEquals(uo, null) && uo == null; }).ToList();
            foreach (var k in dead) dict.Remove(k);
        }

        private static string DescribeComponent(object obj)
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

        private static object SafeGetStatic(FieldInfo field)
        {
            try { return field == null ? null : field.GetValue(null); }
            catch { return null; }
        }

        private static string RefState(object value)
        {
            if (ReferenceEquals(value, null)) return "null";
            var uo = value as UnityEngine.Object;
            if (!ReferenceEquals(uo, null) && uo == null) return "破棄済み";
            return "ok";
        }

        private static string RefStateOf(FieldInfo staticField)
        {
            if (staticField == null) return "?";
            try { return RefState(staticField.GetValue(null)); }
            catch { return "?"; }
        }

        private static string SafeValue(FieldInfo field, object instance)
        {
            try { return field == null ? "?" : (field.GetValue(instance) ?? "null").ToString(); }
            catch { return "?"; }
        }

        private static string CountAlive(Type type)
        {
            try { return UnityEngine.Object.FindObjectsOfType(type).Length.ToString(); }
            catch { return "?"; }
        }

        private static void SafeLogWarning(string where, Exception ex)
        {
            try { PatcherPlugin.Logger.LogWarning($"{Tag}[{where}] 処理中に例外（kPlug 側の処理は継続します）: {ex.Message}"); }
            catch { }
        }
    }
}
