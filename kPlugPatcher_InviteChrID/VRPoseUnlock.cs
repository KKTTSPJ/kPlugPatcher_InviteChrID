using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    internal static class VRPoseUnlock
    {
        internal static readonly PoseMenuCache Cache = new PoseMenuCache();
        internal static VRPoseContext Context;
        private static bool _done, _failed;
        private static int _nextPoll;
        internal static bool Pending { get { return !_done; } }
        internal static bool Enabled { get { return !_failed && PoseUnlock.Enabled; } }
        internal static bool PrepareAI() { return Enabled && Context != null && Context.Refresh(); }

        internal static void TryApply(Harmony harmony)
        {
            if (_done) return;
            if (!Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase)) { _done = true; return; }
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "kPlugVR");
                if (assembly == null) return;
                _done = true;
                Context = new VRPoseContext(assembly, Cache);
                harmony.Patch(Context.Stand, prefix: Generic("StandPrefix", Context.Stand.ReturnType));
                harmony.Patch(Context.Sit, prefix: Generic("SitPrefix", Context.Sit.ReturnType));
                PoseAI.TryApplyVR(harmony, assembly, Context, Cache);
                VRPoseMenu.TryApply(harmony, Context);
                PatcherPlugin.Logger.LogInfo("[PoseUnlock.VR] VRのAI候補拡張を用意しました（設定は通常版と共用）。");
            }
            catch (Exception ex) { Stop(ex); }
        }
        private static HarmonyMethod Generic(string name, Type argument)
        { return new HarmonyMethod(AccessTools.Method(typeof(PoseMenuScope), name).MakeGenericMethod(argument)); }

        internal static void Tick()
        {
            if (_failed || Context == null || PatcherPlugin.IsQuitting || Time.frameCount < _nextPoll) return;
            _nextPoll = Time.frameCount + 15;
            try
            {
                if (!Context.IsLive) { Context.Invalidate(); VRPoseMenu.Forget(); return; }
                Context.Refresh();
                VRPoseMenu.Tick(Context.CanRebuild);
            }
            catch (Exception ex) { Stop(ex); }
        }
        internal static void Invalidate() { if (Context != null) Context.Invalidate(); else Cache.Clear(); }
        private static void Stop(Exception ex)
        {
            if (_failed) return;
            _failed = true; _done = true; Invalidate();
            PatcherPlugin.Logger.LogWarning("[PoseUnlock.VR] この起動中はVR候補拡張を止めます: " + ex.GetBaseException().Message);
        }
    }
}
