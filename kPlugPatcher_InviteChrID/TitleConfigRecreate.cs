using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace kPlugPatcher_InviteChrID
{
    /// <summary>
    /// タイトル画面 → 「ランキングに参加」→ タイトル画面と戻ると、kPlug の Core.Update が毎フレーム NullReferenceException を出す問題（v1.26.0、通常モードのみ）。
    ///
    /// 【原因（kPlug 3.63 / KKP 3.56 の Core.Update）】
    ///   タイトル画面にいる間、kPlug は設定の部品を <c>config = new GameObject().AddComponent&lt;Cfg&gt;()</c> で作り、印 addConfigOnTitle を立てる。
    ///   さらに印が立っていて翻訳の部品 transReader が無ければ <c>config.gameObject.AddComponent&lt;ToolTranslation.Reader&gt;()</c> で足す。
    ///   この GameObject はシーンと一緒に消える（ログの「kPlug :() Config : Component has been destroyed !」）。普段はタイトルを離れたときに
    ///   印が下ろされ、次にタイトルに来たときに作り直される。
    ///   ランキングの名前が未登録のときのタイトルのボタン「ランキングに参加」は RankingEntryScene を追加で読み、タイトルのシーンを閉じてから
    ///   タイトルを読み直す。kPlug から見るとタイトルを離れていないので印が立ったまま、設定の部品だけが消える。
    ///   翻訳の部品も一緒に消えて null と比べると等しくなるので、毎フレーム消えた config の gameObject を読んで NullReferenceException になる
    ///   （タイトルを離れると止まる）。
    ///
    /// 【処理】
    ///   Core.Update の前で、印が立っているのに config が消えていたら、印を下ろす（1回ごとにログを出す）。kPlug はそのフレームのタイトルの処理で、
    ///   普段どおり設定の部品と翻訳の部品を作り直す。消えた部品の値（設定の中身）は C# の側には残っているが、kPlug は普段もタイトルに来るたびに
    ///   作り直しているので、同じ動きになる。kPlugVR の Core.Update にはこの処理が無いので当てない。
    /// </summary>
    internal static class TitleConfigRecreate
    {
        private const string Tag = "[TitleConfigRecreate]";

        internal static ConfigEntry<bool> Enabled;

        private static bool _applied, _gaveUp;
        private static FieldInfo _config, _flag;

        internal static bool Pending => !_applied && !_gaveUp;

        internal static void BindConfig(ConfigFile config)
        {
            Enabled = config.Bind(
                "TitleConfigRecreate", "EnableTitleConfigRecreate", true,
                "タイトル画面から「ランキングに参加」を開いて戻ると、kPlug の Core.Update が毎フレーム NullReferenceException を出す問題の回避策（v1.26.0、通常モードのみ）。" +
                "kPlug がタイトルで作る設定の部品がシーンと一緒に消えたのに、作り直しの印が立ったままになるため。消えていたら印を下ろして、kPlug に普段どおり作り直させます。" +
                "ゲーム中に切り替えられます。");
        }

        internal static void TryApply(Harmony harmony)
        {
            if (!Pending) return;
            if (Paths.ProcessName.EndsWith("VR", StringComparison.OrdinalIgnoreCase)) { _gaveUp = true; return; }   // kPlugVR には無い処理
            try
            {
                Assembly kPlug = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "kPlug");
                if (kPlug == null) return;   // kPlug がまだ読まれていない
                _gaveUp = true;   // 以降は成功・失敗にかかわらず試さない
                Type core = kPlug.GetType("kPlug.Core");
                MethodInfo update = core != null ? AccessTools.Method(core, "Update", Type.EmptyTypes) : null;
                _config = core != null ? AccessTools.Field(core, "config") : null;
                _flag = core != null ? AccessTools.Field(core, "addConfigOnTitle") : null;
                if (update == null || _config == null || !_config.IsStatic || _flag == null || !_flag.IsStatic || _flag.FieldType != typeof(bool) ||
                    !typeof(Component).IsAssignableFrom(_config.FieldType))
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} kPlug.Core の Update・config・addConfigOnTitle が見つからないため適用しません。");
                    return;
                }
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(TitleConfigRecreate), nameof(UpdatePrefix)));
                _applied = true;
                PatcherPlugin.Logger.LogInfo($"{Tag} kPlug.Core.Update に適用しました。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} 適用に失敗しました。元のまま動きます: {ex.Message}");
            }
        }

        private static void UpdatePrefix()
        {
            try
            {
                if (!Enabled.Value || !(bool)_flag.GetValue(null)) return;
                object cfg = _config.GetValue(null);
                // C# の参照は残っているが Unity の側で消えている（== null が true）ときだけ。参照そのものが null なら kPlug の作る前なので触らない
                if (cfg == null || (UnityEngine.Object)cfg != null) return;
                _flag.SetValue(null, false);
                PatcherPlugin.Logger.LogInfo($"{Tag} kPlug の設定の部品が消えていたので、作り直しの印を下ろしました（シーン {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}）。kPlug が作り直します。");
            }
            catch (Exception ex)
            {
                _gaveUp = true;
                Enabled.Value = false;
                PatcherPlugin.Logger.LogWarning($"{Tag} 失敗したので止めます（元のまま動きます）: {ex.Message}");
            }
        }
    }
}
