using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    internal static class VRPoseMenu
    {
        private static VRPoseMenuAccess _access;
        private static object _menu;
        private static Func<object> _lookup;
        private static int _revision, _built;
        private static bool _building, _failed;

        internal static void TryApply(Harmony harmony, VRPoseContext context)
        {
            if (context.MenuType == null)
            {
                PatcherPlugin.Logger.LogInfo("[PoseUnlock.VRMenu] 旧方式のメニューは追加対象外です（AI候補拡張は別に適用）。");
                return;
            }
            try
            {
                var access = new VRPoseMenuAccess(context.MenuType);
                // The native list builders are shared by UI and AI; the scope guard prevents recursion.
                foreach (var method in new[] { context.Piston, context.Tease, context.Service, access.Tabs, access.ServiceTabs })
                {
                    if (!method.ReturnType.IsGenericType || method.ReturnType.GetGenericTypeDefinition() != typeof(List<>))
                        throw new MissingMethodException(method.Name + " List<T>");
                }
                _access = access;
                _lookup = () => context.MenuInstance;
                VRPoseUnlock.Cache.Invalidated += Request;
                foreach (var method in new[] { context.Piston, context.Tease, context.Service, access.Tabs, access.ServiceTabs })
                    harmony.Patch(method, postfix: new HarmonyMethod(AccessTools.Method(typeof(VRPoseMenu), nameof(ListPostfix))
                        .MakeGenericMethod(method.ReturnType.GetGenericArguments()[0])));
                harmony.Patch(access.Start, postfix: new HarmonyMethod(typeof(VRPoseMenu), nameof(StartPostfix)));
                harmony.Patch(access.Define, postfix: new HarmonyMethod(typeof(VRPoseMenu), nameof(DefinePostfix)));
                harmony.Patch(access.Button, postfix: new HarmonyMethod(typeof(VRPoseMenu), nameof(ButtonPostfix)));
                PatcherPlugin.Logger.LogInfo("[PoseUnlock.VRMenu] 現行方式のVRポーズメニューに適用しました（追加候補は *、設定は通常版と共用）。");
            }
            catch (Exception ex) { Stop(ex); }
        }

        private static void ListPostfix<T>(MethodBase __originalMethod, object[] __args, ref List<T> __result)
        {
            if (_failed || PoseMenuScope.Active || !VRPoseUnlock.Enabled || __result == null) return;
            try
            {
                if (VRPoseUnlock.PrepareAI())
                    __result = (List<T>)VRPoseUnlock.Context.Expand((MethodInfo)__originalMethod, __args, __result);
            }
            catch (Exception ex) { Stop(ex); }
        }

        private static void ButtonPostfix(string __0, ref string __result)
        {
            if (_failed || !VRPoseUnlock.Enabled) return;
            try { if (VRPoseUnlock.Cache.IsOutside(__0)) __result = (string.IsNullOrEmpty(__result) ? __0 : __result) + " *"; }
            catch (Exception ex) { Stop(ex); }
        }

        private static void StartPostfix(object __instance)
        {
            if (PatcherPlugin.IsQuitting) return;
            _menu = __instance;
            if (!_failed) _built = _revision;
        }

        private static void DefinePostfix(object __instance)
        {
            if (PatcherPlugin.IsQuitting || _access == null) return;
            try
            {
                if (_building || (!_failed && VRPoseUnlock.Enabled && VRPoseUnlock.PrepareAI())) _access.Clamp(__instance);
            }
            catch (Exception ex) { Stop(ex); }
        }

        internal static void Tick(bool stable)
        {
            if (_access == null || _building || PatcherPlugin.IsQuitting || !stable) return;
            try
            {
                if (_menu == null && _lookup != null)
                {
                    _menu = _lookup();
                    if (_menu != null) Request();
                }
                if (_menu == null || _revision == _built) return;
                if (!VRPoseMenuAccess.Alive(_menu)) { Forget(); return; }
                if (!_access.Ready(_menu)) return;
                int revision = _revision;
                _building = true;
                _access.Refresh(_menu);
                // Preserve requests raised by a failing postfix during the rebuild itself.
                _built = revision;
                PatcherPlugin.Logger.LogInfo("[PoseUnlock.VRMenu] 候補・タブ・表示名を更新しました（設定=" + VRPoseUnlock.Enabled + "）。");
            }
            catch (Exception ex)
            {
                bool alreadyFailed = _failed;
                Stop(ex);
                // One native rebuild may restore the original UI; do not loop if that also fails.
                if (alreadyFailed) _built = _revision;
            }
            finally { _building = false; }
        }

        private static void Request() { unchecked { _revision++; } }
        internal static void Forget() { _menu = null; _built = _revision; }
        private static void Stop(Exception ex)
        {
            if (_failed) return;
            _failed = true; Request();
            PatcherPlugin.Logger.LogWarning("[PoseUnlock.VRMenu] VRメニュー追加を止めます（AI候補拡張は維持）: " + ex.GetBaseException().Message);
        }
    }
}
