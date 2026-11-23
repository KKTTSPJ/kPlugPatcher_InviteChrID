using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    internal sealed class PoseMenuAccess
    {
        internal readonly MethodInfo Stand, Sit, Resolve;
        internal readonly object[] Stands, Sits;
        private readonly FieldInfo _proc, _flag, _ctrl, _context, _map, _tour, _heroine, _anim;
        private readonly FieldInfo _all, _extras, _mapper;
        private readonly FieldInfo[] _blocked;
        private readonly MethodInfo _three, _dark, _les, _experience, _extra;
        private object[] _stamp;
        internal string CurrentName { get { return _anim.GetValue(null) as string; } }
        internal string PointName { get { return Stand.Invoke(null, null).ToString(); } }

        internal PoseMenuAccess(Assembly assembly)
        {
            Type core = assembly.GetType("kPlug.Core", true), tool = assembly.GetType("kPlug.Tools.ToolH", true);
            _proc = Field(core, "hProc", true); _flag = Field(core, "hFlag", true); _ctrl = Field(core, "hCtrl", true);
            if (_proc.FieldType != typeof(HSceneProc) || _flag.FieldType != typeof(HFlag)) throw new InvalidOperationException("HSceneProc/HFlag mismatch");
            _context = Field(core, "hStartCtx", true); _map = Field(core, "mapId", true);
            _tour = Field(core, "tourInFreeH", true); _heroine = Field(core, "heroine", true); _anim = Field(core, "hAnim", true);
            string[] blocked = { "leavingH", "inHresult", "inSpH", "inLoad", "inFade", "inOverlap" };
            _blocked = new FieldInfo[blocked.Length];
            for (int i = 0; i < blocked.Length; i++)
            {
                _blocked[i] = Field(core, blocked[i], true);
                if (_blocked[i].FieldType != typeof(bool)) throw new MissingFieldException(blocked[i] + " (bool)");
            }
            _all = Field(_ctrl.FieldType, "allAnims", false); _extra = Getter(_ctrl.FieldType, "ExtraAnimCtrl");
            _extras = Field(_extra.ReturnType, "extraAnims", false); _mapper = Field(_ctrl.FieldType, "AnimNmMapper", false);
            _les = Getter(_ctrl.FieldType, "IsLesFreeH"); _experience = Getter(_heroine.FieldType, "HExperience");
            _three = Method(tool, "IsThreepH"); _dark = Method(tool, "IsDarknessScene");
            if (_three.ReturnType != typeof(bool) || _dark.ReturnType != typeof(bool) || _les.ReturnType != typeof(bool))
                throw new MissingMemberException("H scene predicates");
            Stand = Method(tool, "StandTypFromCat"); Sit = Method(tool, "GetSitType");
            if (!Stand.ReturnType.IsEnum || !Sit.ReturnType.IsEnum) throw new MissingMemberException("StandTyp/SitTyp");
            Resolve = Method(assembly.GetType("kPlug.DefAndParse.ParserAnim", true), "GetHAnim", typeof(string));
            if (Resolve.ReturnType != typeof(HSceneProc.AnimationListInfo)) throw new MissingMemberException("ParserAnim.GetHAnim return");
            // Chair/Bed/BenchSp and DeskClass have distinct candidates within their groups.
            string[] stands = { "Stand", "Sit", "Sit", "Sit", "Desk", "DeskClass", "Wall", "PoolSide", "Water" };
            string[] sits = { "Bed", "Chair", "Bed", "BenchSp", "Bed", "Bed", "Bed", "Bed", "Bed" };
            Stands = new object[stands.Length]; Sits = new object[sits.Length];
            for (int i = 0; i < stands.Length; i++)
            { Stands[i] = Enum.Parse(Stand.ReturnType, stands[i]); Sits[i] = Enum.Parse(Sit.ReturnType, sits[i]); }
        }

        internal bool Refresh(PoseMenuCache cache)
        {
            var proc = _proc.GetValue(null) as HSceneProc;
            var flag = _flag.GetValue(null) as HFlag;
            object ctrl = _ctrl.GetValue(null);
            if (!Alive(proc) || !Alive(flag) || !Alive(ctrl) || !ReferenceEquals(proc.flags, flag) || flag.isHSceneEnd) return Reset(cache);
            foreach (FieldInfo field in _blocked) if ((bool)field.GetValue(null)) return Reset(cache);
            string mode = flag.mode.ToString(), context = _context.GetValue(null).ToString();
            if (!Allows(mode, context, (bool)_three.Invoke(null, null), (bool)_dark.Invoke(null, null), (bool)_les.Invoke(ctrl, null)))
                return Reset(cache);
            object extra = _extra.Invoke(ctrl, null), heroine = _heroine.GetValue(null);
            var all = _all.GetValue(ctrl) as IList;
            var extras = Alive(extra) ? _extras.GetValue(extra) as IList : null;
            object mapper = _mapper.GetValue(ctrl);
            if (all == null || extras == null || mapper == null || heroine == null || proc.categorys == null || proc.categorys.Count == 0)
                return Reset(cache);
            string sizes = "";
            foreach (object group in all)
            {
                var list = group as IList;
                if (list == null) return Reset(cache);
                sizes += ":" + list.Count;
            }
            object[] stamp = { proc, flag, ctrl, heroine, extra, all, extras, mapper, sizes, extras.Count,
                mode, context, _map.GetValue(null), _tour.GetValue(null), _experience.Invoke(heroine, null), _anim.GetValue(null),
                string.Join(",", proc.categorys.ConvertAll(c => c.ToString()).ToArray()), Stand.Invoke(null, null), Sit.Invoke(null, null) };
            bool changed = _stamp == null;
            if (!changed) for (int i = 0; i < stamp.Length; i++) if (!Equals(stamp[i], _stamp[i])) { changed = true; break; }
            if (changed) { cache.Clear(); _stamp = stamp; }
            return true;
        }

        internal static bool Allows(string mode, string context, bool three, bool dark, bool les)
        {
            return !three && !dark && !les && mode != "peeping" && mode != "lesbian"
                && context != "SpPool1" && context != "SpStorage" && context != "SpLib2";
        }

        internal void Invalidate(PoseMenuCache cache) { _stamp = null; cache.Clear(); }
        private bool Reset(PoseMenuCache cache) { if (_stamp != null) Invalidate(cache); return false; }
        private static bool Alive(object value)
        { return value != null && (!(value is UnityEngine.Object) || (UnityEngine.Object)value != null); }

        private static FieldInfo Field(Type type, string name, bool isStatic)
        {
            var field = AccessTools.Field(type, name);
            if (field == null || field.IsStatic != isStatic) throw new MissingFieldException(type.FullName, name);
            return field;
        }
        private static MethodInfo Getter(Type type, string name)
        {
            var property = AccessTools.Property(type, name);
            var method = property != null ? property.GetGetMethod(true) : null;
            if (method == null || method.IsStatic) throw new MissingMemberException(type.FullName, name);
            return method;
        }
        internal static MethodInfo Method(Type type, string name, params Type[] args)
        {
            var method = AccessTools.Method(type, name, args);
            if (method == null || !method.IsStatic) throw new MissingMethodException(type.FullName, name);
            return method;
        }
    }
}
