using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    internal enum KPlugBodyColliderMode { Keep, Disable, Capsule }

    /// <summary>
    /// KKABMX の不均一な関節の大きさにより Unity 5.6 が体の MeshCollider を作り直す問題への対策
    /// （v1.18.0）。kPlug の部位の印があるものだけをカプセルに差し替える。
    /// MeshColliderMode で元のまま・停止・カプセルをゲーム中に選べ、元へ戻すこともできる。
    /// KK・KKP の通常・VR が対象。KKS、球の当たり判定、マップ、ADV 用の当たり判定は対象外。
    /// </summary>
    internal static class KPlugBodyColliderCapsule
    {
        private const string Tag = "[KPlugBodyCollider]";
        private sealed class Entry
        {
            internal MeshCollider Mesh;
            internal CapsuleCollider Capsule;
        }
        private sealed class Counts
        {
            internal int Restored, Disabled, Capsules, Characters;
        }
        private static readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>();
        private static Type _colDefType;
        private static bool _attempted;
        private static int _failures, _characters, _colliders;
        private static KPlugBodyColliderMode _mode;

        internal static void TryApply(Harmony harmony)
        {
            if (_attempted) return;
            _attempted = true;
            try
            {
                string root = BepInEx.Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase) ? "kPlugVR." : "kPlug.";
                Type chaTools = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!assembly.FullName.Contains("kPlug")) continue;
                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                    catch { continue; }
                    chaTools = chaTools ?? types.FirstOrDefault(t => t != null && t.FullName == root + "Tools.ChaTools");
                    _colDefType = _colDefType ?? types.FirstOrDefault(t => t != null && t.FullName == root + "Tools.ChaTools+ColDefinition");
                }
                string missing = chaTools == null ? root + "Tools.ChaTools" : _colDefType == null ? root + "Tools.ChaTools+ColDefinition" : null;
                var method = missing == null ? AccessTools.Method(chaTools, "AddColliders", new[] { typeof(ChaControl) }) : null;
                if (missing != null || method == null)
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} {missing ?? (chaTools.FullName + ".AddColliders(ChaControl)")} が見つからないため、適用しません。");
                    return;
                }
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(KPlugBodyColliderCapsule).GetMethod(nameof(AddCollidersPostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                _mode = PatcherPlugin.KPlugBodyColliderModeSetting.Value;
                PatcherPlugin.KPlugBodyColliderModeSetting.SettingChanged += SettingChanged;
                var counts = Reapply(_mode);
                PatcherPlugin.Logger.LogInfo($"{Tag} {chaTools.FullName}.AddColliders に当てました（モード: {_mode}。既にあるキャラ {counts.Characters}人・元に戻した {counts.Restored}個・止めた {counts.Disabled}個・カプセルに差し替えた {counts.Capsules}個）。");
            }
            catch (Exception ex) { Warn(ex); }
        }

        private static void AddCollidersPostfix(ChaControl cha)
        {
            try
            {
                Prune();
                if (PatcherPlugin.IsQuitting || _mode == KPlugBodyColliderMode.Keep) return;
                ApplyCharacter(cha, _mode, new Counts());
            }
            catch (Exception ex) { Warn(ex); }
        }

        private static void SettingChanged(object sender, EventArgs args)
        {
            if (PatcherPlugin.IsQuitting) return;
            var previous = _mode;
            _mode = PatcherPlugin.KPlugBodyColliderModeSetting.Value;
            try
            {
                var counts = Reapply(_mode);
                PatcherPlugin.Logger.LogInfo($"{Tag} 設定を変更しました: {previous} → {_mode}（元に戻した {counts.Restored}個・止めた {counts.Disabled}個・カプセルに差し替えた {counts.Capsules}個）。");
            }
            catch (Exception ex) { Warn(ex); }
        }

        private static void Prune()
        {
            foreach (var pair in Entries.ToArray())
                if (pair.Value.Mesh == null)
                {
                    RemoveCapsule(pair.Value);
                    Entries.Remove(pair.Key);
                }
        }

        private static Counts Reapply(KPlugBodyColliderMode mode)
        {
            Prune();
            var counts = new Counts();
            foreach (var pair in Entries.ToArray())
            {
                try { Change(pair.Value.Mesh, mode, counts); }
                catch (Exception ex) { Warn(ex); }
            }
            if (mode != KPlugBodyColliderMode.Keep)
                foreach (var cha in UnityEngine.Object.FindObjectsOfType<ChaControl>())
                {
                    counts.Characters++;
                    try { ApplyCharacter(cha, mode, counts); }
                    catch (Exception ex) { Warn(ex); }
                }
            return counts;
        }

        private static void ApplyCharacter(ChaControl cha, KPlugBodyColliderMode mode, Counts counts)
        {
            if (cha == null) return;
            int changed = 0;
            foreach (var definition in cha.GetComponentsInChildren(_colDefType, true))
            {
                if (definition == null) continue;
                var mesh = definition.gameObject.GetComponent<MeshCollider>();
                if (mesh != null && Change(mesh, mode, counts)) changed++;
            }
            if (changed == 0) return;
            _characters++;
            _colliders += changed;
            if (_characters <= 20)
            {
                string name;
                try { name = cha.fileParam.fullname; }
                catch { name = cha.name; }
                PatcherPlugin.Logger.LogInfo($"{Tag} {name}: {(mode == KPlugBodyColliderMode.Capsule ? "カプセルに差し替え" : "停止")} {changed}個（frame={Time.frameCount}）");
            }
            else if (_characters % 50 == 0)
                PatcherPlugin.Logger.LogInfo($"{Tag} 累計 {_characters}人・{_colliders}個（frame={Time.frameCount}）");
        }

        private static bool Change(MeshCollider mesh, KPlugBodyColliderMode mode, Counts counts)
        {
            int id = mesh.GetInstanceID();
            Entry entry;
            if (!Entries.TryGetValue(id, out entry))
            {
                if (mode == KPlugBodyColliderMode.Keep || !mesh.enabled) return false;
                entry = new Entry { Mesh = mesh };
                Entries.Add(id, entry);
                mesh.enabled = false;
                if (mode == KPlugBodyColliderMode.Disable)
                {
                    counts.Disabled++;
                    return true;
                }
            }
            if (mode == KPlugBodyColliderMode.Keep)
            {
                RemoveCapsule(entry);
                mesh.enabled = true;
                Entries.Remove(id);
                counts.Restored++;
                return true;
            }
            if (mode == KPlugBodyColliderMode.Disable)
            {
                if (entry.Capsule == null) return false;
                RemoveCapsule(entry);
                counts.Disabled++;
                return true;
            }
            if (entry.Capsule != null) return false;
            var s = mesh.transform.localScale;
            var cap = mesh.gameObject.AddComponent<CapsuleCollider>();
            entry.Capsule = cap;
            cap.center = Vector3.zero;
            cap.radius = 0.5f;
            cap.height = 1f;
            cap.direction = s.x >= s.y && s.x >= s.z ? 0 : s.y >= s.z ? 1 : 2;
            cap.isTrigger = mesh.isTrigger;
            counts.Capsules++;
            return true;
        }

        private static void RemoveCapsule(Entry entry)
        {
            if (entry.Capsule == null) return;
            // Destroy はフレーム末なので、切り替えた直後から判定を止める。
            entry.Capsule.enabled = false;
            UnityEngine.Object.Destroy(entry.Capsule);
            entry.Capsule = null;
        }

        private static void Warn(Exception ex)
        {
            _failures++;
            if (_failures == 1) PatcherPlugin.Logger.LogWarning($"{Tag} 当て直しに失敗: {ex}");
        }
    }
}
