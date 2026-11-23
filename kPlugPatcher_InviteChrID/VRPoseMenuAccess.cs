using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    internal sealed class VRPoseMenuAccess
    {
        internal readonly MethodInfo Start, Define, Button, Rebuild;
        internal readonly MethodInfo Tabs, ServiceTabs;
        private readonly Type _type;
        private readonly FieldInfo _main, _frame, _menuGo, _show, _current, _currentService, _available, _availableService;
        private readonly MethodInfo _canChange;

        internal VRPoseMenuAccess(Type type)
        {
            _type = type;
            Start = Instance(type, "Start"); Define = Instance(type, "DefineTabAndAnimList"); Rebuild = Instance(type, "Rebuild");
            Button = AccessTools.Method(type, "ButtonName", new[] { typeof(string) });
            if (Button == null || Button.IsStatic || Button.ReturnType != typeof(string)) throw new MissingMethodException("MenuPose.ButtonName");
            Tabs = PoseMenuAccess.Method(type, "GetAvailableTabs"); ServiceTabs = PoseMenuAccess.Method(type, "GetAvailableSrvTabs");
            _main = VRPoseContext.Field(type, "mainMenu", false); _frame = VRPoseContext.Field(type, "frame", false);
            _menuGo = VRPoseContext.Field(_frame.FieldType, "menuGo", false); _show = VRPoseContext.Field(_main.FieldType, "ShowMenu", false);
            if (_show.FieldType != typeof(bool)) throw new MissingFieldException("MenuRoot.ShowMenu bool");
            _current = VRPoseContext.Field(type, "curTab", false); _currentService = VRPoseContext.Field(type, "curSrvTab", false);
            _available = VRPoseContext.Field(type, "availMainTabs", false); _availableService = VRPoseContext.Field(type, "availSrvTabs", false);
            ValidateTabs(_current, _available, Tabs); ValidateTabs(_currentService, _availableService, ServiceTabs);
            var property = AccessTools.Property(type, "CanChangePos");
            _canChange = property != null ? property.GetGetMethod(true) : null;
            if (_canChange == null || _canChange.IsStatic || _canChange.ReturnType != typeof(bool)) throw new MissingMemberException("MenuPose.CanChangePos");
        }

        internal bool Ready(object menu)
        {
            if (!Alive(menu) || !_type.IsInstanceOfType(menu) || !Alive(_main.GetValue(menu))) return false;
            object frame = _frame.GetValue(menu);
            return frame != null && Alive(_menuGo.GetValue(frame)) && (bool)_canChange.Invoke(menu, null);
        }

        internal void Clamp(object menu)
        { Select(menu, _current, _available, _current.GetValue(menu)); Select(menu, _currentService, _availableService, _currentService.GetValue(menu)); }

        internal void Refresh(object menu)
        {
            object main = _main.GetValue(menu), current = _current.GetValue(menu), service = _currentService.GetValue(menu);
            bool shown = (bool)_show.GetValue(main);
            try
            {
                Rebuild.Invoke(menu, null);
                Select(menu, _current, _available, current);
                Select(menu, _currentService, _availableService, service);
            }
            finally { if (Alive(main)) _show.SetValue(main, shown); }
        }

        private static void Select(object menu, FieldInfo current, FieldInfo available, object desired)
        {
            var list = available.GetValue(menu) as IList;
            if (list != null && list.Count > 0) current.SetValue(menu, list.Contains(desired) ? desired : list[0]);
        }
        private static void ValidateTabs(FieldInfo current, FieldInfo available, MethodInfo getter)
        {
            if (!current.FieldType.IsEnum || !available.FieldType.IsGenericType
                || available.FieldType.GetGenericTypeDefinition() != typeof(List<>)
                || available.FieldType.GetGenericArguments()[0] != current.FieldType || getter.ReturnType != available.FieldType)
                throw new MissingMemberException("MenuPose tab contracts");
        }
        private static MethodInfo Instance(Type type, string name)
        {
            var method = AccessTools.Method(type, name, Type.EmptyTypes);
            if (method == null || method.IsStatic || method.ReturnType != typeof(void)) throw new MissingMethodException(type.FullName, name);
            return method;
        }
        internal static bool Alive(object value)
        {
            if (value == null) return false;
            var unity = value as UnityEngine.Object;
            return ReferenceEquals(unity, null) || UnityAlive(unity);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool UnityAlive(UnityEngine.Object value) { return value != null; }
    }
}
