using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// 追加マップで、床の見た目に当たり判定が無いと招待キャラが下の地面に置かれて埋まる問題（v1.25.0）。
    ///
    /// 【原因】
    ///   kPlug は招待キャラのスポットを ToolSpot.TestSpot で決める。試す位置の 0.7m 上から真下に探し、CollideObjectTyp が種類（床・ベッド・机・椅子）を返す
    ///   一番近い当たり判定の点に置く。追加マップ（番号 97・98・100 以上）では、当たり判定の名前に floor・yuka・ground などを含む物が床になる。
    ///   また H の開始と場所の変更の ToolMaps.MapCustom が、当たり判定の無い見た目のメッシュのうち、名前に floor・yuka・ground・bed・table・bench などを含む物にだけ
    ///   MeshCollider を足す。どちらにも当たらない名前の床（例: DarkSoldier27 Project DIVA H-Map Collection 3.5 の PDHM076 の駅のホーム
    ///   K1Z_station_home_01/02）は当たり判定が無いままなので、探索は床を素通りして下の地面に届き、招待キャラは床より下（PDHM076 では 1.234m 下）に置かれる。
    ///   主の女性・男はマップの H の位置で置かれるので床の上にいる。
    ///
    /// 【処理】
    ///   kPlug（VR は kPlugVR）の ToolMaps.MapCustom の後に、マップ（ActionMap.mapRoot）の見た目のメッシュのうち、名前に設定のキーワードを含み、
    ///   同じ GameObject に当たり判定の無い物に、子の GameObject「kPlugPatcher_floor」（名前に floor を含むので、追加マップでは kPlug が床と見なす）を作り、
    ///   同じメッシュの MeshCollider を付ける（位置・回転・大きさは親と同じ、レイヤーも親と同じ）。マップのファイルは書き換えない。
    ///   設定を変えると、H の間ならその場で付け直す（足した子を全部消してから付ける）。
    ///   既定のマップ（番号 97 未満）では kPlug の床の名前の判定が決まった名前の一覧なので、この子は床と見なされない（効かない）。
    /// </summary>
    internal static class MapFloorColliders
    {
        private const string Tag = "[MapFloorColliders]";
        internal const string ChildName = "kPlugPatcher_floor";

        internal static ConfigEntry<string> FloorMeshKeywords;

        private static bool _applied, _gaveUp;

        /// <summary>kPlug の ToolMaps.MapCustom にまだ当てていない（PatcherPlugin.Update の早い適用から TryApply を呼ぶ）。</summary>
        internal static bool Pending => !_applied && !_gaveUp;

        internal static void BindConfig(ConfigFile config)
        {
            FloorMeshKeywords = config.Bind(
                "MapFloorColliders", "FloorMeshKeywords", "station_home",
                "追加マップで、当たり判定の無い床の見た目に当たり判定を足すメッシュの名前のキーワード（カンマ区切り。名前にこの文字列を含む物）（v1.25.0）。" +
                "kPlug は当たり判定の無い床には招待キャラを置けず、下の地面に置くので埋まって見えます。既定の station_home は Project DIVA H-Map Collection の駅のマップ（PDHM076）のホーム。" +
                "名前は、Unity のエディターなどで床のオブジェクトの名前を調べて指定します。空にすると何も足しません。ゲーム中に切り替えられます（H の間ならその場で付け直します）。");
            FloorMeshKeywords.SettingChanged += (s, e) => Reapply();
        }

        internal static void TryApply(Harmony harmony)
        {
            if (!Pending) return;
            try
            {
                string asmName = Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase) ? "kPlugVR" : "kPlug";
                Assembly kPlug = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == asmName);
                if (kPlug == null) return;   // kPlug がまだ読まれていない
                _gaveUp = true;   // 以降は成功・失敗にかかわらず試さない
                Type tools = kPlug.GetType(asmName + ".Tools.ToolMaps");
                MethodInfo custom = tools != null ? AccessTools.Method(tools, "MapCustom", Type.EmptyTypes) : null;
                if (custom == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} {asmName}.Tools.ToolMaps.MapCustom が見つからないため適用しません。");
                    return;
                }
                harmony.Patch(custom, postfix: new HarmonyMethod(typeof(MapFloorColliders), nameof(MapCustomPostfix)));
                _applied = true;
                PatcherPlugin.Logger.LogInfo($"{Tag} {asmName}.Tools.ToolMaps.MapCustom に適用しました（キーワード: {Describe()}）。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 適用に失敗しました。元のまま動きます: {ex.Message}");
            }
        }

        private static string Describe()
        {
            string[] k = Keywords();
            return k.Length == 0 ? "なし" : string.Join("、", k);
        }

        private static string[] Keywords()
        {
            return (FloorMeshKeywords.Value ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToArray();
        }

        private static void MapCustomPostfix()
        {
            try { Apply("kPlug のマップの準備の後"); }
            catch (Exception ex) { PatcherPlugin.Logger.LogWarning($"{Tag} 当たり判定を足せませんでした（元のまま）: {ex.Message}"); }
        }

        private static void Reapply()
        {
            try
            {
                GameObject root = MapRoot();
                if (root == null) return;   // H の外。次の MapCustom で付ける
                int removed = 0;
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t != null && t.name == ChildName) { UnityEngine.Object.Destroy(t.gameObject); removed++; }
                }
                if (removed > 0) PatcherPlugin.Logger.LogInfo($"{Tag} 設定の変更で、足した当たり判定 {removed}個を外しました。");
                Apply("設定の変更", skipExisting: false);
            }
            catch (Exception ex) { PatcherPlugin.Logger.LogWarning($"{Tag} 付け直せませんでした: {ex.Message}"); }
        }

        private static GameObject MapRoot()
        {
            ActionGame.ActionMap map = UnityEngine.Object.FindObjectOfType<ActionGame.ActionMap>();
            return map != null ? map.mapRoot : null;
        }

        private static void Apply(string reason, bool skipExisting = true)
        {
            string[] keywords = Keywords();
            if (keywords.Length == 0) return;
            GameObject root = MapRoot();
            if (root == null)
            {
                PatcherPlugin.Logger.LogInfo($"{Tag} {reason}: マップ（ActionMap.mapRoot）が見つからないため何もしません。");
                return;
            }
            var added = new List<string>();
            var skipped = new List<string>();
            foreach (MeshRenderer r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r == null || !keywords.Any(k => r.name.Contains(k))) continue;
                if (skipExisting && r.transform.Find(ChildName) != null) continue;   // 前の MapCustom で足した
                if (r.GetComponent<Collider>() != null) { skipped.Add($"{r.name}（当たり判定あり）"); continue; }
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) { skipped.Add($"{r.name}（メッシュなし）"); continue; }

                var child = new GameObject(ChildName);
                child.layer = r.gameObject.layer;
                child.transform.SetParent(r.transform, false);
                var mc = child.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                Bounds b = mc.bounds;
                added.Add($"{r.name}（上面 y={b.max.y:F3}、大きさ {b.size.x:F1}×{b.size.z:F1}）");
            }
            if (added.Count == 0 && skipped.Count == 0) return;
            PatcherPlugin.Logger.LogInfo($"{Tag} {reason}（シーン {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}）: 当たり判定を足した {added.Count}個" +
                                         (added.Count > 0 ? $": {string.Join("、", added.ToArray())}" : "") +
                                         (skipped.Count > 0 ? $" / 足さなかった {skipped.Count}個: {string.Join("、", skipped.ToArray())}" : ""));
        }
    }
}
