using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace kPlugPatcher_InviteChrID
{
    internal sealed class PoseMenuCache
    {
        private sealed class Entry
        {
            internal IList Native, Result;
            internal HashSet<string> Outside = new HashSet<string>(StringComparer.Ordinal);
            internal Dictionary<string, string> Names, Labels;
        }
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private readonly Dictionary<string, bool> _available = new Dictionary<string, bool>(StringComparer.Ordinal);
        internal event Action Invalidated;

        internal void Clear()
        {
            _entries.Clear(); _available.Clear();
            if (Invalidated != null) Invalidated();
        }

        internal IList Expand(MethodInfo method, object[] args, IList native, object[] stands, object[] sits, Func<string, bool> available)
        {
            string key = method.Name;
            foreach (object arg in args) key += ":" + arg;
            Entry entry;
            if (_entries.TryGetValue(key, out entry) && Equal(entry.Native, native)) return Copy(entry.Result);

            entry = new Entry { Native = Copy(native), Result = (IList)Activator.CreateInstance(method.ReturnType) };
            var seen = new HashSet<object>();
            var original = new HashSet<object>();
            foreach (object value in native) original.Add(value);
            Add(entry, native, seen, original, available);
            for (int i = 0; i < stands.Length; i++)
                Add(entry, (IList)PoseMenuScope.Invoke(method, args, stands[i], sits[i]), seen, original, available);
            // Publish only after every scoped call has succeeded.
            _entries[key] = entry;
            return Copy(entry.Result);
        }

        private void Add(Entry entry, IList source, HashSet<object> seen, HashSet<object> original, Func<string, bool> available)
        {
            foreach (object value in source)
            {
                if (value == null || !seen.Add(value)) continue;
                string name = value as string;
                if (name != null)
                {
                    bool exists;
                    if (!_available.TryGetValue(name, out exists)) _available[name] = exists = available(name);
                    if (!exists) continue;
                    if (!original.Contains(value)) entry.Outside.Add(name);
                }
                entry.Result.Add(value);
            }
        }

        internal Dictionary<string, string> Labels(IList draw, Dictionary<string, string> names)
        {
            if (names == null) return names;
            foreach (Entry entry in _entries.Values)
            {
                if (entry.Outside.Count == 0 || !Equal(entry.Result, draw)) continue;
                if (!ReferenceEquals(entry.Names, names))
                {
                    var labels = new Dictionary<string, string>(names, StringComparer.Ordinal);
                    foreach (string name in entry.Outside)
                    {
                        string label;
                        labels[name] = (names.TryGetValue(name, out label) && !string.IsNullOrEmpty(label) ? label : name) + " *";
                    }
                    entry.Names = names; entry.Labels = labels;
                }
                return entry.Labels;
            }
            return names;
        }

        internal bool IsOutside(string name)
        {
            bool outside = false;
            foreach (Entry entry in _entries.Values)
            {
                if (entry.Native.Contains(name)) return false;
                if (entry.Outside.Contains(name)) outside = true;
            }
            return outside;
        }

        private static IList Copy(IList source)
        {
            var result = (IList)Activator.CreateInstance(source.GetType());
            foreach (object value in source) result.Add(value);
            return result;
        }

        private static bool Equal(IList first, IList second)
        {
            if (first == null || second == null || first.Count != second.Count) return false;
            for (int i = 0; i < first.Count; i++) if (!Equals(first[i], second[i])) return false;
            return true;
        }
    }
}
