using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// kPlugVR の衣装一覧（MCosplay）を、読み込むたびに全部作り直さずに使い回す（v1.23.0、VR のみ）。
    /// 背景:
    ///   kPlugVR の VRDealer.SetContext（キャラ選択から Hシーンへ入るときなど）が MCosplay.Init を呼び、UserData/coordinate の全カード（124 件の環境で）を
    ///   毎回 ChaFileCoordinate.LoadFile で完全に読み、PNG を読み直し、サムネイルのテクスチャと材質を作り直していた。
    ///   1回 約2.85〜3.19秒、その前後でゲームのメモリ（private commit）が約1.2〜1.6GiB 増えた。古いテクスチャと材質は破棄されず、後の解放待ちになる。
    ///   一覧で使われるのは、名前・ファイルのパス・サムネイルの材質だけ。衣装を着せるときは kPlugVR がファイルから読み直す（ApplyCoordinate・ToolMaps も同じ）。
    /// 方法:
    ///   - MCosplay.Init の前で、元と同じ順（Illusion.Utils.File.GetAllFiles）にファイルを並べ、前回から大きさと更新日時が変わっていないカードは前回の項目を使い回す。
    ///   - 新しい・変わったカードだけを読む。衣装カードかどうかは、ゲーム本体の ChaFileCoordinate.LoadFile と同じ順で先頭（PNG の後の番号・識別子・版・名前・データの長さ）を
    ///     読んで判断する（中身のデータの展開と ExtendedSave の読み込みはしない。一覧に要らないため）。サムネイルは元と同じく PNG から作る（CPU 側の写しは捨てる）。
    ///   - 消えた・変わったカードの古いテクスチャと材質は破棄する。
    ///   - 一覧の項目の coordinate は空の ChaFileCoordinate にする（kPlugVR 3.6.3 では一覧の外から読まれていない）。
    ///   - 型・フィールドが想定と違えば当てない。途中で失敗したら、その回は元の処理に任せ、この起動の間は使わない。
    /// </summary>
    internal static class VRCoordinateListCache
    {
        private const string Tag = "[CoordListCache]";
        private const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static ConfigEntry<bool> Enabled;

        private sealed class Entry
        {
            internal long Length, WriteTicks;
            internal object Info;   // MCosplay.CoordInfo。カードでなければ null
            internal Texture2D Texture;
            internal Material Material;
        }

        private static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static bool _applied, _gaveUp, _failed;
        private static Type _infoType;
        private static FieldInfo _list, _name, _path, _coordinate, _mat;
        private static MethodInfo _lastString;

        internal static bool Pending => !_applied && !_gaveUp;

        internal static void BindConfig(ConfigFile config)
        {
            Enabled = config.Bind(
                "KPlugPerformance", "CacheVRCoordinateList", true,
                "VR（kPlugVR）のみ（v1.23.0）。kPlugVR の衣装一覧は、Hシーンへ入るたびに UserData/coordinate の全カードを完全に読み直し、サムネイルを作り直します（124件で約3秒、メモリが一時的に約1.2〜1.6GB 増加）。" +
                "有効にすると、前回から変わっていないカードは前回の項目を使い回し、新しい・変わったカードだけを読みます。衣装を着せるときは今までどおりファイルから読みます。ゲーム中に切り替えられます（次に一覧を作るときから）。");
        }

        /// <summary>型が見つかるまで繰り返し呼んでよい（PatcherPlugin.Update の早い適用から呼ぶ）。VR のプロセスだけ。</summary>
        internal static void TryApply(Harmony harmony)
        {
            if (_applied || _gaveUp) return;
            if (!Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase)) { _gaveUp = true; return; }
            try
            {
                Assembly kPlugVR = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    if (asm.GetName().Name == "kPlugVR") { kPlugVR = asm; break; }
                if (kPlugVR == null) return;   // kPlugVR がまだ読まれていない
                _gaveUp = true;   // 以降は成功・失敗にかかわらず試さない
                Type cosplay = kPlugVR.GetType("kPlugVR.Menu.SubMChara.MCosplay");
                if (cosplay == null)
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} この kPlugVR には衣装一覧（MCosplay）が無いため、使いません（kPlugVR 3.5.6 など）。");
                    return;
                }
                _infoType = cosplay.GetNestedType("CoordInfo", BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo init = cosplay.GetMethod("Init", S, null, Type.EmptyTypes, null);
                _list = cosplay.GetField("infosMainCoord", S);
                Type toolMisc = kPlugVR.GetType("kPlugVR.Tools.ToolMisc");
                _lastString = toolMisc != null ? toolMisc.GetMethod("GetLastString", S, null, new[] { typeof(string), typeof(string), typeof(bool) }, null) : null;
                if (_infoType != null)
                {
                    _name = _infoType.GetField("name", I);
                    _path = _infoType.GetField("fileFullPath", I);
                    _coordinate = _infoType.GetField("coordinate", I);
                    _mat = _infoType.GetField("mat", I);
                }
                Type listType = _infoType != null ? typeof(List<>).MakeGenericType(_infoType) : null;
                if (init == null || init.ReturnType != typeof(void) || _list == null || _list.FieldType != listType || _lastString == null || _lastString.ReturnType != typeof(string)
                    || _name == null || _name.FieldType != typeof(string) || _path == null || _path.FieldType != typeof(string)
                    || _coordinate == null || _coordinate.FieldType != typeof(ChaFileCoordinate) || _mat == null || _mat.FieldType != typeof(Material)
                    || _infoType.GetConstructor(Type.EmptyTypes) == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} kPlugVR の MCosplay が想定の形でないため、使いません。");
                    return;
                }
                harmony.Patch(init, prefix: new HarmonyMethod(AccessTools.Method(typeof(VRCoordinateListCache), nameof(InitPrefix))));
                _applied = true;
                PatcherPlugin.Logger.LogInfo($"{Tag} kPlugVR の MCosplay.Init に当てました（設定: {Enabled.Value}）。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 適用に失敗: {ex.Message}");
            }
        }

        private static bool InitPrefix()
        {
            if (_failed || !Enabled.Value) return true;
            try
            {
                Build();
                return false;
            }
            catch (Exception ex)
            {
                _failed = true;
                PatcherPlugin.Logger.LogWarning($"{Tag} 失敗したので、元の処理に任せます（この起動の間は使いません）: {ex}");
                return true;
            }
        }

        private static void Build()
        {
            var sw = Stopwatch.StartNew();
            var files = new List<string>();
            string folder = UserData.Path + "coordinate/";
            if (Directory.Exists(folder)) Illusion.Utils.File.GetAllFiles(folder, "*.png", ref files);

            var list = (IList)Activator.CreateInstance(_list.FieldType);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int reused = 0, loaded = 0, notCards = 0, dropped = 0;
            foreach (string file in files)
            {
                if (!seen.Add(file)) continue;
                var fi = new FileInfo(file);
                long length = fi.Length, ticks = fi.LastWriteTimeUtc.Ticks;
                Entry entry;
                if (Cache.TryGetValue(file, out entry) && entry.Length == length && entry.WriteTicks == ticks)
                {
                    if (entry.Info != null) { list.Add(entry.Info); reused++; } else notCards++;
                    continue;
                }
                if (entry != null) { Destroy(entry); dropped++; }
                entry = Load(file, length, ticks);
                Cache[file] = entry;
                if (entry.Info != null) { list.Add(entry.Info); loaded++; } else notCards++;
            }
            foreach (string gone in new List<string>(Cache.Keys))
            {
                if (seen.Contains(gone)) continue;
                Destroy(Cache[gone]);
                Cache.Remove(gone);
                dropped++;
            }
            _list.SetValue(null, list);
            PatcherPlugin.Logger.LogInfo($"{Tag} 衣装一覧: {list.Count}件（使い回し {reused}・読んだ {loaded}・衣装カードでない {notCards}・破棄 {dropped}）{sw.Elapsed.TotalMilliseconds:F0}ms");
        }

        private static Entry Load(string file, long length, long ticks)
        {
            var entry = new Entry { Length = length, WriteTicks = ticks };
            if (!IsCoordinateCard(file)) return entry;
            byte[] png = PngFile.LoadPngBytes(file);
            if (png == null) return entry;
            var texture = PngAssist.ChangeTextureFromByte(png);
            if (texture == null) return entry;
            texture.Apply(false, true);   // サムネイルは表示だけに使うので、CPU 側の写しは捨てる
            var material = new Material(Shader.Find("UI/Default")) { mainTexture = texture };
            object info = Activator.CreateInstance(_infoType);
            _name.SetValue(info, (string)_lastString.Invoke(null, new object[] { file, "/", true }));
            _path.SetValue(info, file);
            _coordinate.SetValue(info, new ChaFileCoordinate());
            _mat.SetValue(info, material);
            entry.Info = info; entry.Texture = texture; entry.Material = material;
            return entry;
        }

        /// <summary>ゲーム本体の ChaFileCoordinate.LoadFile(Stream) と同じ順に、データの展開の手前まで読む（KK・KKP で同じ）。</summary>
        private static bool IsCoordinateCard(string file)
        {
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read))
                using (var br = new BinaryReader(stream))
                {
                    PngFile.SkipPng(br);
                    if (br.BaseStream.Length - br.BaseStream.Position == 0) return false;
                    if (br.ReadInt32() > 100) return false;
                    if (br.ReadString() != "【KoiKatuClothes】") return false;
                    var version = new Version(br.ReadString());
                    if (0 > ChaFileDefine.ChaFileClothesVersion.CompareTo(version)) return false;
                    br.ReadString();   // 衣装の名前
                    int count = br.ReadInt32();
                    return count >= 0 && count <= br.BaseStream.Length - br.BaseStream.Position;
                }
            }
            catch (EndOfStreamException) { return false; }
            catch (ArgumentException) { return false; }   // 版の文字列が壊れている
            catch (FormatException) { return false; }
            catch (OverflowException) { return false; }
        }

        private static void Destroy(Entry entry)
        {
            if (entry.Material != null) UnityEngine.Object.Destroy(entry.Material);
            if (entry.Texture != null) UnityEngine.Object.Destroy(entry.Texture);
        }
    }
}
