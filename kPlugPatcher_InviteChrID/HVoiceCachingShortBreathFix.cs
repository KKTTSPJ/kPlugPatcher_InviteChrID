using System;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// HVoiceCaching 0.3 が、kPlug のキャラ交代のたびに <c>HVoiceCtrl.Init</c> の中で例外を投げ、
    /// 交代処理（<c>AI_Public.IE_SwapGirls</c>）の後半が実行されなくなる問題の回避策。
    ///
    /// 【原因】ゲーム本体の <c>HVoiceCtrl.LoadShortBreathList</c> は、最初に短い吐息の辞書を空にしてから読み込む。
    /// 2人分（i = 0, 1）それぞれ、<c>dicShortBreathIntos[i]</c> を Clear してから、その性格の短い吐息を読む。
    /// HVoiceCaching 0.3 はこのメソッドを Prefix で丸ごと置き換えているが、空にする手順が抜けている。
    /// 置き換え後の処理は、2人分の <c>dicShortBreathIntos[i]</c> を Clear せずに、キー 0 から順に Add していく。
    /// Hシーン開始時の1回目は辞書が空なので通るが、同じ <c>HVoiceCtrl</c> で2回目の <c>Init</c> が走ると
    /// キャッシュの有無に関係なく ArgumentException（同じキーが既にある）になる。
    /// 同じHシーンで <c>Init</c> を呼び直すのは kPlug のキャラ交代だけなので、交代のたびに必ず起きる
    /// （VR の実機ログ2本で交代 7/7 回・27/27 回。交代の無いログでは0回）。
    ///
    /// 【影響】例外は <c>IE_SwapGirls</c> まで抜けるため、<c>Init</c> より後ろの処理がすべて飛ぶ。
    /// 例外は <c>Init</c> の呼び出しで出るので、その後ろの次の処理が実行されない:
    /// <c>Core.dealer.heroineVoice</c> と <c>voiceName</c> の更新、声の停止の後の <c>InSwap = false</c>、2人目の声の交代。
    /// - kPlug が「どのキャラがどの性格の声か」を覚えておく情報（<c>voiceName</c> / <c>dealer.heroineVoice</c>）が
    ///   カードの入れ替えに追従しなくなる。一度ずれたキャラがメインに戻ると別の性格の声リストが読み込まれ、
    ///   招待キャラの声（kPlug が <c>voiceName</c> から直接鳴らす）も別の性格になる。
    ///   KK_AlternativeVoice では、発声キャラと別の性格のセリフで音声を自動生成していた。
    /// - 例外は最初の <c>Add</c> で出るため、短い吐息の辞書は空にされず、Hシーン開始時の性格のものが
    ///   その後に交代したどのキャラにも使われ続ける（実機ログ3本で確認）。
    /// - 交代で外に出たキャラの <c>InSwap</c> が true のまま残り、そのキャラの思考（<c>AI_Public</c> の
    ///   <c>flag2</c> 判定）や、<c>WaitUntil(() =&gt; !ai.InSwap)</c> で待つコルーチンが止まる。
    ///
    /// 【対策】<c>LoadShortBreathList</c> に HVoiceCaching より先に動く Prefix を当て、本来のメソッドと同じく
    /// <c>dicShortBreathIntos[0]</c> と <c>[1]</c> を空にする。その後の処理（HVoiceCaching のキャッシュ読み込み、
    /// またはゲーム本体の読み込み）は一切変えない。HVoiceCaching がキャッシュを使わずに原型へ処理を戻した場合も、
    /// 原型が同じ辞書を再度空にするだけなので結果は変わらない。
    ///
    /// 【設計上の注意】
    /// - kPlug ではなく**ゲーム本体(Assembly-CSharp)**へのパッチ（<see cref="ParentObjectCtrlStaleRefFix"/> と同じ扱い）。
    ///   設定 <c>EnableHVoiceCachingShortBreathFix</c> で個別に無効化できる。
    /// - HVoiceCaching が入っていない環境では当てない（原型が自分で空にするため不要）。
    /// - 実行順は <c>[HarmonyBefore(HVoiceCaching)]</c> 相当と <c>Priority.First</c> の両方で指定する。
    /// - 例外を外へ出さない。失敗したら何もせず後続に任せる（従来と同じ動作になるだけ）。
    /// </summary>
    internal static class HVoiceCachingShortBreathFix
    {
        private const string Tag = "[HVoiceCachingFix]";
        internal const string HVoiceCachingGuid = "picolet21.koikatsu.HVoiceCaching";

        private static bool _applied;
        private static bool _failureLogged;

        /// <summary>空にした回数（＝回避した交代の回数）。ログ用。</summary>
        private static int _clearCount;

        internal static void TryApply(Harmony harmony)
        {
            if (_applied) return;
            _applied = true;

            try
            {
                if (!Chainloader.PluginInfos.ContainsKey(HVoiceCachingGuid))
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} HVoiceCaching が見つからないため、回避策は適用しません（不要）。");
                    return;
                }

                var method = AccessTools.Method(typeof(HVoiceCtrl), "LoadShortBreathList",
                    new[] { typeof(string), typeof(string), typeof(string) });
                if (method == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} HVoiceCtrl.LoadShortBreathList(string,string,string) が見つからないため、回避策を適用できません。");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(HVoiceCachingShortBreathFix)
                    .GetMethod(nameof(LoadShortBreathListPrefix), BindingFlags.NonPublic | BindingFlags.Static))
                {
                    priority = Priority.First,
                    before = new[] { HVoiceCachingGuid }
                };
                harmony.Patch(method, prefix: prefix);

                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} HVoiceCtrl.LoadShortBreathList に回避策を適用しました" +
                    $"（HVoiceCaching より先に短い吐息の辞書を空にし、kPlug のキャラ交代で例外が出ないようにします）。" +
                    $"Prefix の登録: {DescribePrefixes(method)}");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// 本来の <c>LoadShortBreathList</c> の最初の手順（2人分の辞書を空にする）だけを先に行う。
        /// 戻り値は void（後続の Prefix と原型の実行は止めない）。
        /// </summary>
        private static void LoadShortBreathListPrefix(HVoiceCtrl __instance, string _chaFolder, string _chaFolder1)
        {
            try
            {
                var dics = __instance?.dicShortBreathIntos;
                if (dics == null) return;

                // 空でない辞書があった＝同じ HVoiceCtrl で2回目以降の読み込み（kPlug の交代）。
                // これが HVoiceCaching の例外になっていたケースなので、その時だけログに出す。
                int nonEmpty = 0;
                for (int i = 0; i < dics.Length; i++)
                {
                    if (dics[i] == null) continue;
                    if (dics[i].Count > 0) nonEmpty++;
                    dics[i].Clear();
                }

                if (nonEmpty > 0)
                {
                    _clearCount++;
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag} 声リストの読み直しを検出しました（{_clearCount}回目、_chaFolder={_chaFolder}, _chaFolder1={_chaFolder1}）。" +
                        "短い吐息の辞書を空にしてから HVoiceCaching に渡します。");
                }
            }
            catch (Exception ex)
            {
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    PatcherPlugin.Logger.LogWarning($"{Tag} 辞書を空にできませんでした（後続の処理に任せます）: {ex.Message}");
                }
            }
        }

        /// <summary>適用確認用。登録されている Prefix の持ち主と優先度を並べる。</summary>
        internal static string DescribePrefixes(MethodBase method)
        {
            try
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) return "(なし)";
                var parts = new System.Collections.Generic.List<string>();
                foreach (var p in info.Prefixes)
                {
                    string before = (p.before != null && p.before.Length > 0) ? $", before={string.Join("/", p.before)}" : "";
                    parts.Add($"{p.owner}(priority={p.priority}{before})");
                }
                return parts.Count > 0 ? string.Join(" / ", parts.ToArray()) : "(なし)";
            }
            catch (Exception ex)
            {
                return $"(取得失敗: {ex.Message})";
            }
        }
    }
}
