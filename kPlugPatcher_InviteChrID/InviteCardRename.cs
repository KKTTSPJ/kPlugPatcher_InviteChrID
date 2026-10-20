using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// 招待されるキャラカードを複製し、その複製の名前に連番を付与する。
    ///
    /// 複製する理由:
    ///   InviteCard に渡される ChaFileControl は招待UIが保持しているキャッシュ実体そのものである。
    ///   これを直接改名すると (a) 招待UIの表示名が変わる (b) 次回招待時に連番が二重付与される
    ///   (c) kPlug の在席判定が改名後の名前で行われ、同一カードの再招待がブロックされる、
    ///   という副作用が生じる。複製に対して改名すれば元データは無傷で済む。
    ///
    /// 連番を必ず付与する理由:
    ///   kPlug の在席判定は「在席中の名前一覧に候補カードの名前が含まれるか」である。
    ///   1人目を素の名前のまま招待すると在席リストに素の名前が入り、2人目の招待がブロックされる。
    ///   1人目から連番を付けることで、招待UI側のカード（素の名前）は常に「未在席」と判定され、
    ///   何人でも招待できる。
    ///
    /// 【VR版と非VR版で在席判定の方式が異なる（2026-08-03 修正）】
    ///   `kPlug` と `kPlugVR` は同名クラスを別名前空間に持つ完全に独立した実装であり、
    ///   在席リストの持ち方まで違う。
    ///
    ///   - 非VR版: `Core.inviteUi.PresentNames`（`List&lt;string&gt;` を InviteUI が保持）
    ///   - VR版  : **`PresentNames` が存在しない。** `Dealer.IsAlreadyInHarem` が呼ばれるたびに
    ///             `Core.mainAi.CharaName` ＋（`Core.heroine2Cha != null` なら `Core.secondAi.CharaName`）
    ///             ＋ `Dealer.harem` の各 `ChaControl` から**その場でリストを組み立てて**判定する。
    ///
    ///   旧実装は `using kPlug;` により **非VR版の `kPlug.Core` を静的参照**していたため、
    ///   VRモードでは `Core.inviteUi` が null のまま先頭のガードで即 return し、
    ///   **改名処理が一度も実行されていなかった**。
    ///   本クラスは Dealer の実体型が属するアセンブリから Core / kFileParser を
    ///   リフレクションで解決し、両モードで動作する。
    ///
    /// 【設計上の注意】
    /// - リフレクション情報は必ず**型ごとの辞書**で持つ。単一の static にキャッシュすると
    ///   後から解析した型が先の型を上書きして壊れる。
    /// - 名前の連結規則は自前で持たず、可能な限り kPlug 自身の `kFileParser.GirlName` を呼ぶ。
    ///   GirlName は `lastname + firstname`（区切り無し）に翻訳辞書を適用したものを返すため、
    ///   自前実装だと翻訳名を使う環境で在席判定とズレる。
    /// - Prefix は例外を外へ出さない。招待処理を巻き込んで壊さないため。
    /// </summary>
    public static class InviteCardPatch
    {
        /// <summary>1つの Dealer 型（＝1つの kPlug アセンブリ）に対応するリフレクション情報一式。</summary>
        private sealed class DealerTypeInfo
        {
            // Core（静的）
            public FieldInfo CoreInviteUi;
            public MemberInfo CoreMainAi;       // AI_Main   （フィールド）
            public MemberInfo CoreSecondAi;     // AI_Second （フィールド）
            public MemberInfo CoreHeroine2Cha;  // ChaControl（プロパティ）

            // InviteUI（非VR版のみ）
            public PropertyInfo InviteUiPresentNames;

            // AI_Public.CharaName（AI_Main / AI_Second の基底）
            public FieldInfo AiCharaName;

            // Dealer.harem
            public FieldInfo DealerHarem;

            // kFileParser.GirlName
            public MethodInfo GirlNameFromChaControl;
            public MethodInfo GirlNameFromChaFile;
        }

        private static readonly Dictionary<Type, DealerTypeInfo> _infoByDealerType = new Dictionary<Type, DealerTypeInfo>();

        /// <summary>在席リストをどうしても解決できなかった場合の、セッション内で一意な連番。</summary>
        private static int _fallbackCounter;
        private static bool _fallbackWarned;

        // 既に付与済みの連番サフィックス " (n)" を検出する。
        // RegexOptions.Compiled はゲームの実行環境(Unity 5.6.3f1 / Mono, net35)で
        // ArgumentOutOfRangeException を起こし静的コンストラクタが失敗する
        // (Mono の古い正規表現エンジンが Compiled オプションを検証時に無効値として扱う)。
        // 型初期化に一度失敗すると以後このクラスを使う全呼び出しが
        // TypeInitializationException で失敗し続け、InviteCard 自体が実行されなくなるため使用しない。
        private static readonly Regex SuffixPattern = new Regex(@"\s*\(\d+\)$");

        /// <summary>パッチ適用時に、その Dealer 型に対応するリフレクション情報を用意する。</summary>
        internal static void RegisterDealerType(Type dealerType)
        {
            try
            {
                _infoByDealerType[dealerType] = BuildTypeInfo(dealerType);
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"[InviteCardPatch] {dealerType.FullName} の解析に失敗: {ex.Message}");
            }
        }

        private static DealerTypeInfo BuildTypeInfo(Type dealerType)
        {
            var info = new DealerTypeInfo();

            info.DealerHarem = AccessTools.Field(dealerType, "harem");

            Type[] types;
            try { types = dealerType.Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

            var coreType = types.FirstOrDefault(t => t.Name == "Core");
            if (coreType != null)
            {
                info.CoreInviteUi = AccessTools.Field(coreType, "inviteUi");
                // mainAi / secondAi はフィールド、heroine2Cha はプロパティ（実装差があるため両方試す）
                info.CoreMainAi = (MemberInfo)AccessTools.Field(coreType, "mainAi") ?? AccessTools.Property(coreType, "mainAi");
                info.CoreSecondAi = (MemberInfo)AccessTools.Field(coreType, "secondAi") ?? AccessTools.Property(coreType, "secondAi");
                info.CoreHeroine2Cha = (MemberInfo)AccessTools.Property(coreType, "heroine2Cha") ?? AccessTools.Field(coreType, "heroine2Cha");
            }

            var inviteUiType = types.FirstOrDefault(t => t.Name == "InviteUI");
            if (inviteUiType != null)
            {
                // VR版には存在しない。null のままで正常。
                info.InviteUiPresentNames = AccessTools.Property(inviteUiType, "PresentNames");
            }

            var aiPublicType = types.FirstOrDefault(t => t.Name == "AI_Public");
            if (aiPublicType != null)
            {
                info.AiCharaName = AccessTools.Field(aiPublicType, "CharaName");
            }

            var parserType = types.FirstOrDefault(t => t.Name == "kFileParser");
            if (parserType != null)
            {
                foreach (var m in parserType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "GirlName") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    if (ps[0].ParameterType == typeof(ChaControl)) info.GirlNameFromChaControl = m;
                    else if (ps[0].ParameterType == typeof(ChaFileControl)) info.GirlNameFromChaFile = m;
                }
            }

            PatcherPlugin.Logger.LogInfo(
                $"[InviteCardPatch] {dealerType.FullName} を解析しました "
                + $"(PresentNames={(info.InviteUiPresentNames != null ? "あり(非VR方式)" : "なし(VR方式)")}, "
                + $"GirlName={(info.GirlNameFromChaControl != null ? "解決済" : "未解決")})");

            return info;
        }

        /// <summary>
        /// Dealer.InviteCard(ChaFileControl) の Prefix。
        /// __instance は Dealer の実体（VR版/非VR版で型が違うため object で受ける）。
        /// </summary>
        public static void Prefix(object __instance, ref ChaFileControl guest)
        {
            InviteCardDiagnostics.OnPrefixEntered(guest);
            if (PatcherPlugin.IsQuitting && InviteCardDiagnostics.Enabled)
            {
                PatcherPlugin.Logger.LogWarning("[InviteCardDiag] 終了処理中の状態のため、改名と読み直しを行わずに招待します。");
            }
            if (PatcherPlugin.IsQuitting || guest == null || __instance == null) return;
            KPlugPerfDiagnostics.OnInviteStarted("女性", guest);

            try
            {
                // 0. 招待メニューの一覧が軽量版（LazyInviteCardList）なら、完全なカードを読み直す。
                //    改名より前に行う必要がある（改名は読み直した完全版を複製して行うため）。
                LazyInviteCardList.TryRestore(ref guest);

                DealerTypeInfo info;
                if (!_infoByDealerType.TryGetValue(__instance.GetType(), out info) || info == null)
                {
                    PatcherPlugin.Logger.LogWarning($"[InviteCardPatch] 未知の Dealer 型のため改名できません: {__instance.GetType().FullName}");
                    return;
                }

                // 1. 招待UIが保持する実体を汚さないよう複製する。
                //    CopyChaFile は custom / coordinate / parameter / status を複製し、
                //    キャラクター生成に必要なデータは全て含まれる (kPlug のキャラ交代処理と同じ方式)。
                var copy = new ChaFileControl();
                ChaFile.CopyChaFile(copy, guest);

                if (copy.parameter == null) return;

                // 2〜3. 在席中の名前と衝突しない連番を付ける
                string newGirlName;
                string originalName = RenameTarget(info, __instance, copy, out newGirlName);
                if (originalName == null) return;

                // 4. kPlug には複製の方を渡す。以降の在席登録・キャラ生成・表示名辞書登録は
                //    すべてこの改名済みデータを基に行われるため、名前の整合が保たれる。
                guest = copy;

                PatcherPlugin.Logger.LogInfo($"[InviteCard] 招待カードを複製し改名しました: {originalName} -> {newGirlName}");
            }
            catch (Exception ex)
            {
                if (!PatcherPlugin.IsQuitting)
                {
                    PatcherPlugin.Logger.LogError($"[InviteCardPatch] Error: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        /// <summary>
        /// kPlug 3.599 以降の VR版 <c>Dealer.InviteInVR(ChaFileControl, int, bool)</c> の Prefix。
        ///
        /// 旧VRの <c>InviteCard</c> と違い、呼び出し元 <c>MenuInvite.InviteSomeone</c> が
        /// <c>chaFileControl.CopyAll(chaInfo.file)</c> で**既に複製を作ってから**渡してくる。
        /// そのため、ここでは複製せず受け取ったインスタンスをそのまま改名してよい
        /// （UI が保持する元カードは汚れない）。
        ///
        /// 新VRは名前で重複を弾かないが、ステータス（<c>StatManager.GetStat(girlName)</c>）や
        /// 表示名辞書は依然として名前をキーにしているため、同名キャラが複数いると混ざる。
        /// 改名は <see cref="VRInviteMenuUnblock"/> とセットで意味を持つ。
        /// </summary>
        public static void PrefixInviteInVR(object __instance, ChaFileControl chaFile)
        {
            if (PatcherPlugin.IsQuitting || chaFile == null || __instance == null) return;

            try
            {
                DealerTypeInfo info;
                if (!_infoByDealerType.TryGetValue(__instance.GetType(), out info) || info == null)
                {
                    PatcherPlugin.Logger.LogWarning($"[InviteCardPatch] 未知の Dealer 型のため改名できません: {__instance.GetType().FullName}");
                    return;
                }

                if (chaFile.parameter == null) return;

                string newGirlName;
                string originalName = RenameTarget(info, __instance, chaFile, out newGirlName);
                if (originalName == null) return;

                PatcherPlugin.Logger.LogInfo($"[InviteCard] 招待カードを改名しました(VR新API): {originalName} -> {newGirlName}");
            }
            catch (Exception ex)
            {
                if (!PatcherPlugin.IsQuitting)
                {
                    PatcherPlugin.Logger.LogError($"[InviteCardPatch] Error(InviteInVR): {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        /// <summary>
        /// 在席中の名前と衝突しない連番を付けて <paramref name="target"/> を改名する。
        /// 戻り値は改名前の表示名（失敗時 null）、<paramref name="newGirlName"/> に改名後の表示名を返す。
        /// </summary>
        private static string RenameTarget(DealerTypeInfo info, object dealer, ChaFileControl target, out string newGirlName)
        {
            newGirlName = null;
            if (target.parameter == null) return null;

            string lastName = target.parameter.lastname ?? "";
            // 元カードに既に " (n)" が付いている場合は取り除いてから採番する (二重付与防止)
            string baseFirstName = SuffixPattern.Replace(target.parameter.firstname ?? "", "");

            // 在席中の名前一覧を取得する（VR版/非VR版で取得方法が違う）
            var presentNames = GetPresentNames(info, dealer);

            string newFirstName;
            if (presentNames == null)
            {
                // 在席リストが取れない場合でも改名自体は行う（何もしないと同一カードの複数招待ができないため）。
                // セッション内で単調増加する番号を使い、少なくとも招待どうしの衝突は避ける。
                if (!_fallbackWarned)
                {
                    _fallbackWarned = true;
                    PatcherPlugin.Logger.LogWarning(
                        "[InviteCardPatch] 在席中の名前一覧を取得できませんでした。セッション内連番で代替します"
                        + "（kPlug の構造が変わった可能性があります）。");
                }
                newFirstName = $"{baseFirstName} ({++_fallbackCounter})";
                target.parameter.firstname = newFirstName;
                newGirlName = ResolveGirlName(info, target, lastName, newFirstName);
            }
            else
            {
                int counter = 1;
                do
                {
                    newFirstName = $"{baseFirstName} ({counter})";
                    target.parameter.firstname = newFirstName;
                    newGirlName = ResolveGirlName(info, target, lastName, newFirstName);
                    counter++;
                }
                while (presentNames.Contains(newGirlName));
            }

            return lastName + baseFirstName;
        }

        /// <summary>
        /// 在席中のキャラクター名一覧を取得する。取得できなければ null。
        ///
        /// 非VR版は InviteUI.PresentNames をそのまま使う。
        /// VR版は PresentNames が存在しないため、Dealer.IsAlreadyInHarem と同じ材料
        /// （メインヒロイン ＋ セカンドヒロイン ＋ harem）から組み立てる。
        /// </summary>
        private static List<string> GetPresentNames(DealerTypeInfo info, object dealer)
        {
            // --- 非VR版: InviteUI.PresentNames ---
            if (info.CoreInviteUi != null && info.InviteUiPresentNames != null)
            {
                object inviteUi = SafeGetStatic(info.CoreInviteUi);
                if (!IsNullOrDestroyed(inviteUi))
                {
                    var names = SafeGetValue(info.InviteUiPresentNames, inviteUi) as List<string>;
                    if (names != null) return new List<string>(names);
                }
            }

            // --- VR版: IsAlreadyInHarem と同じ材料を組み立てる ---
            var result = new List<string>();
            bool gotAny = false;

            // メインヒロイン
            object mainAi = SafeGetMember(info.CoreMainAi, null);
            if (!IsNullOrDestroyed(mainAi) && info.AiCharaName != null)
            {
                var name = SafeGetValue(info.AiCharaName, mainAi) as string;
                if (!string.IsNullOrEmpty(name)) { result.Add(name); gotAny = true; }
            }

            // セカンドヒロイン（存在する場合のみ）
            object heroine2 = SafeGetMember(info.CoreHeroine2Cha, null);
            if (!IsNullOrDestroyed(heroine2))
            {
                object secondAi = SafeGetMember(info.CoreSecondAi, null);
                if (!IsNullOrDestroyed(secondAi) && info.AiCharaName != null)
                {
                    var name = SafeGetValue(info.AiCharaName, secondAi) as string;
                    if (!string.IsNullOrEmpty(name)) { result.Add(name); gotAny = true; }
                }
            }

            // 招待済みメンバー
            if (info.DealerHarem != null)
            {
                var harem = SafeGetValue(info.DealerHarem, dealer) as System.Collections.IEnumerable;
                if (harem != null)
                {
                    gotAny = true; // 空リストでも「取得できた」とみなす（1人目の招待時は空が正しい）
                    foreach (var item in harem)
                    {
                        var cha = item as ChaControl;
                        if (cha == null) continue;
                        string name = InvokeGirlName(info.GirlNameFromChaControl, cha);
                        if (string.IsNullOrEmpty(name) && cha.chaFile != null && cha.chaFile.parameter != null)
                            name = cha.chaFile.parameter.lastname + cha.chaFile.parameter.firstname;
                        if (!string.IsNullOrEmpty(name)) result.Add(name);
                    }
                }
            }

            return gotAny ? result : null;
        }

        /// <summary>
        /// kPlug 自身の kFileParser.GirlName で名前を組み立てる。
        /// 解決できない場合のみ、GirlName と同じ規則（lastname + firstname を区切り無しで連結）で代替する。
        /// </summary>
        private static string ResolveGirlName(DealerTypeInfo info, ChaFileControl file, string lastName, string firstName)
        {
            string name = InvokeGirlName(info.GirlNameFromChaFile, file);
            return string.IsNullOrEmpty(name) ? (lastName + firstName) : name;
        }

        private static string InvokeGirlName(MethodInfo method, object arg)
        {
            if (method == null || arg == null) return null;
            try { return method.Invoke(null, new[] { arg }) as string; }
            catch { return null; }
        }

        // ---------- リフレクション用ヘルパー ----------

        private static object SafeGetStatic(FieldInfo field)
        {
            if (field == null) return null;
            try { return field.GetValue(null); }
            catch { return null; }
        }

        private static object SafeGetValue(MemberInfo member, object target)
        {
            if (member == null) return null;
            try
            {
                var f = member as FieldInfo;
                if (f != null) return f.GetValue(target);
                var p = member as PropertyInfo;
                if (p != null) return p.GetValue(target, null);
            }
            catch { }
            return null;
        }

        /// <summary>静的メンバ（フィールド/プロパティ）の値を取る。</summary>
        private static object SafeGetMember(MemberInfo member, object target)
        {
            return SafeGetValue(member, target);
        }

        /// <summary>null と Unity の「破棄済み」をまとめて判定する。</summary>
        private static bool IsNullOrDestroyed(object v)
        {
            if (v == null) return true;
            var uo = v as UnityEngine.Object;
            if (!ReferenceEquals(uo, null)) return uo == null;
            return false;
        }
    }
}
