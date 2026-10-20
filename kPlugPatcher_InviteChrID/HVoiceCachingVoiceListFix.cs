using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// HVoiceCaching 0.3 を入れていると、3P（女の子2人）の Hシーンがロード画面のまま進まなくなる問題の回避策。
    ///
    /// 【原因】ゲーム本体の <c>HVoiceCtrl.LoadVoice</c> は、<c>Init</c> が用意した空の辞書を前提に
    /// <c>dicVoiceIntos[_main][_mode].Add(0, ...)</c> を ContainsKey なしで行う。
    /// HVoiceCaching 0.3 の <c>LoadVoiceListPre</c> は2人分のキャッシュを順に読むが、
    /// <b>1人目をキャッシュから読み込んだ後で2人目のキャッシュが無いと、そのまま原型へ処理を戻す。</b>
    /// 1人目の <c>*_Voice_0.cache</c> を読めなければ原型に戻すのは正しいが、2人目の <c>*_Voice_1.cache</c> が無いときも
    /// 1人目の辞書を埋めたまま原型に戻す。
    /// 原型は1人目から読み直すので、最初の <c>Add</c> で ArgumentException（同じキーが既にある）になる。
    /// 例外は <c>HSceneProc.Start</c> / <c>VRHScene.Start</c> まで抜けてHシーンの準備が止まる。
    /// 例外で HVoiceCaching の Postfix も動かず <c>*_Voice_1.cache</c> が作られないため、同じ組み合わせでは毎回止まる。
    /// <c>*_Voice_0.cache</c> はほとんどの性格で既にあるので、実質 3P はほぼ必ず止まる。
    ///
    /// 【対策】<c>LoadVoiceList</c> に HVoiceCaching の後で動く Prefix を当て、
    /// <b>原型が実行されるとき（<c>__runOriginal</c> が true）だけ</b>、2人分の辞書を <c>Init</c> と同じ状態
    /// （空にして、モード 0〜10 の空の辞書を入れ直す）に戻す。原型はこのあと2人分を最初から読むので、
    /// 読み込まれる中身は HVoiceCaching が無い場合と同じになる。原型が最後まで動けば HVoiceCaching の Postfix が
    /// 2人分のキャッシュを保存し、次回から同じ組み合わせは2人ともキャッシュから読まれる。
    /// HVoiceCaching が2人分をキャッシュから読んだとき（原型が動かないとき）は何もしない。
    ///
    /// 【設計上の注意】
    /// - kPlug ではなく**ゲーム本体(Assembly-CSharp)**へのパッチ（<see cref="HVoiceCachingShortBreathFix"/> と同じ扱い）。
    ///   設定 <c>EnableHVoiceCachingVoiceListFix</c> で個別に無効化できる。
    /// - HVoiceCaching が入っていない環境では当てない。
    /// - 実行順は <c>after = HVoiceCaching</c> と <c>Priority.Last</c> の両方で指定する。
    /// - 例外を外へ出さない。失敗したら何もせず原型に任せる（従来と同じ動作になるだけ）。
    /// </summary>
    internal static class HVoiceCachingVoiceListFix
    {
        private const string Tag = "[HVoiceCachingFix]";

        /// <summary>原型のモードの数（<c>Init</c> と <c>LoadVoiceList</c> の 0〜10）。</summary>
        private const int ModeCount = 11;

        private static bool _applied;
        private static bool _failureLogged;

        /// <summary>中身の入った辞書を空にした回数（＝例外を回避した回数）。ログ用。</summary>
        private static int _resetCount;

        internal static void TryApply(Harmony harmony)
        {
            if (_applied) return;
            _applied = true;

            try
            {
                if (!Chainloader.PluginInfos.ContainsKey(HVoiceCachingShortBreathFix.HVoiceCachingGuid))
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} HVoiceCaching が見つからないため、3P の声リストの回避策は適用しません（不要）。");
                    return;
                }

                var method = AccessTools.Method(typeof(HVoiceCtrl), "LoadVoiceList",
                    new[] { typeof(string), typeof(string), typeof(string) });
                if (method == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} HVoiceCtrl.LoadVoiceList(string,string,string) が見つからないため、3P の声リストの回避策を適用できません。");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(HVoiceCachingVoiceListFix)
                    .GetMethod(nameof(LoadVoiceListPrefix), BindingFlags.NonPublic | BindingFlags.Static))
                {
                    priority = Priority.Last,
                    after = new[] { HVoiceCachingShortBreathFix.HVoiceCachingGuid }
                };
                harmony.Patch(method, prefix: prefix);

                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} HVoiceCtrl.LoadVoiceList に回避策を適用しました" +
                    "（HVoiceCaching がキャッシュを途中まで読んでから原型に戻したとき、辞書を空にして 3P のロードが止まらないようにします）。" +
                    $"Prefix の登録: {HVoiceCachingShortBreathFix.DescribePrefixes(method)}");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 3P の声リストの回避策の適用に失敗: {ex.Message}");
            }
        }

        /// <summary>
        /// 原型が実行されるときだけ、2人分の声の辞書を <c>Init</c> 直後と同じ状態に戻す。
        /// 戻り値は void（原型の実行は止めない）。
        /// </summary>
        private static void LoadVoiceListPrefix(HVoiceCtrl __instance, bool __runOriginal, string _chaFolder, string _chaFolder1)
        {
            if (!__runOriginal) return;   // HVoiceCaching が2人分をキャッシュから読んだ（原型は動かない）

            try
            {
                var intos = __instance?.dicVoiceIntos;
                var ptns = __instance?.dicVoicePtn;
                if (intos == null || ptns == null) return;

                // 中身が入っていた＝HVoiceCaching が1人目だけキャッシュから読んで原型に戻したケース。
                // これが例外になっていたので、その時だけログに出す（2人とも空なら Init の直後と同じなので何もしない）。
                bool filled = false;
                for (int i = 0; i < intos.Length && i < ptns.Length; i++)
                {
                    if (HasEntries(intos[i]) || HasEntries(ptns[i]))
                    {
                        filled = true;
                        break;
                    }
                }
                if (!filled) return;

                for (int i = 0; i < intos.Length && i < ptns.Length; i++)
                {
                    if (intos[i] == null || ptns[i] == null) continue;
                    intos[i].Clear();
                    ptns[i].Clear();
                    for (int k = 0; k < ModeCount; k++)
                    {
                        intos[i].Add(k, new Dictionary<int, Dictionary<int, HVoiceCtrl.VoiceInfo>>());
                        ptns[i].Add(k, new Dictionary<int, HVoiceCtrl.VoicePtn>());
                    }
                }

                _resetCount++;
                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} 3P の声リストで HVoiceCaching がキャッシュを途中まで読んでから原型に戻したため、辞書を空にしました" +
                    $"（{_resetCount}回目、_chaFolder={_chaFolder}, _chaFolder1={_chaFolder1}）。");
            }
            catch (Exception ex)
            {
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    PatcherPlugin.Logger.LogWarning($"{Tag} 声の辞書を空にできませんでした（原型に任せます）: {ex.Message}");
                }
            }
        }

        /// <summary>モードごとの辞書のどれかに中身があるか。</summary>
        private static bool HasEntries<T>(Dictionary<int, Dictionary<int, T>> byMode)
        {
            if (byMode == null) return false;
            foreach (var kv in byMode)
            {
                if (kv.Value != null && kv.Value.Count > 0) return true;
            }
            return false;
        }
    }
}
