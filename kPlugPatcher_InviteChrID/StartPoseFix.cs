using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using BepInEx;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    internal static class StartPoseFix
    {
        private static bool _done, _failed;
        private static FieldInfo _rows, _categories;
        internal static bool Pending { get { return !_done; } }

        internal static void TryApply(Harmony harmony)
        {
            if (_done) return;
            try
            {
                bool vr = Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase);
                Type scene = typeof(HSceneProc);
                if (vr)
                {
                    var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "kPlugVR");
                    if (assembly == null) return;
                    scene = VRPoseContext.Field(assembly.GetType("kPlugVR.Core", true), "hProc", true).FieldType;
                }
                _done = true;
                var method = PrepareTarget(scene);
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(StartPoseFix), nameof(Postfix)));
                PatcherPlugin.Logger.LogInfo("[StartPoseFix] H開始時の体位選択に適用しました（" + (vr ? "VR" : "通常モード") + "）。");
            }
            catch (Exception ex) { Stop(ex); }
        }

        internal static MethodInfo PrepareTarget(Type scene)
        {
            var method = AccessTools.Method(scene, "LoadAnimationListInfo", new[] { typeof(int), typeof(int) });
            _rows = AccessTools.Field(scene, "lstUseAnimInfo"); _categories = AccessTools.Field(scene, "categorys");
            if (method == null || method.ReturnType != typeof(HSceneProc.AnimationListInfo) || _rows == null
                || _categories == null || _categories.FieldType != typeof(List<int>))
                throw new MissingMemberException(scene.FullName + ".LoadAnimationListInfo/lstUseAnimInfo/categorys");
            return method;
        }

        private static void Postfix(object __instance, int _category, int _appointAction, ref HSceneProc.AnimationListInfo __result)
        {
            if (PatcherPlugin.IsQuitting || _failed || __instance == null) return;
            if (_category != -1 || _appointAction != -1) return;
            try
            {
                var categories = _categories.GetValue(__instance) as List<int>;
                var selected = StartPoseRules.Select(__result, _rows.GetValue(__instance) as IList,
                    categories, Categories, _category, _appointAction);
                if (ReferenceEquals(selected, __result)) return;
                PatcherPlugin.Logger.LogInfo("[StartPoseFix] 開始体位を選び直しました（" + Label(__result) + " → " + Label(selected)
                    + "、categorys=[" + string.Join(",", categories.ConvertAll(c => c.ToString()).ToArray()) + "]）。");
                __result = selected;
            }
            catch (Exception ex) { Stop(ex); }
        }

        private static IEnumerable<int> Categories(HSceneProc.AnimationListInfo info)
        {
            if (info.lstCategory != null) foreach (var category in info.lstCategory) if (category != null) yield return category.category;
        }

        private static string Label(HSceneProc.AnimationListInfo info)
        { return info == null ? "null" : info.nameAnimation + "/" + info.mode + "/id=" + info.id; }

        private static void Stop(Exception ex)
        {
            _done = true; _failed = true;
            PatcherPlugin.Logger.LogWarning("[StartPoseFix] 失敗したのでこの起動中は止めます（元の選択は維持）: " + ex.Message);
        }
    }
}
