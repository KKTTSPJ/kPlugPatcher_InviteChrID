using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// kPlug 3.599 以降の VR版で、同じキャラカードを何度でも招待できるようにする。
    ///
    /// 【3.59 以前（旧VR）との違い】
    ///   旧VRは「在席中の名前一覧に候補カードの名前が含まれるか」で重複を弾いていたため、
    ///   <see cref="InviteCardPatch"/> の改名だけで複数招待が可能になった。
    ///
    ///   新VRの招待UI (<c>kPlugVR.Menu.MenuInvite</c>) は**名前ではなくカード項目の状態**で弾く。
    ///   <c>InviteSomeone</c> はカードを複製して <c>Dealer.InviteInVR</c> に渡し、成功するとその項目の
    ///   <c>isCreated</c> を true にして、<c>IE_RebuildTab</c> で一覧を組み直す。
    ///   一覧を作る <c>TabContent</c> は、<c>isCreated</c> の項目にはボタンを作らない。
    ///
    ///   つまり招待に成功したカードはボタンごと一覧から消える。**改名しても再招待はできない。**
    ///
    /// 【回避策】
    ///   <c>InviteSomeone</c> の Postfix で <c>chaInfo.isCreated</c> を false に戻す。
    ///   原型が直後に走らせる <c>IE_RebuildTab</c> は `WaitForSeconds` を挟んでから
    ///   <c>TabContent</c> を呼ぶため、**Postfix で戻した値がそのまま反映され**、
    ///   カードが一覧に残る。こちらでタブを組み直す必要はない。
    ///
    ///   招待キャラを退場させたときに kPlug が呼ぶ <c>MenuInvite.CleanCreatedFileInfo</c> は
    ///   <c>isCreated = false</c> を代入するだけなので、既に false でも副作用は無い。
    ///
    /// 【既知の制限（未検証）】
    ///   <c>MenuInvite.TryToReloadDefAppearence(cha, invId)</c> は <c>idAndChaFile[invId]</c> の
    ///   **元カード**を <c>cha.chaFile.CopyAll(...)</c> で書き戻す。ここを通ると改名前の名前に戻るため、
    ///   同名キャラが再び衝突する可能性がある（呼び出し元は <c>ToolMaps</c>。マップ変更時と思われる）。
    /// </summary>
    internal static class VRInviteMenuUnblock
    {
        private const string Tag = "[VRInviteUnblock]";

        private static FieldInfo _isCreatedField;
        private static FieldInfo _chaInfoFileField;
        private static bool _applied;
        private static int _unblockCount;

        internal static void TryApply(Harmony harmony)
        {
            if (_applied) return;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.FullName.Contains("kPlug")) continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                var type = types.FirstOrDefault(t => t != null && t.FullName != null && t.FullName.EndsWith(".Menu.MenuInvite"));
                if (type == null) continue;   // 3.59 以前には存在しない（旧VRは改名だけで足りる）

                try
                {
                    TryPatchMainCharacters(harmony, type);
                    if (!PatcherPlugin.EnableVRInviteMenuUnblock.Value) { _applied = true; return; }

                    var method = AccessTools.DeclaredMethod(type, "InviteSomeone");
                    if (method == null)
                    {
                        PatcherPlugin.Logger.LogWarning($"{Tag} {type.FullName}.InviteSomeone() が見つかりませんでした。");
                        return;
                    }

                    var chaInfoType = method.GetParameters().Length > 0 ? method.GetParameters()[0].ParameterType : null;
                    _isCreatedField = chaInfoType != null ? AccessTools.Field(chaInfoType, "isCreated") : null;
                    _chaInfoFileField = chaInfoType != null ? AccessTools.Field(chaInfoType, "file") : null;
                    if (_isCreatedField == null)
                    {
                        PatcherPlugin.Logger.LogWarning($"{Tag} ChaInfo.isCreated が見つからないため適用できません。");
                        return;
                    }

                    harmony.Patch(method, postfix: new HarmonyMethod(typeof(VRInviteMenuUnblock)
                        .GetMethod(nameof(InviteSomeonePostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                    _applied = true;
                    PatcherPlugin.Logger.LogInfo(
                        $"{Tag} {type.FullName}.InviteSomeone() に「招待済みでも一覧に残す」回避策を適用しました。");
                    return;
                }
                catch (Exception ex)
                {
                    var message = ex.Message ?? string.Empty;
                    if (ex is TypeLoadException || ex is ReflectionTypeLoadException
                        || message.Contains("Could not load type") || message.Contains("IL Compile Error")
                        || message.Contains("cannot be loaded"))
                    {
                        PatcherPlugin.Logger.LogInfo($"{Tag} {type.FullName} は対象外です（このモードでは kPlugVR のクラスをロードできません）: {message}");
                    }
                    else
                    {
                        PatcherPlugin.Logger.LogError($"{Tag} {type.FullName} への適用に失敗: {message}");
                    }
                    return;
                }
            }
        }

        // ---------- メインヒロイン・セカンドヒロインを候補に出す（v1.30.2） ----------
        // kPlugVR の MenuInvite.InitAndTabAndInfoInH は、H の開始情報（HStartInf.cardNames）に当たるカードの
        // isCreated を true にして、一覧から外す。ShowSameNameCardsInInviteList は旧 API の判定用で、この経路には効かない。
        // メニューを作る（Start）前に呼ばれるので、直後に false に戻せば一覧に出る。招待は PrefixInviteInVR が「名前 (1)」に改名する。

        private static FieldInfo _girlInfosField, _initDoneField;
        private static int _mainRestored;

        private static void TryPatchMainCharacters(Harmony harmony, Type menuInvite)
        {
            if (!PatcherPlugin.ShowSameNameCardsInInviteList.Value) return;
            var method = AccessTools.DeclaredMethod(menuInvite, "InitAndTabAndInfoInH");
            _girlInfosField = AccessTools.Field(menuInvite, "girlInfos");
            _initDoneField = AccessTools.Field(menuInvite, "isInfoInitInCurH");
            var infoType = menuInvite.GetNestedType("ChaInfo", BindingFlags.Public | BindingFlags.NonPublic);
            if (_isCreatedField == null && infoType != null) _isCreatedField = AccessTools.Field(infoType, "isCreated");
            if (_chaInfoFileField == null && infoType != null) _chaInfoFileField = AccessTools.Field(infoType, "file");
            if (method == null || _girlInfosField == null || _initDoneField == null || _isCreatedField == null)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} MenuInvite.InitAndTabAndInfoInH / girlInfos / isInfoInitInCurH / ChaInfo.isCreated が見つからないため、メインヒロインを候補に出す処理は当てません。");
                return;
            }
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(VRInviteMenuUnblock).GetMethod(nameof(InitInfoPrefix), BindingFlags.NonPublic | BindingFlags.Static)),
                postfix: new HarmonyMethod(typeof(VRInviteMenuUnblock).GetMethod(nameof(InitInfoPostfix), BindingFlags.NonPublic | BindingFlags.Static)));
            PatcherPlugin.Logger.LogInfo($"{Tag} MenuInvite.InitAndTabAndInfoInH に「メイン・セカンドのカードも候補に出す」処理を適用しました。");
        }

        private static void InitInfoPrefix(out bool __state)
        {
            try { __state = _initDoneField.GetValue(null) is bool && (bool)_initDoneField.GetValue(null); }
            catch { __state = true; }
        }

        private static void InitInfoPostfix(bool __state)
        {
            if (__state) return;   // この H では作成済み（招待済みのカードを戻さない）
            try
            {
                var list = _girlInfosField.GetValue(null) as System.Collections.IEnumerable;
                if (list == null) return;
                int n = 0;
                string names = "";
                foreach (var info in list)
                {
                    if (info == null || !(_isCreatedField.GetValue(info) is bool) || !(bool)_isCreatedField.GetValue(info)) continue;
                    _isCreatedField.SetValue(info, false);
                    if (n++ < 3) names += (names.Length > 0 ? "、" : "") + DescribeCard(info);
                }
                if (n > 0)
                {
                    _mainRestored += n;
                    PatcherPlugin.Logger.LogInfo($"{Tag} メイン・セカンドのカード {n} 枚を招待候補に戻しました（{names}。通算 {_mainRestored} 枚）。");
                }
            }
            catch (Exception ex)
            {
                try { PatcherPlugin.Logger.LogWarning($"{Tag} メインヒロインを候補に戻す処理で例外（kPlug 側は継続します）: {ex.Message}"); }
                catch { }
            }
        }

        /// <summary>招待に成功して立った「招待済み」フラグを戻し、同じカードを再び選べるようにする。</summary>
        private static void InviteSomeonePostfix(object[] __args)
        {
            try
            {
                if (_isCreatedField == null || __args == null || __args.Length == 0) return;

                object chaInfo = __args[0];
                if (chaInfo == null) return;

                object value = _isCreatedField.GetValue(chaInfo);
                if (!(value is bool) || !(bool)value) return;   // 招待が失敗した場合は何もしない

                _isCreatedField.SetValue(chaInfo, false);
                _unblockCount++;

                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} 招待済みフラグを解除しました（同じカードを再度招待できます）: {DescribeCard(chaInfo)} / 通算 {_unblockCount} 回");
            }
            catch (Exception ex)
            {
                try { PatcherPlugin.Logger.LogWarning($"{Tag} 処理中に例外（kPlug 側の処理は継続します）: {ex.Message}"); }
                catch { }
            }
        }

        private static string DescribeCard(object chaInfo)
        {
            try
            {
                var file = _chaInfoFileField != null ? _chaInfoFileField.GetValue(chaInfo) as ChaFileControl : null;
                if (file == null || file.parameter == null) return "(カード情報不明)";
                return file.parameter.lastname + file.parameter.firstname;
            }
            catch { return "?"; }
        }
    }
}
