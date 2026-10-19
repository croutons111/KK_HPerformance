using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace KK_HLoadCache
{
    // H の体位開始・体位変更で画面全体が止まる問題の計測と対策（KK_Rankou #401 から分離）。
    // 本体の読み込み経路（AssetBundleManager / ChaControl.LoadAnimation / GlobalMethod 等）に直接フックするので、
    // 本体のキャラ（ヒロイン）にも、本体の処理を呼んでいる他プラグイン（KK_Rankou のゲスト等）にも同じように効く。
    // 他プラグインへの依存・API は持たない。
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid    = "KK_HLoadCache";
        public const string PluginName    = "KK_HLoadCache";
        public const string PluginVersion = "0.6.0";

        internal static new ManualLogSource Logger;
        internal static ConfigEntry<bool> MasterEnabled;
        public static bool IsEnabled => MasterEnabled == null || MasterEnabled.Value;

        private void Awake()
        {
            Logger = base.Logger;
            MasterEnabled = Config.Bind("General", "Enabled", true,
                "Enable or disable the whole plugin. OFF = vanilla behavior (no hooks are applied). Applies immediately.");

            ControllerCache.Init(Config, Logger, () => IsEnabled, gameObject);
            ListTextCache.Init(Config, Logger, () => IsEnabled, gameObject);
            BundleLookupCache.Init(Config, Logger, () => IsEnabled, gameObject);
            LoadProfiler.Init(Config, Logger, () => IsEnabled, gameObject);
            FrameProfiler.Init(Config, Logger, () => IsEnabled, gameObject);
            DynamicBoneLimiter.Init(Config, Logger, () => IsEnabled, gameObject);

            // ビルドマーカー: 実際に読み込まれた DLL の更新時刻（ゲームを再起動しないと DLL は再ロードされない）
            string dllBuild;
            try { dllBuild = File.GetLastWriteTime(typeof(Plugin).Assembly.Location).ToString("MM/dd HH:mm:ss"); }
            catch { dllBuild = "unknown"; }
            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded. [BUILD {dllBuild}] dll={typeof(Plugin).Assembly.Location}");
        }
    }
}
