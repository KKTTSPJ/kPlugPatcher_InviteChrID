using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    internal sealed class PoseAIAccess
    {
        internal readonly MethodInfo[] Decisions;
        internal readonly MethodInfo GetPiston, GoToPiston;
        private readonly MethodInfo _pistonAction, _serviceAction, _resolveMethod;
        private readonly FieldInfo _actor, _flag, _previousPiston, _previousService, _chain;
        private readonly Func<bool, List<string>> _names;
        private readonly Func<string> _currentName, _pointName;
        private readonly Dictionary<string, HSceneProc.AnimationListInfo> _resolved = new Dictionary<string, HSceneProc.AnimationListInfo>(StringComparer.Ordinal);

        internal PoseAIAccess(Assembly assembly, PoseMenuAccess menu, PoseMenuCache cache,
            MethodInfo pistonMenu, MethodInfo serviceMenu, Type srv)
            : this(assembly, "kPlug.", cache)
        {
            _currentName = () => menu.CurrentName; _pointName = () => menu.PointName; _resolveMethod = menu.Resolve;
            _names = service =>
            {
                var names = new List<string>();
                if (service) foreach (object tab in Enum.GetValues(srv)) names.AddRange((List<string>)serviceMenu.Invoke(null, new[] { tab }));
                else names.AddRange((List<string>)pistonMenu.Invoke(null, null));
                return names;
            };
        }

        internal PoseAIAccess(Assembly assembly, VRPoseContext context, PoseMenuCache cache)
            : this(assembly, "kPlugVR.", cache)
        {
            _currentName = () => context.CurrentName; _pointName = () => context.PointName;
            _resolveMethod = context.Resolve; _names = context.Names;
        }

        private PoseAIAccess(Assembly assembly, string root, PoseMenuCache cache)
        {
            Type core = assembly.GetType(root + "Core", true), tool = assembly.GetType(root + "Tools.ToolAI", true);
            _actor = Field(core, "mainAi", true); _flag = Field(core, "hFlag", true);
            if (_flag.FieldType != typeof(HFlag)) throw new MissingFieldException("Core.hFlag type");
            _previousPiston = Field(_actor.FieldType, "pistonChangeOnLastThought", false);
            _previousService = Field(_actor.FieldType, "serviceChangeOnLastThought", false);
            _chain = Field(_actor.FieldType, "pistChainCount", false);
            if (_previousPiston.FieldType != typeof(bool) || _previousService.FieldType != typeof(bool) || _chain.FieldType != typeof(int))
                throw new MissingFieldException("AI decision counters");
            Decisions = new[] { PoseMenuAccess.Method(tool, "ChangePiston"), PoseMenuAccess.Method(tool, "ChangeService"),
                PoseMenuAccess.Method(tool, "GoFromPistonToService") };
            foreach (var method in Decisions) if (method.ReturnType != typeof(bool)) throw new MissingMethodException(method.Name + " bool");
            GetPiston = PoseMenuAccess.Method(tool, "GetAvailablePiston");
            if (GetPiston.ReturnType != typeof(List<string>)) throw new MissingMethodException("ToolAI.GetAvailablePiston List<string>");
            _pistonAction = Action(_actor.FieldType, "ChangePistonByHerself", typeof(HSceneProc.AnimationListInfo));
            _serviceAction = Action(_actor.FieldType, "ChangeServiceByHerself", typeof(HSceneProc.AnimationListInfo));
            GoToPiston = Action(_actor.FieldType, "GoToPistonByHerself", typeof(string));
            cache.Invalidated += _resolved.Clear;
        }

        internal object Actor
        {
            get
            {
                object actor = _actor.GetValue(null);
                return actor != null && (!(actor is UnityEngine.Object) || (UnityEngine.Object)actor != null) ? actor : null;
            }
        }
        internal string Point { get { return _pointName(); } }
        internal bool Previous(object actor, bool service) { return (bool)(service ? _previousService : _previousPiston).GetValue(actor); }
        internal int Chain(object actor) { return (int)_chain.GetValue(actor); }

        internal List<KeyValuePair<string, HSceneProc.AnimationListInfo>> Candidates(bool service)
        {
            var names = _names(service);
            var flags = _flag.GetValue(null) as HFlag;
            var current = flags != null ? flags.nowAnimationInfo : null;
            string mode = service ? "houshi" : "sonyu";
            return PoseAIChoices.Build(names, _currentName(), Resolve,
                row => !ReferenceEquals(row, current) && row.mode.ToString() == mode);
        }

        internal void Dispatch(object actor, bool service, HSceneProc.AnimationListInfo target)
        { (service ? _serviceAction : _pistonAction).Invoke(actor, new object[] { target }); }

        private HSceneProc.AnimationListInfo Resolve(string name)
        {
            HSceneProc.AnimationListInfo row;
            if (!_resolved.TryGetValue(name, out row))
                _resolved[name] = row = _resolveMethod.Invoke(null, new object[] { name }) as HSceneProc.AnimationListInfo;
            return row;
        }

        private static FieldInfo Field(Type type, string name, bool isStatic)
        {
            var field = AccessTools.Field(type, name);
            if (field == null || field.IsStatic != isStatic) throw new MissingFieldException(type.FullName, name);
            return field;
        }
        private static MethodInfo Action(Type type, string name, Type argument)
        {
            var method = AccessTools.Method(type, name, new[] { argument });
            if (method == null || method.IsStatic || method.ReturnType != typeof(void)) throw new MissingMethodException(type.FullName, name);
            return method;
        }
    }
}
