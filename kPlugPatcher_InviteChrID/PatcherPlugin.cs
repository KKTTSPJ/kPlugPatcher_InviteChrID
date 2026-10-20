using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using System.Linq;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// kPlug の招待機能は「キャラ名」をキーにして在席管理・重複判定・退場処理を行っている。
    /// そのため同名キャラ（＝同じキャラカードの複数招待）が扱えない。
    ///
    /// 本プラグインは、招待されるキャラカードデータ（メモリ上の複製）の名前に連番を付与することで
    /// kPlug から見て別人として扱わせ、同一カードの複数招待を可能にする。
    ///
    /// 【重要な設計方針】
    /// - ディスク上のキャラカードファイルは一切変更しない。
    /// - 招待UIが保持しているキャラカード実体も変更しない（複製を作ってそちらを改名する）。
    /// - セーブデータ由来のヒロイン招待には介入しない（セーブデータの氏名を汚さないため）。
    /// - 改名結果は kPlug の在席リスト(PresentNames)に自然に反映されるため、
    ///   在席判定・退場処理・名前指定APIとの整合が保たれる。
    ///
    /// また本プラグインは「kPlug に対するパッチ処理を集約する場所」として位置づけており、
    /// 招待が無反応になる不具合の回避策（<see cref="SpotManagerBonesFix"/>）もここに置く。
    /// 他プラグイン（KK_AlternativeVoice 等）は kPlug の挙動に手を出さない方針とする。
    /// </summary>
#if KK
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInProcess("Koikatu")]
    [BepInProcess("KoikatuVR")]
#elif KKP
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInProcess("Koikatsu Party")]
    [BepInProcess("Koikatsu Party VR")]
