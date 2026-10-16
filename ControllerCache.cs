using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace KK_HLoadCache
{
    // 段階 3a: H シーン中のアニメーションコントローラ（RuntimeAnimatorController）の読み込みをキャッシュする。
    //   本体 ChaControl.LoadAnimation / HSceneProc.ChangeAnimator は読んだ直後に UnloadAssetBundle(force) でバンドルを閉じる
    //   ＝次回は開き直し＋コントローラと参照クリップを再展開（khh_f_base 毎回 243ms 等）。
    //   AssetBundleManager.LoadAsset で型が RuntimeAnimatorController の読み込みだけを横取りし、2回目以降は保持分を返す。
    //   ★参照カウント: 本体の LoadAsset は LoadAssetBundle で参照+1 する。ヒット時もバンドルが既に開いていれば同じく +1
    //     （後で force:false で閉じる呼び出し側と数を合わせる）。開いていなければ何もしない（閉じに来ても no-op）。
    //   キャッシュに入れる/返すのは H シーン中だけ。保持期間は Config（HScene=シーン終了で破棄 / Session=起動中ずっと）。
    internal static class ControllerCache
    {
        internal enum CacheLifetime { HScene, Session }

        private const string HarmonyId = "KK_HLoadCache.Cache";

        private static ConfigEntry<bool>          _enabled;
        private static ConfigEntry<CacheLifetime> _lifetime;
        private static ManualLogSource            _log;
        private static Func<bool>                 _hostEnabled;
        private static Harmony                    _harmony;
        private static bool                       _patched;
        private static bool                       _loggedOthers;
        private static bool                       _inH;

        private static readonly Dictionary<string, RuntimeAnimatorController> _cache = new Dictionary<string, RuntimeAnimatorController>();
        private static int _hits, _loads;

        internal static void Init(ConfigFile cfg, ManualLogSource log, Func<bool> hostEnabled, GameObject host)
        {
            _log = log;
            _hostEnabled = hostEnabled;
            _enabled = cfg.Bind("Cache", "Animator Controllers", true,
                "Keep animation controllers loaded during H scenes instead of re-reading them from disk every time a position "
                + "starts or changes. The first use of each position still loads normally. Turning this off clears the cache. Applies immediately.");
            _lifetime = cfg.Bind("Cache", "Controller Cache Lifetime", CacheLifetime.HScene,
                "HScene = clear the cache when the H scene ends. "
                + "Session = keep it until the game is closed, so positions used in an earlier H scene are fast from the first time. "
                + "Session uses more memory the more positions are used (the cached animations cannot be freed).");
            _lifetime.SettingChanged += (s, e) =>
            {
                // H シーン外で HScene に切り替えたら即破棄（シーン中ならシーン終了時に破棄）
                if (_lifetime.Value == CacheLifetime.HScene && !_inH && _cache.Count > 0)
                {
                    _log.LogInfo($"[Cache] 保持期間を HScene に変更 → 保持 {_cache.Count} 件を破棄");
                    Clear();
                }
            };
            host.AddComponent<ControllerCacheMonitor>();
        }

        // 毎フレーム（Monitor から）: Config / 全体スイッチの状態にパッチを合わせる。
        internal static void SyncPatches()
        {
            bool want = _enabled != null && _enabled.Value && (_hostEnabled == null || _hostEnabled());
            if (want == _patched) return;
            if (want) Patch(); else Unpatch();
        }

        internal static void SyncPatchesOff() { if (_patched) Unpatch(); }

        private static void Patch()
        {
            if (_harmony == null) _harmony = new Harmony(HarmonyId);
            var load = AccessTools.Method(typeof(AssetBundleManager), "LoadAsset",
                new[] { typeof(string), typeof(string), typeof(Type), typeof(string) });
            var start = AccessTools.Method(typeof(HSceneProc), "Start");
            var destroy = AccessTools.Method(typeof(HSceneProc), "OnDestroy");
            if (load == null || start == null || destroy == null)
            {
                _log.LogWarning($"[Cache] フック先が見つからない（LoadAsset={load != null} HSceneProc.Start={start != null} OnDestroy={destroy != null}）"
                    + " → コントローラのキャッシュは無効（本体の動作のまま）。要調査");
                _patched = true; // 毎フレーム再試行しない
                return;
            }
            _harmony.Patch(load,
                prefix:  new HarmonyMethod(typeof(ControllerCache), nameof(LoadAssetPrefix)),
                postfix: new HarmonyMethod(typeof(ControllerCache), nameof(LoadAssetPostfix)));
            _harmony.Patch(start,   prefix:  new HarmonyMethod(typeof(ControllerCache), nameof(HSceneStartPrefix)));
            _harmony.Patch(destroy, postfix: new HarmonyMethod(typeof(ControllerCache), nameof(HSceneDestroyPostfix)));
            _patched = true;
            // 途中で ON にしたときは H シーン中かどうかをここで拾う（1回だけ）
            _inH = UnityEngine.Object.FindObjectOfType<HSceneProc>() != null;
            _log.LogInfo($"[Cache] コントローラのキャッシュ ON（保持期間 {_lifetime.Value}・H シーン中={_inH}）");
            if (!_loggedOthers)
            {
                _loggedOthers = true;
                LogOtherPatches(load, "AssetBundleManager.LoadAsset");
            }
        }

        private static void Unpatch()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
            _patched = false;
            if (_cache.Count > 0) _log.LogInfo($"[Cache] コントローラのキャッシュ OFF → 保持 {_cache.Count} 件を破棄");
            Clear();
        }

        private static void Clear()
        {
            _cache.Clear();
        }

        private static string Key(string bundle, string asset, string manifest)
            => (string.IsNullOrEmpty(manifest) ? "abdata" : manifest) + "|" + bundle + "|" + asset;

        // ── フック ──

        private static void HSceneStartPrefix()
        {
            _inH = true;
            _hits = 0;
            _loads = 0;
        }

        private static void HSceneDestroyPostfix()
        {
            _inH = false;
            bool keep = _lifetime.Value == CacheLifetime.Session;
            _log.LogInfo($"[Cache] H シーン終了: コントローラ ヒット {_hits} / 読み込み {_loads} / 保持 {_cache.Count} 件"
                + (keep ? "（Session: 保持を継続）" : "（HScene: 破棄）"));
            if (!keep) Clear();
        }

        private static bool LoadAssetPrefix(string assetBundleName, string assetName, Type type, string manifestAssetBundleName,
                                            ref AssetBundleLoadAssetOperation __result, out bool __state)
        {
            __state = false; // true = キャッシュから返した（postfix で記録しない）
            if (!_inH || type != typeof(RuntimeAnimatorController) || !Plugin.IsEnabled) return true;
            if (string.IsNullOrEmpty(assetBundleName) || string.IsNullOrEmpty(assetName)) return true;

            string key = Key(assetBundleName, assetName, manifestAssetBundleName);
            if (!_cache.TryGetValue(key, out var rac)) return true;
            if (rac == null)
            {
                // 想定外: キャッシュしたコントローラが破棄されている（破棄するコードは本体/Rankou/AnimationLoader に無い）
                _log.LogWarning($"[Cache] 保持中のコントローラ {assetBundleName} : {assetName} が破棄されていた → 読み直す（本体の動作）。要調査");
                _cache.Remove(key);
                return true;
            }

            // 参照カウントを本体と合わせる: 既に開いているバンドルなら LoadAssetBundle（開いていれば +1 するだけ）
            string manifest = string.IsNullOrEmpty(manifestAssetBundleName) ? "abdata" : manifestAssetBundleName;
            if (AssetBundleManager.ManifestBundlePack.TryGetValue(manifest, out var pack)
                && (pack.LoadedAssetBundles.ContainsKey(assetBundleName) || pack.CreateAssetBundles.ContainsKey(assetBundleName)))
            {
                AssetBundleManager.LoadAssetBundle(assetBundleName, false, manifestAssetBundleName);
            }

            __result = new AssetBundleLoadAssetOperationSimulation(rac);
            __state = true;
            _hits++;
            _log.LogDebug($"[Cache] ヒット {assetBundleName} : {assetName}");
            return false;
        }

        private static void LoadAssetPostfix(string assetBundleName, string assetName, Type type, string manifestAssetBundleName,
                                             AssetBundleLoadAssetOperation __result, bool __state)
        {
            if (__state || !_inH || type != typeof(RuntimeAnimatorController) || !Plugin.IsEnabled) return;
            if (__result == null || __result.IsEmpty()) return;
            var rac = __result.GetAsset<RuntimeAnimatorController>();
            if (rac == null) return;
            _cache[Key(assetBundleName, assetName, manifestAssetBundleName)] = rac;
            _loads++;
        }

        private static void LogOtherPatches(MethodBase m, string label)
        {
            var info = Harmony.GetPatchInfo(m);
            if (info == null) return;
            var owners = new List<string>();
            foreach (var p in info.Prefixes)    if (p.owner != HarmonyId) owners.Add(p.owner + "(prefix)");
            foreach (var p in info.Postfixes)   if (p.owner != HarmonyId) owners.Add(p.owner + "(postfix)");
            foreach (var p in info.Transpilers) if (p.owner != HarmonyId) owners.Add(p.owner + "(transpiler)");
            foreach (var p in info.Finalizers)  if (p.owner != HarmonyId) owners.Add(p.owner + "(finalizer)");
            _log.LogInfo($"[Cache] {label} に付いている他のフック: {(owners.Count > 0 ? string.Join(", ", owners.ToArray()) : "なし")}");
        }
    }

    internal class ControllerCacheMonitor : MonoBehaviour
    {
        private void Update() => ControllerCache.SyncPatches();

        private void OnDestroy()
        {
            // 後始末はゲートしない（CLAUDE.md）。パッチを残さない。
            ControllerCache.SyncPatchesOff();
        }
    }
}
