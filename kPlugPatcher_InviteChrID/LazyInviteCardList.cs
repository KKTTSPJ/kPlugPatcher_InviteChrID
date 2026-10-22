using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// kPlug の招待メニュー（<c>InviteUI</c>）が全キャラカードを完全な形で読み込み、
    /// Hシーンの間ずっと保持する問題の回避策（遅延読み込み）。kPlug 3.5.6（旧API）向け。
    ///
    /// 【問題】
    ///   <c>InviteUI.Start()</c> が <c>kFileParser.GetGirlCardList</c> → <c>GetCardList</c> で
    ///   全カードを <c>ChaFileControl.LoadCharaFile</c> で読み込み、一覧 <c>otherGirls</c> に保持する。
    ///   1枚あたり約30MB（拡張データを含む）、約100枚で Hシーン入場ごとに約3GB。
    ///   Mono (Boehm GC) はヒープを OS に返さないため、ピークがそのままプロセスの上限を押し上げる。
    ///
    /// 【方針】
    ///   一覧の表示に必要なのは parameter（名前・性格）と顔画像だけ。完全なカードは招待の瞬間だけ要る。
    ///
    ///   1. <c>InviteUI.Start()</c> の実行中だけ有効にする（それ以外の GetCardList 利用、
    ///      たとえば <c>FindCardData</c> は顔画像なども使うので対象外）
    ///   2. <c>ChaFileControl.LoadCharaFile(string, byte, …)</c> の Postfix で「完全版 → パス・性別」を控える
    ///   3. <c>GetCardList</c> の Transpiler で、一覧へ追加する <c>List&lt;ChaFileControl&gt;.Add</c> を差し替え、
    ///      **1枚読み込むごとに軽量版へ置き換えて完全版を手放す**。
    ///      Start() の後でまとめて差し替えると、読み込み中は全カードが同時に生きていてピークが下がらないため。
    ///   4. <c>InviteUI.Start()</c> の Postfix で、顔画像を作り終えた軽量版から画像データを外す
    ///   5. 招待の瞬間（<see cref="InviteCardPatch.Prefix"/> の先頭）に、控えたパスから読み直す
    ///
    /// 【軽量版】
    ///   <c>new ChaFileControl()</c> ＋ <c>CopyParameter</c> ＋ <c>charaFileName</c> ＋ 顔画像データ（Start の間だけ）。
    ///   <c>CopyAll</c> は使わない（KKAPI の CopyAll 後フックが拡張データごとコピーしてしまう）。
    ///   元のインスタンスの中身を削る方式も不可（ExtendedSave の WeakKeyDictionary のキーが生き残り、
    ///   拡張データが回収されない）。**新しいインスタンスに差し替えて元を手放す**必要がある。
    ///
    /// 【注意】
    ///   - <c>InviteUI.GirlCardTab</c> は一覧の要素の <c>parameter.personality</c> / <c>voiceRate</c> を
    ///     書き換えてから招待する（ダークネス用の性格の読み替え）。読み直したカードにはこの parameter を写す。
    ///   - 男性一覧（sex=0 で読まれるバディ用カード）は <c>Dealer.InviteBuddy</c> という別の入口から招待されるため、
    ///     そこに読み直しの Prefix を当てる（v1.6.0。設定 IncludeMaleCards で無効化すると完全版のまま）。
    ///   - 読み直しの入口にパッチが当たっていない側は軽量化しない（見た目の無いキャラが生成されるのを防ぐ）。
    ///
    /// 【部分読み込み（v1.7.0）】
    ///   完全に読んでから軽量版に置き換える方式でも、読み込みの途中では拡張データ（KKEx。Material Editor の埋め込みテクスチャなど）の
    ///   展開や他プラグインの読み込みフックが走り、Hシーン入場ごとに約12秒・コミット +2GB かかっていた
    ///   そこで GetCardList 内の LoadCharaFile 呼び出しも差し替え、InviteUI.Start の実行中は
    ///   **全身画像・顔画像・Parameter ブロックだけ**をファイルから読む（<see cref="TryPartialLoad"/>）。
    ///   kPlug が一覧のカードで使うのは parameter / pngData / facePngData だけであることを 3.56（非VR・VR）で確認済み。
    ///   部分読み込みに失敗したカードは、元の LoadCharaFile で完全に読み、上の方式で軽量版にする。
    ///
    /// 【kPlugVR 3.599 以降の新しい招待メニュー（MenuInvite、v1.13.0）】
    ///   VR の新しい API では、キャラ選択の画面（<c>VRDealer.SetContext(Context.HSelect)</c>）に入るたびに
    ///   <c>MenuInvite.InitFileList()</c> が <c>kFileParser.GetGirlCardList</c> / <c>GetMaleCardList</c> で全カードを完全に読み、
    ///   静的なリスト <c>girlInfos</c> / <c>boyInfos</c>（<c>ChaInfo.file</c>）に次のキャラ選択まで保持する。
    ///   女性250枚の環境で1回あたり 19〜27秒・Mono ヒープ +11〜13GB、2回目で 27GB になりメモリを使い切って落ちた。
    ///   そこで <c>InitFileList()</c> の実行中も部分読み込みの範囲にし（VR のプロセスの kPlugVR だけ）、
    ///   完全なカードが要る次の2か所の手前で、<c>ChaInfo.file</c> 自体を完全版に差し替える。
    ///   - <c>MenuInvite.InviteSomeone(ChaInfo, …)</c>: 一覧のカードを <c>CopyAll</c> で写してから <c>InviteInVR</c> に渡すため、
    ///     写す前に差し替える（<c>InviteInVR</c> に届くのは写した別のインスタンスなので、そこでは見分けられない）。
    ///   - <c>MenuInvite.TryToReloadDefAppearence(cha, id)</c>: <c>idAndChaFile[id].file</c> を <c>CopyAll</c> する。
    ///     メインのキャラ（id 0/1）は <c>InitAndTabAndInfoInH</c> で一覧の項目がそのまま登録されるので、ここでも差し替える。
    ///   <c>InitAndTabAndInfoInH</c> はダークネス用に一覧の <c>parameter.personality</c> / <c>voiceRate</c> を書き換えるので、読み直したカードに写す。
    ///
    /// 【KK 以外のカード（v1.14.0）】
    ///   部分読み込みが受け付ける識別子を、ゲーム本体（と Party Card Compatibility）の LoadFile と同じにした（<see cref="IsAcceptedCharaMark"/>）。
    ///   それまでは「【KoiKatuChara】」だけで、パーティー・サンシャインのカード（KKP では KKP で作ったカードすべて）が毎回完全に読まれていた。
    ///   版の新しい Parameter はゲーム本体と同じく読まずに既定値のままにする。
    /// </summary>
    internal static class LazyInviteCardList
    {
        private const string Tag = "[LazyInviteCards]";

        private sealed class RefComparer : IEqualityComparer<ChaFileControl>
        {
            internal static readonly RefComparer Instance = new RefComparer();
            public bool Equals(ChaFileControl x, ChaFileControl y) { return ReferenceEquals(x, y); }
            public int GetHashCode(ChaFileControl obj) { return RuntimeHelpers.GetHashCode(obj); }
        }

        private sealed class CardSource
        {
            public string Path;
            public byte Sex;
        }

        /// <summary><c>InviteUI.Start()</c> / <c>MenuInvite.InitFileList()</c> の実行中だけ 1 以上になる。</summary>
        private static int _captureDepth;

        /// <summary>今の範囲が <c>MenuInvite.InitFileList()</c>（kPlugVR の新しい API）なら true。読み直しの入口の判定に使う。</summary>
        private static bool _captureIsMenuInvite;

        /// <summary>MenuInvite の読み直しの入口（InviteSomeone / TryToReloadDefAppearence）が当たっているか。</summary>
        private static bool _menuInviteHooked;

        // MenuInvite（kPlugVR）の型の情報。リフレクションで扱う（kPlugVR は参照しない。csproj の注意を参照）
        private static FieldInfo _chaInfoFileField;
        private static FieldInfo _idAndChaFileField;
        private static int _menuInviteRestored;

        /// <summary>LoadCharaFile 直後から一覧に追加されるまでの間だけ持つ「完全版 → 読み込み元」。</summary>
        private static readonly Dictionary<ChaFileControl, CardSource> _pending =
            new Dictionary<ChaFileControl, CardSource>(RefComparer.Instance);

        /// <summary>「軽量版 → 読み込み元」。招待時に読み直すために使う。次の InviteUI.Start() で作り直す。</summary>
        private static readonly Dictionary<ChaFileControl, CardSource> _lightToSource =
            new Dictionary<ChaFileControl, CardSource>(RefComparer.Instance);

        /// <summary>女性の読み直しの入口（Dealer.InviteCard の Prefix）が当たっているか。</summary>
        private static bool _inviteCardHooked;

        /// <summary>男性の読み直しの入口（Dealer.InviteBuddy の Prefix）が当たっているか。</summary>
        private static bool _inviteBuddyHooked;

        /// <summary>PatcherPlugin が InviteCard に <see cref="InviteCardPatch.Prefix"/> を当てた後に呼ぶ。</summary>
        internal static void NotifyInviteCardHooked()
        {
            _inviteCardHooked = true;
        }

        private static int _lightenedThisScene;
        private static int _lightenedMaleThisScene;
        private static int _keptFullThisScene;
        private static int _partialLoadedThisScene;
        private static int _partialFailedThisScene;
        private static int _parameterSkippedThisScene;
        private static readonly Dictionary<string, int> _otherMarkCounts = new Dictionary<string, int>();
        private static readonly Dictionary<string, List<string>> _failedByReason = new Dictionary<string, List<string>>();

        /// <summary>部分読み込みしたカード（最初から軽量版）→ 読み込み元。一覧に追加されるまでの間だけ持つ。</summary>
        private static readonly Dictionary<ChaFileControl, CardSource> _preLight =
            new Dictionary<ChaFileControl, CardSource>(RefComparer.Instance);

        private static PropertyInfo _charaFileNameProp;
        private static readonly HashSet<string> _appliedKeys = new HashSet<string>();
        private static readonly HashSet<string> _failureLoggedKeys = new HashSet<string>();
        private static int _lastTranspileReplaced;
        private static int _lastTranspileLoadReplaced;

        // ===================== 適用 =====================

        /// <summary>何度呼んでもよい（適用済みの対象は飛ばす）。型が見つかり次第適用するため、早い段階から繰り返し呼ばれる。</summary>
        internal static void TryApply(Harmony harmony)
        {
            TryPatchLoadCharaFile(harmony);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.FullName.Contains("kPlug")) continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                TryPatchGetCardList(harmony, types);
                TryPatchInviteUIStart(harmony, types);

                // MenuInvite は VR の UI の型を土台にしているので、通常モードでは触らない
                // （読み込めない型の中身を調べると Mono ごと落ちる）
                if (IsVRProcess && assembly.GetName().Name.IndexOf("kPlugVR", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    TryPatchMenuInvite(harmony, types);
                }
            }
        }

        private static bool IsVRProcess
        {
            get { return BepInEx.Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase); }
        }

        private static void TryPatchMenuInvite(Harmony harmony, Type[] types)
        {
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".Menu.MenuInvite"));
            if (type == null) return;

            string key = type.FullName + "|MenuInvite";
            if (_appliedKeys.Contains(key)) return;
            _appliedKeys.Add(key);

            try
            {
                if (!PatcherPlugin.LazyInviteVRMenuInvite.Value)
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} 設定 EnableVRMenuInvite が無効のため、{type.FullName} の一覧は完全版のまま読み込みます。");
                    return;
                }

                var chaInfoType = type.GetNestedType("ChaInfo", BindingFlags.Public | BindingFlags.NonPublic);
                _chaInfoFileField = chaInfoType != null ? chaInfoType.GetField("file", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) : null;
                _idAndChaFileField = AccessTools.Field(type, "idAndChaFile");
                var initFileList = AccessTools.DeclaredMethod(type, "InitFileList", Type.EmptyTypes);
                var inviteSomeone = AccessTools.DeclaredMethod(type, "InviteSomeone");
                var reload = AccessTools.DeclaredMethod(type, "TryToReloadDefAppearence");

                if (_chaInfoFileField == null || _chaInfoFileField.FieldType != typeof(ChaFileControl)
                    || _idAndChaFileField == null || initFileList == null || inviteSomeone == null || reload == null)
                {
                    PatcherPlugin.Logger.LogWarning(
                        $"{Tag} {type.FullName} の構造が想定と違うため、新しい招待メニューの遅延読み込みは適用しません（一覧は完全版のまま）: " +
                        $"ChaInfo.file={_chaInfoFileField != null} / idAndChaFile={_idAndChaFileField != null} / InitFileList={initFileList != null} / " +
                        $"InviteSomeone={inviteSomeone != null} / TryToReloadDefAppearence={reload != null}");
                    return;
                }
                if (inviteSomeone.GetParameters().Length == 0 || inviteSomeone.GetParameters()[0].ParameterType != chaInfoType
                    || reload.GetParameters().Length < 2 || reload.GetParameters()[1].ParameterType != typeof(int))
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}.InviteSomeone / TryToReloadDefAppearence の引数が想定と違うため、新しい招待メニューの遅延読み込みは適用しません。");
                    return;
                }

                MenuInviteFaceShrink.Init(type, chaInfoType);

                // 読み直しの入口を先に当て、両方当たってから範囲指定を当てる（入口が無いまま軽量版にすると、見た目の無いキャラが生成される）
                harmony.Patch(inviteSomeone, prefix: Method(nameof(InviteSomeonePrefix)));
                harmony.Patch(reload, prefix: Method(nameof(ReloadDefAppearencePrefix)));
                _menuInviteHooked = true;
                harmony.Patch(initFileList,
                    prefix: Method(nameof(MenuInviteInitPrefix)),
                    postfix: Method(nameof(MenuInviteInitPostfix)),
                    finalizer: Method(nameof(InviteUIStartFinalizer)));
                PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName} の一覧（InitFileList）に遅延読み込みを、InviteSomeone / TryToReloadDefAppearence に読み直しを適用しました（frame={UnityEngine.Time.frameCount}）。");
            }
            catch (Exception ex)
            {
                _menuInviteHooked = false;
                LogApplyFailureOnce(key, $"{type.FullName}（新しい招待メニュー）", ex);
            }
        }

        private static void TryPatchLoadCharaFile(Harmony harmony)
        {
            const string key = "ChaFileControl.LoadCharaFile";
            if (_appliedKeys.Contains(key)) return;
            _appliedKeys.Add(key);

            try
            {
                _charaFileNameProp = AccessTools.Property(typeof(ChaFile), "charaFileName");

                var method = AccessTools.Method(typeof(ChaFileControl), "LoadCharaFile",
                    new[] { typeof(string), typeof(byte), typeof(bool), typeof(bool) });
                if (method == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} ChaFileControl.LoadCharaFile(string, byte, bool, bool) が見つかりません。遅延読み込みは無効です。");
                    return;
                }
                harmony.Patch(method, postfix: Method(nameof(LoadCharaFilePostfix)));
                PatcherPlugin.Logger.LogInfo($"{Tag} ChaFileControl.LoadCharaFile に読み込み元の記録を適用しました。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} ChaFileControl.LoadCharaFile への適用に失敗: {ex.Message}");
            }
        }

        private static void TryPatchGetCardList(Harmony harmony, Type[] types)
        {
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".DefAndParse.kFileParser"));
            if (type == null) return;

            string key = type.FullName + "|GetCardList";
            if (_appliedKeys.Contains(key)) return;

            try
            {
                var method = AccessTools.DeclaredMethod(type, "GetCardList");
                if (method == null)
                {
                    _appliedKeys.Add(key);
                    PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}.GetCardList() が見つかりません。");
                    return;
                }

                _lastTranspileReplaced = 0;
                harmony.Patch(method, transpiler: Method(nameof(GetCardListTranspiler)));
                _appliedKeys.Add(key);

                if (_lastTranspileReplaced > 0)
                {
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag} {type.FullName}.GetCardList() の一覧への追加 {_lastTranspileReplaced} 箇所を「読み込むたびに軽量版へ置き換え」に、" +
                        $"カードの読み込み {_lastTranspileLoadReplaced} 箇所を「部分読み込み」に差し替えました（frame={UnityEngine.Time.frameCount}）。");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning(
                        $"{Tag} {type.FullName}.GetCardList() に List<ChaFileControl>.Add が見つからず、差し替えられませんでした（kPlug の構造が変わった可能性があります）。");
                }
            }
            catch (Exception ex)
            {
                LogApplyFailureOnce(key, $"{type.FullName}.GetCardList()", ex);
            }
        }

        private static void TryPatchInviteUIStart(Harmony harmony, Type[] types)
        {
            // 3.599 以降の VR には InviteUI が無い（kPlugVR.Menu.MenuInvite に作り直された）。本クラスは旧APIのみ対象。
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".CmpH.InviteUI"));
            if (type == null) return;

            string key = type.FullName + "|Start";
            if (_appliedKeys.Contains(key)) return;

            try
            {
                var method = AccessTools.DeclaredMethod(type, "Start", Type.EmptyTypes);
                if (method == null)
                {
                    _appliedKeys.Add(key);
                    PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}.Start() が見つかりません。");
                    return;
                }

                harmony.Patch(method,
                    prefix: Method(nameof(InviteUIStartPrefix)),
                    postfix: Method(nameof(InviteUIStartPostfix)),
                    finalizer: Method(nameof(InviteUIStartFinalizer)));
                _appliedKeys.Add(key);
                PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName}.Start() に遅延読み込みの範囲指定を適用しました（frame={UnityEngine.Time.frameCount}）。");
                InviteUIFaceShrink.TryApply(harmony, type);
            }
            catch (Exception ex)
            {
                LogApplyFailureOnce(key, $"{type.FullName}.Start()", ex);
            }
        }

        // ===================== InviteUI.Start の範囲指定 =====================

        private static void InviteUIStartPrefix()
        {
            _captureDepth++;
            if (_captureDepth != 1) return;
            _captureIsMenuInvite = false;
            ResetSceneState();
        }

        private static void ResetSceneState()
        {
            // 前の Hシーンの軽量版は、InviteUI ごと捨てられているので対応表も作り直す
            _lightToSource.Clear();
            _pending.Clear();
            _preLight.Clear();
            _lightenedThisScene = 0;
            _lightenedMaleThisScene = 0;
            _keptFullThisScene = 0;
            _partialLoadedThisScene = 0;
            _partialFailedThisScene = 0;
            _parameterSkippedThisScene = 0;
            _otherMarkCounts.Clear();
            _failedByReason.Clear();
        }

        /// <summary>Start() が最後まで走った場合のみ呼ばれる。顔画像を作り終えたので画像データを外す。</summary>
        private static void InviteUIStartPostfix(object __instance)
        {
            InviteUIFaceShrink.Apply(__instance);
            try
            {
                foreach (var light in _lightToSource.Keys)
                {
                    light.pngData = null;
                    light.facePngData = null;
                }

                if (_lightenedThisScene > 0 || _keptFullThisScene > 0)
                {
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag} 招待メニューのカード {_lightenedThisScene} 枚を軽量版にしました" +
                        $"（うち男性一覧 {_lightenedMaleThisScene} 枚。完全版のまま: {_keptFullThisScene} 枚。" +
                        $"部分読み込み {_partialLoadedThisScene} 枚 / 部分読み込みに失敗して完全に読んだもの {_partialFailedThisScene} 枚）。招待する瞬間に読み直します。" +
                        DescribeLoadBreakdown());
                }

                InviteCardDiagnostics.LogPatchState("招待メニュー作成後");
            }
            catch (Exception ex)
            {
                SafeLogWarning("InviteUI.Start(後処理)", ex);
            }
        }

        private static Exception InviteUIStartFinalizer(Exception __exception)
        {
            _captureDepth = Math.Max(0, _captureDepth - 1);
            if (_captureDepth == 0)
            {
                // 一覧に追加されなかった完全版を抱え込まないよう必ず空にする
                _pending.Clear();
                _preLight.Clear();
                _captureIsMenuInvite = false;
            }
            return __exception;
        }

        // ===================== MenuInvite（kPlugVR の新しい API）=====================

        private static void MenuInviteInitPrefix()
        {
            _captureDepth++;
            if (_captureDepth != 1) return;
            // 前のキャラ選択の一覧（girlInfos）は、この後 kPlugVR が新しい一覧に置き換える。対応表も作り直す
            _captureIsMenuInvite = true;
            ResetSceneState();
            _menuInviteRestored = 0;
        }

        /// <summary>InitFileList() が最後まで走った場合のみ呼ばれる。顔画像のテクスチャを作り終えたので全身画像のデータを外す。</summary>
        private static void MenuInviteInitPostfix()
        {
            try
            {
                // 顔画像データ（facePngData）は InviteSomeone が招待したカードに写すので残す。全身画像（pngData）は以後使われない
                foreach (var light in _lightToSource.Keys)
                {
                    light.pngData = null;
                }

                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} 新しい招待メニュー（MenuInvite）のカード {_lightenedThisScene} 枚を軽量版にしました" +
                    $"（完全版のまま: {_keptFullThisScene} 枚。部分読み込み {_partialLoadedThisScene} 枚 / 部分読み込みに失敗して完全に読んだもの {_partialFailedThisScene} 枚）。" +
                    $"招待する瞬間と見た目の読み直しの前に、完全なカードを読み直します。" + DescribeLoadBreakdown());

                MenuInviteFaceShrink.Apply();
            }
            catch (Exception ex)
            {
                SafeLogWarning("MenuInvite.InitFileList(後処理)", ex);
            }
        }

        /// <summary>MenuInvite.InviteSomeone(ChaInfo chaInfo, …) の Prefix。CopyAll で写される前に、一覧の項目を完全版に差し替える。</summary>
        private static void InviteSomeonePrefix(object __0)
        {
            if (PatcherPlugin.IsQuitting || __0 == null) return;
            UpgradeChaInfo(__0, "招待");
        }

        /// <summary>MenuInvite.TryToReloadDefAppearence(ChaControl cha, int id) の Prefix。idAndChaFile[id] の項目を完全版に差し替える。</summary>
        private static void ReloadDefAppearencePrefix(int __1)
        {
            if (PatcherPlugin.IsQuitting) return;
            try
            {
                var dict = _idAndChaFileField.GetValue(null) as System.Collections.IDictionary;
                if (dict == null || !dict.Contains(__1)) return;
                UpgradeChaInfo(dict[__1], $"見た目の読み直し（id={__1}）");
            }
            catch (Exception ex)
            {
                SafeLogWarning("見た目の読み直しの前の差し替え", ex);
            }
        }

        /// <summary>
        /// MenuInvite の一覧の項目（ChaInfo）の file が軽量版なら、控えたパスから完全なカードを読み直し、file 自体を差し替える。
        /// 一度差し替えた項目は完全版のまま残る（招待したキャラとメインのキャラの分だけ。元の kPlugVR はすべて完全版を持っていた）。
        /// </summary>
        private static void UpgradeChaInfo(object chaInfo, string why)
        {
            try
            {
                var light = _chaInfoFileField.GetValue(chaInfo) as ChaFileControl;
                CardSource source;
                if (light == null || !_lightToSource.TryGetValue(light, out source)) return;

                var begin = KPlugMemoryDiagnostics.Take();
                var full = new ChaFileControl();
                bool ok;
                try { ok = full.LoadCharaFile(source.Path, source.Sex); }
                catch (Exception ex) { ok = false; SafeLogWarning($"カードの読み直し ({source.Path})", ex); }

                if (!ok)
                {
                    PatcherPlugin.Logger.LogError(
                        $"{Tag} {why}のためのカード読み直しに失敗しました。軽量版のまま使われるため、見た目が既定の状態になる可能性があります: {source.Path}");
                    return;
                }

                // ダークネス用の性格・声の読み替え（InitAndTabAndInfoInH）を含む parameter を写す
                full.CopyParameter(light.parameter);
                _chaInfoFileField.SetValue(chaInfo, full);
                _lightToSource.Remove(light);
                _menuInviteRestored++;

                PatcherPlugin.Logger.LogInfo($"{Tag} {why}のためカードを読み直しました（このキャラ選択で {_menuInviteRestored} 枚目）: {full.parameter.fullname} ({source.Path})" +
                    $" / 所要 {begin.watch.ElapsedMilliseconds}ms / {KPlugMemoryDiagnostics.Diff(begin, KPlugMemoryDiagnostics.Take())}");
            }
            catch (Exception ex)
            {
                SafeLogWarning($"{why}の前の差し替え", ex);
            }
        }

        // ===================== 読み込みの記録と差し替え =====================

        private static void LoadCharaFilePostfix(ChaFileControl __instance, bool __result, string filename, byte sex)
        {
            if (_captureDepth <= 0 || !__result || __instance == null || string.IsNullOrEmpty(filename)) return;
            if (!PatcherPlugin.EnableLazyInviteCardList.Value) return;

            _pending[__instance] = new CardSource { Path = filename, Sex = sex };
        }

        /// <summary>
        /// GetCardList 内の <c>list.Add(chaFileControl)</c> の代わりに呼ばれる。
        /// InviteUI.Start() の実行中で、かつ女性カードなら、軽量版に置き換えて追加する（完全版はここで手放される）。
        /// それ以外は原型どおりそのまま追加する。
        /// </summary>
        private static void AddMaybeLight(List<ChaFileControl> list, ChaFileControl item)
        {
            CardSource source;

            // 部分読み込みしたカードは、最初から軽量版なのでそのまま一覧に入れる
            if (_captureDepth > 0 && item != null && _preLight.TryGetValue(item, out source))
            {
                _preLight.Remove(item);
                _lightToSource[item] = source;
                _lightenedThisScene++;
                if (source.Sex == 0) _lightenedMaleThisScene++;
                list.Add(item);
                return;
            }

            if (_captureDepth > 0 && item != null && _pending.TryGetValue(item, out source))
            {
                _pending.Remove(item);

                // 男性一覧（sex=0。バディ用フォルダの女性カードも sex=0 で読まれる）は Dealer.InviteBuddy で読み直す。
                // 設定で対象外にした場合は、v1.5.2 以前と同じく完全版のまま残す。
                // 読み直しの入口（InviteCard / InviteBuddy）にパッチが当たっていない側は、軽量版にすると
                // 見た目の無いキャラが生成されるので完全版のまま残す（Hシーンがパッチ適用より先に始まった場合など）。
                bool isMaleList = source.Sex == 0;
                if (CanRestore(source.Sex))
                {
                    var light = MakeLight(item);
                    if (light != null)
                    {
                        _lightToSource[light] = source;
                        _lightenedThisScene++;
                        if (isMaleList) _lightenedMaleThisScene++;
                        list.Add(light);
                        return;
                    }
                }
                _keptFullThisScene++;
            }

            list.Add(item);
        }

        /// <summary>この性別の一覧を軽量版にしてよいか（招待時に読み直す入口にパッチが当たっているか）。</summary>
        private static bool CanRestore(byte sex)
        {
            // 新しい招待メニューは、男女とも InviteSomeone の手前で読み直す（kPlugVR は男性一覧も sex=1 で読む）
            if (_captureIsMenuInvite) return _menuInviteHooked;
            return sex == 0
                ? _inviteBuddyHooked && PatcherPlugin.LazyInviteIncludeMaleCards.Value
                : _inviteCardHooked;
        }

        /// <summary>
        /// GetCardList 内の <c>chaFileControl.LoadCharaFile(path, sex, noLoadPng, noLoadStatus)</c> の代わりに呼ばれる。
        /// InviteUI.Start の実行中で軽量版にしてよいカードなら、全身画像・顔画像・Parameter だけを読む。
        /// それ以外、または部分読み込みに失敗した場合は、元の LoadCharaFile をそのまま呼ぶ。
        /// </summary>
        private static bool LoadForList(ChaFileControl instance, string filename, byte sex, bool noLoadPng, bool noLoadStatus)
        {
            if (_captureDepth > 0 && instance != null && !string.IsNullOrEmpty(filename)
                && PatcherPlugin.EnableLazyInviteCardList.Value && PatcherPlugin.LazyInvitePartialLoad.Value
                && CanRestore(sex))
            {
                string reason;
                if (TryPartialLoad(instance, filename, sex, out reason))
                {
                    _preLight[instance] = new CardSource { Path = filename, Sex = sex };
                    _partialLoadedThisScene++;
                    return true;
                }

                _partialFailedThisScene++;
                List<string> examples;
                if (!_failedByReason.TryGetValue(reason, out examples)) _failedByReason[reason] = examples = new List<string>();
                examples.Add(Path.GetFileName(filename));
                if (_failureLoggedKeys.Add("PartialLoad|" + reason))
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} 部分読み込みできなかったため完全に読み込みます（同じ理由は以後省略。一覧を作り終えたときに理由ごとの枚数を出します）: {reason} ({filename})");
                }
            }

            return instance.LoadCharaFile(filename, sex, noLoadPng, noLoadStatus);
        }

        /// <summary>
        /// ゲーム本体の <c>ChaFile.LoadFile(BinaryReader, …)</c>と同じ順に読み、
        /// 全身画像・顔画像・Parameter ブロックだけを取り出す。Custom / Coordinate / Status / 拡張データ（KKEx）は読まない。
        /// ChaFile.LoadFile を通らないので、ExtendedSave・Sideloader などの読み込みフックも走らない。
        /// </summary>
        private static bool TryPartialLoad(ChaFileControl c, string filename, byte sex, out string reason)
        {
            reason = null;
            try
            {
                if (_charaFileNameProp != null)
                {
                    _charaFileNameProp.SetValue(c, Path.GetFileName(filename), null);
                }
                string path = c.ConvertCharaFilePath(filename, sex);

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var br = new BinaryReader(fs))
                {
                    long pngSize = PngFile.GetPngSize(br);
                    if (pngSize != 0)
                    {
                        c.pngData = br.ReadBytes((int)pngSize);
                        if (br.BaseStream.Length - br.BaseStream.Position == 0) { reason = "PNG のみでキャラデータが無い"; return false; }
                    }

                    int productNo = br.ReadInt32();
                    if (productNo > 100) { reason = "productNo が範囲外"; return false; }
                    string mark = br.ReadString();
                    if (!IsAcceptedCharaMark(mark)) { reason = "キャラカードの識別子が違う"; return false; }

                    var version = new Version(br.ReadString());
                    if (ChaFileDefine.ChaFileVersion.CompareTo(version) < 0) { reason = "カードのバージョンが新しすぎる"; return false; }
                    c.loadVersion = version;

                    int faceSize = br.ReadInt32();
                    if (faceSize != 0) c.facePngData = br.ReadBytes(faceSize);

                    int headerSize = br.ReadInt32();
                    var header = MessagePack.MessagePackSerializer.Deserialize<global::BlockHeader>(br.ReadBytes(headerSize));
                    br.ReadInt64();
                    long dataStart = br.BaseStream.Position;

                    var info = header.SearchInfo(ChaFileParameter.BlockName);
                    if (info == null) { reason = "Parameter ブロックが無い"; return false; }
                    if (ChaFileDefine.ChaFileParameterVersion.CompareTo(new Version(info.version)) < 0)
                    {
                        // ゲーム本体の LoadFile も、版の新しい Parameter は読まずに既定値のまま成功として返す（サンシャインのカードの 0.0.6 など）。
                        // 完全に読んでも同じ結果になるので、同じく既定値のまま軽量版にする
                        _parameterSkippedThisScene++;
                    }
                    else
                    {
                        br.BaseStream.Seek(dataStart + info.pos, SeekOrigin.Begin);
                        c.SetParameterBytes(br.ReadBytes((int)info.size));
                    }
                    if (mark != "【KoiKatuChara】") CountOtherMark(mark);
                }

                // 元の LoadCharaFile(BinaryReader) と同じく、範囲外の値を直す（ここでは Parameter の分だけ）
                if (!c.skipRangeCheck) ChaFileControl.CheckDataRangeParameter(c);
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 部分読み込みで受け付けるキャラカードの識別子。ゲーム本体（と、それを広げるプラグイン）の LoadFile と同じにする。
        /// 受け付けない識別子は完全な読み込みに回すので、ここで狭く判定しても読めなくなるカードは無い（遅くなるだけ）。
        ///   - KK 本体は「【KoiKatuChara】」だけ。IllusionFixes の Party Card Compatibility（KK_Fix_PartyCardCompatibility）が入っていると、
        ///     「【KoiKatuChara」で始まる識別子をすべて受け付ける（パーティーの「【KoiKatuCharaS】」「【KoiKatuCharaSP】」、
        ///     サンシャインの「【KoiKatuCharaSun】」も。同プラグイン 22.1 の CustomCardTokenCompare）。
        ///   - KKP 本体は ChaFileDefine.CharaFileMarkCheck（【KoiKatuChara】【KoiKatuCharaS】【KoiKatuCharaSP】）。
        /// 確認した環境では、これらの識別子のカード8枚が毎回完全に読まれていた。
        /// </summary>
        private static bool IsAcceptedCharaMark(string mark)
        {
            if (mark == null) return false;
            if (mark == "【KoiKatuChara】") return true;
#if KKP
            return Array.IndexOf(ChaFileDefine.CharaFileMarkCheck, mark) >= 0;
#else
            if (_partyCardCompat == null)
            {
                _partyCardCompat = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("KK_Fix_PartyCardCompatibility");
                PatcherPlugin.Logger.LogInfo($"{Tag} Party Card Compatibility は{(_partyCardCompat.Value ? "あります。パーティー・サンシャインのカードも部分読み込みします" : "ありません。KK のカードだけを部分読み込みします")}。");
            }
            return _partyCardCompat.Value && mark.StartsWith("【KoiKatuChara", StringComparison.OrdinalIgnoreCase);
#endif
        }

#if !KKP
        private static bool? _partyCardCompat;
#endif

        private static void CountOtherMark(string mark)
        {
            int n;
            _otherMarkCounts.TryGetValue(mark, out n);
            _otherMarkCounts[mark] = n + 1;
        }

        /// <summary>一覧を作り終えたときに出す、識別子・失敗の理由ごとの内訳（無ければ空文字）。</summary>
        private static string DescribeLoadBreakdown()
        {
            var parts = new List<string>();
            if (_otherMarkCounts.Count > 0)
                parts.Add("KK 以外のカードの部分読み込み: " + string.Join(", ", _otherMarkCounts.Select(kv => $"{kv.Key} {kv.Value}枚").ToArray()));
            if (_parameterSkippedThisScene > 0)
                parts.Add($"Parameter の版が新しいため既定値のまま（ゲーム本体の読み込みと同じ）: {_parameterSkippedThisScene}枚");
            if (_failedByReason.Count > 0)
                parts.Add("完全に読んだもの: " + string.Join(", ", _failedByReason.Select(kv =>
                    $"{kv.Key} {kv.Value.Count}枚（{string.Join(" / ", kv.Value.Take(3).ToArray())}{(kv.Value.Count > 3 ? " ほか" : "")}）").ToArray()));
            return parts.Count == 0 ? "" : " / " + string.Join(" / ", parts.ToArray());
        }

        private static ChaFileControl MakeLight(ChaFileControl full)
        {
            try
            {
                var light = new ChaFileControl();
                light.CopyParameter(full.parameter);
                light.pngData = full.pngData;          // 顔画像の生成に使う。Start() の後で外す
                light.facePngData = full.facePngData;
                if (_charaFileNameProp != null)
                {
                    _charaFileNameProp.SetValue(light, full.charaFileName, null);
                }
                return light;
            }
            catch (Exception ex)
            {
                if (_failureLoggedKeys.Add("MakeLight"))
                {
                    SafeLogWarning("軽量版の作成（以後このカードは完全版のまま扱います）", ex);
                }
                return null;
            }
        }

        private static IEnumerable<CodeInstruction> GetCardListTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var helper = AccessTools.Method(typeof(LazyInviteCardList), nameof(AddMaybeLight));
            var loadHelper = AccessTools.Method(typeof(LazyInviteCardList), nameof(LoadForList));
            var loadTarget = AccessTools.Method(typeof(ChaFileControl), "LoadCharaFile",
                new[] { typeof(string), typeof(byte), typeof(bool), typeof(bool) });
            int replaced = 0;
            int loadReplaced = 0;

            for (int i = 0; i < list.Count; i++)
            {
                var ci = list[i];
                if (ci.opcode != OpCodes.Callvirt && ci.opcode != OpCodes.Call) continue;

                var mi = ci.operand as MethodInfo;
                if (mi == null) continue;

                // chaFileControl.LoadCharaFile(path, sex, noLoadPng, noLoadStatus) → LoadForList(chaFileControl, path, sex, …)
                // スタックは [instance, string, byte, bool, bool] → bool で、差し替え先の static メソッドと一致する
                if (loadTarget != null && mi == loadTarget)
                {
                    var loadCall = new CodeInstruction(OpCodes.Call, loadHelper);
                    ci.MoveLabelsTo(loadCall);
                    ci.MoveBlocksTo(loadCall);
                    list[i] = loadCall;
                    loadReplaced++;
                    continue;
                }

                if (mi.Name != "Add" || mi.DeclaringType != typeof(List<ChaFileControl>)) continue;

                // スタックは [list, item] → void で、差し替え先の static メソッドと一致する
                var call = new CodeInstruction(OpCodes.Call, helper);
                ci.MoveLabelsTo(call);
                ci.MoveBlocksTo(call);
                list[i] = call;
                replaced++;
            }

            _lastTranspileReplaced = replaced;
            _lastTranspileLoadReplaced = loadReplaced;
            return list;
        }

        // ===================== 男性（バディ）招待 =====================

        /// <summary>
        /// Dealer.InviteBuddy(ChaFileControl guest) に読み直しの Prefix を当てる。PatcherPlugin の本パスから Dealer 型ごとに呼ぶ。
        /// 男性一覧（InviteUI.otherMales）から招待する入口はここだけで、渡したカードは
        /// IE_LoadBuddiesList でキャラ生成と aI_Public.chaFile（交代用）にそのまま使われる。
        /// 女性の InviteCard と違い改名は不要（kPlug の BuddyIsAlreadyThere が同じ男性の重複招待を止める）。
        /// </summary>
        internal static void TryPatchInviteBuddy(Harmony harmony, Type dealerType)
        {
            string key = dealerType.FullName + "|InviteBuddy";
            if (_appliedKeys.Contains(key)) return;
            _appliedKeys.Add(key);

            try
            {
                var method = AccessTools.Method(dealerType, "InviteBuddy", new[] { typeof(ChaFileControl) });
                if (method == null)
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} {dealerType.FullName}.InviteBuddy(ChaFileControl) が無いため、男性カードの読み直しは適用しません。");
                    return;
                }
                harmony.Patch(method, prefix: Method(nameof(InviteBuddyPrefix)));
                _inviteBuddyHooked = true;
                PatcherPlugin.Logger.LogInfo($"{Tag} {dealerType.FullName}.InviteBuddy() に男性カードの読み直しを適用しました。");
            }
            catch (Exception ex)
            {
                LogApplyFailureOnce(key, $"{dealerType.FullName}.InviteBuddy()", ex);
            }
        }

        private static void InviteBuddyPrefix(ref ChaFileControl guest)
        {
            if (PatcherPlugin.IsQuitting || guest == null) return;
            KPlugPerfDiagnostics.OnInviteStarted("バディ", guest);
            try
            {
                TryRestore(ref guest);
            }
            catch (Exception ex)
            {
                SafeLogWarning("男性カードの読み直し", ex);
            }
        }

        // ===================== 招待時の読み直し =====================

        /// <summary>
        /// <paramref name="guest"/> が軽量版なら、控えたパスから完全なカードを読み直して差し替える。
        /// 軽量版の parameter（GirlCardTab による性格・声の読み替えを含む）を読み直したカードに写す。
        /// <see cref="InviteCardPatch.Prefix"/> の先頭（複製・改名の前）から呼ぶ。
        /// </summary>
        internal static bool TryRestore(ref ChaFileControl guest)
        {
            if (guest == null) return false;

            CardSource source;
            if (!_lightToSource.TryGetValue(guest, out source))
            {
                if (InviteCardDiagnostics.Enabled)
                {
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag} 軽量版の対応表に無いカードのため読み直しません（対応表 {_lightToSource.Count} 件 / " +
                        $"pngData={(guest.pngData != null ? "あり" : "なし")}）: {guest.charaFileName}");
                }
                return false;
            }

            var begin = KPlugMemoryDiagnostics.Take();
            var full = new ChaFileControl();
            bool ok;
            try
            {
                ok = full.LoadCharaFile(source.Path, source.Sex);
            }
            catch (Exception ex)
            {
                ok = false;
                SafeLogWarning($"カードの読み直し ({source.Path})", ex);
            }

            if (!ok)
            {
                PatcherPlugin.Logger.LogError(
                    $"{Tag} 招待のためのカード読み直しに失敗しました。軽量版のまま招待するため、見た目が既定の状態になる可能性があります: {source.Path}");
                return false;
            }

            full.CopyParameter(guest.parameter);
            guest = full;

            PatcherPlugin.Logger.LogInfo($"{Tag} 招待のためカードを読み直しました: {full.parameter.lastname}{full.parameter.firstname} ({source.Path})" +
                $" / 所要 {begin.watch.ElapsedMilliseconds}ms / {KPlugMemoryDiagnostics.Diff(begin, KPlugMemoryDiagnostics.Take())}");
            return true;
        }

        /// <summary>診断用。<paramref name="card"/> がこのクラスの作った軽量版かどうか。</summary>
        internal static bool IsLight(ChaFileControl card)
        {
            return card != null && _lightToSource.ContainsKey(card);
        }

        /// <summary>診断用。現在の Hシーンで作った軽量版の数。</summary>
        internal static int LightCount
        {
            get { return _lightToSource.Count; }
        }

        // ===================== 補助 =====================

        private static HarmonyMethod Method(string name)
        {
            return new HarmonyMethod(typeof(LazyInviteCardList).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
        }

        private static void LogApplyFailureOnce(string key, string target, Exception ex)
        {
            if (!_failureLoggedKeys.Add(key)) return;

            var message = ex.Message ?? string.Empty;
            if (ex is TypeLoadException || ex is ReflectionTypeLoadException
                || message.Contains("Could not load type") || message.Contains("IL Compile Error")
                || message.Contains("cannot be loaded"))
            {
                PatcherPlugin.Logger.LogInfo($"{Tag} {target} は対象外です（このモードでは kPlugVR のクラスをロードできません）: {message}");
            }
            else
            {
                PatcherPlugin.Logger.LogError($"{Tag} {target} への適用に失敗: {message}");
            }
        }

        private static void SafeLogWarning(string where, Exception ex)
        {
            try { PatcherPlugin.Logger.LogWarning($"{Tag}[{where}] 処理中に例外（kPlug 側の処理は継続します）: {ex.Message}"); }
            catch { }
        }
    }
}
