using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>CharacterShadowsVR / CharacterShadowsDesktop の選択肢。</summary>
    internal enum CharacterShadowMode
    {
        /// <summary>元のまま。</summary>
        Keep,
        /// <summary>kPlug の招待キャラ（AI_Public）とバディ（AI_Buddy）の投射影を止める。メインのキャラと男の影は残す。</summary>
        Invited,
        /// <summary>Hシーンの全キャラ（男を含む）の投射影を止める。</summary>
        All,
    }

    /// <summary>
    /// Hシーンの間、キャラの投射影（影を地面や他のキャラに落とす処理）を止める（v1.23.0）。
    /// 背景:
    ///   KK VR・女3（AA4＋何も描かないカメラの停止の上で）、招待キャラだけで GPU 約 −2.39ms/f・fps 約 +3.1%、全員で約 −3.09ms/f・+3.4%。
    ///   影の生成（影マップ）が約 2.5〜2.8ms 減った。見た目の変化は、前後を直接比べなければ気付きにくい。
    /// 方法:
    ///   - 対象を変えるときだけ書き、毎フレームは触らない。1秒に1回、Manager.Character に登録されたキャラから対象を選び直す
    ///     （招待・交代・着替えで増えた Renderer にも1秒以内に当てる）。
    ///   - 通常の Renderer は shadowCastingMode を Off にする。影だけを描く Renderer（ShadowsOnly）は Off にすると本体が見えてしまうので、
    ///     代わりに Renderer.enabled を false にする。元の値を覚えて、対象から外れたとき・設定を Keep にしたとき・Hシーンを出たときに戻す。
    ///   - 自分が書いた値を他の処理が書き換えた Renderer は、取り合わずに手放す（戻すときも上書きしない）。
    ///   - 招待キャラの見分け方は DynamicBoneThrottle と同じ（kPlug の AI_Public・AI_Buddy がちょうどその型で付いている。AI_Main・AI_Second は派生なので対象外）。
    /// 既定は VR・通常モードとも Invited（v1.23.1。通常版 KK・女4 で Invited は GPU 約 −3.0ms・fps 37 → 41、All は約 −6.2ms・fps 45 前後）。
    /// </summary>
    internal static class InvitedCharacterShadows
    {
        private const string Tag = "[CharacterShadows]";

        internal static ConfigEntry<CharacterShadowMode> VRMode;
        internal static ConfigEntry<CharacterShadowMode> DesktopMode;

        private sealed class Lease
        {
            internal readonly Renderer Renderer;
            internal readonly ShadowCastingMode OriginalShadow;
            internal readonly bool OriginalEnabled, Hide;
            internal Lease(Renderer renderer)
            {
                Renderer = renderer; OriginalShadow = renderer.shadowCastingMode;
                OriginalEnabled = renderer.enabled; Hide = OriginalShadow == ShadowCastingMode.ShadowsOnly;
            }
            // 自分が変えた方の値だけを持つ。触っていない方の値は戻さない
            internal bool Owned { get { return Hide ? !Renderer.enabled : Renderer.shadowCastingMode == ShadowCastingMode.Off; } }
            internal void Apply()
            {
                if (Hide) Renderer.enabled = false;
                else Renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
            internal void Restore()
            {
                if (Hide) Renderer.enabled = OriginalEnabled;
                else Renderer.shadowCastingMode = OriginalShadow;
            }
        }

        private static readonly Dictionary<Renderer, Lease> Held = new Dictionary<Renderer, Lease>();
        private static readonly HashSet<Renderer> Abandoned = new HashSet<Renderer>();
        private static readonly HashSet<Renderer> Desired = new HashSet<Renderer>();
        private static readonly List<Renderer> Buffer = new List<Renderer>();
        private static readonly bool VRProcess = Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase);
        private static CharacterShadowMode _applied = CharacterShadowMode.Keep;
        private static Type _aiPublicType, _aiBuddyType;
        private static bool _typesResolved, _failed;
        private static float _nextScan;
        private static string _lastSummary;

        internal static void BindConfig(ConfigFile config)
        {
            VRMode = config.Bind(
                "KPlugPerformance", "CharacterShadowsVR", CharacterShadowMode.Invited,
                "VR（kPlugVR）の Hシーンの間、キャラの投射影（影を落とす処理）を止めます（v1.23.0）。Keep = 元のまま、Invited = 招待キャラとバディだけ（メインのキャラと男の影は残す）、All = 全キャラ。" +
                "KK VR・女3で、Invited は GPU 約2.4ms、All は約3.1ms 短くなりました。キャラの影が消えます。1秒に1回対象を選び直し、Keep にしたときと Hシーンを出たときは元に戻します。ゲーム中に切り替えられます。");
            DesktopMode = config.Bind(
                "KPlugPerformance", "CharacterShadowsDesktop", CharacterShadowMode.Invited,
                "通常モードの Hシーンで、キャラの投射影を止めます（v1.23.0、既定は v1.23.1 から Invited）。選択肢は CharacterShadowsVR と同じ。KK・女4で、Invited は GPU 約3.0ms、All は約6.2ms 短くなりました。ゲーム中に切り替えられます。");
            VRMode.SettingChanged += (s, e) => _nextScan = 0f;
            DesktopMode.SettingChanged += (s, e) => _nextScan = 0f;
        }

        /// <summary>PatcherPlugin.Update から毎フレーム呼ぶ（重い処理は1秒に1回と、設定や Hシーンが変わったときだけ）。</summary>
        internal static void Tick()
        {
            if (_failed || VRMode == null) return;
            try
            {
                var mode = VRProcess ? VRMode.Value : DesktopMode.Value;
                if (mode == CharacterShadowMode.Keep && _applied == CharacterShadowMode.Keep) return;
                if (Time.unscaledTime < _nextScan) return;
                _nextScan = Time.unscaledTime + 1f;

                var want = InHScene() ? mode : CharacterShadowMode.Keep;
                if (want != _applied)
                {
                    PatcherPlugin.Logger.LogInfo($"{Tag} {_applied} → {want}（{(VRProcess ? "VR" : "通常モード")}。frame={Time.frameCount}）");
                    _applied = want;
                }
                if (want == CharacterShadowMode.Keep)
                {
                    RestoreAll();
                    Abandoned.Clear();
                    _lastSummary = null;
                    return;
                }
                Reconcile(want);
            }
            catch (Exception ex)
            {
                _failed = true;
                PatcherPlugin.Logger.LogWarning($"{Tag} 失敗したので、元に戻してこの起動の間は止めます: {ex}");
                try { RestoreAll(); } catch { }
            }
        }

        private static bool InHScene()
        {
            return SceneManager.GetSceneByName("H").isLoaded || SceneManager.GetSceneByName("VRHScene").isLoaded;
        }

        private static void Reconcile(CharacterShadowMode want)
        {
            Desired.Clear();
            int characters = 0;
            var manager = Singleton<Manager.Character>.Instance;
            if (manager != null && manager.dictEntryChara != null)
            {
                foreach (var cha in manager.dictEntryChara.Values)
                {
                    if (cha == null || !cha.gameObject.activeInHierarchy || !cha.visibleAll) continue;
                    if (want == CharacterShadowMode.Invited && !IsInvited(cha)) continue;
                    characters++;
                    cha.GetComponentsInChildren(true, Buffer);
                    foreach (var r in Buffer)
                        if (r is MeshRenderer || r is SkinnedMeshRenderer) Desired.Add(r);
                }
                Buffer.Clear();
            }

            int released = 0, restored = 0, written = 0;
            foreach (var renderer in Held.Keys.ToList())
            {
                var lease = Held[renderer];
                if (renderer == null) { Held.Remove(renderer); continue; }   // キャラと一緒に破棄された
                if (!lease.Owned) { Held.Remove(renderer); Abandoned.Add(renderer); released++; continue; }
                if (!Desired.Contains(renderer)) { lease.Restore(); Held.Remove(renderer); restored++; }
            }
            foreach (var renderer in Desired)
            {
                if (renderer == null || Abandoned.Contains(renderer) || Held.ContainsKey(renderer) || renderer.shadowCastingMode == ShadowCastingMode.Off) continue;
                var lease = new Lease(renderer);
                if (lease.Hide && !lease.OriginalEnabled) continue;   // 影だけの Renderer が元から止まっている
                Held.Add(renderer, lease);   // 書く前に覚える（途中で失敗しても戻せるように）
                lease.Apply();
                written++;
            }
            Desired.Clear();

            int hidden = Held.Values.Count(l => l.Hide);
            string summary = $"対象のキャラ {characters}人・止めている Renderer {Held.Count}個（うち影だけの Renderer {hidden}個）";
            if (written > 0 || restored > 0 || released > 0 || summary != _lastSummary)
            {
                _lastSummary = summary;
                PatcherPlugin.Logger.LogInfo($"{Tag} {want}: {summary}。今回 止めた {written}・戻した {restored}・他の処理が書き換えたので手放した {released}（frame={Time.frameCount}）");
            }
        }

        private static bool IsInvited(ChaControl cha)
        {
            if (!_typesResolved)
            {
                _typesResolved = true;
                // 通常モードで kPlugVR の型に深く触れると Mono がネイティブで落ちるため、動作中のモードのアセンブリだけを見る（DynamicBoneThrottle と同じ）
                string wanted = VRProcess ? "kPlugVR" : "kPlug";
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == wanted);
                if (asm != null)
                {
                    _aiPublicType = asm.GetType(wanted + ".CmpH.AI_Public");
                    _aiBuddyType = asm.GetType(wanted + ".CmpH.AI_Buddy");
                }
                if (_aiPublicType == null)
                    PatcherPlugin.Logger.LogWarning($"{Tag} {wanted}.CmpH.AI_Public が見つからないため、Invited では何も止めません。");
            }
            if (_aiPublicType == null) return false;
            foreach (var mb in cha.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t == _aiPublicType || (_aiBuddyType != null && t == _aiBuddyType)) return true;
            }
            return false;
        }

        private static void RestoreAll()
        {
            if (Held.Count == 0) return;
            int restored = 0, released = 0;
            foreach (var pair in Held.ToList())
            {
                try
                {
                    if (pair.Key == null) continue;
                    if (pair.Value.Owned) { pair.Value.Restore(); restored++; }
                    else released++;
                }
                catch (Exception ex) { PatcherPlugin.Logger.LogWarning($"{Tag} 元に戻せませんでした: {ex.Message}"); }
            }
            Held.Clear();
            PatcherPlugin.Logger.LogInfo($"{Tag} 元に戻しました: Renderer {restored}個（他の処理が書き換えていたので触らなかった {released}個。frame={Time.frameCount}）");
        }
    }
}
