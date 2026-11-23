using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    internal static class PoseUnlock
    {
        internal static ConfigEntry<bool> UnlockMenuPoses;
        private static bool _done, _failed;
        private static PoseMenuAccess _access;
        private static readonly PoseMenuCache Cache = new PoseMenuCache();
        internal static bool Pending { get { return !_done; } }

        internal static void BindConfig(ConfigFile config)
        {
            UnlockMenuPoses = config.Bind("PoseUnlock", "UnlockMenuPoses", true,
                "KK/KKPのkPlug体位メニューとAIでHポイントの種類による絞り込みを外します。VRメニューは3.599以降の現行方式のみ、VRのAIにも対応。追加候補の表示名に * を付け、AIは選択先カテゴリの全候補から一様に選びます。ゲーム中切替可。VRのメニューは選択・AI判断・ロード等の終了後に更新します。家具のない場所では浮く・埋まる場合があります。3P・レズ・暗闇・専用候補の特殊H・覗き・結果画面は対象外。次の体位キーと自動連鎖は変更しません。");
            UnlockMenuPoses.SettingChanged += (sender, args) => { Clear(); VRPoseUnlock.Invalidate(); };
        }

        internal static void TryApply(Harmony harmony)
        {
            if (_done) return;
            if (Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase))
            { VRPoseUnlock.TryApply(harmony); _done = !VRPoseUnlock.Pending; return; }
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "kPlug");
                if (assembly == null) return;
                _done = true;
                var access = new PoseMenuAccess(assembly);
                Type selector = assembly.GetType("kPlug.CmpH.AnimSelector", true);
                Type tool = assembly.GetType("kPlug.Tools.ToolAnimSelector", true);
                Type srv = selector.GetNestedType("SrvTabs", BindingFlags.Public | BindingFlags.NonPublic);
                if (srv == null || !srv.IsEnum) throw new MissingMemberException("AnimSelector.SrvTabs");
                var methods = new[] { PoseMenuAccess.Method(tool, "GetAvailablePiston"),
                    PoseMenuAccess.Method(tool, "GetAvailableService", srv), PoseMenuAccess.Method(tool, "GetAvailableTease"),
                    PoseMenuAccess.Method(selector, "GetAvailableTabs"), PoseMenuAccess.Method(selector, "GetAvailableSrvTabs") };
                foreach (var method in methods)
                    if (!method.ReturnType.IsGenericType || method.ReturnType.GetGenericTypeDefinition() != typeof(List<>))
                        throw new MissingMemberException(method.Name + " List<T>");
                var draw = AccessTools.Method(selector, "DrawButtons", new[] { typeof(List<string>) });
                var names = AccessTools.Field(selector, "buttonNames");
                var destroy = AccessTools.Method(selector, "OnDestroy", Type.EmptyTypes);
                if (draw == null || draw.IsStatic || names == null || names.IsStatic || names.FieldType != typeof(Dictionary<string, string>))
                    throw new MissingMemberException("AnimSelector.DrawButtons/buttonNames");
                if (destroy == null || destroy.IsStatic || destroy.ReturnType != typeof(void)) throw new MissingMethodException("AnimSelector.OnDestroy");
                // Validate every contract before installing any hook.
                _access = access;
                harmony.Patch(access.Stand, prefix: Generic(typeof(PoseMenuScope), "StandPrefix", access.Stand.ReturnType));
                harmony.Patch(access.Sit, prefix: Generic(typeof(PoseMenuScope), "SitPrefix", access.Sit.ReturnType));
                foreach (var method in methods)
                    harmony.Patch(method, postfix: Generic(typeof(PoseUnlock), nameof(ListPostfix), method.ReturnType.GetGenericArguments()[0]));
                harmony.Patch(draw, prefix: new HarmonyMethod(typeof(PoseUnlock), nameof(DrawPrefix)),
                    finalizer: new HarmonyMethod(typeof(PoseUnlock), nameof(DrawFinalizer)));
                harmony.Patch(destroy, finalizer: new HarmonyMethod(typeof(PoseUnlock), nameof(Clear)));
                PatcherPlugin.Logger.LogInfo("[PoseUnlock] kPlug体位メニューの候補拡張を用意しました（通常モードのみ、追加候補は *）。");
                PoseAI.TryApply(harmony, assembly, access, Cache, methods[0], methods[1], srv);
            }
            catch (Exception ex) { Stop(ex); }
        }

        private static HarmonyMethod Generic(Type type, string name, Type argument)
        { return new HarmonyMethod(AccessTools.Method(type, name).MakeGenericMethod(argument)); }

        internal static bool Enabled
        { get { return !PatcherPlugin.IsQuitting && !_failed && UnlockMenuPoses != null && UnlockMenuPoses.Value; } }

        internal static bool PrepareAI() { return Enabled && _access != null && _access.Refresh(Cache); }

        private static void ListPostfix<T>(MethodBase __originalMethod, object[] __args, ref List<T> __result)
        {
            if (PatcherPlugin.IsQuitting || _failed || PoseMenuScope.Active || !UnlockMenuPoses.Value || __result == null) return;
            try
            {
                if (!_access.Refresh(Cache)) return;
                __result = (List<T>)Cache.Expand((MethodInfo)__originalMethod, __args, __result, _access.Stands, _access.Sits,
                    name => _access.Resolve.Invoke(null, new object[] { name }) != null);
            }
            catch (Exception ex) { Stop(ex); }
        }

        private static void DrawPrefix(List<string> __0, ref Dictionary<string, string> ___buttonNames, out Dictionary<string, string> __state)
        {
            __state = null;
            if (PatcherPlugin.IsQuitting || _failed || !UnlockMenuPoses.Value) return;
            try
            {
                var labels = Cache.Labels(__0, ___buttonNames);
                if (ReferenceEquals(labels, ___buttonNames)) return;
                __state = ___buttonNames;
                ___buttonNames = labels;
            }
            catch (Exception ex) { Stop(ex); }
        }

        private static void DrawFinalizer(ref Dictionary<string, string> ___buttonNames, Dictionary<string, string> __state)
        {
            // Restore even when IMGUI exits by throwing; preserve the original exception.
            if (__state != null) ___buttonNames = __state;
        }

        // Release catalog and scene references when the menu component leaves the H scene.
        private static void Clear()
        {
            if (_access != null) _access.Invalidate(Cache);
            else Cache.Clear();
        }

        private static void Stop(Exception ex)
        {
            if (_failed) return;
            _failed = true; _done = true; Clear();
            string saveError = "";
            try { if (UnlockMenuPoses != null) UnlockMenuPoses.Value = false; }
            catch (Exception save) { saveError = " (設定保存: " + save.Message + ")"; }
            PatcherPlugin.Logger.LogWarning("[PoseUnlock] 失敗したのでメニュー拡張を止めます（元の候補は維持）: " + ex.Message + saveError);
        }
    }
}
