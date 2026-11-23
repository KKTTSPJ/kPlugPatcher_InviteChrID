using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    internal static class PoseAI
    {
        private static PoseAIAccess _access;
        private static bool _failed;
        private static int _empty;
        private static Func<bool> _enabled, _prepare;

        internal static void TryApply(Harmony harmony, Assembly assembly, PoseMenuAccess menu, PoseMenuCache cache,
            MethodInfo piston, MethodInfo service, Type srv)
        {
            try
            {
                Install(harmony, new PoseAIAccess(assembly, menu, cache, piston, service, srv),
                    () => PoseUnlock.Enabled, PoseUnlock.PrepareAI);
            }
            catch (Exception ex) { Stop(ex); }
        }

        internal static void TryApplyVR(Harmony harmony, Assembly assembly, VRPoseContext context, PoseMenuCache cache)
        {
            try { Install(harmony, new PoseAIAccess(assembly, context, cache), () => VRPoseUnlock.Enabled, VRPoseUnlock.PrepareAI); }
            catch (Exception ex) { Stop(ex); }
        }

        private static void Install(Harmony harmony, PoseAIAccess access, Func<bool> enabled, Func<bool> prepare)
        {
            _access = access; _enabled = enabled; _prepare = prepare;
            foreach (var method in _access.Decisions)
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(PoseAI), nameof(DecisionPrefix)));
            harmony.Patch(_access.GetPiston, prefix: new HarmonyMethod(typeof(PoseAI), nameof(PistonListPrefix)));
            harmony.Patch(_access.GoToPiston, postfix: new HarmonyMethod(typeof(PoseAI), nameof(GoToPistonPostfix)));
            PatcherPlugin.Logger.LogInfo("[PoseUnlock.AI] AIの体位候補を拡張しました（UnlockMenuPosesと連動、同じ体位を除き一様抽選）。");
        }

        private static bool Enabled { get { return _enabled != null ? _enabled() : PoseUnlock.Enabled; } }
        private static bool Prepare() { return _prepare != null ? _prepare() : PoseUnlock.PrepareAI(); }

        private static bool DecisionPrefix(MethodBase __originalMethod, ref bool __result)
        {
            if (_failed || !Enabled) return true;
            bool dispatched = false;
            try
            {
                if (!Prepare()) return true;
                object actor = _access.Actor;
                if (actor == null) return true;
                string route = __originalMethod.Name;
                bool service = route != "ChangePiston", previous = _access.Previous(actor, service);
                // Draw only the random values used by the corresponding native decision.
                float first = 0f, second = 0f; int limit = 0;
                if (route == "GoFromPistonToService") limit = RollLimit();
                else
                {
                    first = Roll();
                    if (service) second = Roll();
                }
                __result = false;
                if (!PoseAIChoices.ShouldChange(route, previous, _access.Chain(actor), first, second, limit)) return false;
                var candidates = _access.Candidates(service);
                if (candidates.Count == 0) { Empty(service); return false; }
                var choice = candidates[PickIndex(candidates.Count)];
                Report(route, choice.Key, candidates.Count);
                // Do not retry the native decision if handing off has already started a coroutine.
                dispatched = true;
                _access.Dispatch(actor, service, choice.Value);
                __result = true;
                return false;
            }
            catch (Exception ex) { Stop(ex); return !dispatched; }
        }

        private static bool PistonListPrefix(ref List<string> __result)
        {
            if (_failed || !Enabled) return true;
            try
            {
                if (!Prepare() || _access.Actor == null) return true;
                var candidates = _access.Candidates(false);
                var names = new List<string>();
                foreach (var choice in candidates) names.Add(choice.Key);
                __result = names;
                if (names.Count == 0) Empty(false);
                return false;
            }
            catch (Exception ex) { Stop(ex); return true; }
        }

        private static void GoToPistonPostfix(string __0)
        {
            if (_failed || !Enabled) return;
            try
            {
                if (Prepare()) Report("ServiceToPiston", __0, _access.Candidates(false).Count);
            }
            catch (Exception ex) { Stop(ex); }
        }

        private static void Report(string route, string name, int count)
        {
            PatcherPlugin.Logger.LogInfo("[PoseUnlock.AI] 選択 route=" + route + " hPoint=" + _access.Point
                + " target=" + name + " candidates=" + count + "（変更の完了はゲーム側で処理）。");
        }
        // Keep Unity internal calls outside the OFF/pending paths, including desktop test runners.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float Roll() { return UnityEngine.Random.Range(0f, 100f); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RollLimit() { return UnityEngine.Random.Range(2, 9); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int PickIndex(int count) { return UnityEngine.Random.Range(0, count); }
        private static void Empty(bool service)
        {
            int bit = service ? 1 : 2;
            if ((_empty & bit) != 0) return;
            _empty |= bit;
            PatcherPlugin.Logger.LogWarning("[PoseUnlock.AI] 有効な" + (service ? "奉仕" : "挿入") + "候補がないため、AIの変更を見送ります（各カテゴリ初回のみ記録）。");
        }
        private static void Stop(Exception ex)
        {
            if (_failed) return;
            _failed = true;
            PatcherPlugin.Logger.LogWarning("[PoseUnlock.AI] この起動中はAI候補拡張を止めます（メニュー設定は維持）: " + ex.Message);
        }
    }
}