#endif
    [BepInDependency("kPlug.Katarsys", BepInDependency.DependencyFlags.SoftDependency)]
    public class PatcherPlugin : BaseUnityPlugin
    {
#if KK
        public const string GUID = "kkttspj.kk.kplugpatcher";
        /// <summary>1.10.x までの GUID。設定ファイルの引き継ぎに使う（<see cref="MigrateLegacyConfigFile"/>）。</summary>
        private const string LegacyGuid = "com.your.kk.kplugpatcher";
        public const string PluginName = "kPlug Patcher Invite ChrID";
#elif KKP
        public const string GUID = "kkttspj.kkp.kplugpatcher";
        private const string LegacyGuid = "com.your.kkp.kplugpatcher";
        public const string PluginName = "KKP kPlug Patcher Invite ChrID";
#endif
        public const string Version = "1.11.2";

        internal static new ManualLogSource Logger;
        internal static bool IsQuitting = false;
        private bool _patched = false;
        private bool _kPlugMissing = false;

        // 招待無反応の回避策（SpotManagerBonesFix）の設定
        internal static ConfigEntry<bool> EnableSpotManagerBonesFix;
        internal static ConfigEntry<bool> VerboseSpotManagerDiagnostics;

        // Inviteアイコンが反応しない問題の調査用診断（InviteUIDiagnostics）の設定
        internal static ConfigEntry<bool> EnableInviteUIDiagnostics;

        // Core.hStartCtx がKK環境で異常な値に固定される問題の調査用診断（HStartCtxDiagnostics）の設定
        internal static ConfigEntry<bool> EnableHStartCtxDiagnostics;

        // ゲーム本体(H.HSceneProc)側の体位カテゴリ選択ロジックを追う診断（HSceneCategoryDiagnostics）の設定
        internal static ConfigEntry<bool> EnableHSceneCategoryDiagnostics;

        // 衣装変更後にkPlugが破棄済み参照を掴み続ける問題の調査用診断（CoordinateChangeStaleRefDiagnostics）の設定
        internal static ConfigEntry<bool> EnableCoordinateChangeStaleRefDiagnostics;

        // 衣装変更後にkPlugが破棄済み参照を掴み続ける問題の回避策（CoordinateChangeStaleRefFix）の設定
        internal static ConfigEntry<bool> EnableCoordinateChangeStaleRefFix;
        // 同回避策のうち、キャラ交代後の BellyCtrl（お腹の膨らみ）の破棄済みボーンの復旧（v1.10.7）
        internal static ConfigEntry<bool> EnableBellyCtrlStaleBoneFix;

        // 絶頂ループ（ゲーム本体 ParentObjectCtrl の破棄済み参照）の回避策の設定。
        // kPlug ではなく「ゲーム本体(Assembly-CSharp)へのパッチ」なので個別に切り離せるようにしてある。
        internal static ConfigEntry<bool> EnableParentObjectCtrlStaleRefFix;

        // IK の目標が null になり絶頂ループになる問題（MotionIKNullTargetGuard）の設定。
        // EnableExtraAnimIkTargetGuard は kPlug 側、EnableMotionIKNullTargetGuard はゲーム本体(Assembly-CSharp)側の安全網。
        internal static ConfigEntry<bool> EnableExtraAnimIkTargetGuard;
        internal static ConfigEntry<bool> EnableMotionIKNullTargetGuard;

        // kPlugVR 本体の null チェック漏れに対する個別ガード（KPlugVRNullGuards）の設定
        internal static ConfigEntry<bool> EnableVRRootMenuVisibilityGuard;
        internal static ConfigEntry<bool> EnableVRCtrlLateUpdateGuard;
        internal static ConfigEntry<bool> EnableHinputParserChainGuard;

        // SpotCtrl.AdaptColSclToPose の NRE の原因を特定するための診断の設定
        internal static ConfigEntry<bool> EnableSpotCtrlAdaptColDiagnostics;

        // kPlug 3.599 以降の VR版で、同じカードを何度でも招待できるようにする回避策の設定
        internal static ConfigEntry<bool> EnableVRInviteMenuUnblock;

        // 招待メニューのカード一覧を遅延読み込みにしてメモリ使用量を抑える回避策（LazyInviteCardList）の設定
        internal static ConfigEntry<bool> EnableLazyInviteCardList;
        internal static ConfigEntry<bool> LazyInviteIncludeMaleCards;
        internal static ConfigEntry<bool> LazyInvitePartialLoad;

        // 在席中のキャラと同名のカードを招待リストに出す回避策（InviteListSameNameUnblock）の設定
        internal static ConfigEntry<bool> ShowSameNameCardsInInviteList;

        internal static ConfigEntry<bool> EnableAIPublicKbGuard;
        internal static ConfigEntry<bool> EnableAIMainFaceColorGuard;
        internal static ConfigEntry<bool> EnableAISecondFaceColorGuard;
        internal static ConfigEntry<bool> EnableGuestFaceColorGuard;
        internal static ConfigEntry<bool> EnableHCtrlKissGuard;
        internal static ConfigEntry<bool> EnableCopyLiquidMapGuard;

        // 招待時に InviteCard の Prefix が呼ばれない現象を調べる診断（InviteCardDiagnostics）の設定
        internal static ConfigEntry<bool> EnableInviteCardDiagnostics;

        // kPlug の招待一覧以外のメモリ消費を追跡する診断（KPlugMemoryDiagnostics）の設定
        internal static ConfigEntry<bool> EnableKPlugMemoryDiagnostics;

        // 招待1人ごとのコストと複数人招待時のフレームレート低下を調べる診断（KPlugPerfDiagnostics）の設定
        internal static ConfigEntry<bool> EnableKPlugPerfDiagnostics;
        internal static ConfigEntry<bool> KPlugPerfProfileMethods;
        internal static ConfigEntry<bool> KPlugPerfProfileCoroutines;
        internal static ConfigEntry<bool> KPlugPerfProfileGame;
        internal static ConfigEntry<bool> KPlugPerfTraceSwapSection;

        // 複数人招待時の DynamicBone（揺れもの）の負荷を下げる（DynamicBoneThrottle）の設定
        internal static ConfigEntry<bool> DisableInvitedDynamicBones;
        internal static ConfigEntry<bool> DisableInvitedBreastDynamicBones;
        internal static ConfigEntry<bool> LimitDynamicBoneIterations;
        internal static ConfigEntry<int> MaxDynamicBoneIterationsPerFrame;

        // HVoiceCaching が kPlug のキャラ交代のたびに例外を投げ、交代処理の後半が飛ぶ問題の回避策（HVoiceCachingShortBreathFix）の設定
        internal static ConfigEntry<bool> EnableHVoiceCachingShortBreathFix;
        // HVoiceCaching を入れていると 3P の Hシーンのロードが止まる問題の回避策（HVoiceCachingVoiceListFix）の設定
        internal static ConfigEntry<bool> EnableHVoiceCachingVoiceListFix;

        // kPlug のキャラ交代で表情リストが読み直されない問題の対策（FaceListReloadOnSwap）の設定
        internal static ConfigEntry<bool> EnableFaceListReloadOnSwap;

        // kPlug の FaceCtrl.IE_SetFace の NRE の原因を調べる診断（FaceCtrlSetFaceDiagnostics）の設定
        internal static ConfigEntry<bool> EnableSetFaceNreDiagnostics;

        // 招待キャラ複数との同時キス（MultiGuestKiss）の設定
        internal static ConfigEntry<bool> EnableMultiGuestKiss;
        internal static ConfigEntry<float> MultiKissStartDistance;
        internal static ConfigEntry<float> MultiKissStartAngle;
        internal static ConfigEntry<float> MultiKissKeepDistance;
        internal static ConfigEntry<float> MultiKissKeepAngle;
        internal static ConfigEntry<bool> EnableStuckGuestKissFix;

        /// <summary>
        /// パッチの適用に失敗したときのログ（1.11.0）。通常モードで kPlugVR の型に当てようとして、
        /// kPlugVR のクラスがロードできずに失敗した場合（IL Compile Error など）は、実害が無いので Info で記録する。
        /// それ以外の失敗は従来どおり Error。
        /// </summary>
        internal static void LogPatchFailure(string prefix, Type type, Exception ex)
        {
            var message = ex != null ? ex.Message : "(不明)";
            var typeName = type != null ? type.FullName : "(不明)";
            if (typeName.StartsWith("kPlugVR.") && !Paths.ProcessName.EndsWith("VR") && IsAssemblyUnavailable(ex))
            {
                Logger.LogInfo($"{prefix} {typeName} は対象外です（通常モードでは kPlugVR のクラスをロードできません。実害はありません）: {message}");
                return;
            }
            Logger.LogError($"{prefix} {typeName} への適用に失敗: {message}");
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

        /// <summary>
        /// 1.10.x までの GUID の設定ファイル（{LegacyGuid}.cfg）を、新しい GUID の名前に写して読み直す（1.11.0）。
        /// 新しいファイルが既にあれば何もしない（2回目以降の起動）。古いファイルは残す（旧版に戻したときのため）。
        /// </summary>
        private void MigrateLegacyConfigFile()
        {
            try
            {
                string newPath = Config.ConfigFilePath;
                string oldPath = Path.Combine(Path.GetDirectoryName(newPath), LegacyGuid + ".cfg");
                if (File.Exists(newPath) || !File.Exists(oldPath)) return;
                File.Copy(oldPath, newPath);
                Config.Reload();
                Logger.LogInfo($"[設定] {Path.GetFileName(oldPath)} を {Path.GetFileName(newPath)} に引き継ぎました（GUID の変更。古いファイルは残します）。");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[設定] 古い設定ファイル（{LegacyGuid}.cfg）を引き継げませんでした。既定値で続行します: {ex.Message}");
            }
        }

        private void Awake()
        {
            Logger = base.Logger;
            Logger.LogInfo($"{PluginName} v{Version} がロードされました。");
            // 1.10.x までの GUID の設定ファイルを引き継ぐ。どの設定を読むよりも前に行う（1.11.0）。
            MigrateLegacyConfigFile();

            EnableSpotManagerBonesFix = Config.Bind(
                "SpotManagerBonesFix", "EnableSpotManagerBonesFix", true,
                "招待が無反応になる不具合の回避策を有効にするかどうか。kPlug の SpotManager.AdaptCaliber が破棄済みのボーン参照に触れて NullReferenceException を起こし、招待処理がロックされる問題を、kPlug 自身の BonesCtrl.FindBones() を呼び直すことで回避します。");
            VerboseSpotManagerDiagnostics = Config.Bind(
                "SpotManagerBonesFix", "VerboseSpotManagerDiagnostics", false,
                "AdaptCaliber の呼び出しごとに、ボーン参照の詳細な状態をログ出力するかどうか。原因調査が必要なときだけ有効にしてください（通常は破棄済みを検出・復旧したときのみログが出ます）。");
            EnableInviteUIDiagnostics = Config.Bind(
                "InviteUIDiagnostics", "EnableInviteUIDiagnostics", false,
                "Inviteアイコンが反応しない問題を調査するための診断。isValidCtx/isOpenable/showMenu等の状態が変化した瞬間だけログに出力する（読み取り専用、値の書き換えは行わない）。原因特定後は無効化して構わない。");
            EnableHStartCtxDiagnostics = Config.Bind(
                "HStartCtxDiagnostics", "EnableHStartCtxDiagnostics", false,
                "Core.hStartCtx がKK環境で異常な値(SpLib1等)に固定される問題を調査するための診断。ParserScene.HStartCtx()の呼び出しごとに、呼び出し回数・戻り値・判定材料のhAnimをログ出力する（読み取り専用）。原因特定後は無効化して構わない。");
            EnableHSceneCategoryDiagnostics = Config.Bind(
                "HSceneCategoryDiagnostics", "EnableHSceneCategoryDiagnostics", false,
                "ゲーム本体(H.HSceneProc)がHシーン開始時にどの体位カテゴリを選ぶかを追うための診断。CreateListAnimationFileName/LoadAnimationListInfoの呼び出しごとに、categorys・kindMap.categoryes・絞り込み結果・最終選択結果をログ出力する（読み取り専用、kPlugではなくゲーム本体Assembly-CSharpへのパッチ）。原因特定後は無効化して構わない。");
            EnableCoordinateChangeStaleRefFix = Config.Bind(
                "CoordinateChangeStaleRefFix", "EnableCoordinateChangeStaleRefFix", true,
                "衣装変更(CoordinateChanged)後にkPlugが破棄済み参照を掴み続け、毎フレームNullReferenceExceptionを出し続ける問題の回避策を有効にするかどうか。ColDefsManager.gos / BindersCtrl.binders / BonesCtrl.customs から破棄済み要素を除去し、破棄済みボーン参照は kPlug 自身の BonesCtrl.FindBones() を呼び直して復旧します。");
            EnableBellyCtrlStaleBoneFix = Config.Bind(
                "CoordinateChangeStaleRefFix", "EnableBellyCtrlStaleBoneFix", true,
                "kPlug のキャラ交代（AI_Public.IE_SwapGirls）の後、お腹の膨らみ（BellyCtrl、妊娠設定が有効なとき）が破棄済みの骨を掴み続け、BellyCtrl.LateUpdate → LerpInflation で毎フレーム NullReferenceException を出す問題の回避策（v1.10.7）。BellyCtrl には骨を取り直す処理が無いため、破棄済みの骨（cf_s_waist01/02・cf_s_spine01/02）だけを kPlug 自身の ChaTools.FindInCha() で探し直して差し替えます（入れ替え後の基準値はそのまま）。復旧できるまでの間は、その骨の書き込みだけを飛ばします。EnableCoordinateChangeStaleRefFix が有効なときだけ働きます。VR・通常モードの両方が対象。変更はゲーム再起動後に反映されます。");
            EnableParentObjectCtrlStaleRefFix = Config.Bind(
                "ParentObjectCtrlStaleRefFix", "EnableParentObjectCtrlStaleRefFix", true,
                "招待・キャラ交代の後、ゲージが上がっていないのに絶頂が繰り返される問題の回避策を有効にするかどうか。ゲーム本体の ParentObjectCtrl が破棄済みの小物 GameObject を参照して NullReferenceException を起こし、その結果 HSonyu.Proc の絶頂終了処理（flags.gaugeFemale = 0f）が実行されないことが原因です。破棄済み要素を読み飛ばす等価な実装に置き換えます。※本項目は kPlug ではなくゲーム本体(Assembly-CSharp)へのパッチです。");
            EnableExtraAnimIkTargetGuard = Config.Bind(
                "MotionIKNullTargetGuard", "EnableExtraAnimIkTargetGuard", true,
                "kPlug の追加アニメーション（ToolExtrAnimIK.SetIkTargets）が、アニメーションの切り替えのたびに手足の IK の目標をキャラの中から名前で探して付け直す際、見つからないと null をそのまま入れてしまう問題のガード（v1.10.6）。キャラの骨格が一時的にキャラの外にあるとき（例: KK_SetParentVR で男の位置を動かしている間）に起き、その後ゲーム本体の MotionIK で例外が続いて絶頂が繰り返されます（絶頂ループ）。null にされた目標だけを元に戻し、フック先が null の IkHook.Create は作らずに戻します。kPlug が見つけた目標を入れた場合は何もしません。どのキャラ・どの手足かを最初の1回と50回ごとにログ出力します。VR・通常モードの両方が対象。変更はゲーム再起動後に反映されます。");
            EnableMotionIKNullTargetGuard = Config.Bind(
                "MotionIKNullTargetGuard", "EnableMotionIKNullTargetGuard", true,
                "ゲーム本体の MotionIK.LinkIK が、手足の IK の目標（effector.target）や曲げの目標（bend.bendGoal）が null のときに NullReferenceException を起こす問題の安全網（v1.10.6）。この例外はアニメーションの切り替え（HActionBase.SetPlay）の途中で起きるため、HSonyu.Proc の絶頂の終了処理（finish の解除・ゲージのリセット）が飛び、絶頂が繰り返されます。目標が null の手足だけを飛ばし、残りの手足とアニメーションの切り替えの後ろの処理を実行させます。原因が kPlug 以外でも効きます。どのキャラ・どの手足かを最初の1回と50回ごとにログ出力します。※本項目は kPlug ではなくゲーム本体(Assembly-CSharp)へのパッチです。変更はゲーム再起動後に反映されます。");
            EnableLazyInviteCardList = Config.Bind(
                "LazyInviteCardList", "EnableLazyInviteCardList", true,
                "kPlug の招待メニューが全キャラカードを完全な形で読み込み、Hシーンの間ずっと保持する問題（1枚あたり約30MB、約100枚でHシーン入場ごとに約3GB）の回避策。一覧には名前・性格と顔画像だけを持つ軽量版を置き、完全なカードは招待する瞬間に読み直します。読み込み中も1枚ずつ軽量版へ置き換えるため、読み込み時のメモリのピークも下がります。kPlug 3.5.6 など旧API（InviteUI があるもの）向け。男性一覧の扱いは IncludeMaleCards で切り替えます。変更はゲーム再起動後に反映されます。");
            LazyInviteIncludeMaleCards = Config.Bind(
                "LazyInviteCardList", "IncludeMaleCards", true,
                "男性一覧（kPlug の EnBuddyInvite で表示されるバディ用カード。SearchBuddyInMale で男性フォルダも含む）も軽量版にするかどうか。有効な場合、バディを招待する瞬間（Dealer.InviteBuddy）に完全なカードを読み直します。無効にすると男性一覧は完全版のまま保持します（v1.5.2 以前の動作。1枚あたり約30MB）。EnableLazyInviteCardList が有効なときだけ意味を持ちます。変更はゲーム再起動後に反映されます。");
            LazyInvitePartialLoad = Config.Bind(
                "LazyInviteCardList", "PartialLoad", true,
                "招待メニューの一覧用カードを、完全に読んでから軽量版にするのではなく、最初から全身画像・顔画像・パラメータだけを読むかどうか（v1.7.0）。拡張データ（Material Editor のテクスチャなど）の展開と他プラグインの読み込み処理を省くため、Hシーン入場時の読み込み時間と一時的なメモリ増加（約12秒・約2GB）を減らします。部分読み込みに失敗したカードは完全に読みます。無効にすると v1.6.x の動作に戻ります。EnableLazyInviteCardList が有効なときだけ意味を持ちます。");
            ShowSameNameCardsInInviteList = Config.Bind(
                "InviteCard", "ShowSameNameCardsInInviteList", true,
                "在席中のキャラクター（メインヒロイン・セカンドヒロイン・招待済み）と同じ名前のカードも、招待リストに表示するかどうか（v1.8.2）。kPlug は InviteUI.IsAlreadyInHarem で名前が一致するカードを一覧から外しますが、本プラグインは招待の瞬間にカードを複製して改名するため、同名でも別個体として扱えます。対象はカード一覧（女性・男性）だけで、ストーリー／デート相手のヒロイン（セーブデータ由来で改名しない）は従来どおり除外されます。無効にすると kPlug 本来の動作に戻ります。変更はゲーム再起動後に反映されます。");
            EnableVRInviteMenuUnblock = Config.Bind(
                "InviteCard", "EnableVRInviteMenuUnblock", true,
                "kPlug 3.599 以降の VR版で、同じキャラカードを何度でも招待できるようにするかどうか。新しい招待UI(kPlugVR.Menu.MenuInvite)は、招待に成功したカードを一覧から外してしまうため、改名だけでは複数招待ができません。InviteSomeone の Postfix で「招待済み」フラグを戻し、カードを一覧に残します。3.59 以前のVRと非VR版では対象の型が無いため何もしません。無効にすると、新VRでは同じカードを1回しか招待できなくなります。");
            EnableSpotCtrlAdaptColDiagnostics = Config.Bind(
                "SpotCtrlAdaptColDiagnostics", "EnableSpotCtrlAdaptColDiagnostics", false,
                "kPlug の SpotCtrl.AdaptColSclToPose() が出す NullReferenceException の原因を特定するための診断。例外が発生したときだけ、ai / ai.actionBinder / infos / spotCol のどれが null だったかをログ出力します（読み取り専用。例外は抑止せずそのまま通すため、ゲーム側の挙動は変わりません）。1セッション5件まで。原因特定後は無効化して構いません。");
            EnableCoordinateChangeStaleRefDiagnostics = Config.Bind(
                "CoordinateChangeStaleRefDiagnostics", "EnableCoordinateChangeStaleRefDiagnostics", true,
                "特定のキャラカードで衣装変更(CoordinateChanged)後にkPlugが破棄済み参照を掴み続け、毎フレームNREを出し続ける問題を調査するための診断。ColDefsManager.LateUpdate/BindersCtrl.Update/BonesCtrl.LateUpdateを監視し、破棄済み(destroyed)のGameObject/Transformが検出された時だけログ出力する（読み取り専用、修復は行わない）。原因特定後は無効化して構わない。");
            EnableVRRootMenuVisibilityGuard = Config.Bind(
                "KPlugVRNullGuards", "EnableVRRootMenuVisibilityGuard", true,
                "kPlugVR の VRCtrl.SetMenuVisibility() が VrRootMenu.menuGo だけ null チェックをしておらず、Hシーン開始時に NullReferenceException を出す問題のガード。menuGo が null／破棄済みのときだけ SetMenuVisibility をスキップし、スキップ開始時と解消時に原因切り分け用の値をログ出力します。VRモードのみ対象。変更はゲーム再起動後に反映されます。");
            EnableVRCtrlLateUpdateGuard = Config.Bind(
                "KPlugVRNullGuards", "EnableVRCtrlLateUpdateGuard", true,
                "kPlugVR の VRCtrl.LateUpdate() が、タイトル画面で作られた VRCtrl を破棄するフレーム（createdOnTitle=true かつ Core.onTitle=false）でだけ出す NullReferenceException を抑止します。Finalizer で例外を捨てるだけで原型の処理は一切変えません（例外より後ろは原型でも実行されないため、失われる処理はありません）。抑止した最初の1回をログ出力します。VRモードのみ対象。変更はゲーム再起動後に反映されます。");
            EnableHinputParserChainGuard = Config.Bind(
                "KPlugVRNullGuards", "EnableHinputParserChainGuard", true,
                "kPlugVR の HinputParser.Chain() が Hシーン開始時に参照未確定で NRE を出す問題のガード。Core.hCtrl / Core.dealer / Core.mainAi が null のとき、または入力待ちタイマーが0以下で hCtrl.VRCtrl / VRCtrl.leads が null のときだけ Chain をスキップします（いずれも原型は何もしないか、副作用の前に例外になるため、失われる処理はありません）。スキップ開始・解消時と、ガード条件の外で NRE が出た場合（例外はそのまま通す）に参照の状態をログ出力します。VRモードのみ対象。変更はゲーム再起動後に反映されます。");
            EnableAIPublicKbGuard = Config.Bind(
                "KPlugVRNullGuards", "EnableAIPublicKbGuard", true,
                "kPlugVR の AI_Public.LateUpdate() が、キャラ招待直後で BonesCtrl.kb が未初期化の間に bc.kb.min_exitation を参照して NullReferenceException を出す問題のガード。kb が null のときは min_exitation による下限補正だけをスキップし（非VR版 kPlug で作者が入れた null チェックと同じ結果）、LateUpdate の残りは実行します。スキップ開始時と解消時にログ出力します。既に null チェックがある非VR版には適用されません。変更はゲーム再起動後に反映されます。");
            EnableAIMainFaceColorGuard = Config.Bind(
                "KPlugVRNullGuards", "EnableAIMainFaceColorGuard", true,
                "kPlug の AI_Main.Ahegao() が、アヘ顔の表示中に Core.heroineCha.customMatFace（顔のマテリアル）を null チェックせず SetColor するため、毎フレーム NullReferenceException を出す問題のガード。マテリアルが null／破棄済みのときだけ頬の色の上書きをスキップし、Ahegao() の残り（内部状態の記録）と呼び出し元 LateUpdate の残りは実行します（原型では例外でどちらも実行されません）。スキップ開始時と解消時にログ出力します。EnableAhegao が有効なときだけ起きます。変更はゲーム再起動後に反映されます。");
            EnableAISecondFaceColorGuard = Config.Bind(
                "KPlugVRNullGuards", "EnableAISecondFaceColorGuard", true,
                "kPlug の AI_Second.Ahegao()（3P の2人目）が、アヘ顔の表示中に Core.heroine2Cha.customMatFace（顔のマテリアル）を null チェックせず SetColor するため、毎フレーム NullReferenceException を出す問題のガード（v1.10.4）。EnableAIMainFaceColorGuard（1人目）の2人目版です。マテリアルが null／破棄済みのときだけ頬の色の上書きをスキップし、Ahegao() の残り（内部状態の記録）と呼び出し元 LateUpdate の残り（2人目のまばたき設定など）は実行します。スキップ開始時と解消時にログ出力します。VR・通常モードの両方が対象。変更はゲーム再起動後に反映されます。");
            EnableGuestFaceColorGuard = Config.Bind(
                "KPlugVRNullGuards", "EnableGuestFaceColorGuard", true,
                "kPlug の招待キャラの表情のコルーチン（FaceCtrl.IE_SetFace）が、アヘ顔の表示中に顔のマテリアル（customMatFace）を null チェックせず SetColor するため、表情が変わるたびに NullReferenceException が出る問題のガード（v1.10.3）。EnableAIMainFaceColorGuard（メインヒロイン側）の招待キャラ版です。マテリアルが使えないときだけ頬の色の上書きを飛ばし、今の表情の記録（例外で飛んでいた最後の処理）まで実行します。どのキャラで起きたかをログに出します。VR・通常モードの両方が対象。変更はゲーム再起動後に反映されます。");
            EnableHCtrlKissGuard = Config.Bind(
                "KPlugVRNullGuards", "EnableHCtrlKissGuard", true,
                "kPlugVR の HCtrl.ParseTouchables() が、招待したキャラへのキスが終わる瞬間（キスの相手 kissable_ai が null になったフレーム）に kissable_ai.KissByPlayer() を呼んで NullReferenceException を出す問題のガード。null のときはその呼び出しだけをスキップします（原型ではここで例外になり、メソッドの最後の文なので失われる処理はありません）。最初の1回と50回ごとにログ出力します。kPlug 3.63 以降の VR は作者が修正済みのため適用されません。VRモードのみ対象。変更はゲーム再起動後に反映されます。");
            EnableCopyLiquidMapGuard = Config.Bind(
                "KPlugVRNullGuards", "EnableCopyLiquidMapGuard", true,
                "kPlug の ChaTools.CopyLiquidMapToMat()（体・顔の液体の模様を、メインヒロインのマテリアルから別のマテリアルへ写す処理）が、写し元・写し先のマテリアル（customMatFace / customMatBody）を null チェックせずに使うため、顔の素材を指定していない顔 MOD のキャラなどで Hシーン開始時に NullReferenceException を出す問題のガード（v1.10.5）。どちらかが null のときだけコピーをスキップして false（コピーしなかった）を返します。原型は最初のコピーより前に例外になるため、失われる処理はありません（例外で飛んでいた呼び出し元の残りは実行されるようになります）。最初の1回と50回ごとにログ出力します。VR・通常モードの両方が対象。変更はゲーム再起動後に反映されます。");
            EnableInviteCardDiagnostics = Config.Bind(
                "InviteCard", "EnableInviteCardDiagnostics", false,
                "カード招待のときに InviteCard の Prefix（改名・遅延読み込みの読み直し）が呼ばれているかを調べるための診断。Prefix に入ったとき、招待が始まったとき（呼び出し元のスタック付き）、Hシーンごとに InviteCard に付いているパッチの一覧をログ出力します（読み取り専用）。原因特定後は無効化して構いません。変更はゲーム再起動後に反映されます。");

            EnableKPlugMemoryDiagnostics = Config.Bind(
                "KPlugMemoryDiagnostics", "EnableKPlugMemoryDiagnostics", false,
                "kPlug の招待カード一覧以外のメモリ消費を調べるための診断（読み取り専用）。InviteUI.Start の前後の所要時間・Mono ヒープ全体と使用量・コミットの変化と、InviteUI.Start 以外で kPlug が全カードを読み込む処理（kFileParser.GetCardList。FindCardData など）の回数・読んだ枚数・呼び出し元をログ出力します。最初の15回は1回ずつ、以降は60秒ごとにまとめて出します。原因特定後は無効化して構いません。変更はゲーム再起動後に反映されます。");

            EnableKPlugPerfDiagnostics = Config.Bind(
                "KPlugPerfDiagnostics", "EnableKPlugPerfDiagnostics", false,
                "招待1人ごとのコストと、複数人招待時のフレームレート低下を調べるための診断（読み取り専用）。30秒ごとにフレーム時間・キャラ数・GC・例外の件数と、kPlug のメソッド別の処理時間の上位をログ出力します。キャラの読み込み・再読み込み（ChaControl.Load / Reload / ChangeCoordinateTypeAndReload）の所要時間と呼び出し元、招待の瞬間とその5秒後・20秒後のメモリとオブジェクト数も記録します。計測自体にわずかな負荷があります。原因特定後は無効化して構いません。変更はゲーム再起動後に反映されます。");
            KPlugPerfProfileMethods = Config.Bind(
                "KPlugPerfDiagnostics", "ProfileKPlugMethods", false,
                "kPlug の MonoBehaviour の Update / LateUpdate / FixedUpdate / OnGUI（約150個）に処理時間の計測を当てるかどうか。起動後、1フレーム8個ずつ当てます。無効にするとフレーム時間などのまとめだけを出します（計測の負荷を除いたフレーム時間の比較に使えます）。");
            KPlugPerfProfileCoroutines = Config.Bind(
                "KPlugPerfDiagnostics", "ProfileInviteCoroutines", false,
                "招待に関係する kPlug のコルーチン（Dealer / SpotManager / SpotCtrl / AI_Public / AI_Buddy / AI_Main / InviteUI の IE_…）の1回ごとの処理時間も計測するかどうか。ProfileKPlugMethods が有効なときだけ意味を持ちます。");

            KPlugPerfProfileGame = Config.Bind(
                "KPlugPerfDiagnostics", "ProfileGameCharaUpdates", true,
                "ゲーム本体のキャラごとの更新処理（DynamicBone / Ver01 / Ver02、Manager.Character、FaceBlendShape、EyeLookController、NeckLookControllerVer2、服・髪・アクセサリのコンポーネント）にも処理時間の計測を当てるかどうか（v1.7.4）。フレーム低下のうち、kPlug 以外の部分の割合を調べるためのもの。呼び出し回数が多い（DynamicBone はキャラ1人あたり約40個）ため、計測自体の負荷は kPlug の分より大きくなります。ProfileKPlugMethods が有効なときだけ意味を持ちます。");

            KPlugPerfTraceSwapSection = Config.Bind(
                "KPlugPerfDiagnostics", "TraceSwapSection", false,
                "キャラ交代で約11秒止まる件の調査（v1.8.0）。交代処理（AI_Public.IE_SwapGirls）の遅い区間で呼ばれる kPlug の処理（statManager の各種、GetStats、ReplaceFaceImage、AdaptAnim など）と、ゲーム本体の HVoiceCtrl.Init に計測を当て、1回 30ms 以上かかった呼び出しを1件ずつログ出力します（読み取り専用）。");
            DisableInvitedDynamicBones = Config.Bind(
                "DynamicBoneThrottle", "DisableInvitedDynamicBones", true,
                "kPlug が招待したキャラとバディの揺れもの（DynamicBone / DynamicBone_Ver01。髪・スカート・アクセサリなど）の計算を止める（v1.8.0）。複数人招待時のフレームレート低下の主因（5人で約6割）への対策。止めた揺れものは初期の姿勢で静止します。主人公・メインヒロインは対象外。変更はゲーム再起動後に反映されます。");
            DisableInvitedBreastDynamicBones = Config.Bind(
                "DynamicBoneThrottle", "DisableInvitedBreastDynamicBones", false,
                "DisableInvitedDynamicBones が有効なとき、招待キャラとバディの胸の揺れ（DynamicBone_Ver02）も止めるかどうか。数が少なく負荷は小さいので、既定では止めません。変更はゲーム再起動後に反映されます。");
            LimitDynamicBoneIterations = Config.Bind(
                "DynamicBoneThrottle", "LimitDynamicBoneIterations", true,
                "すべてのキャラの揺れもの（DynamicBone / DynamicBone_Ver01）について、1フレームの計算回数を MaxIterationsPerFrame までに抑える（v1.8.0）。元の処理はフレームが遅いと1フレームで最大3回計算するため、フレームレートが下がるほど重くなる悪循環になる。フレームレートが60以上なら元々1回以下なので見た目は変わりません。変更はゲーム再起動後に反映されます。");
            MaxDynamicBoneIterationsPerFrame = Config.Bind(
                "DynamicBoneThrottle", "MaxIterationsPerFrame", 1,
                "LimitDynamicBoneIterations が有効なときの、1フレームあたりの揺れものの計算回数の上限（1〜2。3以上は元の処理と同じ）。");
            EnableHVoiceCachingShortBreathFix = Config.Bind(
                "HVoiceCachingFix", "EnableHVoiceCachingShortBreathFix", true,
                "HVoiceCaching 0.3 を併用しているとき、kPlug のキャラ交代のたびに HVoiceCtrl.Init の中で例外（An element with the same key already exists）が出て、交代処理（AI_Public.IE_SwapGirls）の後半が実行されない問題の回避策（v1.8.3）。例外が出ると、kPlug がキャラごとの声の性格を覚え直せず、交代を重ねるうちに別の性格の声で話すようになります。また交代で外に出たキャラが「交代中」のまま残り、自分から動かなくなります。HVoiceCaching が短い吐息の辞書を空にせずに読み込むことが原因なので、その直前に本来のゲーム処理と同じく辞書を空にします。HVoiceCaching が入っていない環境では何もしません。※ kPlug ではなくゲーム本体(Assembly-CSharp)へのパッチです。変更はゲーム再起動後に反映されます。");
            EnableHVoiceCachingVoiceListFix = Config.Bind(
                "HVoiceCachingFix", "EnableHVoiceCachingVoiceListFix", true,
                "HVoiceCaching 0.3 を併用しているとき、3P（女の子2人）の Hシーンがロード画面のまま進まなくなる問題の回避策（v1.9.1）。HVoiceCaching は2人目の声のキャッシュが無いと、1人目をキャッシュから読み込んだまま本来の読み込みに処理を戻すため、本来の読み込みが HVoiceCtrl.LoadVoice で例外（An element with the same key already exists）になり、Hシーンの準備が止まります。本来の読み込みが行われるときだけ、その直前に声の辞書を HVoiceCtrl.Init 直後と同じ状態に戻します。HVoiceCaching が2人分をキャッシュから読んだときと、HVoiceCaching が入っていない環境では何もしません。※ kPlug ではなくゲーム本体(Assembly-CSharp)へのパッチです。変更はゲーム再起動後に反映されます。");
            EnableSetFaceNreDiagnostics = Config.Bind(
                "SetFaceDiagnostics", "EnableSetFaceNreDiagnostics", false,
                "kPlug の招待キャラの表情のコルーチン（FaceCtrl.IE_SetFace）が出す NullReferenceException の原因を調べるための診断（v1.10.2、読み取り専用）。例外が出たときだけ、キャラ名・カードの性格・表情の種類・アヘ顔の状態と、コルーチンがたどる参照（ai.Cha、Core.config、hProc.voice.faceLists[0]、mouthCtrl、fileCustom.face.baseMakeup、customMatFace）が null かどうかをログ出力します。例外は抑止しません。原因特定後は無効化して構いません。変更はゲーム再起動後に反映されます。");
            EnableFaceListReloadOnSwap = Config.Bind(
                "FaceListFix", "EnableFaceListReloadOnSwap", true,
                "kPlug のキャラ交代で、交代後のキャラの性格に合わせて表情リスト（HFaceList）を読み直す（v1.8.4）。kPlug は交代時に声のリストだけを読み直し、表情のリストは Hシーン開始時のメインキャラの性格のまま使い続けるため、交代後のキャラはセリフと合わない表情になります。HVoiceCtrl.Init が別の性格で呼び直されたときだけ働き、Hシーン開始時の読み込みは増えません。kPlug は招待キャラの表情にもメインの表情リストを使うため、招待キャラの表情も「今のメインの性格の表」になります。※ kPlug ではなくゲーム本体(Assembly-CSharp)へのパッチです。変更はゲーム再起動後に反映されます。");

            EnableMultiGuestKiss = Config.Bind(
                "MultiGuestKiss", "EnableMultiGuestKiss", true,
                "kPlugVR で、招待したキャラ複数と同時にキスできるようにする（v1.9.0）。kPlug は範囲内の最初の1人としかキスしないため、ほかの招待キャラがキス中のときに限り、StartDistance / StartAngle の範囲に入った2人目以降のキャラのキスも始めます（1人だけのキスは kPlug 本来の範囲のまま）。同時にキスした全員にキス回数・経験値・興奮が入り、キスの声が重なります。KK_CyuVR を入れていれば、全員の口と舌が動きます。VRモードのみ対象。変更はゲーム再起動後に反映されます（範囲の値はすぐ反映）。");
            MultiKissStartDistance = Config.Bind(
                "MultiGuestKiss", "StartDistance", 0.18f,
                new ConfigDescription("2人目以降のキャラのキスを始める距離（m）。キャラの鼻とカメラ（ヘッドセット）の距離がこれより近いと始めます。kPlug 3.56 の本来の範囲は 0.12m（KissDist 設定で ±0.1m）、KK_CyuVR の KissDistance の既定値は 0.18m。",
                    new AcceptableValueRange<float>(0.05f, 0.5f)));
            MultiKissStartAngle = Config.Bind(
                "MultiGuestKiss", "StartAngle", 30f,
                new ConfigDescription("2人目以降のキャラのキスを始める角度（度）。カメラの正面とキャラの鼻の方向のずれがこれより小さいと始めます。kPlug の本来の範囲は 25°。2人の顔が横に並んでいると、どちらも正面から少しずれるため、kPlug より広くしてあります。",
                    new AcceptableValueRange<float>(5f, 90f)));
            MultiKissKeepDistance = Config.Bind(
                "MultiGuestKiss", "KeepDistance", 0.22f,
                new ConfigDescription("同時キスを続ける距離（m）。この距離より離れるとキスを終えます。StartDistance より小さい値は StartDistance として扱います。2人目以降がいる間は、kPlug が選んだキャラもこの範囲にいる限りキスを続けます。",
                    new AcceptableValueRange<float>(0.05f, 0.6f)));
            MultiKissKeepAngle = Config.Bind(
                "MultiGuestKiss", "KeepAngle", 45f,
                new ConfigDescription("同時キスを続ける角度（度）。StartAngle より小さい値は StartAngle として扱います。",
                    new AcceptableValueRange<float>(5f, 90f)));
            EnableStuckGuestKissFix = Config.Bind(
                "MultiGuestKiss", "EnableStuckGuestKissFix", true,
                "kPlugVR で、キスの相手が同じフレームで別の招待キャラに直接替わると、前の相手のキス終了（FinishKiss）が呼ばれず、離れてもキス状態のまま残る問題の後始末（v1.9.0）。kPlug のキスの相手でも同時キスの対象でもないのにキス状態のキャラのキスを終わらせます（KK_CyuVR では、残っていたキャラの口と舌が動き続けていました）。EnableMultiGuestKiss とは別に切り替えられます。VRモードのみ対象。変更はゲーム再起動後に反映されます。");

            if (!IsKPlugLoaded())
            {
                Logger.LogWarning("kPlugのアセンブリが見つかりません。kPlugがインストールされていないため、このプラグインを無効化します。");
                _kPlugMissing = true;
                this.enabled = false;
            }
        }

        /// <summary>OnApplicationQuit が呼ばれたフレーム。終了が取り消されたかの判定に使う。</summary>
        private int _quitRequestedFrame = -1;

        /// <summary>
        /// 終了確認ダイアログで「いいえ」を選ぶと、ゲームは終了せずに続く。
        /// ウィンドウを閉じようとした時点で Unity が全 MonoBehaviour の OnApplicationQuit を呼ぶが、
        /// ゲーム本体の Manager.Scene.OnApplicationQuit が Application.CancelQuit() で終了を取り消し、
        /// 終了確認の "Exit" シーンを表示する。
        /// そのため、ここで立てた IsQuitting は <see cref="Update"/> で「次のフレームが来たら取り消された」とみなして戻す。
        /// 戻さないと、改名・遅延読み込みの読み直しなど IsQuitting を見る処理が以後すべて止まる
        /// 「はい」で本当に終了する場合は、GameExit() から再び OnApplicationQuit が呼ばれて立て直される。
        /// </summary>
        private void OnApplicationQuit()
        {
            IsQuitting = true;
            _quitRequestedFrame = Time.frameCount;
        }

        /// <summary>終了が取り消されて次のフレームが来たら IsQuitting を戻す。戻した場合 true。</summary>
        private bool ResetQuittingIfCancelled()
        {
            if (!IsQuitting || Time.frameCount <= _quitRequestedFrame) return false;

            IsQuitting = false;
            Logger.LogInfo($"ゲームの終了が取り消されたため（終了確認で「いいえ」を選んだ場合など）、終了処理中の状態を解除しました（frame={_quitRequestedFrame} -> {Time.frameCount}）。");
            _quitRequestedFrame = -1;
            return true;
        }

        private bool IsKPlugLoaded()
        {
            // kPlugVR も対象に含める。VRモード実行時は kPlugVR.dll 独自の Dealer クラス
            // (kPlugVR.CmpBase.Dealer) が使われ、非VR版 kPlug.dll の Dealer とは別の型のため、
            // どちらか一方でも見つかれば有効とする。
            return AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.FullName.Contains("kPlug"));
        }

        private Harmony _harmony;
        private int _nextGuardAttemptFrame;

        /// <summary>ガード適用の再試行間隔。型が見つかるまで数フレームおきに試す。</summary>
        private const int GuardAttemptIntervalFrames = 15;

        private Harmony GetHarmony()
        {
            return _harmony ?? (_harmony = new Harmony(GUID));
        }

        private static bool AnyNullGuardEnabled()
        {
            return EnableVRRootMenuVisibilityGuard.Value || EnableVRCtrlLateUpdateGuard.Value
                || EnableHinputParserChainGuard.Value || EnableAIPublicKbGuard.Value
                || EnableAIMainFaceColorGuard.Value || EnableAISecondFaceColorGuard.Value
                || EnableGuestFaceColorGuard.Value || EnableHCtrlKissGuard.Value
                || EnableCopyLiquidMapGuard.Value;
        }

        private void FixedUpdate()
        {
            // 物理演算の回数を数える（処理落ちすると1フレームに複数回呼ばれる）。KPlugPerfDiagnostics の Q3
            if (EnableKPlugPerfDiagnostics != null && EnableKPlugPerfDiagnostics.Value) KPlugPerfDiagnostics.OnFixedUpdate();
        }

        private void Update()
        {
            ResetQuittingIfCancelled();
            if (IsQuitting || _kPlugMissing) return;

            KPlugMemoryDiagnostics.Tick();
            KPlugPerfDiagnostics.Tick();

            // kPlugVR 向けの null ガードだけは、下の 300 フレーム待ちを使わずに、型が見つかり次第適用する。
            // 【G】【H】の対象は「タイトル画面で作られた VRCtrl が自分を破棄するフレーム」で、
            // 300 フレーム待ちが明ける前に過ぎてしまい、ガードが間に合わなかった実例がある
            // 二重適用は KPlugVRNullGuards 側の適用済みキーで防いでいるため、繰り返し呼んでよい。
            if (!_patched && Time.frameCount >= _nextGuardAttemptFrame
                && (AnyNullGuardEnabled() || EnableLazyInviteCardList.Value || EnableKPlugMemoryDiagnostics.Value
                    || EnableMultiGuestKiss.Value || EnableStuckGuestKissFix.Value))
            {
                _nextGuardAttemptFrame = Time.frameCount + GuardAttemptIntervalFrames;
                if (AnyNullGuardEnabled()) KPlugVRNullGuards.TryApply(GetHarmony());

                // 招待キャラ複数との同時キス（kPlugVR の HCtrl.ParseTouchables）。VR の型が見つかり次第適用する。
                MultiGuestKiss.TryApply(GetHarmony());

                // 招待メニュー（InviteUI）は Hシーン入場時に作られ、その Start() で全カードを読み込む。
                // 300 フレーム待ちより前に Hシーンへ入っても間に合うよう、ガード類と同じくここで適用する。
                if (EnableLazyInviteCardList.Value) LazyInviteCardList.TryApply(GetHarmony());

                // kPlug の招待一覧以外のメモリ消費の診断も、InviteUI.Start に間に合うようここで適用する
                if (EnableKPlugMemoryDiagnostics.Value) KPlugMemoryDiagnostics.TryApply(GetHarmony());

                // 在席判定は招待メニューの組み立て時（InviteUI.Start の直後）に呼ばれるため、ここで適用する
                if (ShowSameNameCardsInInviteList.Value) InviteListSameNameUnblock.TryApply(GetHarmony());
            }

            if (_patched) return;
            if (Time.frameCount < 300) return;

            // 非VR版 kPlug.dll の Dealer と、VR版 kPlugVR.dll の Dealer は完全に別クラスであり、
            // どちらか一方にしかパッチしないと、そちらのモードでしか本プラグインが機能しなくなる。
            // 見つかった全ての ".Dealer" 型 (重複除去) にパッチを適用する。
            var dealerTypes = new List<Type>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.FullName.Contains("kPlug")) continue;

                try
                {
                    var types = assembly.GetExportedTypes();
                    var dealerType = types.FirstOrDefault(t => t.FullName.EndsWith(".Dealer"));
                    if (dealerType != null && !dealerTypes.Contains(dealerType))
                    {
                        dealerTypes.Add(dealerType);
                    }
                }
                catch (ReflectionTypeLoadException) { continue; }
            }

            if (dealerTypes.Count == 0)
            {
                if (Time.realtimeSinceStartup > 60f)
                {
                    Logger.LogError($"{PluginName}: 起動から60秒経過しましたが、Dealer 型が見つかりません。kPlugのバージョンが異なる可能性があります。");
                    _kPlugMissing = true;
                }
                return;
            }

            Logger.LogInfo($"{PluginName}: ターゲット型を {dealerTypes.Count} 件発見しました。パッチ適用を開始します。");

            var harmony = GetHarmony();
            var patchInviteCard = typeof(InviteCardPatch).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static);

            // Dealer.InviteCard(ChaFileControl guest) : カード招待の入口。
            // ここで「複製 + 改名」を行い、以降の kPlug 側処理すべてに改名後の名前を通す。
            // kPlug 3.599 以降の VR版では InviteCard が廃止され、
            // InviteInVR(ChaFileControl, int, bool) に置き換わった。
            // バージョン判定はせず、「どちらのメソッドが在るか」で対象を決める
            var patchInviteInVR = typeof(InviteCardPatch).GetMethod("PrefixInviteInVR", BindingFlags.Public | BindingFlags.Static);

            foreach (var dealerType in dealerTypes)
            {
                // 改名処理は Dealer の型ごとに Core / kFileParser を解決する必要がある
                // （VR版と非VR版で在席リストの持ち方が違うため）。パッチ適用前に登録しておく。
                InviteCardPatch.RegisterDealerType(dealerType);

                var targetInviteCard = AccessTools.Method(dealerType, "InviteCard", new[] { typeof(ChaFileControl) });
                var targetInviteInVR = AccessTools.Method(dealerType, "InviteInVR",
                    new[] { typeof(ChaFileControl), typeof(int), typeof(bool) });

                if (targetInviteCard != null && patchInviteCard != null)
                {
                    harmony.Patch(targetInviteCard, new HarmonyMethod(patchInviteCard));
                    Logger.LogInfo($"{PluginName}: InviteCard パッチ適用成功 ({dealerType.FullName}.InviteCard)");
                    LazyInviteCardList.NotifyInviteCardHooked();
                    InviteCardDiagnostics.OnInviteCardPatched(harmony, dealerType, targetInviteCard);
                }
                else if (targetInviteInVR != null && patchInviteInVR != null)
                {
                    harmony.Patch(targetInviteInVR, new HarmonyMethod(patchInviteInVR));
                    Logger.LogInfo($"{PluginName}: InviteCard パッチ適用成功 ({dealerType.FullName}.InviteInVR / kPlug 3.599 以降の新API)");
                }
                else
                {
                    Logger.LogError($"{PluginName}: {dealerType.FullName} に InviteCard(ChaFileControl) も InviteInVR(ChaFileControl,int,bool) も見つかりませんでした。このモードでは同名キャラの複数招待が無効です。");
                }

                // 男性一覧を軽量版にする場合、バディ招待の瞬間に完全なカードを読み直す
                if (EnableLazyInviteCardList.Value && LazyInviteIncludeMaleCards.Value)
                {
                    LazyInviteCardList.TryPatchInviteBuddy(harmony, dealerType);
                }
            }

            // 新VRの招待UIは「招待済みのカードを一覧から外す」方式のため、改名だけでは複数招待できない。
            if (EnableVRInviteMenuUnblock.Value)
            {
                VRInviteMenuUnblock.TryApply(harmony);
            }

            // 招待1人ごとのコストと複数人招待時のフレームレート低下の診断（メソッドの計測は Tick で少しずつ当てる）
            KPlugPerfDiagnostics.TryApply(harmony);

            // 複数人招待時の揺れもの（DynamicBone）の負荷を下げる
            DynamicBoneThrottle.TryApply(harmony);

            // HVoiceCaching が交代のたびに HVoiceCtrl.Init の中で例外を投げ、交代処理の後半が飛ぶ問題の回避策。
            // HVoiceCaching の読み込みが終わっている必要があるため（Chainloader.PluginInfos で有無を見る）、ここで適用する。
            if (EnableHVoiceCachingShortBreathFix.Value)
            {
                HVoiceCachingShortBreathFix.TryApply(harmony);
            }

            // HVoiceCaching を入れていると 3P の Hシーンのロードが止まる問題の回避策。
            if (EnableHVoiceCachingVoiceListFix.Value)
            {
                HVoiceCachingVoiceListFix.TryApply(harmony);
            }

            // kPlug のキャラ交代で表情リストが最初のメインの性格のまま残る問題の対策。
            if (EnableFaceListReloadOnSwap.Value)
            {
                FaceListReloadOnSwap.TryApply(harmony);
            }

            // kPlug の FaceCtrl.IE_SetFace の NRE の原因を調べる診断（読み取り専用）。
            if (EnableSetFaceNreDiagnostics.Value)
            {
                FaceCtrlSetFaceDiagnostics.TryApply(harmony);
            }

            // 招待が無反応になる不具合（SpotManager.AdaptCaliber の NRE によるロック）の回避策。
            // Dealer と同じく VR版/非VR版の両方の SpotManager 型が対象になる。
            SpotManagerBonesFix.TryApply(harmony);

            // Inviteアイコンが反応しない問題の調査用診断（読み取り専用）。
            if (EnableInviteUIDiagnostics.Value)
            {
                InviteUIDiagnostics.TryApply(harmony);
            }

            // Core.hStartCtx がKK環境で異常な値に固定される問題の調査用診断（読み取り専用）。
            if (EnableHStartCtxDiagnostics.Value)
            {
                HStartCtxDiagnostics.TryApply(harmony);
            }

            // ゲーム本体(H.HSceneProc)側の体位カテゴリ選択ロジックを追う診断（読み取り専用）。
            if (EnableHSceneCategoryDiagnostics.Value)
            {
                HSceneCategoryDiagnostics.TryApply(harmony);
            }

            // SpotCtrl.AdaptColSclToPose の NRE の原因を特定するための診断（読み取り専用・例外は抑止しない）。
            if (EnableSpotCtrlAdaptColDiagnostics.Value)
            {
                SpotCtrlAdaptColDiagnostics.TryApply(harmony);
            }

            // 衣装変更後にkPlugが破棄済み参照を掴み続ける問題の調査用診断（読み取り専用）。
            // 回避策より先に登録し、診断側が「修復前の壊れた状態」を観測できるようにする
            // （回避策の Prefix は Priority.Last で後から走る）。
            if (EnableCoordinateChangeStaleRefDiagnostics.Value)
            {
                CoordinateChangeStaleRefDiagnostics.TryApply(harmony);
            }

            // 同問題の回避策（破棄済み要素の除去 / FindBones() による復旧）。
            if (EnableCoordinateChangeStaleRefFix.Value)
            {
                CoordinateChangeStaleRefFix.TryApply(harmony);
            }

            // 絶頂ループの回避策。同じ「破棄済み参照」が原因だが、こちらはゲーム本体側で起きている。
            if (EnableParentObjectCtrlStaleRefFix.Value)
            {
                ParentObjectCtrlStaleRefFix.TryApply(harmony);
            }

            // IK の目標が null になって起きる絶頂ループの回避策（kPlug 側と、ゲーム本体側の安全網）
            if (EnableExtraAnimIkTargetGuard.Value)
            {
                MotionIKNullTargetGuard.TryApplyKPlug(harmony);
            }
            if (EnableMotionIKNullTargetGuard.Value)
            {
                MotionIKNullTargetGuard.TryApplyGame(harmony);
            }

            // kPlug / kPlugVR 本体の null チェック漏れに対する個別ガード
            // （VRCtrl.SetMenuVisibility / VRCtrl.LateUpdate / HinputParser.Chain / AI_Public.LateUpdate / AI_Main.Ahegao /
            //   HCtrl.ParseTouchables）。
            if (AnyNullGuardEnabled())
            {
                KPlugVRNullGuards.TryApply(harmony);
            }

            // 招待キャラ複数との同時キス（上の早期適用で済んでいれば何もしない）
            MultiGuestKiss.TryApply(harmony);

            _patched = true;
        }
    }
}
