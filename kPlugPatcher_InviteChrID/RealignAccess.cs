using System;
using System.Reflection;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    // Metadata only; scene objects are read afresh for each request.
    internal sealed class RealignAccess
    {
        internal readonly FieldInfo HProc, HFlag, HCtrl, Dealer, Leaving, Result, Spot, Ghost;
        internal readonly MethodInfo NeedReinit;
        internal readonly PropertyInfo DealerBusy;
        private readonly FieldInfo _animChange, _force, _location;
        private readonly FieldInfo[] _busy;
        private readonly PropertyInfo _ready;

        internal RealignAccess(Type core, Type toolSpot)
        {
            HProc = Field(core, "hProc", true);
            HFlag = Field(core, "hFlag", true);
            HCtrl = Field(core, "hCtrl", true);
            Dealer = Field(core, "dealer", true);
            DealerBusy = AccessTools.Property(Dealer.FieldType, "InProcess");
            if (DealerBusy == null || DealerBusy.PropertyType != typeof(bool) || DealerBusy.GetGetMethod(true) == null
                || DealerBusy.GetGetMethod(true).IsStatic) throw new MissingMemberException("Dealer.InProcess");
            Leaving = Bool(core, "leavingH", true);
            Result = Bool(core, "inHresult", true);
            // Dealer moved between namespaces in some releases; follow its declared type.
            Spot = Field(Dealer.FieldType, "spotManager", false);
            Type manager = Spot.FieldType;
            Ghost = Field(manager, "ghost", false);
            _animChange = Bool(manager, "animChange", false);
            _force = Bool(manager, "forcePoseRefresh", false);
            _location = Bool(Ghost.FieldType, "locationHavChange", false);
            _ready = AccessTools.Property(manager, "spotsAreInit");
            if (_ready == null || _ready.PropertyType != typeof(bool) || _ready.GetGetMethod(true) == null
                || _ready.GetGetMethod(true).IsStatic) throw new MissingMemberException("spotsAreInit");
            string[] busy = { "inLocationUpdate", "inRefresh", "inPushAfterRefresh", "inAltMode", "charDeletion", "hEnd" };
            _busy = new FieldInfo[busy.Length];
            for (int i = 0; i < busy.Length; i++) _busy[i] = Bool(manager, busy[i], false);
            NeedReinit = toolSpot != null ? AccessTools.Method(toolSpot, "NeedReInit", new[] { Ghost.FieldType }) : null;
            if (NeedReinit == null || !NeedReinit.IsStatic || NeedReinit.ReturnType != typeof(bool))
                throw new MissingMethodException("ToolSpot.NeedReInit");
        }

        private static FieldInfo Field(Type type, string name, bool isStatic)
        {
            var field = type != null ? AccessTools.Field(type, name) : null;
            if (field == null || field.IsStatic != isStatic) throw new MissingFieldException(name);
            return field;
        }

        private static FieldInfo Bool(Type type, string name, bool isStatic)
        {
            var field = Field(type, name, isStatic);
            if (field.FieldType != typeof(bool)) throw new MissingFieldException(name + " (bool)");
            return field;
        }

        internal bool Request(object manager, out object ghost, out string reason)
        {
            ghost = null;
            reason = null;
            if (manager == null || !Spot.FieldType.IsInstanceOfType(manager)) { reason = "整列の部品がない"; return false; }
            if (!(bool)_ready.GetValue(manager, null)) { reason = "位置の準備中"; return false; }
            foreach (var field in _busy) if ((bool)field.GetValue(manager)) { reason = "整列・移動・編集処理中 (" + field.Name + ")"; return false; }
            if ((bool)_force.GetValue(manager)) { reason = "整列要求済み"; return false; }
            ghost = Ghost.GetValue(manager);
            if (ghost == null) { reason = "前回の位置情報がない"; return false; }
            bool oldAnim = (bool)_animChange.GetValue(manager), oldLocation = (bool)_location.GetValue(ghost);
            try
            {
                _location.SetValue(ghost, true);
                _force.SetValue(manager, true);
                _animChange.SetValue(manager, true);
                return true;
            }
            catch
            {
                _animChange.SetValue(manager, oldAnim);
                _force.SetValue(manager, false);
                _location.SetValue(ghost, oldLocation);
                throw;
            }
        }
    }
}
