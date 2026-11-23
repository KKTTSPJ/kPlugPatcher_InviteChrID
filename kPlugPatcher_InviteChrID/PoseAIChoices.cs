using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace kPlugPatcher_InviteChrID
{
    internal static class PoseAIChoices
    {
        // Preserve the inspected AI's decision probabilities, independently of candidate location.
        internal static bool ShouldChange(string route, bool previous, int chain, float first, float second, int limit)
        {
            switch (route)
            {
                case "ChangePiston": return first > (previous ? 45f : 10f);
                case "ChangeService": return first > (previous ? 90f : 45f) || second > (previous ? 85f : 35f);
                case "GoFromPistonToService": return chain >= limit;
                default: throw new ArgumentException("Unknown AI route", "route");
            }
        }

        internal static List<KeyValuePair<string, T>> Build<T>(IEnumerable<string> names, string current,
            Func<string, T> resolve, Predicate<T> allowed) where T : class
        {
            var result = new List<KeyValuePair<string, T>>();
            var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
            var uniqueRows = new HashSet<T>(ReferenceComparer<T>.Instance);
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name) || name == current || !uniqueNames.Add(name)) continue;
                T row = resolve(name);
                if (row != null && allowed(row) && uniqueRows.Add(row)) result.Add(new KeyValuePair<string, T>(name, row));
            }
            return result;
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
            public bool Equals(T x, T y) { return ReferenceEquals(x, y); }
            public int GetHashCode(T value) { return RuntimeHelpers.GetHashCode(value); }
        }
    }
}
