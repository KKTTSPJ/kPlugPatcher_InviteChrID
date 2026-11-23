using System;
using System.Collections;
using System.Collections.Generic;

namespace kPlugPatcher_InviteChrID
{
    internal static class StartPoseRules
    {
        // Only choose from existing usable rows. Never mutate the expanded menu.
        internal static T Select<T>(T current, IList groups, ICollection<int> categories,
            Func<T, IEnumerable<int>> categoryOf, int category, int appoint) where T : class
        {
            if (category != -1 || appoint != -1 || current == null || groups == null
                || categories == null || categories.Count == 0 || Matches(current, categories, categoryOf)) return current;
            T first = null;
            foreach (object group in groups)
            {
                var rows = group as IEnumerable<T>;
                if (rows == null) continue;
                foreach (var row in rows)
                {
                    if (row == null || !Matches(row, categories, categoryOf)) continue;
                    if (first == null) first = row;
                    // Preserve the game's special-row priority after narrowing.
                    var values = categoryOf(row);
                    if (values != null) foreach (int value in values) if (value >= 1000) return row;
                }
            }
            return first ?? current;
        }

        private static bool Matches<T>(T row, ICollection<int> categories, Func<T, IEnumerable<int>> categoryOf)
        {
            var values = categoryOf(row);
            if (values != null) foreach (int value in values) if (categories.Contains(value)) return true;
            return false;
        }
    }
}
