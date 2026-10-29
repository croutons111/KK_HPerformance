using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace KK_HPerformance
{
    // H シーンの性能対策（体位開始/変更の停止＝読み込みキャッシュ・FPS＝揺れ骨/メタボール）と計測。KK_Rankou #401 から分離。
    // 旧名 KK_HLoadCache（v0.9.0 で改名・v1.0.0 で公開）。
    // 本体の読み込み経路（AssetBundleManager / ChaControl.LoadAnimation / GlobalMethod 等）に直接フックするので、
    // 本体のキャラ（ヒロイン）にも、本体の処理を呼んでいる他プラグイン（KK_Rankou のゲスト等）にも同じように効く。
    // 他プラグインへの依存・API は持たない。
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid    = "KK_HPerformance";
        public const string PluginName    = "KK_HPerformance";
        public const string PluginVersion = "1.0.0";

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
            DynamicBoneCulling.Init(Config, Logger, () => IsEnabled, gameObject);
            MetaballTuning.Init(Config, Logger, () => IsEnabled, gameObject);

            // ビルドマーカー: 実際に読み込まれた DLL の更新時刻（ゲームを再起動しないと DLL は再ロードされない）
            string dllBuild;
            try { dllBuild = File.GetLastWriteTime(typeof(Plugin).Assembly.Location).ToString("MM/dd HH:mm:ss"); }
            catch { dllBuild = "unknown"; }
            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded. [BUILD {dllBuild}] dll={typeof(Plugin).Assembly.Location}");
        }
    }
}
