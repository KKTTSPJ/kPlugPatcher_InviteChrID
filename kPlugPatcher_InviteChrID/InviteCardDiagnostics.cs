using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// 「Dealer.InviteCard の Prefix（<see cref="InviteCardPatch.Prefix"/>）が呼ばれないまま招待が実行される」
    /// 現象を調べるための診断（読み取り専用。招待の挙動は変えない）。
    ///   v1.5.0 の検証で、招待したキャラが軽量版のまま生成され白くなった。
    ///   読み直しログも改名ログも一切出ておらず、Prefix 自体が動いていないと判断したが、
    ///   パッチ適用ログは成功していて、原因をログから特定できなかった。
    ///
    /// 記録する内容:
    ///   1. Prefix に入るたびに、受け取ったカードが軽量版かどうか
    ///   2. InviteCard に付いているパッチの一覧（適用直後と、Hシーンごとの InviteUI.Start 後）
    ///   3. kPlug 内部の Dealer.IE_Invite(asCard=true) が呼ばれた時点で、Prefix を通ったかどうかと呼び出し元のスタック
    ///      （InviteCard からしか呼ばれないため、Prefix の回数が増えていなければ Prefix を通らずに招待されたことになる）
    /// </summary>
    internal static class InviteCardDiagnostics
    {
        private const string Tag = "[InviteCardDiag]";
        private const int MaxInviteLogs = 30;

        /// <summary>Prefix を当てた Dealer.InviteCard(ChaFileControl)。旧API のみ。</summary>
        private static MethodBase _inviteCardTarget;

        private static int _prefixCalls;
        private static int _prefixCallsAtLastInvite;
        private static int _inviteLogs;

        internal static bool Enabled
        {
            get { return PatcherPlugin.EnableInviteCardDiagnostics != null && PatcherPlugin.EnableInviteCardDiagnostics.Value; }
        }

        // ===================== 適用 =====================

        /// <summary>InviteCard に Prefix を当てた直後に呼ぶ。</summary>
        internal static void OnInviteCardPatched(Harmony harmony, Type dealerType, MethodBase inviteCard)
        {
            if (!Enabled) return;
            _inviteCardTarget = inviteCard;

            try
            {
                var body = inviteCard.GetMethodBody();
                int ilSize = body != null ? body.GetILAsByteArray().Length : -1;
                PatcherPlugin.Logger.LogInfo($"{Tag} {dealerType.FullName}.InviteCard の IL サイズ: {ilSize} バイト");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} InviteCard の IL サイズを取得できませんでした: {ex.Message}");
            }

            LogPatchState("適用直後");

            try
            {
                var ieInvite = AccessTools.Method(dealerType, "IE_Invite", new[] { typeof(float), typeof(bool), typeof(bool) });
                if (ieInvite == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {dealerType.FullName}.IE_Invite(float, bool, bool) が見つかりません。招待時の確認は行いません。");
                    return;
                }
                harmony.Patch(ieInvite, prefix: new HarmonyMethod(typeof(InviteCardDiagnostics).GetMethod(
                    nameof(IEInvitePrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                PatcherPlugin.Logger.LogInfo($"{Tag} {dealerType.FullName}.IE_Invite() に診断パッチを適用しました（カード招待のたびにログ出力）。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} IE_Invite への適用に失敗: {ex.Message}");
            }
        }

        // ===================== 記録 =====================

        /// <summary><see cref="InviteCardPatch.Prefix"/> の先頭から呼ぶ。</summary>
        internal static void OnPrefixEntered(ChaFileControl guest)
        {
            _prefixCalls++;
            if (!Enabled) return;

            try
            {
                string name = guest != null && guest.parameter != null
                    ? guest.parameter.lastname + guest.parameter.firstname : "(null)";
                string file = guest != null ? guest.charaFileName : "(null)";
                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} InviteCard の Prefix に入りました #{_prefixCalls}: {name} ({file}) " +
                    $"軽量版={(LazyInviteCardList.IsLight(guest) ? "はい" : "いいえ")} / 軽量版の対応表 {LazyInviteCardList.LightCount} 件");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} Prefix 入口の記録に失敗: {ex.Message}");
            }
        }

        /// <summary>InviteCard に付いているパッチを一覧する。</summary>
        internal static void LogPatchState(string when)
        {
            if (!Enabled || _inviteCardTarget == null) return;

            try
            {
                var info = Harmony.GetPatchInfo(_inviteCardTarget);
                if (info == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} [{when}] InviteCard にパッチが1つも付いていません（外された可能性があります）。");
                    return;
                }

                var sb = new StringBuilder();
                sb.Append($"{Tag} [{when}] InviteCard のパッチ: prefix {info.Prefixes.Count} / postfix {info.Postfixes.Count}"
                          + $" / transpiler {info.Transpilers.Count} / finalizer {info.Finalizers.Count}");
                foreach (var p in info.Prefixes)
                {
                    sb.Append($" | prefix: {p.owner} {Describe(p.PatchMethod)} (priority={p.priority})");
                }
                foreach (var p in info.Postfixes.Concat(info.Transpilers).Concat(info.Finalizers))
                {
                    sb.Append($" | other: {p.owner} {Describe(p.PatchMethod)}");
                }
                PatcherPlugin.Logger.LogInfo(sb.ToString());
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} [{when}] パッチ一覧の取得に失敗: {ex.Message}");
            }
        }

        /// <summary>Dealer.IE_Invite(float, bool, bool) の Prefix。コルーチンを作る時点（InviteCard の中）で呼ばれる。</summary>
        private static void IEInvitePrefix(bool asCard)
        {
            if (!asCard || !Enabled) return;

            try
            {
                bool viaPrefix = _prefixCalls > _prefixCallsAtLastInvite;
                _prefixCallsAtLastInvite = _prefixCalls;

                if (_inviteLogs >= MaxInviteLogs) return;
                _inviteLogs++;

                string stack = DescribeStack();
                if (viaPrefix)
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} カード招待を開始（Prefix を通過済み）。呼び出し元: {stack}");
                }
                else
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} カード招待が Prefix を通らずに開始されました（Prefix 呼び出し累計 {_prefixCalls} 回）。呼び出し元: {stack}");
                    LogPatchState("Prefix を通らなかった招待の時点");
                }
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 招待時の記録に失敗: {ex.Message}");
            }
        }

        // ===================== 補助 =====================

        private static string DescribeStack()
        {
            var frames = new StackTrace(1, false).GetFrames();
            if (frames == null) return "(取得不可)";

            var sb = new StringBuilder();
            int n = 0;
            foreach (var f in frames)
            {
                if (n >= 8) break;
                var m = f.GetMethod();
                if (n > 0) sb.Append(" <- ");
                sb.Append(m == null ? "(不明/動的メソッド)" : Describe(m));
                n++;
            }
            return sb.ToString();
        }

        private static string Describe(MethodBase m)
        {
            if (m == null) return "(null)";
            string type = m.DeclaringType != null ? m.DeclaringType.FullName : "(型なし/動的)";
            return type + "." + m.Name;
        }
    }
}
