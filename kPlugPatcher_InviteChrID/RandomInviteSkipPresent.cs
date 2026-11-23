using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// Ctrl+I（kPlug のランダム招待）で、もう居るキャラが何度でも選ばれる問題（v1.26.1、通常モードのみ）。
    ///
    /// 【kPlug の処理（3.56 / 3.63 / 3.64、KK・KKP とも同じ）】
    ///   InviteUI.Update で Keys.RandomInvite()（招待メニューのキーを Ctrl と一緒に押した）が true なら、
    ///   フリーH ではカード一覧 otherGirls のうち IsAlreadyInHaremV2(ChaFileControl) が false のものを候補にし、
    ///   その中から1枚を選んで Dealer.InviteCard に渡す。IsAlreadyInHaremV2 は「カードの名前が PresentNames に入っているか」を比べる。
    ///   本編のH（Heroine の一覧）は別のオーバーロードで判定され、本プラグインは触らない。
    ///   kPlugVR 3.56 はランダム招待の条件が常に false で働かず、3.599 以降の kPlugVR には処理自体が無い。
    ///
    /// 【本プラグインと組み合わさると候補から外れない】
    ///   1. InviteCardPatch は招待するカードを複製して「名前 (1)」に改名するので、PresentNames に入るのは「名前 (1)」で、
    ///      一覧に残る元のカード（「名前」）とは一致しない。
    ///   2. InviteListSameNameUnblock は IsAlreadyInHaremV2(ChaFileControl) を常に false にしている（一覧に同名のカードを出すため）。
    ///   このため候補は毎回全カードになり、居る子が何度でも選ばれて「名前 (2)」「名前 (3)」と増えていた。
    ///
    /// 【処理】
    ///   Keys.RandomInvite() が true を返したフレームの InviteUI.Update の中だけ、IsAlreadyInHaremV2(ChaFileControl) の判定を
    ///   「末尾の " (n)" を取り除いた名前どうしで、PresentNames に入っているか」に置き換える（判定は InviteListSameNameUnblock の Prefix から呼ぶ）。
    ///   InviteUI.Update を抜けたら（例外でも）元に戻すので、同じフレームの OnGUI（招待メニューの表示）は今までどおり。
    ///   メニューから選んで同じカードを何人も招待することは、これまでどおりできる。
    /// </summary>
    internal static class RandomInviteSkipPresent
    {
        private const string Tag = "[RandomInvite]";

        internal static ConfigEntry<bool> Enabled;

        private static bool _applied, _gaveUp;
        private static PropertyInfo _presentNames;
        private static MethodInfo _girlName;

        // RegexOptions.Compiled は使わない（InviteCardPatch.SuffixPattern と同じ理由）
        private static readonly Regex SuffixPattern = new Regex(@"\s*\(\d+\)$");

        /// <summary>Keys.RandomInvite() が true を返したフレーム。InviteUI.Update を抜けたら -1 に戻す。</summary>
        private static int _activeFrame = -1;
        private static HashSet<string> _present;
        private static bool _presentFailed;
        private static int _checked, _excluded;
        private static readonly List<string> _excludedNames = new List<string>();

        internal static bool Pending => !_applied && !_gaveUp;
        internal static bool Applied => _applied;

        internal static void BindConfig(ConfigFile config)
        {
            Enabled = config.Bind(
                "InviteCard", "RandomInviteSkipPresent", true,
                "kPlug のランダム招待（招待メニューのキーを Ctrl と一緒に押す。既定は Ctrl+I）で、もう居るキャラ（メインの子と招待済みの子）を候補から外すかどうか（v1.26.1、通常モードのフリーHのみ）。" +
                "本プラグインは招待したカードを「名前 (1)」のように改名し、同じ名前のカードも一覧に出すため、kPlug の判定では居る子が候補から外れず、同じ子が何度も選ばれていました。" +
                "有効にすると、末尾の \" (n)\" を取り除いた名前で比べて外します。招待メニューの表示と、メニューから同じカードを何人も招待することは変わりません。ゲーム中に切り替えられます。");
        }

        internal static void TryApply(Harmony harmony)
        {
            if (!Pending) return;
            if (Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase)) { _gaveUp = true; return; }   // kPlugVR ではランダム招待が働かない
            try
            {
                Assembly kPlug = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "kPlug");
                if (kPlug == null) return;   // kPlug がまだ読まれていない
                _gaveUp = true;   // 以降は成功・失敗にかかわらず試さない

                Type keys = kPlug.GetType("kPlug.Inputs.Keys");
                Type inviteUi = kPlug.GetType("kPlug.CmpH.InviteUI");
                Type parser = null;
                try { parser = kPlug.GetTypes().FirstOrDefault(t => t.Name == "kFileParser"); }
                catch (ReflectionTypeLoadException ex) { parser = ex.Types.FirstOrDefault(t => t != null && t.Name == "kFileParser"); }

                MethodInfo randomInvite = keys != null ? AccessTools.Method(keys, "RandomInvite", Type.EmptyTypes) : null;
                MethodInfo update = inviteUi != null ? AccessTools.Method(inviteUi, "Update", Type.EmptyTypes) : null;
                _presentNames = inviteUi != null ? AccessTools.Property(inviteUi, "PresentNames") : null;
                _girlName = parser != null ? AccessTools.Method(parser, "GirlName", new[] { typeof(ChaFileControl) }) : null;

                if (randomInvite == null || randomInvite.ReturnType != typeof(bool) || update == null ||
                    _presentNames == null || !typeof(List<string>).IsAssignableFrom(_presentNames.PropertyType))
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} kPlug の Keys.RandomInvite・InviteUI.Update・InviteUI.PresentNames が見つからないため適用しません。");
                    return;
                }

                harmony.Patch(randomInvite, postfix: new HarmonyMethod(typeof(RandomInviteSkipPresent), nameof(RandomInvitePostfix)));
                harmony.Patch(update, finalizer: new HarmonyMethod(typeof(RandomInviteSkipPresent), nameof(UpdateFinalizer)));
                _applied = true;
                PatcherPlugin.Logger.LogInfo($"{Tag} kPlug の Keys.RandomInvite と InviteUI.Update に適用しました" +
                    (_girlName == null ? "（kFileParser.GirlName が見つからないので、カードの名前は姓＋名で比べます）。" : "。"));
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 適用に失敗しました。元のまま動きます: {ex.Message}");
            }
        }

        private static void RandomInvitePostfix(bool __result)
        {
            if (!__result || Enabled == null || !Enabled.Value) return;
            _activeFrame = Time.frameCount;
            _present = null;
            _presentFailed = false;
            _checked = 0;
            _excluded = 0;
            _excludedNames.Clear();
        }

        private static Exception UpdateFinalizer(Exception __exception)
        {
            if (_activeFrame >= 0)
            {
                _activeFrame = -1;
                try
                {
                    // 本編のH（Heroine の一覧）や、招待できない状態でキーを押したときは判定が呼ばれないので出さない
                    if (_checked > 0)
                    {
                        PatcherPlugin.Logger.LogInfo(
                            $"{Tag} ランダム招待: カード {_checked} 枚のうち、もう居る {_excluded} 枚を候補から外しました" +
                            (_excludedNames.Count > 0 ? $"（{string.Join("、", _excludedNames.ToArray())}）。" : "。"));
                    }
                }
                catch { }
                _present = null;
                _excludedNames.Clear();
            }
            return __exception;
        }

        /// <summary>
        /// ランダム招待の候補を作っている最中なら、<paramref name="present"/> に「もう居るか」を入れて true を返す。
        /// それ以外（招待メニューの表示など）や、居る子の名前が取れなかったときは false を返し、呼び出し側は今までどおりに判定する。
        /// </summary>
        internal static bool TryDecide(object inviteUi, ChaFileControl card, out bool present)
        {
            present = false;
            if (_activeFrame != Time.frameCount || _presentFailed || card == null || inviteUi == null) return false;
            try
            {
                if (_present == null)
                {
                    var names = _presentNames.GetValue(inviteUi, null) as List<string>;
                    if (names == null) { _presentFailed = true; return false; }
                    _present = new HashSet<string>();
                    foreach (var n in names)
                    {
                        if (!string.IsNullOrEmpty(n)) _present.Add(BaseName(n));
                    }
                }

                string name = null;
                if (_girlName != null)
                {
                    try { name = _girlName.Invoke(null, new object[] { card }) as string; } catch { }
                }
                if (string.IsNullOrEmpty(name) && card.parameter != null)
                    name = (card.parameter.lastname ?? "") + (card.parameter.firstname ?? "");
                if (string.IsNullOrEmpty(name)) return false;

                string baseName = BaseName(name);
                present = _present.Contains(baseName);
                _checked++;
                if (present)
                {
                    _excluded++;
                    if (_excludedNames.Count < 10 && !_excludedNames.Contains(baseName)) _excludedNames.Add(baseName);
                }
                return true;
            }
            catch (Exception ex)
            {
                _presentFailed = true;
                PatcherPlugin.Logger.LogWarning($"{Tag} 居る子の判定に失敗したので、今回は今までどおりに選びます: {ex.Message}");
                return false;
            }
        }

        private static string BaseName(string name)
        {
            return SuffixPattern.Replace(name, "");
        }
    }
}
