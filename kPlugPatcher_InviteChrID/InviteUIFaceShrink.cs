using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// kPlug（通常モード・旧API）の招待メニュー（<c>kPlug.CmpH.InviteUI</c>）の顔画像のテクスチャを、ボタンの表示の大きさに作り直し、
    /// InviteUI が消えるときに破棄する（v1.17.0）。VR の新しい招待メニューの <see cref="MenuInviteFaceShrink"/> の通常モード版。
    ///
    /// 【問題】
    ///   <c>InviteUI.Start()</c> はカードごとに <c>PngAssist.ChangeTextureFromByte(pngData, imageW, imageH)</c> で顔画像を作り、
    ///   faces / dateFaces / otherFaces / maleFaces に入れる。LoadImage なので大きさはカードの画像のまま（高解像度のカードは 756x1056）、
    ///   ミップマップ付き、読み書き可能（GPU 側に加えて CPU 側にも写しが残る）。KK 通常の 253枚で GPU 約300MB ＋ CPU 約300MB。
    ///   ボタンの表示は imageW x imageH（画面の高さの 1/4 x その 0.7 倍。1080p で 189x270）。
    ///   さらに InviteUI.OnDestroy は空で、<c>Core.inviteUi</c>（static）が Hシーンを出た後も古い InviteUI を指したままなので、
    ///   一覧ごと回収されず、タイトルに戻っても残っていた（次の Hシーンで Core.inviteUi が上書きされるまで）。
    ///
    /// 【処理】
    ///   - <c>InviteUI.Start()</c> の後（LazyInviteCardList の Postfix から呼ぶ）: 4つの一覧の顔画像のうち、imageW x imageH に収まらないものは
    ///     縦横比を保って縮めた新しいテクスチャ（ミップマップ付き、読み書き不可）に差し替えて元を破棄し、収まるものは CPU 側の写しだけを捨てる。
    ///     kPlug 3.63 は顔画像を GUIContent.image（ボタンの絵）にしか使わない。
    ///   - <c>InviteUI.OnDestroy()</c> の後: 4つの一覧の顔画像を破棄し、一覧を空にする。
    /// 設定: [LazyInviteCardList] ShrinkInviteMenuFaces / ReleaseInviteMenuFaces（既定 true）。
    /// </summary>
    internal static class InviteUIFaceShrink
    {
        private const string Tag = "[InviteUIFaces]";
        private static readonly string[] ListNames = { "faces", "dateFaces", "otherFaces", "maleFaces" };

        private static FieldInfo[] _lists;
        private static FieldInfo _imageW, _imageH;
        private static bool _applied;

        /// <summary>LazyInviteCardList が InviteUI.Start に当てた後に呼ぶ。OnDestroy に破棄を当てる。</summary>
        internal static void TryApply(Harmony harmony, Type inviteUIType)
        {
            if (_applied) return;
            _applied = true;
            try
            {
                const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var lists = new List<FieldInfo>();
                foreach (var name in ListNames)
                {
                    var f = inviteUIType.GetField(name, I);
                    if (f != null && typeof(List<Texture>).IsAssignableFrom(f.FieldType)) lists.Add(f);
                }
                _imageW = inviteUIType.GetField("imageW", I);
                _imageH = inviteUIType.GetField("imageH", I);
                if (_imageW != null && _imageW.FieldType != typeof(int)) _imageW = null;
                if (_imageH != null && _imageH.FieldType != typeof(int)) _imageH = null;
                if (lists.Count == 0)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {inviteUIType.FullName} に顔画像の一覧（{string.Join(" / ", ListNames)}）が見つからないため、顔画像の縮小・破棄は行いません。");
                    return;
                }
                _lists = lists.ToArray();

                var onDestroy = AccessTools.DeclaredMethod(inviteUIType, "OnDestroy", Type.EmptyTypes);
                if (onDestroy != null)
                {
                    harmony.Patch(onDestroy, postfix: new HarmonyMethod(typeof(InviteUIFaceShrink).GetMethod(nameof(OnDestroyPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                }
                PatcherPlugin.Logger.LogInfo($"{Tag} {inviteUIType.FullName} の顔画像の一覧 {_lists.Length} 個に、縮小（Start の後）" +
                    (onDestroy != null ? "と破棄（OnDestroy の後）" : "（OnDestroy が見つからないため破棄はしない）") +
                    $"を用意しました（表示の大きさ imageW/imageH={(_imageW != null && _imageH != null ? "あり" : "なし。252x352 まで")}）。");
            }
            catch (Exception ex)
            {
                _lists = null;
                PatcherPlugin.Logger.LogWarning($"{Tag} 顔画像の縮小・破棄の準備に失敗しました（kPlug のまま動きます）: {ex.Message}");
            }
        }

        /// <summary>InviteUI.Start() の後に呼ぶ。設定が無効なら何もしない。</summary>
        internal static void Apply(object inviteUI)
        {
            if (_lists == null || inviteUI == null || PatcherPlugin.ShrinkInviteMenuFaces == null || !PatcherPlugin.ShrinkInviteMenuFaces.Value) return;
            try
            {
                var sw = Stopwatch.StartNew();
                int maxW = 252, maxH = 352;   // imageW/imageH が読めないときは KK のカードの画像の大きさ
                if (_imageW != null && _imageH != null)
                {
                    int w = (int)_imageW.GetValue(inviteUI), h = (int)_imageH.GetValue(inviteUI);
                    if (w > 0 && h > 0) { maxW = w; maxH = h; }
                }
                int shrunk = 0, unreadable = 0, other = 0;
                double gpuBefore = 0, gpuAfter = 0, cpuFreed = 0;
                foreach (var field in _lists)
                {
                    var list = field.GetValue(inviteUI) as List<Texture>;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var tex = list[i] as Texture2D;
                        if (tex == null) { other++; continue; }
                        double size = MenuInviteFaceShrink.EstimateMB(tex.width, tex.height);
                        gpuBefore += size;
                        cpuFreed += size;   // LoadImage のテクスチャは読み書き可能なので、CPU 側にも同じ大きさの写しがある
                        var small = MenuInviteFaceShrink.Shrink(tex, maxW, maxH);
                        if (small != null)
                        {
                            list[i] = small;
                            UnityEngine.Object.Destroy(tex);
                            gpuAfter += MenuInviteFaceShrink.EstimateMB(small.width, small.height);
                            shrunk++;
                        }
                        else
                        {
                            tex.Apply(false, true);
                            gpuAfter += size;
                            unreadable++;
                        }
                    }
                }
                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} 招待メニューの顔画像を作り直しました（表示の大きさ {maxW}x{maxH}）: 縮小 {shrunk} 枚・CPU 側の写しだけ捨てた {unreadable} 枚" +
                    (other > 0 ? $"（Texture2D でないもの・空 {other}）" : "") +
                    $" / GPU 側の見積 {gpuBefore:F0}MB → {gpuAfter:F0}MB、CPU 側の写し 約{cpuFreed:F0}MB を解放 / 所要 {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 顔画像の作り直しに失敗しました（残りはそのまま使われます）: {ex.Message}");
            }
        }

        private static void OnDestroyPostfix(object __instance)
        {
            if (_lists == null || __instance == null || PatcherPlugin.ReleaseInviteMenuFaces == null || !PatcherPlugin.ReleaseInviteMenuFaces.Value) return;
            try
            {
                int destroyed = 0;
                foreach (var field in _lists)
                {
                    var list = field.GetValue(__instance) as List<Texture>;
                    if (list == null) continue;
                    foreach (var tex in list)
                    {
                        if (tex == null) continue;
                        UnityEngine.Object.Destroy(tex);
                        destroyed++;
                    }
                    list.Clear();
                }
                if (destroyed > 0)
                    PatcherPlugin.Logger.LogInfo($"{Tag} Hシーンが終わって招待メニュー（InviteUI）が破棄されたので、顔画像 {destroyed} 枚を破棄しました。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 顔画像の破棄に失敗しました: {ex.Message}");
            }
        }
    }
}
