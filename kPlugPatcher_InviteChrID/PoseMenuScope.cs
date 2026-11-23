using System;
using System.Reflection;

namespace kPlugPatcher_InviteChrID
{
    // Only candidate enumeration sees the substitute values; no scene field is changed.
    internal static class PoseMenuScope
    {
        [ThreadStatic] private static object _stand;
        [ThreadStatic] private static object _sit;
        internal static bool Active { get { return _stand != null; } }

        internal static object Invoke(MethodInfo method, object[] args, object stand, object sit)
        {
            object oldStand = _stand, oldSit = _sit;
            try
            {
                _stand = stand; _sit = sit;
                return method.Invoke(null, args);
            }
            finally { _stand = oldStand; _sit = oldSit; }
        }

        internal static bool StandPrefix<T>(ref T __result)
        {
            if (!(_stand is T)) return true;
            __result = (T)_stand;
            return false;
        }

        internal static bool SitPrefix<T>(ref T __result)
        {
            if (!(_sit is T)) return true;
            __result = (T)_sit;
            return false;
        }
    }
}
