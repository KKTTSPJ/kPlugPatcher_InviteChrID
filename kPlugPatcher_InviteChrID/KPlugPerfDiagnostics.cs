using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// 招待1人ごとのコストと、複数人招待時のフレームレート低下を調べるための診断（読み取り専用。kPlug の挙動は変えない）。
    /// 記録する内容:
    ///   1. フレームと kPlug の処理時間（一定時間ごとのまとめ）
    ///      平均・最大フレーム時間、キャラ数、GC 回数、例外の件数と上位、ヒープ・コミット、
    ///      kPlug の MonoBehaviour の Update / LateUpdate / FixedUpdate / OnGUI（と招待関係のコルーチン）の
    ///      メソッド別の処理時間の上位
    ///   2. キャラの読み込み・再読み込み（ChaControl.Load / Reload / ChangeCoordinateTypeAndReload）
    ///      1回ごとの所要時間・ヒープとコミットの変化・対象キャラ・呼び出し元。重いものは1件ずつ、全体はまとめに出す
    ///   3. 招待1人ごとの前後比較：招待の瞬間と、その 5秒後・20秒後のヒープ・コミット・Unity確保・オブジェクト数
    ///   4. （v1.7.3 Q1）カードの丸ごとコピー（ChaFile.CopyChaFile / CopyAll）の所要時間・呼び出し元。交代で約10秒止まる件の内訳
    ///   5. （v1.7.3 Q2）シーン変化時の RenderTexture / Texture2D の名前・大きさ別の内訳と、
    ///      Manager.Character.dictEntryChara の登録数（破棄済みで残っているもの）。Hシーンを出てもメモリが戻らない件
    ///   6. （v1.7.3 Q3）1フレームあたりの FixedUpdate（物理演算）の回数と、Rigidbody / Collider / DynamicBone /
    ///      SkinnedMeshRenderer / Light / Camera の数。フレーム低下のうち kPlug 以外の部分の切り分け
    ///   7. （v1.7.4 R1）1024×1024 以上の RenderTexture を作った呼び出し元（アセンブリ名付き）を記録し、
    ///      シーン変化時に「生きている RenderTexture」を作成元別に集計する。Hシーンを出ても約3.5GB 残る件の持ち主の特定
    ///   8. （v1.7.4 R2）招待関係のコルーチンの MoveNext を、実行前後の状態番号（<>1__state）付きで記録し、100ms 以上の区間を出す
    ///   9. （v1.7.4 R3）ゲーム本体のキャラごとの更新（DynamicBone / Ver01 / Ver02、Manager.Character、FaceBlendShape、
    ///      EyeLookController、NeckLookControllerVer2、服・髪・アクセサリのコンポーネント）も kPlug と同じ方法で計測する
    ///   10.（v1.8.0 S1）交代（AI_Public.IE_SwapGirls の状態 7→8）で呼ばれる処理と、
    ///      ゲーム本体の HVoiceCtrl.Init に計測を当て、1回 30ms 以上かかった呼び出しを1件ずつ出す。交代で約11秒止まる件の特定
    /// </summary>
    internal static class KPlugPerfDiagnostics
    {
        private const string Tag = "[KPlugPerf]";
        private const float WindowSeconds = 30f;
        private const int PatchesPerFrame = 8;
        private const double HeavyReloadMs = 50.0;
        private const int MaxHeavyReloadLogs = 40;

        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        internal static bool Enabled
        {
            get { return PatcherPlugin.EnableKPlugPerfDiagnostics != null && PatcherPlugin.EnableKPlugPerfDiagnostics.Value; }
        }

        // ===================== 1. メソッド別の処理時間 =====================

        private sealed class MethodStat
        {
            public string Name;
            public bool IsGame;   // ゲーム本体（Assembly-CSharp）なら true。まとめで kPlug と分けて合計する
            public long Ticks;
            public long MaxTicks;
            public int Calls;
        }

        private static readonly Dictionary<MethodBase, MethodStat> _methodStats = new Dictionary<MethodBase, MethodStat>();
        private static readonly Queue<MethodBase> _patchQueue = new Queue<MethodBase>();
        private static readonly Queue<MethodBase> _coroutineQueue = new Queue<MethodBase>();
        private static readonly HashSet<MethodBase> _gameMethods = new HashSet<MethodBase>();
        private static bool _queued;
        private static int _patchedCount, _patchFailed;
        private static string _firstPatchError;
        private static Harmony _harmony;

        // フレーム
        private static float _windowStart = -1f;
        private static int _frames;
        private static double _frameMsSum, _frameMsMax;
        private static int _framesOver33, _framesOver66;
        private static int _gcAtWindowStart;

        // 例外（Application.logMessageReceived）
        private static bool _logHooked;
        private static int _exceptions;
        private static readonly Dictionary<string, int> _exceptionKinds = new Dictionary<string, int>();

        // ===================== 2. キャラの読み込み・再読み込み =====================

        private static int _reloadDepth;
        private static KPlugMemoryDiagnostics.Measure _reloadBegin;
        private static string _reloadMethod, _reloadChara;
        private static int _heavyReloadLogged;
        private static readonly Dictionary<string, double[]> _reloadByCaller = new Dictionary<string, double[]>(); // [回数, 合計ms]

        // ===================== 3. 招待の前後 =====================

        private sealed class InviteSnapshot
        {
            public string Label;
            public float Time;
            public KPlugMemoryDiagnostics.Measure M;
            public long UnityAlloc;
            public int Charas, GameObjects, Textures, Materials, Meshes;
            public bool Logged5, Logged20;
        }

        private static readonly List<InviteSnapshot> _invites = new List<InviteSnapshot>();
        private static int _inviteSeq;

        // ===================== 4. カードのコピー（Q1） =====================

        private static int _copyDepth;
        private static KPlugMemoryDiagnostics.Measure _copyBegin;
        private static string _copyMethod, _copyChara;
        private static int _heavyCopyLogged;
        private static readonly Dictionary<string, double[]> _copyByCaller = new Dictionary<string, double[]>(); // [回数, 合計ms]

        // ===================== 5. シーン変化時の内訳（Q2） =====================

        private static string _lastScene;
        private static float _lastSceneCheck = -1f;

        // ===================== 6. 物理演算（Q3） =====================

        private static int _fixedSteps;

        // ===================== 7. RenderTexture の作成元（R1） =====================

        private sealed class RtRecord
        {
            public WeakReference Rt;
            public string Caller;
            public double Mb;
        }

        private static readonly List<RtRecord> _rtRecords = new List<RtRecord>();
        private static readonly HashSet<int> _rtSeen = new HashSet<int>();   // InstanceID（コンストラクタの重複記録を防ぐ）
        private static readonly Dictionary<string, double[]> _rtCreatedByCaller = new Dictionary<string, double[]>(); // 累計 [数, MB]

        // ===================== 8. コルーチンの区間（R2） =====================

        private sealed class CoroState
        {
            public long Start;
            public int State;
        }

        private static readonly Dictionary<Type, FieldInfo> _coroStateFields = new Dictionary<Type, FieldInfo>();
        private static int _coroLogged;
        private const double CoroLogMs = 100.0;

        // ===================== 10. 遅い呼び出し（S1） =====================

        private const double SlowCallLogMs = 30.0;
        private const int MaxSlowCallLogs = 200;
        private static int _slowCallLogged;
        private static readonly Dictionary<MethodBase, string> _slowCallNames = new Dictionary<MethodBase, string>();

        /// <summary>交代の状態 7→8 の区間で呼ばれる kPlug の処理（型名 → メソッド名）。同名の多重定義はすべて対象。</summary>
        private static readonly string[][] SwapSectionTargets =
        {
            new[] { "StatManager", "SessionOrgasmCount" }, new[] { "StatManager", "GirlHcountToHFlag" },
            new[] { "StatManager", "GirlStatToHFlag" }, new[] { "StatManager", "GetDaily" }, new[] { "StatManager", "EditGirlHcount" },
            new[] { "AI_Public", "GetStats" }, new[] { "AI_Public", "ForceKokanReveal" }, new[] { "SpotCtrl", "AdaptAnim" },
            new[] { "ToolUI", "ReplaceFaceImage" }, new[] { "ParserScene", "FindCharFacePng" },
            new[] { "BreastPusher", "SwapPusherValues" }, new[] { "BellyCtrl", "SwapBellyValues" },
            new[] { "KokanBehavior", "CopyCount" }, new[] { "HCtrl", "AdaptPoseRequiringExpBoost" },
            new[] { "ChaTools", "SetSiruOnCha" }, new[] { "TongueCtrl", "Create" }, new[] { "TongueCtrl", "AutoDestroy" },
            new[] { "ClothesCtrl", "StoreMainAccs" }, new[] { "ClothesCtrl", "ApplyCostumeFromDailyInH" },
            new[] { "ToolH", "ClearMetaball" }, new[] { "ToolH", "MaleTotalOrgasm" },
        };
        private const int MaxCoroLogs = 80;

        // ===================== 適用 =====================

        /// <summary>PatcherPlugin の本パス（frame 300 以降）から1回呼ぶ。メソッドの列挙だけ行い、パッチは Tick で少しずつ当てる。</summary>
        internal static void TryApply(Harmony harmony)
        {
            if (!Enabled || _queued) return;
            _queued = true;
            _harmony = harmony;

            HookLogMessages();
            PatchCharaReload(harmony);
            PatchCardCopy(harmony);
            PatchRenderTextureCtors(harmony);

            if (!PatcherPlugin.KPlugPerfProfileMethods.Value) return;

            bool coroutines = PatcherPlugin.KPlugPerfProfileCoroutines.Value;
            var coroutineOwners = new HashSet<string> { "Dealer", "SpotManager", "SpotCtrl", "AI_Public", "AI_Buddy", "AI_Main", "InviteUI" };
            string[] updateNames = { "Update", "LateUpdate", "FixedUpdate", "OnGUI" };

            // 動作中のモードの kPlug だけを対象にする。
            // 通常モードで kPlugVR の型（基底型などを解決できない）に IsAssignableFrom / GetMethod / GetMethodBody を行うと、
            // Mono が例外ではなくネイティブのアクセス違反で落ちる（v1.7.1 でタイトル画面のクラッシュとして発生。
            // error.log: "mono.dll caused an Access Violation"）。
            string wanted = IsVRProcess() ? "kPlugVR" : "kPlug";
            PatcherPlugin.Logger.LogInfo($"{Tag} 計測対象のメソッドを {wanted} から列挙します。");

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an = assembly.GetName().Name;
                if (an != wanted) continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                if (PatcherPlugin.KPlugPerfTraceSwapSection.Value) PatchSwapSectionTargets(harmony, types);

                foreach (var t in types)
                {
                    try
                    {
                        if (t == null || t.IsGenericTypeDefinition) continue;

                        if (typeof(MonoBehaviour).IsAssignableFrom(t))
                        {
                            foreach (var name in updateNames)
                            {
                                var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                                    null, Type.EmptyTypes, null);
                                if (m != null && !m.IsAbstract && m.GetMethodBody() != null) _patchQueue.Enqueue(m);
                            }
                        }

                        // 招待関係のコルーチン（コンパイラが生成する入れ子クラスの MoveNext）
                        if (coroutines && t.IsNested && t.DeclaringType != null && coroutineOwners.Contains(t.DeclaringType.Name)
                            && t.Name.StartsWith("<") && typeof(System.Collections.IEnumerator).IsAssignableFrom(t))
                        {
                            var mn = t.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                            if (mn != null && mn.GetMethodBody() != null) _coroutineQueue.Enqueue(mn);
                        }
                    }
                    catch { }
                }
            }

            // R3: ゲーム本体のキャラごとの更新処理（Assembly-CSharp。どのモードでも読める）
            if (PatcherPlugin.KPlugPerfProfileGame.Value)
            {
                var gameTypes = new[] { typeof(DynamicBone), typeof(DynamicBone_Ver01), typeof(DynamicBone_Ver02), typeof(Manager.Character),
                    typeof(FaceBlendShape), typeof(EyeLookController), typeof(NeckLookControllerVer2),
                    typeof(ChaClothesComponent), typeof(ChaCustomHairComponent), typeof(ChaAccessoryComponent) };
                foreach (var gt in gameTypes)
                {
                    foreach (var name in updateNames)
                    {
                        try
                        {
                            var m = gt.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                            if (m != null && !m.IsAbstract && m.GetMethodBody() != null) { _patchQueue.Enqueue(m); _gameMethods.Add(m); }
                        }
                        catch { }
                    }
                }
            }

            PatcherPlugin.Logger.LogInfo($"{Tag} {_patchQueue.Count} メソッド（うちゲーム本体 {_gameMethods.Count}）とコルーチン {_coroutineQueue.Count} 個に処理時間の計測を当てます（1フレーム {PatchesPerFrame} 個ずつ）。");
        }

        /// <summary>VR 版のプロセス（KoikatuVR / Koikatsu Party VR）かどうか。</summary>
        private static bool IsVRProcess()
        {
            try { return BepInEx.Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static void HookLogMessages()
        {
            if (_logHooked) return;
            try
            {
                Application.logMessageReceived += OnLogMessage;
                _logHooked = true;
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 例外の件数の記録を開始できませんでした: {ex.Message}");
            }
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception) return;
            try
            {
                _exceptions++;
                string first = stackTrace ?? "";
                int nl = first.IndexOf('\n');
                if (nl >= 0) first = first.Substring(0, nl);
                string key = Short(condition, 40) + " @ " + Short(first.Trim(), 80);
                int n;
                _exceptionKinds.TryGetValue(key, out n);
                _exceptionKinds[key] = n + 1;
            }
            catch { }
        }

        private static void PatchCharaReload(Harmony harmony)
        {
            var targets = new List<MethodBase>();
            try
            {
                var t = typeof(ChaControl);
                AddIfFound(targets, AccessTools.Method(t, "Load", new[] { typeof(bool) }));
                AddIfFound(targets, AccessTools.Method(t, "Reload", new[] { typeof(bool), typeof(bool), typeof(bool), typeof(bool) }));
                AddIfFound(targets, AccessTools.Method(t, "ChangeCoordinateTypeAndReload", new[] { typeof(bool) }));
                AddIfFound(targets, AccessTools.Method(t, "ChangeCoordinateTypeAndReload", new[] { typeof(ChaFileDefine.CoordinateType), typeof(bool) }));
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} ChaControl の読み込みメソッドの取得に失敗: {ex.Message}");
            }

            foreach (var m in targets)
            {
                try
                {
                    harmony.Patch(m, prefix: H(nameof(ReloadPrefix)), finalizer: H(nameof(ReloadFinalizer)));
                }
                catch (Exception ex)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} ChaControl.{m.Name} への適用に失敗: {ex.Message}");
                }
            }
            PatcherPlugin.Logger.LogInfo($"{Tag} キャラの読み込み・再読み込み（ChaControl.{string.Join("/", targets.Select(x => x.Name).Distinct().ToArray())}）の計測を適用しました（{targets.Count} 個）。");
        }

        private static void PatchCardCopy(Harmony harmony)
        {
            var targets = new List<MethodBase>();
            try
            {
                AddIfFound(targets, AccessTools.Method(typeof(ChaFile), "CopyChaFile", new[] { typeof(ChaFile), typeof(ChaFile) }));
                AddIfFound(targets, AccessTools.Method(typeof(ChaFile), "CopyAll", new[] { typeof(ChaFile) }));
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} ChaFile のコピーメソッドの取得に失敗: {ex.Message}");
            }

            int ok = 0;
            foreach (var m in targets)
            {
                try
                {
                    harmony.Patch(m, prefix: H(nameof(CopyPrefix)), finalizer: H(nameof(CopyFinalizer)));
                    ok++;
                }
                catch (Exception ex)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} ChaFile.{m.Name} への適用に失敗: {ex.Message}");
                }
            }
            PatcherPlugin.Logger.LogInfo($"{Tag} カードのコピー（ChaFile.CopyChaFile / CopyAll）の計測を適用しました（{ok} 個）。");
        }

        private static void AddIfFound(List<MethodBase> list, MethodBase m)
        {
            if (m != null) list.Add(m);
        }

        private static HarmonyMethod H(string name)
        {
            return new HarmonyMethod(typeof(KPlugPerfDiagnostics).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
        }

        // ===================== 毎フレーム =====================

        /// <summary>PatcherPlugin.Update から毎フレーム呼ぶ。</summary>
        internal static void Tick()
        {
            if (!Enabled) return;
            try
            {
                ApplyQueuedPatches();

                float now = Time.realtimeSinceStartup;
                if (_windowStart < 0f)
                {
                    StartWindow(now);
                }
                else
                {
                    double ms = Time.unscaledDeltaTime * 1000.0;
                    _frames++;
                    _frameMsSum += ms;
                    if (ms > _frameMsMax) _frameMsMax = ms;
                    if (ms > 33.4) _framesOver33++;
                    if (ms > 66.7) _framesOver66++;

                    if (now - _windowStart >= WindowSeconds) FlushWindow(now);
                }

                CheckInviteSnapshots(now);
                CheckSceneChange(now);
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 集計中に例外: {ex.Message}");
            }
        }

        private static void ApplyQueuedPatches()
        {
            if ((_patchQueue.Count == 0 && _coroutineQueue.Count == 0) || _harmony == null) return;

            for (int i = 0; i < PatchesPerFrame && (_patchQueue.Count > 0 || _coroutineQueue.Count > 0); i++)
            {
                bool coro = _patchQueue.Count == 0;
                var m = coro ? _coroutineQueue.Dequeue() : _patchQueue.Dequeue();
                try
                {
                    bool isGame = _gameMethods.Contains(m);
                    _methodStats[m] = new MethodStat
                    {
                        Name = (isGame ? "[本体]" : "") + (m.DeclaringType != null ? ShortTypeName(m.DeclaringType) + "." + m.Name : m.Name),
                        IsGame = isGame
                    };
                    if (coro) _harmony.Patch(m, prefix: H(nameof(CoroPrefix)), finalizer: H(nameof(CoroFinalizer)));
                    else _harmony.Patch(m, prefix: H(nameof(ProfPrefix)), finalizer: H(nameof(ProfFinalizer)));
                    _patchedCount++;
                }
                catch (Exception ex)
                {
                    _methodStats.Remove(m);
                    _patchFailed++;
                    if (_firstPatchError == null) _firstPatchError = $"{m.DeclaringType?.FullName}.{m.Name}: {ex.Message}";
                }
            }

            if (_patchQueue.Count == 0 && _coroutineQueue.Count == 0)
            {
                PatcherPlugin.Logger.LogInfo($"{Tag} 処理時間の計測を適用しました: 成功 {_patchedCount} / 失敗 {_patchFailed}" +
                    (_firstPatchError != null ? $"（最初の失敗: {_firstPatchError}）" : ""));
            }
        }

        private static void StartWindow(float now)
        {
            _windowStart = now;
            _frames = 0;
            _frameMsSum = 0;
            _frameMsMax = 0;
            _framesOver33 = 0;
            _framesOver66 = 0;
            _gcAtWindowStart = GC.CollectionCount(0);
            _exceptions = 0;
            _exceptionKinds.Clear();
            _reloadByCaller.Clear();
            _copyByCaller.Clear();
            _fixedSteps = 0;
            foreach (var s in _methodStats.Values) { s.Ticks = 0; s.MaxTicks = 0; s.Calls = 0; }
        }

        private static void FlushWindow(float now)
        {
            int females = 0, males = 0;
            try
            {
                foreach (var c in UnityEngine.Object.FindObjectsOfType<ChaControl>())
                {
                    if (c == null || c.chaFile == null) continue;
                    if (c.sex == 0) males++; else females++;
                }
            }
            catch { }

            // キャラがいない場面（タイトルなど）では出さない
            if (females + males > 0 && _frames > 0)
            {
                double secs = now - _windowStart;
                long totalTicks = 0, gameTicks = 0;
                int totalCalls = 0;
                foreach (var s in _methodStats.Values)
                {
                    if (s.IsGame) { gameTicks += s.Ticks; continue; }
                    totalTicks += s.Ticks; totalCalls += s.Calls;
                }

                var top = _methodStats.Values.Where(s => s.Calls > 0).OrderByDescending(s => s.Ticks).Take(12)
                    .Select(s => $"{s.Name} {s.Ticks * TicksToMs / _frames:F2}ms/f(×{(double)s.Calls / _frames:F1}/f, 最大{s.MaxTicks * TicksToMs:F1}ms)")
                    .ToArray();

                var sb = new StringBuilder();
                sb.Append($"{Tag} 直近 {secs:F0}秒: フレーム {_frames}（平均 {_frameMsSum / _frames:F1}ms ≒ {1000.0 * _frames / (_frameMsSum > 0 ? _frameMsSum : 1):F0}fps / " +
                          $"最大 {_frameMsMax:F0}ms / 33ms超 {_framesOver33}回・66ms超 {_framesOver66}回）/ キャラ 女{females} 男{males} / " +
                          $"GC +{GC.CollectionCount(0) - _gcAtWindowStart} / 例外 {_exceptions}件 / {KPlugMemoryDiagnostics.Describe(KPlugMemoryDiagnostics.Take())}");
                sb.Append($" | kPlug 計測分 合計 {totalTicks * TicksToMs / _frames:F2}ms/f（{(double)totalCalls / _frames:F0}回/f）");
                if (_gameMethods.Count > 0) sb.Append($" | ゲーム本体 計測分 合計 {gameTicks * TicksToMs / _frames:F2}ms/f（DynamicBone・表情・視線など）");
                if (top.Length > 0) sb.Append(" | 上位: " + string.Join(", ", top));
                if (_reloadByCaller.Count > 0)
                {
                    sb.Append(" | キャラ再読み込み: " + string.Join(", ", _reloadByCaller.OrderByDescending(kv => kv.Value[1]).Take(6)
                        .Select(kv => $"{kv.Key} {kv.Value[0]:F0}回・計{kv.Value[1]:F0}ms").ToArray()));
                }
                if (_copyByCaller.Count > 0)
                {
                    sb.Append(" | カードのコピー: " + string.Join(", ", _copyByCaller.OrderByDescending(kv => kv.Value[1]).Take(6)
                        .Select(kv => $"{kv.Key} {kv.Value[0]:F0}回・計{kv.Value[1]:F0}ms").ToArray()));
                }
                sb.Append(" | " + DescribePhysicsAndScene());
                if (_exceptionKinds.Count > 0)
                {
                    sb.Append(" | 例外の上位: " + string.Join(", ", _exceptionKinds.OrderByDescending(kv => kv.Value).Take(3)
                        .Select(kv => $"{kv.Key} ×{kv.Value}").ToArray()));
                }
                PatcherPlugin.Logger.LogInfo(sb.ToString());
            }

            StartWindow(now);
        }

        // ===================== 計測パッチ（kPlug のメソッド） =====================

        private static void ProfPrefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        private static void ProfFinalizer(long __state, MethodBase __originalMethod)
        {
            long dt = Stopwatch.GetTimestamp() - __state;
            MethodStat s;
            if (__originalMethod == null || !_methodStats.TryGetValue(__originalMethod, out s)) return;
            s.Ticks += dt;
            s.Calls++;
            if (dt > s.MaxTicks) s.MaxTicks = dt;
        }

        // ===================== 計測パッチ（ChaControl の読み込み） =====================

        private static void ReloadPrefix(ChaControl __instance, MethodBase __originalMethod)
        {
            _reloadDepth++;
            if (_reloadDepth != 1) return;
            try
            {
                _reloadBegin = KPlugMemoryDiagnostics.Take();
                _reloadMethod = __originalMethod != null ? __originalMethod.Name : "?";
                var p = __instance != null && __instance.chaFile != null ? __instance.chaFile.parameter : null;
                _reloadChara = p != null ? p.lastname + p.firstname : "?";
            }
            catch { }
        }

        private static void ReloadFinalizer()
        {
            _reloadDepth = Math.Max(0, _reloadDepth - 1);
            if (_reloadDepth != 0) return;
            try
            {
                var end = KPlugMemoryDiagnostics.Take();
                double ms = _reloadBegin.watch != null ? _reloadBegin.watch.Elapsed.TotalMilliseconds : -1;
                string caller = DescribeCaller();
                string key = _reloadMethod + "←" + caller;

                double[] agg;
                if (!_reloadByCaller.TryGetValue(key, out agg)) _reloadByCaller[key] = agg = new double[2];
                agg[0] += 1;
                agg[1] += ms;

                if (ms >= HeavyReloadMs && _heavyReloadLogged < MaxHeavyReloadLogs)
                {
                    _heavyReloadLogged++;
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag} キャラの{_reloadMethod}: {_reloadChara} 所要 {ms:F0}ms / {KPlugMemoryDiagnostics.Diff(_reloadBegin, end)} / 呼び出し元: {caller}" +
                        (_heavyReloadLogged == MaxHeavyReloadLogs ? "（以降はまとめにだけ出します）" : ""));
                }
            }
            catch { }
        }

        /// <summary>ChaControl・Harmony・本プラグインの枠を除いた呼び出し元を2段。</summary>
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
                if (t == null) continue;
                if (t == typeof(ChaControl) || t == typeof(KPlugPerfDiagnostics)) continue;
                if (t.Namespace != null && (t.Namespace.StartsWith("HarmonyLib") || t.Namespace.StartsWith("MonoMod"))) continue;
                parts.Add(ShortTypeName(t) + "." + m.Name);
                if (parts.Count >= 2) break;
            }
            return parts.Count > 0 ? string.Join(" <- ", parts.ToArray()) : "(不明)";
        }

        // ===================== 3. 招待の前後 =====================

        /// <summary>招待の入口（InviteCardPatch.Prefix / LazyInviteCardList.InviteBuddyPrefix）から、読み直しの前に呼ぶ。</summary>
        internal static void OnInviteStarted(string kind, ChaFileControl guest)
        {
            if (!Enabled) return;
            try
            {
                var p = guest != null ? guest.parameter : null;
                var s = TakeSnapshot($"{kind}#{++_inviteSeq} {(p != null ? p.lastname + p.firstname : "?")}");
                _invites.Add(s);
                PatcherPlugin.Logger.LogInfo($"{Tag} 招待 {s.Label} 開始: {DescribeSnapshot(s)}");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 招待の記録に失敗: {ex.Message}");
            }
        }

        private static void CheckInviteSnapshots(float now)
        {
            if (_invites.Count == 0) return;
            for (int i = _invites.Count - 1; i >= 0; i--)
            {
                var s = _invites[i];
                if (!s.Logged5 && now - s.Time >= 5f) { s.Logged5 = true; LogInviteDelta(s, "5秒後"); }
                if (!s.Logged20 && now - s.Time >= 20f) { s.Logged20 = true; LogInviteDelta(s, "20秒後"); _invites.RemoveAt(i); }
            }
        }

        private static void LogInviteDelta(InviteSnapshot s0, string when)
        {
            var s1 = TakeSnapshot(s0.Label);
            // 20秒以内に次の招待があった場合は、その分も含まれる（ログの「開始」の時刻で判断する）
            PatcherPlugin.Logger.LogInfo(
                $"{Tag} 招待 {s0.Label} の{when}: {KPlugMemoryDiagnostics.Diff(s0.M, s1.M)} / Unity確保 {Delta(s0.UnityAlloc, s1.UnityAlloc)} / " +
                $"キャラ {s0.Charas}→{s1.Charas} / GameObject {s0.GameObjects}→{s1.GameObjects} / Texture2D {s0.Textures}→{s1.Textures} / " +
                $"Material {s0.Materials}→{s1.Materials} / Mesh {s0.Meshes}→{s1.Meshes}");
        }

        private static InviteSnapshot TakeSnapshot(string label)
        {
            var s = new InviteSnapshot { Label = label, Time = Time.realtimeSinceStartup, M = KPlugMemoryDiagnostics.Take(), UnityAlloc = -1 };
            try { s.UnityAlloc = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(); } catch { }
            try
            {
                s.Charas = UnityEngine.Object.FindObjectsOfType<ChaControl>().Length;
                s.GameObjects = Resources.FindObjectsOfTypeAll<GameObject>().Length;
                s.Textures = Resources.FindObjectsOfTypeAll<Texture2D>().Length;
                s.Materials = Resources.FindObjectsOfTypeAll<Material>().Length;
                s.Meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            }
            catch { }
            return s;
        }

        private static string DescribeSnapshot(InviteSnapshot s)
        {
            return $"{KPlugMemoryDiagnostics.Describe(s.M)} / Unity確保 {KPlugMemoryDiagnostics.MB(s.UnityAlloc)} / キャラ {s.Charas} / " +
                   $"GameObject {s.GameObjects} / Texture2D {s.Textures} / Material {s.Materials} / Mesh {s.Meshes}";
        }

        // ===================== 4. カードのコピー（Q1） =====================

        private static void CopyPrefix(MethodBase __originalMethod, object[] __args)
        {
            _copyDepth++;
            if (_copyDepth != 1) return;
            try
            {
                _copyBegin = KPlugMemoryDiagnostics.Take();
                _copyMethod = __originalMethod != null ? __originalMethod.Name : "?";
                // CopyChaFile(dst, src) は2番目、CopyAll(src) は1番目がコピー元
                ChaFile src = null;
                if (__args != null && __args.Length > 0) src = __args[__args.Length - 1] as ChaFile;
                var p = src != null ? src.parameter : null;
                _copyChara = p != null ? p.lastname + p.firstname : "?";
            }
            catch { }
        }

        private static void CopyFinalizer()
        {
            _copyDepth = Math.Max(0, _copyDepth - 1);
            if (_copyDepth != 0) return;
            try
            {
                double ms = _copyBegin.watch != null ? _copyBegin.watch.Elapsed.TotalMilliseconds : -1;
                // CopyAll は頻繁に呼ばれうるので、スタックを調べるのは 1ms 以上かかったものだけにする
                string caller = ms >= 1.0 ? DescribeCaller() : "(1ms未満)";
                string key = _copyMethod + "←" + caller;
                double[] agg;
                if (!_copyByCaller.TryGetValue(key, out agg)) _copyByCaller[key] = agg = new double[2];
                agg[0] += 1;
                agg[1] += ms;

                if (ms >= HeavyReloadMs && _heavyCopyLogged < MaxHeavyReloadLogs)
                {
                    _heavyCopyLogged++;
                    var end = KPlugMemoryDiagnostics.Take();
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag} カードの{_copyMethod}: コピー元 {_copyChara} 所要 {ms:F0}ms / {KPlugMemoryDiagnostics.Diff(_copyBegin, end)} / 呼び出し元: {caller}" +
                        (_heavyCopyLogged == MaxHeavyReloadLogs ? "（以降はまとめにだけ出します）" : ""));
                }
            }
            catch { }
        }

        // ===================== 5. シーン変化時の内訳（Q2） =====================

        private static void CheckSceneChange(float now)
        {
            if (now - _lastSceneCheck < 1f) return;
            _lastSceneCheck = now;
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (_lastScene == null) { _lastScene = scene; return; }
            if (scene == _lastScene) return;
            string from = _lastScene;
            _lastScene = scene;
            LogInventory($"シーン変化 {from} -> {scene}");
        }

        private static void LogInventory(string reason)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append($"{Tag} [内訳] {reason}: ");

                // RenderTexture（名前・大きさ・形式別。容量は幅×高さ×1画素のバイト数＋深度で見積もった値）
                var rts = Resources.FindObjectsOfTypeAll<RenderTexture>();
                var rtGroups = new Dictionary<string, double[]>(); // [数, 見積MB, 生成済みの数]
                double rtTotal = 0;
                foreach (var rt in rts)
                {
                    if (rt == null) continue;
                    double mb = rt.width * (double)rt.height * (BytesPerPixel(rt.format) + rt.depth / 8.0) / 1048576.0;
                    rtTotal += mb;
                    string key = $"{(string.IsNullOrEmpty(rt.name) ? "(名前なし)" : rt.name)} {rt.width}x{rt.height} {rt.format}";
                    double[] g;
                    if (!rtGroups.TryGetValue(key, out g)) rtGroups[key] = g = new double[3];
                    g[0] += 1; g[1] += mb; if (rt.IsCreated()) g[2] += 1;
                }
                sb.Append($"RenderTexture {rts.Length}個・見積 {rtTotal:F0}MB（上位: " +
                    string.Join(", ", rtGroups.OrderByDescending(kv => kv.Value[1]).Take(12)
                        .Select(kv => $"{kv.Key} ×{kv.Value[0]:F0}(生成済{kv.Value[2]:F0})={kv.Value[1]:F0}MB").ToArray()) + "）");

                // Texture2D（名前別。非圧縮 RGBA32 と仮定した見積。圧縮テクスチャは過大に出る）
                var texs = Resources.FindObjectsOfTypeAll<Texture2D>();
                var texGroups = new Dictionary<string, double[]>();
                double texTotal = 0;
                foreach (var tx in texs)
                {
                    if (tx == null) continue;
                    double mb = tx.width * (double)tx.height * 4 * (tx.mipmapCount > 1 ? 1.33 : 1.0) / 1048576.0;
                    texTotal += mb;
                    string key = string.IsNullOrEmpty(tx.name) ? $"(名前なし {tx.width}x{tx.height})" : tx.name;
                    double[] g;
                    if (!texGroups.TryGetValue(key, out g)) texGroups[key] = g = new double[2];
                    g[0] += 1; g[1] += mb;
                }
                sb.Append($" / Texture2D {texs.Length}個・見積 {texTotal:F0}MB（上位: " +
                    string.Join(", ", texGroups.OrderByDescending(kv => kv.Value[1]).Take(8)
                        .Select(kv => $"{kv.Key} ×{kv.Value[0]:F0}={kv.Value[1]:F0}MB").ToArray()) + "）");

                // Manager.Character の登録（kPlug は招待キャラを CreateFemale で登録し、Destroy だけで片付ける）
                try
                {
                    var dict = Singleton<Manager.Character>.Instance != null ? Singleton<Manager.Character>.Instance.dictEntryChara : null;
                    if (dict != null)
                    {
                        int alive = 0, dead = 0;
                        var deadNames = new List<string>();
                        foreach (var kv in dict)
                        {
                            if (kv.Value) { alive++; continue; }
                            dead++;
                            if (deadNames.Count < 8)
                            {
                                string n = "?";
                                try
                                {
                                    var cf = ((ChaInfo)kv.Value).chaFile; // 破棄済みでも C# 側のフィールドは読める
                                    if (cf != null && cf.parameter != null) n = cf.parameter.lastname + cf.parameter.firstname;
                                }
                                catch { }
                                deadNames.Add(n);
                            }
                        }
                        sb.Append($" / Character の登録 {dict.Count}件（生存 {alive} / 破棄済みで残っている {dead}" +
                            (deadNames.Count > 0 ? ": " + string.Join(", ", deadNames.ToArray()) : "") + "）");
                    }
                }
                catch (Exception ex)
                {
                    sb.Append($" / Character の登録: 取得失敗 {ex.Message}");
                }

                sb.Append($" / {KPlugMemoryDiagnostics.Describe(KPlugMemoryDiagnostics.Take())}");
                PatcherPlugin.Logger.LogInfo(sb.ToString());
                PatcherPlugin.Logger.LogInfo($"{Tag} [RenderTexture の作成元] {reason}: {DescribeLiveRenderTexturesByCaller()}");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 内訳の記録に失敗: {ex.Message}");
            }
        }

        private static double BytesPerPixel(RenderTextureFormat f)
        {
            switch (f)
            {
                case RenderTextureFormat.ARGBHalf: case RenderTextureFormat.RGFloat: return 8;
                case RenderTextureFormat.ARGBFloat: return 16;
                case RenderTextureFormat.RHalf: return 2;
                case RenderTextureFormat.R8: return 1;
                case RenderTextureFormat.Depth: case RenderTextureFormat.Shadowmap: return 0;
                default: return 4;
            }
        }

        // ===================== 6. 物理演算とシーンの構成（Q3） =====================

        /// <summary>PatcherPlugin.FixedUpdate から呼ぶ。</summary>
        internal static void OnFixedUpdate()
        {
            _fixedSteps++;
        }

        private static string DescribePhysicsAndScene()
        {
            try
            {
                int rb = 0, rbDynamic = 0, col = 0, db = 0, db2 = 0, smr = 0, smrVisible = 0, lights = 0, shadowLights = 0, cams = 0;
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Rigidbody>()) { rb++; if (!r.isKinematic && !r.IsSleeping()) rbDynamic++; }
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider>()) { if (c.enabled) col++; }
                foreach (var d in UnityEngine.Object.FindObjectsOfType<DynamicBone>()) { if (d.enabled) db++; }
                foreach (var d in UnityEngine.Object.FindObjectsOfType<DynamicBone_Ver02>()) { if (d.enabled) db2++; }
                foreach (var s in UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>()) { if (s.enabled) { smr++; if (s.isVisible) smrVisible++; } }
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>()) { if (l.enabled) { lights++; if (l.shadows != LightShadows.None) shadowLights++; } }
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>()) { if (c.enabled) cams++; }

                return $"物理: FixedUpdate {(double)_fixedSteps / Math.Max(1, _frames):F2}回/f（固定間隔 {Time.fixedDeltaTime * 1000:F0}ms・上限 {Time.maximumDeltaTime * 1000:F0}ms）" +
                       $" / Rigidbody {rb}（動作中 {rbDynamic}）/ Collider {col} / DynamicBone {db} / DynamicBone_Ver02 {db2}" +
                       $" / SkinnedMeshRenderer {smr}（画面内 {smrVisible}）/ Light {lights}（影あり {shadowLights}）/ Camera {cams}";
            }
            catch (Exception ex)
            {
                return "物理・構成: 取得失敗 " + ex.Message;
            }
        }

        // ===================== 7. RenderTexture の作成元（R1） =====================

        private static void PatchRenderTextureCtors(Harmony harmony)
        {
            int ok = 0;
            foreach (var c in typeof(RenderTexture).GetConstructors(BindingFlags.Instance | BindingFlags.Public))
            {
                try
                {
                    harmony.Patch(c, postfix: H(nameof(RenderTextureCtorPostfix)));
                    ok++;
                }
                catch (Exception ex)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} RenderTexture のコンストラクタ（{c}）への適用に失敗: {ex.Message}");
                }
            }
            PatcherPlugin.Logger.LogInfo($"{Tag} RenderTexture の作成元の記録を適用しました（コンストラクタ {ok} 個。1024×1024 以上のみ記録）。");
        }

        private static void RenderTextureCtorPostfix(RenderTexture __instance)
        {
            try
            {
                if (__instance == null) return;
                int w = __instance.width, h = __instance.height;
                if ((long)w * h < 1024L * 1024L) return;
                if (!_rtSeen.Add(__instance.GetInstanceID())) return;

                double mb = w * (double)h * BytesPerPixel(__instance.format) / 1048576.0;
                string caller = DescribeCallerWithAssembly();
                _rtRecords.Add(new RtRecord { Rt = new WeakReference(__instance), Caller = caller, Mb = mb });

                double[] agg;
                if (!_rtCreatedByCaller.TryGetValue(caller, out agg)) _rtCreatedByCaller[caller] = agg = new double[2];
                agg[0] += 1;
                agg[1] += mb;
            }
            catch { }
        }

        /// <summary>生きている RenderTexture を作成元別に集計した文字列。死んだ記録はここで捨てる。</summary>
        private static string DescribeLiveRenderTexturesByCaller()
        {
            var live = new Dictionary<string, double[]>();
            int liveCount = 0;
            double liveMb = 0;
            for (int i = _rtRecords.Count - 1; i >= 0; i--)
            {
                var r = _rtRecords[i];
                var rt = r.Rt.Target as RenderTexture;
                if (rt == null || !rt)   // C# 側で回収済み、または Unity 側で破棄済み
                {
                    _rtRecords.RemoveAt(i);
                    continue;
                }
                liveCount++;
                liveMb += r.Mb;
                double[] g;
                if (!live.TryGetValue(r.Caller, out g)) live[r.Caller] = g = new double[2];
                g[0] += 1; g[1] += r.Mb;
            }
            _rtSeen.Clear();
            foreach (var r in _rtRecords) { var rt = r.Rt.Target as RenderTexture; if (rt) _rtSeen.Add(rt.GetInstanceID()); }

            return $"記録中の1024以上の RenderTexture のうち生存 {liveCount}個・{liveMb:F0}MB（作成元別: " +
                   string.Join(", ", live.OrderByDescending(kv => kv.Value[1]).Take(8)
                       .Select(kv => $"{kv.Key} ×{kv.Value[0]:F0}={kv.Value[1]:F0}MB").ToArray()) + "）" +
                   " / 作成の累計（作成元別）: " +
                   string.Join(", ", _rtCreatedByCaller.OrderByDescending(kv => kv.Value[1]).Take(6)
                       .Select(kv => $"{kv.Key} ×{kv.Value[0]:F0}={kv.Value[1]:F0}MB").ToArray());
        }

        /// <summary>UnityEngine・Harmony・本プラグインの枠を除いた呼び出し元を3段。先頭にアセンブリ名を付ける。</summary>
        private static string DescribeCallerWithAssembly()
        {
            var frames = new StackTrace(2, false).GetFrames();
            if (frames == null) return "(不明)";
            var parts = new List<string>();
            foreach (var f in frames)
            {
                var m = f.GetMethod();
                if (m == null) continue;
                var t = m.DeclaringType;
                if (t == null) continue;
                if (t == typeof(KPlugPerfDiagnostics) || t == typeof(RenderTexture)) continue;
                string ns = t.Namespace ?? "";
                if (ns.StartsWith("HarmonyLib") || ns.StartsWith("MonoMod")) continue;
                if (parts.Count == 0)
                {
                    if (ns.StartsWith("UnityEngine")) continue;
                    parts.Add("[" + t.Assembly.GetName().Name + "] " + ShortTypeName(t) + "." + m.Name);
                }
                else
                {
                    parts.Add(ShortTypeName(t) + "." + m.Name);
                }
                if (parts.Count >= 3) break;
            }
            return parts.Count > 0 ? string.Join(" <- ", parts.ToArray()) : "(不明)";
        }

        // ===================== 10. 遅い呼び出し（S1） =====================

        private static void PatchSwapSectionTargets(Harmony harmony, Type[] kplugTypes)
        {
            var targets = new List<MethodBase>();
            foreach (var pair in SwapSectionTargets)
            {
                var type = kplugTypes.FirstOrDefault(x => x != null && x.Name == pair[0] && !x.IsNested);
                if (type == null) continue;
                try
                {
                    foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (m.Name == pair[1] && !m.IsAbstract && !m.IsGenericMethodDefinition && m.GetMethodBody() != null) targets.Add(m);
                    }
                }
                catch { }
            }
            try
            {
                var voiceInit = AccessTools.Method(typeof(HVoiceCtrl), "Init", new[] { typeof(string), typeof(string), typeof(string) });
                if (voiceInit != null) targets.Add(voiceInit);
            }
            catch { }

            int ok = 0;
            foreach (var m in targets)
            {
                try
                {
                    _slowCallNames[m] = (m.DeclaringType == typeof(HVoiceCtrl) ? "[本体]" : "") + ShortTypeName(m.DeclaringType) + "." + m.Name;
                    harmony.Patch(m, prefix: H(nameof(SlowPrefix)), finalizer: H(nameof(SlowFinalizer)));
                    ok++;
                }
                catch (Exception ex)
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} {m.DeclaringType?.Name}.{m.Name} への計測の適用に失敗: {ex.Message}");
                }
            }
            PatcherPlugin.Logger.LogInfo($"{Tag} 交代の区間の処理 {ok} 個に計測を当てました（1回 {SlowCallLogMs:F0}ms 以上を1件ずつ出します）。");
        }

        private static void SlowPrefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        private static void SlowFinalizer(long __state, MethodBase __originalMethod)
        {
            double ms = (Stopwatch.GetTimestamp() - __state) * TicksToMs;
            if (ms < SlowCallLogMs || _slowCallLogged >= MaxSlowCallLogs) return;
            try
            {
                _slowCallLogged++;
                string name;
                if (__originalMethod == null || !_slowCallNames.TryGetValue(__originalMethod, out name)) name = __originalMethod?.Name ?? "?";
                PatcherPlugin.Logger.LogInfo($"{Tag} 遅い呼び出し {name}: {ms:F0}ms / 呼び出し元: {DescribeCaller()}" +
                    (_slowCallLogged == MaxSlowCallLogs ? "（以降は出しません）" : ""));
            }
            catch { }
        }

        // ===================== 8. コルーチンの区間（R2） =====================

        private static void CoroPrefix(object __instance, out CoroState __state)
        {
            __state = new CoroState { Start = Stopwatch.GetTimestamp(), State = ReadCoroState(__instance) };
        }

        private static void CoroFinalizer(object __instance, CoroState __state, MethodBase __originalMethod)
        {
            if (__state == null) return;
            long dt = Stopwatch.GetTimestamp() - __state.Start;
            MethodStat s;
            if (__originalMethod != null && _methodStats.TryGetValue(__originalMethod, out s))
            {
                s.Ticks += dt;
                s.Calls++;
                if (dt > s.MaxTicks) s.MaxTicks = dt;

                double ms = dt * TicksToMs;
                if (ms >= CoroLogMs && _coroLogged < MaxCoroLogs)
                {
                    _coroLogged++;
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag} コルーチン {s.Name}: 状態 {__state.State}→{ReadCoroState(__instance)} の1回の実行が {ms:F0}ms" +
                        (_coroLogged == MaxCoroLogs ? "（以降は出しません）" : ""));
                }
            }
        }

        /// <summary>コンパイラが生成したコルーチンの状態番号（<>1__state / $PC）。読めなければ -99。</summary>
        private static int ReadCoroState(object instance)
        {
            if (instance == null) return -99;
            try
            {
                var t = instance.GetType();
                FieldInfo f;
                if (!_coroStateFields.TryGetValue(t, out f))
                {
                    f = t.GetField("<>1__state", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? t.GetField("$PC", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _coroStateFields[t] = f;
                }
                return f != null ? (int)f.GetValue(instance) : -99;
            }
            catch { return -99; }
        }

        // ===================== 補助 =====================

        private static string Delta(long a, long b)
        {
            if (a < 0 || b < 0) return "?";
            double d = (b - a) / 1048576.0;
            return $"{KPlugMemoryDiagnostics.MB(a)}→{KPlugMemoryDiagnostics.MB(b)}({(d >= 0 ? "+" : "")}{d:F0})";
        }

        private static string ShortTypeName(Type t)
        {
            // コルーチンの入れ子型 "<IE_Invite>c__Iterator3" は、外側の型名と合わせて短く出す
            if (t.IsNested && t.DeclaringType != null && t.Name.StartsWith("<"))
            {
                int end = t.Name.IndexOf('>');
                return t.DeclaringType.Name + "." + (end > 1 ? t.Name.Substring(1, end - 1) : t.Name);
            }
            return t.Name;
        }

        private static string Short(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
