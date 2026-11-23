using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    internal sealed class VRPoseContext
    {
        internal readonly Type SceneType, MenuType;
        internal readonly MethodInfo Stand, Sit, Resolve, Piston, Tease, Service;
        internal readonly object[] Stands, Sits;
        private readonly PoseMenuCache _cache;
        private readonly FieldInfo _proc, _flags, _ctrl, _heroine, _anim, _start, _categories, _procFlags, _all, _mapper, _extras, _dealer;
        private readonly MethodInfo _extra, _experience, _three, _dark;
        private readonly Func<object> _inH, _map;
        private readonly List<Func<object>> _blocked = new List<Func<object>>();
        private readonly List<Func<object>> _busy = new List<Func<object>>();
        private readonly FieldInfo _actor;
        private readonly Func<object, object> _rootMenu, _poseMenu;
        private readonly object[] _tabs;
        private object[] _stamp;
        internal string CurrentName { get { return _anim.GetValue(null) as string; } }
        internal string PointName { get { return Stand.Invoke(null, null).ToString(); } }

        internal VRPoseContext(Assembly assembly, PoseMenuCache cache)
        {
            _cache = cache;
            Type core = assembly.GetType("kPlugVR.Core", true), tool = assembly.GetType("kPlugVR.Tools.ToolH", true);
            _proc = Field(core, "hProc", true); SceneType = _proc.FieldType;
            if (SceneType.FullName != "VRHScene") throw new MissingMemberException("Core.hProc VRHScene");
            _flags = Field(core, "hFlag", true); _ctrl = Field(core, "hCtrl", true);
            _actor = Field(core, "mainAi", true);
            _heroine = Field(core, "heroine", true); _anim = Field(core, "hAnim", true); _start = Field(core, "hStartCtx", true);
            _categories = Field(SceneType, "categorys", false); _procFlags = Field(SceneType, "flags", false);
            if (_flags.FieldType != typeof(HFlag) || _procFlags.FieldType != typeof(HFlag) || _categories.FieldType != typeof(List<int>))
                throw new MissingMemberException("VR flags/categorys types");
            _inH = StaticValue(core, "InH", "inH", typeof(bool));
            _map = StaticValue(core, "mapId", "mapId", typeof(int));
            _dealer = core.GetField("VRDealer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (string name in new[] { "InLoad", "InFade", "InOverlap" })
            {
                var read = StaticValue(core, name, char.ToLowerInvariant(name[0]) + name.Substring(1), typeof(bool));
                _blocked.Add(read); _busy.Add(read);
            }
            foreach (string name in new[] { "catchSceneEnd", "inSpH" })
            {
                var read = StaticValue(core, name, name, typeof(bool)); _blocked.Add(read);
                if (name == "catchSceneEnd") _busy.Add(read);
            }
            foreach (string name in new[] { "leavingH", "inHresult" })
                if (core.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null)
                {
                    var read = StaticValue(core, name, name, typeof(bool)); _blocked.Add(read); _busy.Add(read);
                }
            _all = Field(_ctrl.FieldType, "allAnims", false); _mapper = Field(_ctrl.FieldType, "AnimNmMapper", false);
            _extra = Getter(_ctrl.FieldType, "ExtraAnimCtrl"); _extras = Field(_extra.ReturnType, "extraAnims", false);
            _experience = Getter(_heroine.FieldType, "HExperience");
            Stand = PoseMenuAccess.Method(tool, "StandTypFromCat"); Sit = PoseMenuAccess.Method(tool, "GetSitType");
            _three = PoseMenuAccess.Method(tool, "IsThreepH"); _dark = PoseMenuAccess.Method(tool, "IsDarknessScene");
            if (!Stand.ReturnType.IsEnum || !Sit.ReturnType.IsEnum || _three.ReturnType != typeof(bool) || _dark.ReturnType != typeof(bool))
                throw new MissingMemberException("VR ToolH signatures");
            Resolve = PoseMenuAccess.Method(assembly.GetType("kPlugVR.DefAndParse.ParserAnim", true), "GetHAnim", typeof(string));
            if (Resolve.ReturnType != typeof(HSceneProc.AnimationListInfo)) throw new MissingMemberException("VR GetHAnim return");
            Type selector = assembly.GetType("kPlugVR.Tools.ToolAnimSelector", true);
            Piston = PoseMenuAccess.Method(selector, "GetAvailablePiston"); Tease = PoseMenuAccess.Method(selector, "GetAvailableTease");
            MenuType = assembly.GetType("kPlugVR.Menu.MenuPose");
            if (MenuType != null)
            {
                if (_dealer == null) throw new MissingFieldException("Core.VRDealer");
                _rootMenu = InstanceValue(_dealer.FieldType, "VRMenuRoot");
                _poseMenu = InstanceValue(Field(MenuType, "mainMenu", false).FieldType, "MenuPos");
            }
            Type serviceOwner = MenuType ?? selector;
            Type srv = (MenuType ?? assembly.GetType("kPlugVR.CmpH.AnimSelector", true)).GetNestedType(MenuType != null ? "ServTab" : "SrvTabs");
            if (srv == null || !srv.IsEnum) throw new MissingMemberException("VR service tab enum");
            Service = PoseMenuAccess.Method(serviceOwner, "GetAvailableService", srv);
            foreach (var method in new[] { Piston, Tease, Service })
                if (method.ReturnType != typeof(List<string>)) throw new MissingMethodException(method.Name + " List<string>");
            var tabs = new List<object>();
            foreach (object value in Enum.GetValues(srv)) if (value.ToString() != "None") tabs.Add(value);
            _tabs = tabs.ToArray();
            string[] stands = { "Stand", "Sit", "Sit", "Sit", "Desk", "DeskClass", "Wall", "PoolSide", "Water" };
            string[] sits = { "Bed", "Chair", "Bed", "BenchSp", "Bed", "Bed", "Bed", "Bed", "Bed" };
            Stands = new object[stands.Length]; Sits = new object[sits.Length];
            for (int i = 0; i < stands.Length; i++) { Stands[i] = Enum.Parse(Stand.ReturnType, stands[i]); Sits[i] = Enum.Parse(Sit.ReturnType, sits[i]); }
        }

        internal bool IsLive
        {
            get
            {
                var flag = _flags.GetValue(null) as HFlag;
                return (bool)_inH() && Alive(_proc.GetValue(null)) && Alive(_ctrl.GetValue(null)) && Alive(flag) && !flag.isHSceneEnd;
            }
        }

        internal bool Refresh()
        {
            if (!IsLive) return Reset();
            foreach (var blocked in _blocked) if ((bool)blocked()) return Reset();
            object proc = _proc.GetValue(null), ctrl = _ctrl.GetValue(null), flag = _flags.GetValue(null);
            if (!ReferenceEquals(_procFlags.GetValue(proc), flag) || (_dealer != null && !Alive(_dealer.GetValue(null)))) return Reset();
            string mode = ((HFlag)flag).mode.ToString(), start = _start.GetValue(null).ToString();
            if (!PoseMenuAccess.Allows(mode, start, (bool)_three.Invoke(null, null), (bool)_dark.Invoke(null, null), false)) return Reset();
            var categories = _categories.GetValue(proc) as List<int>;
            object heroine = _heroine.GetValue(null), extra = _extra.Invoke(ctrl, null), mapper = _mapper.GetValue(ctrl);
            var all = _all.GetValue(ctrl) as IList;
            var extras = Alive(extra) ? _extras.GetValue(extra) as IList : null;
            if (categories == null || categories.Count == 0 || heroine == null || mapper == null || all == null || extras == null) return Reset();
            string sizes = "";
            foreach (object group in all) { var list = group as IList; if (list == null) return Reset(); sizes += ":" + list.Count; }
            // Normal aibu/houshi/sonyu changes do not alter these menu candidates.
            object[] stamp = { proc, flag, ctrl, heroine, extra, mapper, all, extras, sizes, extras.Count, start, _map(),
                _experience.Invoke(heroine, null), string.Join(",", categories.ConvertAll(c => c.ToString()).ToArray()),
                Stand.Invoke(null, null), Sit.Invoke(null, null) };
            bool changed = _stamp == null;
            if (!changed) for (int i = 0; i < stamp.Length; i++) if (!Equals(stamp[i], _stamp[i])) { changed = true; break; }
            if (changed) { _stamp = stamp; _cache.Clear(); }
            return true;
        }

        internal bool CanRebuild
        {
            get
            {
                if (!IsLive || !Alive(_actor.GetValue(null))) return false;
                foreach (var busy in _busy) if ((bool)busy()) return false;
                return true;
            }
        }

        internal object MenuInstance
        {
            get
            {
                if (_rootMenu == null || _poseMenu == null) return null;
                object dealer = _dealer.GetValue(null);
                if (!Alive(dealer)) return null;
                object root = _rootMenu(dealer);
                if (!Alive(root)) return null;
                object menu = _poseMenu(root);
                return Alive(menu) ? menu : null;
            }
        }

        internal IList Expand(MethodInfo method, object[] args, IList native)
        { return _cache.Expand(method, args, native, Stands, Sits, name => Resolve.Invoke(null, new object[] { name }) != null); }

        private List<string> ExpandedNames(MethodInfo method, object[] args)
        {
            // Suppress UI postfixes while reading the original H-point candidates.
            var native = (IList)PoseMenuScope.Invoke(method, args, Stand.Invoke(null, null), Sit.Invoke(null, null));
            return (List<string>)Expand(method, args, native);
        }

        internal List<string> Names(bool service)
        {
            if (!service) return ExpandedNames(Piston, new object[0]);
            var names = new List<string>();
            foreach (object tab in _tabs) names.AddRange(ExpandedNames(Service, new[] { tab }));
            return names;
        }
        internal void Invalidate() { _stamp = null; _cache.Clear(); }
        private bool Reset() { if (_stamp != null) Invalidate(); return false; }
        private static bool Alive(object value)
        { return value != null && (!(value is UnityEngine.Object) || (UnityEngine.Object)value != null); }
        internal static FieldInfo Field(Type type, string name, bool isStatic)
        {
            var field = AccessTools.Field(type, name);
            if (field == null || field.IsStatic != isStatic) throw new MissingFieldException(type.FullName, name);
            return field;
        }
        private static MethodInfo Getter(Type type, string name)
        {
            var property = AccessTools.Property(type, name);
            if (property == null || property.GetGetMethod(true) == null || property.GetGetMethod(true).IsStatic) throw new MissingMemberException(type.FullName, name);
            return property.GetGetMethod(true);
        }
        private static Func<object> StaticValue(Type type, string propertyName, string fieldName, Type expected)
        {
            var property = type.GetProperty(propertyName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.PropertyType == expected && property.GetGetMethod(true) != null && property.GetGetMethod(true).IsStatic)
                return () => property.GetValue(null, null);
            var field = Field(type, fieldName, true);
            if (field.FieldType != expected) throw new MissingFieldException(fieldName + " type");
            return () => field.GetValue(null);
        }
        private static Func<object, object> InstanceValue(Type type, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.GetGetMethod(true) != null && !property.GetGetMethod(true).IsStatic)
                return value => type.IsInstanceOfType(value) ? property.GetValue(value, null) : null;
            var field = Field(type, name, false);
            return value => type.IsInstanceOfType(value) ? field.GetValue(value) : null;
        }
    }
}
