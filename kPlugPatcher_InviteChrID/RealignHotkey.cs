using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    internal static class RealignHotkey
    {
        private const string Tag = "[RealignHotkey]";
        internal static ConfigEntry<KeyboardShortcut> RealignShortcut;
        private static RealignAccess _access;
        private static bool _done, _failed;
        private static object _pendingGhost;
        private static int _requestFrame;
        internal static bool Pending { get { return !_done; } }

        internal static void BindConfig(ConfigFile config)
        {
            RealignShortcut = config.Bind("RealignHotkey", "RealignShortcut", KeyboardShortcut.Empty,
                "通常版KK/KKPのHで、招待キャラ全員の整列をkPlugに頼むキー。既定は未割り当て。自動整列の設定がオフでも1回実行します。抱き合わせ等の扱いはkPlugのまま。整列中の連打は見送ります。VRは対象外です。");
        }

        internal static void TryApply(Harmony harmony)
        {
            if (_done) return;
            if (Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase)) { _done = true; return; }
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "kPlug");
                if (assembly == null) return;
                _done = true;
                var access = new RealignAccess(assembly.GetType("kPlug.Core"), assembly.GetType("kPlug.Tools.ToolSpot"));
                if (access.HProc.FieldType != typeof(HSceneProc) || access.HFlag.FieldType != typeof(HFlag))
                    throw new InvalidOperationException("HSceneProc/HFlag type mismatch");
                // Read-only diagnostic: observe the branch kPlug actually chooses.
                harmony.Patch(access.NeedReinit, postfix: new HarmonyMethod(typeof(RealignHotkey), nameof(DecisionPostfix)));
                _access = access;
                PatcherPlugin.Logger.LogInfo(Tag + " 整列ホットキーを用意しました（既定は未割り当て、通常モードのみ）。");
            }
            catch (Exception ex) { Stop(ex); }
        }

        internal static void Tick()
        {
            if (PatcherPlugin.IsQuitting || _failed || Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                if (_pendingGhost != null && Time.frameCount - _requestFrame > 120)
                {
                    _pendingGhost = null;
                    PatcherPlugin.Logger.LogInfo(Tag + " 整列方式の診断は記録されませんでした（H終了・部品変更等）。");
                }
                if (RealignShortcut.Value.MainKey == KeyCode.None || !RealignShortcut.Value.IsDown()) return;
                if (_access == null) { Skip("kPlugの準備待ち、または非対応の版"); return; }
                var proc = _access.HProc.GetValue(null) as HSceneProc;
                var flags = _access.HFlag.GetValue(null) as HFlag;
                object dealer = _access.Dealer.GetValue(null), ctrl = _access.HCtrl.GetValue(null);
                if (!Alive(proc) || !Alive(flags) || !Alive(dealer) || !Alive(ctrl) || !ReferenceEquals(proc.flags, flags)
                    || flags.isHSceneEnd || (bool)_access.Leaving.GetValue(null) || (bool)_access.Result.GetValue(null))
                { Skip("Hの操作中ではない、または結果画面・終了中"); return; }
                if ((bool)_access.DealerBusy.GetValue(dealer, null)) { Skip("招待・交代等の処理中"); return; }
                object manager = _access.Spot.GetValue(dealer);
                if (!Alive(manager)) { Skip("整列の部品がない"); return; }
                object ghost; string reason;
                if (!_access.Request(manager, out ghost, out reason)) { Skip(reason); return; }
                _pendingGhost = ghost; _requestFrame = Time.frameCount;
                PatcherPlugin.Logger.LogInfo(Tag + " 招待キャラの整列をkPlugに頼みました（全員の作り直しを要求）。");
            }
            catch (Exception ex) { Stop(ex); }
        }

        private static bool Alive(object value)
        { return value != null && (!(value is UnityEngine.Object) || (UnityEngine.Object)value != null); }

        private static void DecisionPostfix(object __0, bool __result)
        {
            if (PatcherPlugin.IsQuitting || _failed || !ReferenceEquals(_pendingGhost, __0)) return;
            try
            {
                _pendingGhost = null;
                PatcherPlugin.Logger.LogInfo(Tag + " kPlugの整列方式：" + (__result ? "全員を作り直す" : "近くの人だけ") + "。");
            }
            catch (Exception ex) { Stop(ex); }
        }

        private static void Skip(string reason) { PatcherPlugin.Logger.LogInfo(Tag + " 見送り：" + reason + "。"); }
        private static void Stop(Exception ex)
        {
            _failed = true; _done = true; _pendingGhost = null;
            PatcherPlugin.Logger.LogWarning(Tag + " 失敗したのでこの起動中は止めます（元の処理は維持）: " + ex.Message);
        }
    }
}
