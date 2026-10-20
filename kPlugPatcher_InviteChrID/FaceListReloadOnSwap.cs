using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// kPlug のキャラ交代で声のリストだけが読み直され、表情のリストが最初のメインキャラの性格のまま残る問題の対策。
    ///
    /// 【原因】Hシーン開始時、ゲームは表情と声のリストを性格ごとに読み込む（通常版 <c>HSceneProc</c>、VR版 <c>VRHScene</c> とも同じ順）。
    /// 先に <c>FaceListCtrl.LoadText</c> でヒロイン（3P なら2人目も）の性格の表情リスト（HFaceList）を読み、
    /// 次に <c>HVoiceCtrl.Init</c> で声・吐息・短い吐息のリストを読む。
    /// kPlug の交代（<c>AI_Public.IE_SwapGirls</c>）は、性格が変わるとき <c>voice.Init</c> だけを呼び直し、
    /// <c>FaceListCtrl.LoadText</c> を呼ばない（3.56 / 3.63、通常版 / VR版とも）。
    /// そのため交代後のキャラは「新しい性格のセリフ」に「最初のメインの性格の表情番号表」を当てて顔を作る。
    /// 表情番号の意味は性格ごとに違うので、セリフと合わない顔になる。番号が表に無ければ顔は変わらない。
    ///
    /// 【対策】<c>HVoiceCtrl.Init</c> が呼ばれたとき、その HVoiceCtrl の <c>faceLists[0]</c> / <c>[1]</c> が
    /// 今どの性格の表を持っているかを調べ、Init に渡された性格と違えば <c>LoadText</c> で読み直す。
    /// - 表がどの性格かはゲームが覚えていないため、<c>LoadText</c> の Prefix で「最後に読んだ性格」を記録しておく。
    /// - Hシーン開始時は表情の方が先に同じ性格で読まれているので、比較が一致して何もしない（読み込みは増えない）。
    /// - 表情を一度も読んでいない <c>FaceListCtrl</c>（記録が無いもの）には触らない。
    /// - 読み直した結果が空（その性格の表情リストが無い）なら、前の性格で読み直して元に戻す。
    ///
    /// 【比較に使う性格名】Init の引数は KK_AlternativeVoice の Prefix（Priority.Last）がカスタム性格を
    /// 元の性格へ振り替える。<c>LoadText</c> の引数は振り替え前の名前なので、比べるのも振り替え前の名前にそろえる。
    /// そのため Init の引数は Priority.First の Prefix で控えておき、読み直しにもその名前を使う
    /// （振り替えは KK_AlternativeVoice の <c>FaceListCtrl.LoadInfo</c> パッチが従来どおり行う）。
    ///
    /// 【Finalizer にする理由】Init の中で他プラグイン（HVoiceCaching 0.3）が例外を投げると Postfix は実行されない
    /// （<see cref="HVoiceCachingShortBreathFix"/>）。その場合も声と吐息のリストは新しい性格で読み終わっているので、
    /// 表情も追従させる。例外そのものは握りつぶさず、そのまま呼び出し元へ返す。
    ///
    /// 【影響】kPlug は招待キャラの表情にも <c>voice.faceLists[0]</c> を使う（<c>FaceCtrl</c> 系の <c>SetFace</c>）。
    /// これまでは招待キャラの表情も最初のメインの性格の表だったが、対策後は「今のメインの性格の表」になる。
    /// どちらも招待キャラ本人の性格ではない（kPlug の仕様）。
    /// </summary>
    internal static class FaceListReloadOnSwap
    {
        private const string Tag = "[FaceListReload]";

        private static bool _applied;
        private static bool _failureLogged;
        private static int _reloadCount;

        /// <summary>FaceListCtrl ごとの「最後に LoadText で読んだ性格名」。キーは Unity のインスタンスID。</summary>
        private static readonly Dictionary<int, string> _loadedNames = new Dictionary<int, string>();

        internal static void TryApply(Harmony harmony)
        {
            if (_applied) return;
            _applied = true;

            try
            {
                var loadText = AccessTools.Method(typeof(FaceListCtrl), "LoadText", new[] { typeof(string) });
                var init = AccessTools.Method(typeof(HVoiceCtrl), "Init", new[] { typeof(string), typeof(string), typeof(string) });
                if (loadText == null || init == null)
                {
                    PatcherPlugin.Logger.LogWarning(
                        $"{Tag} FaceListCtrl.LoadText(string) または HVoiceCtrl.Init(string,string,string) が見つからないため、適用しません。");
                    return;
                }

                harmony.Patch(loadText, prefix: new HarmonyMethod(Get(nameof(LoadTextPrefix))));
                harmony.Patch(init,
                    prefix: new HarmonyMethod(Get(nameof(InitPrefix))) { priority = Priority.First },
                    finalizer: new HarmonyMethod(Get(nameof(InitFinalizer))));

                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} HVoiceCtrl.Init に表情リストの追従を適用しました" +
                    "（kPlug のキャラ交代で声のリストが読み直されたとき、表情のリストも同じ性格で読み直します）。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗: {ex.Message}");
            }
        }

        private static MethodInfo Get(string name)
        {
            return typeof(FaceListReloadOnSwap).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        }

        private static void LoadTextPrefix(FaceListCtrl __instance, string _chaNo)
        {
            try
            {
                if (__instance == null) return;
                _loadedNames[__instance.GetInstanceID()] = _chaNo ?? string.Empty;
            }
            catch
            {
                // 記録できなくても読み込み自体は止めない（その FaceListCtrl は以後の対象外になるだけ）
            }
        }

        /// <summary>KK_AlternativeVoice が振り替える前の性格名を控える。</summary>
        private static void InitPrefix(string _chaFolder, string _chaFolder1, out string[] __state)
        {
            __state = new[] { _chaFolder, _chaFolder1 };
        }

        private static void InitFinalizer(HVoiceCtrl __instance, string[] __state)
        {
            try
            {
                if (__instance == null || __state == null) return;
                var faceLists = __instance.faceLists;
                if (faceLists == null) return;

                for (int i = 0; i < 2 && i < faceLists.Length && i < __state.Length; i++)
                {
                    ReloadIfChanged(faceLists[i], i, __state[i]);
                }
            }
            catch (Exception ex)
            {
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    PatcherPlugin.Logger.LogWarning($"{Tag} 表情リストの読み直しに失敗しました（表情は前の性格のままになります）: {ex.Message}");
                }
            }
        }

        private static void ReloadIfChanged(FaceListCtrl face, int slot, string newName)
        {
            if (face == null || string.IsNullOrEmpty(newName)) return;

            string oldName;
            if (!_loadedNames.TryGetValue(face.GetInstanceID(), out oldName)) return;   // 表情を読んだことが無い
            if (string.Equals(oldName, newName, StringComparison.Ordinal)) return;     // Hシーン開始時・同じ性格

            var sw = Stopwatch.StartNew();
            face.LoadText(newName);
            int count = CountEntries(face);

            if (count == 0 && !string.IsNullOrEmpty(oldName))
            {
                face.LoadText(oldName);
                PatcherPlugin.Logger.LogWarning(
                    $"{Tag} 枠{slot}: {newName} の表情リストが見つからないため、{oldName} のまま使います（{CountEntries(face)}件）。");
                return;
            }

            _reloadCount++;
            PatcherPlugin.Logger.LogInfo(
                $"{Tag} 枠{slot}: 表情リストを {oldName} → {newName} に読み直しました（{_reloadCount}回目、{count}件、{sw.ElapsedMilliseconds}ms）。");
        }

        private static int CountEntries(FaceListCtrl face)
        {
            int count = 0;
            var lib = face.facelib;
            if (lib == null) return 0;
            foreach (var kind in lib)
            {
                if (kind == null) continue;
                foreach (var dic in kind)
                {
                    if (dic != null) count += dic.Count;
                }
            }
            return count;
        }
    }
}
