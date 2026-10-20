using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// 「Invite アイコンは表示されるが押しても反応しない」問題の原因調査用の診断パッチ。
    ///
    /// InviteUI.Update() の Postfix として動作し、クリック可否を決めている
    /// isValidCtx / isOpenable / showMenu と、その内訳（Core.hStartCtx など）を
    /// 状態が変化した瞬間だけログに出力する。
    ///
    /// 【なぜ「変化した瞬間だけ」か】
    /// InviteUI.Update() は毎フレーム呼ばれるため、そのまま出力すると大量のログになる。
    /// アイコンをクリックすれば showMenu が変化するため、実質的に「クリックに反応して
    /// ログが出る」のと同じ効果が得られる。加えて isValidCtx/isOpenable が変化した瞬間も
    /// 捕捉できるため、クリックが effectively 無視された（showMenu が変化しなかった）
    /// ケースでも、直前の isValidCtx/isOpenable の値から原因を追える。
    ///
    /// 【設計上の注意】
    /// - リフレクション情報は必ず型ごとの辞書で持つ（同名クラスの情報を上書きして壊した過去の不具合の教訓）。
    /// - 本パッチは読み取り専用の診断であり、値を書き換えない。
    /// - 例外を外へ出さない。
    /// </summary>
    public static class InviteUIDiagnostics
    {
        private sealed class InviteUiTypeInfo
        {
            public FieldInfo IsValidCtx;
            public FieldInfo IsOpenable;
            public FieldInfo ShowMenu;
            public FieldInfo IsInDarkness;
            public FieldInfo EnableBoyCardTab;

            // Core側の静的メンバ
            public FieldInfo CoreHStartCtx;
            public FieldInfo CoreInHresult;
            public FieldInfo CoreInOverlap;
            public FieldInfo CoreConfig;
            public FieldInfo CoreHCtrl;
            public FieldInfo CoreJukeboxCtrl;
            public FieldInfo CoreDiary;
            public PropertyInfo QueryInConfigOrHelp;

            // Core.config / Core.jukeboxCtrl / Core.diary の showMenu（MenuCtrl.showMenu を継承）
            public FieldInfo MenuCtrlShowMenu;

            // Core.hCtrl.MapSelect.showMenu
            public PropertyInfo HCtrlMapSelect;
        }

        private static readonly Dictionary<Type, InviteUiTypeInfo> _infoByType = new Dictionary<Type, InviteUiTypeInfo>();

        // 直近ログ出力したスナップショット文字列（型ごと）。変化が無ければ再出力しない。
        private static readonly Dictionary<Type, string> _lastSnapshotByType = new Dictionary<Type, string>();

        internal static void TryApply(Harmony harmony)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.FullName.Contains("kPlug")) continue;

                Type[] types;
                try { types = assembly.GetExportedTypes(); }
                catch (ReflectionTypeLoadException) { continue; }

                var inviteUiType = types.FirstOrDefault(t => t.FullName.EndsWith(".InviteUI"));
                if (inviteUiType == null) continue;

                try
                {
                    var info = BuildTypeInfo(inviteUiType);
                    if (info == null)
                    {
                        PatcherPlugin.Logger.LogWarning($"[InviteUIDiagnostics] {inviteUiType.FullName}: 必要なフィールドを解決できませんでした。診断を無効化します。");
                        continue;
                    }
                    _infoByType[inviteUiType] = info;

                    var updateMethod = AccessTools.Method(inviteUiType, "Update", Type.EmptyTypes);
                    if (updateMethod == null)
                    {
                        PatcherPlugin.Logger.LogWarning($"[InviteUIDiagnostics] {inviteUiType.FullName}.Update() が見つかりませんでした。");
                        continue;
                    }

                    var postfix = typeof(InviteUIDiagnostics).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static);
                    harmony.Patch(updateMethod, postfix: new HarmonyMethod(postfix));
                    PatcherPlugin.Logger.LogInfo($"[InviteUIDiagnostics] {inviteUiType.FullName}.Update() に診断パッチを適用しました（状態が変化した時のみログ出力）。");
                }
                catch (Exception ex)
                {
                    PatcherPlugin.Logger.LogError($"[InviteUIDiagnostics] {inviteUiType.FullName} の解析に失敗: {ex.Message}");
                }
            }
        }

        private static InviteUiTypeInfo BuildTypeInfo(Type inviteUiType)
        {
            var menuCtrlType = inviteUiType.BaseType; // MenuCtrl
            var info = new InviteUiTypeInfo
            {
                IsValidCtx = AccessTools.Field(inviteUiType, "isValidCtx"),
                IsOpenable = AccessTools.Field(inviteUiType, "isOpenable"),
                ShowMenu = menuCtrlType != null ? AccessTools.Field(menuCtrlType, "showMenu") : AccessTools.Field(inviteUiType, "showMenu"),
                IsInDarkness = AccessTools.Field(inviteUiType, "isInDarkness"),
                EnableBoyCardTab = AccessTools.Field(inviteUiType, "enableBoyCardTab"),
            };

            if (info.IsValidCtx == null || info.IsOpenable == null || info.ShowMenu == null)
            {
                return null;
            }

            Type[] types;
            try { types = inviteUiType.Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

            var coreType = types.FirstOrDefault(t => t.Name == "Core");
            if (coreType != null)
            {
                info.CoreHStartCtx = AccessTools.Field(coreType, "hStartCtx");
                info.CoreInHresult = AccessTools.Field(coreType, "inHresult");
                info.CoreInOverlap = AccessTools.Field(coreType, "inOverlap");
                info.CoreConfig = AccessTools.Field(coreType, "config");
                info.CoreHCtrl = AccessTools.Field(coreType, "hCtrl");
                info.CoreJukeboxCtrl = AccessTools.Field(coreType, "jukeboxCtrl");
                info.CoreDiary = AccessTools.Field(coreType, "diary");
            }

            var queryType = types.FirstOrDefault(t => t.Name == "Query");
            if (queryType != null)
            {
                info.QueryInConfigOrHelp = AccessTools.Property(queryType, "InConfigOrHelp");
            }

            // Core.config / Core.jukeboxCtrl / Core.diary はいずれも MenuCtrl 派生で showMenu を持つ。
            // 型は違ってもフィールド名は共通なので、後で対象インスタンスの実型から都度取得する。
            info.MenuCtrlShowMenu = null; // インスタンス取得後に動的解決する（下記 GetShowMenuOf 参照）

            var hCtrlType = types.FirstOrDefault(t => t.Name == "HCtrl");
            if (hCtrlType != null)
            {
                info.HCtrlMapSelect = AccessTools.Property(hCtrlType, "MapSelect");
            }

            return info;
        }

        private static void Postfix(object __instance)
        {
            try
            {
                InviteUiTypeInfo info;
                var type = __instance.GetType();
                if (!_infoByType.TryGetValue(type, out info) || info == null) return;

                bool isValidCtx = (bool)info.IsValidCtx.GetValue(__instance);
                bool isOpenable = (bool)info.IsOpenable.GetValue(__instance);
                bool showMenu = (bool)info.ShowMenu.GetValue(__instance);
                bool isInDarkness = info.IsInDarkness != null && (bool)info.IsInDarkness.GetValue(__instance);
                bool enableBoyCardTab = info.EnableBoyCardTab != null && (bool)info.EnableBoyCardTab.GetValue(__instance);

                string hStartCtx = info.CoreHStartCtx != null ? info.CoreHStartCtx.GetValue(null)?.ToString() : "?";
                bool inHresult = info.CoreInHresult != null && (bool)info.CoreInHresult.GetValue(null);
                bool inOverlap = info.CoreInOverlap != null && (bool)info.CoreInOverlap.GetValue(null);
                bool inConfigOrHelp = info.QueryInConfigOrHelp != null && (bool)(info.QueryInConfigOrHelp.GetValue(null, null) ?? false);

                bool configShowMenu = GetShowMenuOf(info.CoreConfig);
                bool jukeboxShowMenu = GetShowMenuOf(info.CoreJukeboxCtrl);
                bool diaryShowMenu = GetShowMenuOf(info.CoreDiary);
                bool mapSelectShowMenu = GetMapSelectShowMenu(info);

                string snapshot = string.Join("|", new[]
                {
                    "isValidCtx=" + isValidCtx,
                    "isOpenable=" + isOpenable,
                    "showMenu=" + showMenu,
                    "hStartCtx=" + hStartCtx,
                    "isInDarkness=" + isInDarkness,
                    "enableBoyCardTab=" + enableBoyCardTab,
                    "inHresult=" + inHresult,
                    "inOverlap=" + inOverlap,
                    "inConfigOrHelp=" + inConfigOrHelp,
                    "config.showMenu=" + configShowMenu,
                    "jukebox.showMenu=" + jukeboxShowMenu,
                    "diary.showMenu=" + diaryShowMenu,
                    "mapSelect.showMenu=" + mapSelectShowMenu,
                });

                string last;
                _lastSnapshotByType.TryGetValue(type, out last);
                if (snapshot == last) return;
                _lastSnapshotByType[type] = snapshot;

                PatcherPlugin.Logger.LogInfo($"[InviteUIDiagnostics] {type.FullName} 状態変化: {snapshot}");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"[InviteUIDiagnostics] Postfix Error: {ex.Message}");
            }
        }

        private static bool GetShowMenuOf(FieldInfo staticFieldHoldingMenuCtrlInstance)
        {
            if (staticFieldHoldingMenuCtrlInstance == null) return false;
            object instance = staticFieldHoldingMenuCtrlInstance.GetValue(null);
            if (instance == null) return false;
            var showMenuField = AccessTools.Field(instance.GetType(), "showMenu");
            if (showMenuField == null) return false;
            return (bool)(showMenuField.GetValue(instance) ?? false);
        }

        private static bool GetMapSelectShowMenu(InviteUiTypeInfo info)
        {
            if (info.CoreHCtrl == null || info.HCtrlMapSelect == null) return false;
            object hCtrl = info.CoreHCtrl.GetValue(null);
            if (hCtrl == null) return false;
            object mapSelect = info.HCtrlMapSelect.GetValue(hCtrl, null);
            if (mapSelect == null) return false;
            var showMenuField = AccessTools.Field(mapSelect.GetType(), "showMenu");
            if (showMenuField == null) return false;
            return (bool)(showMenuField.GetValue(mapSelect) ?? false);
        }
    }
}
