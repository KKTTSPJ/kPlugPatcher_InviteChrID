using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// kPlugVR 3.599 以降の新しい招待メニュー（MenuInvite）の顔画像のテクスチャを、ボタンの表示に足りる大きさに作り直す（v1.14.0、設定で有効にしたときだけ）。
    ///
    /// 【問題】
    ///   <c>MenuInvite.GetChaInfoList</c> はカードごとに <c>PngAssist.ChangeTextureFromByte(pngData, 9, 13)</c> で全身画像のテクスチャを作る。
    ///   LoadImage なので、大きさはカードの画像のまま（高解像度のカードは 756x1056）、ミップマップ付き、読み書き可能
    ///   （GPU 側に加えて CPU 側にも写しが残る）。確認した環境では 756x1056 が49枚で約198MB、252x352 が約200枚で約100MB（GPU 側の見積）。
    ///   メニューのボタンは小さい（9x13 cm 程度）。
    ///
    /// 【処理】
    ///   <c>MenuInvite.InitFileList()</c> の後（LazyInviteCardList の Postfix から呼ぶ）に、一覧（girlInfos / boyInfos）の <c>ChaInfo.face</c> を見て、
    ///   - 252x352 に収まらないものは、縦横比を保って縮めた新しいテクスチャ（ミップマップ付き、読み書き不可）に差し替え、元を破棄する。
    ///   - 収まるものは、CPU 側の写しだけを捨てる（Apply(false, true)）。
    ///   顔画像のマテリアルは、Hシーンに入ってメニューを作るとき（MenuInvite.Start → TabContent）に face から作られるので、その前に差し替えれば反映される。
    ///   kPlugVR 3.6.3 は face を表示にしか使わない。
    ///
    /// v1.14.0〜v1.14.3 は既定で無効（ちらつきの切り分けのため）。v1.14.4 から既定で有効（VR で見た目に問題が無いことを確認）。
    /// </summary>
    internal static class MenuInviteFaceShrink
    {
        private const string Tag = "[MenuInviteFaces]";
        private const int MaxWidth = 252, MaxHeight = 352;   // KK のカードの画像の大きさ

        private static FieldInfo _girlInfos, _boyInfos, _face;

        /// <summary>LazyInviteCardList が MenuInvite の型を見つけたときに呼ぶ。</summary>
        internal static void Init(Type menuInviteType, Type chaInfoType)
        {
            const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            _girlInfos = menuInviteType.GetField("girlInfos", S);
            _boyInfos = menuInviteType.GetField("boyInfos", S);
            _face = chaInfoType.GetField("face", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_face == null || !typeof(Texture).IsAssignableFrom(_face.FieldType) || _girlInfos == null)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} {menuInviteType.FullName} の構造が想定と違うため、顔画像の縮小は行いません（girlInfos={_girlInfos != null} / ChaInfo.face={_face != null}）。");
                _face = null;
            }
        }

        /// <summary>MenuInvite.InitFileList() の後に呼ぶ。設定が無効なら何もしない。</summary>
        internal static void Apply()
        {
            if (_face == null || PatcherPlugin.ShrinkVRMenuFaces == null || !PatcherPlugin.ShrinkVRMenuFaces.Value) return;
            try
            {
                var sw = Stopwatch.StartNew();
                int shrunk = 0, unreadable = 0, missing = 0;
                double gpuBefore = 0, gpuAfter = 0, cpuFreed = 0;
                foreach (var field in new[] { _girlInfos, _boyInfos })
                {
                    var list = field != null ? field.GetValue(null) as IList : null;
                    if (list == null) continue;
                    foreach (var info in list)
                    {
                        var tex = info != null ? _face.GetValue(info) as Texture2D : null;
                        if (tex == null) { missing++; continue; }
                        double size = EstimateMB(tex.width, tex.height);
                        gpuBefore += size;
                        cpuFreed += size;   // LoadImage のテクスチャは読み書き可能なので、CPU 側にも同じ大きさの写しがある
                        var small = Shrink(tex, MaxWidth, MaxHeight);
                        if (small != null)
                        {
                            _face.SetValue(info, small);
                            UnityEngine.Object.Destroy(tex);
                            gpuAfter += EstimateMB(small.width, small.height);
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
                    $"{Tag} 新しい招待メニューの顔画像を作り直しました: 縮小 {shrunk} 枚・CPU 側の写しだけ捨てた {unreadable} 枚（顔画像なし {missing}）/ " +
                    $"GPU 側の見積 {gpuBefore:F0}MB → {gpuAfter:F0}MB、CPU 側の写し 約{cpuFreed:F0}MB を解放 / 所要 {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 顔画像の作り直しに失敗しました（残りはそのまま使われます）: {ex.Message}");
            }
        }

        /// <summary>ミップマップ付き RGBA32 の見積（MB）。InviteUIFaceShrink（通常モード）も使う。</summary>
        internal static double EstimateMB(int w, int h)
        {
            return w * h * 4 * 4.0 / 3.0 / (1024 * 1024);
        }

        /// <summary>maxW x maxH に収まらなければ縮めた新しいテクスチャを返す。収まるなら null。InviteUIFaceShrink（通常モード）も使う。</summary>
        internal static Texture2D Shrink(Texture2D src, int maxW, int maxH)
        {
            float scale = Mathf.Min((float)maxW / src.width, (float)maxH / src.height);
            if (scale >= 1f) return null;
            int w = Mathf.Max(1, Mathf.RoundToInt(src.width * scale));
            int h = Mathf.Max(1, Mathf.RoundToInt(src.height * scale));

            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var dst = new Texture2D(w, h, TextureFormat.ARGB32, true);
                dst.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                dst.Apply(true, true);
                dst.name = src.name;
                return dst;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
