using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// kPlug の招待カード一覧以外のメモリ消費を追跡するための診断（読み取り専用。kPlug の挙動は変えない）。
    ///   v1.6.0 でも、kPlug なしより Hシーン入場1分後のコミットが約 1.1GB 多く、
    ///   Mono の使用量（GC.GetTotalMemory）でも Unity確保でも説明できなかった。
    ///
    /// 記録する内容:
    ///   B. InviteUI.Start() の前後：所要時間、その間の GetCardList の回数と読んだ枚数、
    ///      Mono ヒープ全体（Boehm GC が広げたら返さない量）と使用量、プロセスのコミット、GC 回数
    ///   C. InviteUI.Start() の外での kFileParser.GetCardList() の呼び出し（FindCardData など）：
    ///      回数、読んだ枚数、所要時間、ヒープの変化、呼び出し元。最初の数回は1回ずつ、以降は一定時間ごとにまとめて出す
    /// </summary>
    internal static class KPlugMemoryDiagnostics
    {
        private const string Tag = "[KPlugMemDiag]";
        private const int MaxDetailedOutsideCalls = 15;
        private const float SummaryIntervalSeconds = 60f;

        private static readonly HashSet<string> _appliedKeys = new HashSet<string>();

        // ---- B: InviteUI.Start ----
        private static int _startDepth;
        private static Measure _startBegin;
        private static int _startGetCardListCalls;
        private static int _startCardsLoaded;

        // ---- C: GetCardList（Start の外）----
        [ThreadStatic] private static Stack<Measure> _callStack;
        private static int _outsideCallsTotal;
        private static int _outsideDetailedLogged;
        private static int _windowCalls;
        private static int _windowCards;
        private static long _windowElapsedMs;
        private static readonly Dictionary<string, int> _windowCallers = new Dictionary<string, int>();
        private static float _windowStart = -1f;

        internal static bool Enabled
        {
            get { return PatcherPlugin.EnableKPlugMemoryDiagnostics != null && PatcherPlugin.EnableKPlugMemoryDiagnostics.Value; }
        }

        internal struct Measure
        {
            public long heap, used, commit;
            public int gc;
            public Stopwatch watch;
        }

        // ===================== 適用 =====================

        /// <summary>何度呼んでもよい（適用済みの対象は飛ばす）。PatcherPlugin の早期パスから呼ぶ。</summary>
        internal static void TryApply(Harmony harmony)
        {
            if (!Enabled) return;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.FullName.Contains("kPlug")) continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                Patch(harmony, types, ".CmpH.InviteUI", "Start", Type.EmptyTypes,
                    nameof(StartPrefix), null, nameof(StartFinalizer));
                Patch(harmony, types, ".DefAndParse.kFileParser", "GetCardList", null,
                    nameof(GetCardListPrefix), nameof(GetCardListPostfix), nameof(GetCardListFinalizer));
            }
        }

        private static void Patch(Harmony harmony, Type[] types, string typeSuffix, string methodName, Type[] args,
            string prefix, string postfix, string finalizer)
        {
            var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(typeSuffix));
            if (type == null) return;

            string key = type.FullName + "|" + methodName;
            if (_appliedKeys.Contains(key)) return;
            _appliedKeys.Add(key);

            try
            {
                var method = args != null ? AccessTools.DeclaredMethod(type, methodName, args) : AccessTools.DeclaredMethod(type, methodName);
                if (method == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}.{methodName}() が見つかりません。");
                    return;
                }
                harmony.Patch(method,
                    prefix: prefix != null ? H(prefix) : null,
                    postfix: postfix != null ? H(postfix) : null,
                    finalizer: finalizer != null ? H(finalizer) : null);
                PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName}.{methodName}() に診断パッチを適用しました。");
            }
            catch (Exception ex)
            {
                // 通常モードで kPlugVR の型を触ると失敗する（既知）。1行だけ Info で残す。
                PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName}.{methodName}() は対象外です: {ex.Message}");
            }
        }

        private static HarmonyMethod H(string name)
        {
            return new HarmonyMethod(typeof(KPlugMemoryDiagnostics).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
        }

        // ===================== B: InviteUI.Start =====================

        private static void StartPrefix()
        {
            _startDepth++;
            if (_startDepth != 1) return;
            try
            {
                _startGetCardListCalls = 0;
                _startCardsLoaded = 0;
                _startBegin = Take();
            }
            catch { }
        }

        private static Exception StartFinalizer(Exception __exception)
        {
            _startDepth = Math.Max(0, _startDepth - 1);
            if (_startDepth != 0) return __exception;

            try
            {
                var end = Take();
                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} InviteUI.Start: 所要 {_startBegin.watch.ElapsedMilliseconds}ms / " +
                    $"GetCardList {_startGetCardListCalls}回・{_startCardsLoaded}枚 / {Diff(_startBegin, end)}" +
                    (__exception != null ? $" / 例外で終了: {__exception.GetType().Name}" : ""));
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} InviteUI.Start の記録に失敗: {ex.Message}");
            }
            return __exception;
        }

        // ===================== C: GetCardList =====================

        private static void GetCardListPrefix()
        {
            try
            {
                if (_callStack == null) _callStack = new Stack<Measure>();
                _callStack.Push(Take());
            }
            catch { }
        }

        private static void GetCardListPostfix(object __result, string cardPath, int sex)
        {
            try
            {
                if (_callStack == null || _callStack.Count == 0) return;
                var begin = _callStack.Pop();
                int cards = (__result as System.Collections.ICollection)?.Count ?? -1;

                if (_startDepth > 0)
                {
                    _startGetCardListCalls++;
                    if (cards > 0) _startCardsLoaded += cards;
                    return;
                }

                // ---- Start の外 ----
                var end = Take();
                long ms = begin.watch.ElapsedMilliseconds;
                string caller = DescribeCaller();

                _outsideCallsTotal++;
                _windowCalls++;
                if (cards > 0) _windowCards += cards;
                _windowElapsedMs += ms;
                int n;
                _windowCallers.TryGetValue(caller, out n);
                _windowCallers[caller] = n + 1;
                if (_windowStart < 0f) _windowStart = UnityEngine.Time.realtimeSinceStartup;

                if (_outsideDetailedLogged < MaxDetailedOutsideCalls)
                {
                    _outsideDetailedLogged++;
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag} InviteUI.Start 以外で GetCardList #{_outsideCallsTotal}: sex={sex} 読んだ {cards}枚 所要 {ms}ms / " +
                        $"{Diff(begin, end)} / 場所={ShortPath(cardPath)} / 呼び出し元: {caller}" +
                        (_outsideDetailedLogged == MaxDetailedOutsideCalls ? "（以降は一定時間ごとにまとめて出します）" : ""));
                }

                FlushWindowIfDue(false);
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} GetCardList の記録に失敗: {ex.Message}");
            }
        }

        private static Exception GetCardListFinalizer(Exception __exception)
        {
            // 例外で抜けた場合は Postfix が呼ばれないので、積んだ計測を捨てる
            if (__exception != null && _callStack != null && _callStack.Count > 0) _callStack.Pop();
            return __exception;
        }

        /// <summary>PatcherPlugin.Update から毎フレーム呼ぶ。呼び出しが止まった後も最後のまとめを出すため。</summary>
        internal static void Tick()
        {
            if (!Enabled || _windowCalls == 0) return;
            try { FlushWindowIfDue(false); } catch { }
        }

        private static void FlushWindowIfDue(bool force)
        {
            if (_windowCalls == 0 || _windowStart < 0f) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (!force && now - _windowStart < SummaryIntervalSeconds) return;

            var callers = string.Join(", ", _windowCallers.OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}={kv.Value}回").ToArray());
            PatcherPlugin.Logger.LogInfo(
                $"{Tag} 直近 {now - _windowStart:F0}秒の InviteUI.Start 以外の GetCardList: {_windowCalls}回・{_windowCards}枚・" +
                $"所要計 {_windowElapsedMs}ms（累計 {_outsideCallsTotal}回）/ 現在 {Describe(Take())} / 呼び出し元別: {callers}");

            _windowCalls = 0;
            _windowCards = 0;
            _windowElapsedMs = 0;
            _windowCallers.Clear();
            _windowStart = -1f;
        }

        // ===================== 計測 =====================

        internal static Measure Take()
        {
            var m = new Measure
            {
                heap = -1, used = -1, commit = GetCommit(), gc = GC.CollectionCount(0),
                watch = Stopwatch.StartNew()
            };
            if (!_monoExportFailed)
            {
                try
                {
                    m.heap = mono_gc_get_heap_size();
                    m.used = mono_gc_get_used_size();
                    if (m.heap <= 0) m.heap = -1;
                    if (m.used <= 0) m.used = -1;
                }
                catch { _monoExportFailed = true; }
            }
            return m;
        }

        internal static string Diff(Measure a, Measure b)
        {
            return $"ヒープ全体 {MB(a.heap)}→{MB(b.heap)}({DMB(a.heap, b.heap)}) / 使用 {MB(a.used)}→{MB(b.used)}({DMB(a.used, b.used)}) / " +
                   $"コミット {MB(a.commit)}→{MB(b.commit)}({DMB(a.commit, b.commit)}) / GC+{b.gc - a.gc}";
        }

        internal static string Describe(Measure m)
        {
            return $"ヒープ全体 {MB(m.heap)} / 使用 {MB(m.used)} / コミット {MB(m.commit)}";
        }

        internal static string MB(long v) { return v < 0 ? "不明" : $"{v / 1048576.0:F0}MB"; }

        private static string DMB(long a, long b)
        {
            if (a < 0 || b < 0) return "?";
            double d = (b - a) / 1048576.0;
            return (d >= 0 ? "+" : "") + d.ToString("F0");
        }

        private static string ShortPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "(null)";
            int i = p.IndexOf("chara/", StringComparison.OrdinalIgnoreCase);
            return i >= 0 ? p.Substring(i) : p;
        }

        /// <summary>kFileParser と本クラス・Harmony の枠を除いた呼び出し元を最大4段。</summary>
        private static string DescribeCaller()
        {
            var frames = new StackTrace(2, false).GetFrames();
            if (frames == null) return "(不明)";

            var parts = new List<string>();
            foreach (var f in frames)
            {
                var m = f.GetMethod();
                if (m == null) continue;
                var t = m.DeclaringType;
                string tn = t != null ? t.Name : "";
                if (t == typeof(KPlugMemoryDiagnostics) || (tn == "kFileParser" && m.Name.Contains("GetCardList"))) continue;
                if (t != null && t.Namespace != null && t.Namespace.StartsWith("HarmonyLib")) continue;
                parts.Add(tn + "." + m.Name);
                if (parts.Count >= 4) break;
            }
            return parts.Count > 0 ? string.Join(" <- ", parts.ToArray()) : "(不明)";
        }

        // ---- Win32（KK_MemDiag と同じ方法。Mono の Process は 0 を返すため）----
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCountersEx
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        // Mono ヒープ全体と使用量。Profiler.GetMonoHeapSizeLong などは製品版では 0 を返すため（1600 のログで確認）、
        // Mono ランタイムの公開関数を直接呼ぶ（Koikatu_Data/Mono/mono.dll がエクスポートしていることを確認済み）。
        [DllImport("mono", CallingConvention = CallingConvention.Cdecl)]
        private static extern long mono_gc_get_heap_size();

        [DllImport("mono", CallingConvention = CallingConvention.Cdecl)]
        private static extern long mono_gc_get_used_size();

        private static bool _monoExportFailed;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool K32GetProcessMemoryInfo(IntPtr hProcess, ref ProcessMemoryCountersEx counters, uint size);

        private static long GetCommit()
        {
            try
            {
                var pmc = new ProcessMemoryCountersEx();
                pmc.cb = (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx));
                if (K32GetProcessMemoryInfo(GetCurrentProcess(), ref pmc, pmc.cb))
                    return (long)pmc.PrivateUsage.ToUInt64();
            }
            catch { }
            return -1;
        }
    }
}
