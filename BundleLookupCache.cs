using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace KK_HLoadCache
{
    // 段階 3d: 表を探すときの「バンドルの中身の一覧」と「バンドル一覧」をキャッシュする。
    //   揺れ/汁/メタボール/愛撫可否の表（GlobalMethod.LoadAllFolder<T> / LoadAllFolderInOneFile<T>）と
    //   LoadAllListText の初回は、フォルダの全バンドルについて AssetBundleCheck.GetAllAssetName を呼ぶ。
    //   この関数はバンドルを AssetBundle.LoadFromFile で直接開いて名前を取り Unload(true) で閉じる
    //   （本体の読み込み管理を通らない）＝12 個以上 × 毎回で 1表 約25ms。結果はバンドル名と引数だけで決まる。
    //   ジェネリックの関数には割り込まず、その下のジェネリックでない2関数をキャッシュする。
    //   ★GetAssetBundleNameListFromPath: Sideloader が postfix で mod のバンドルを足すが「未登録なら足す」
    //     （!__result.Contains）＝二重にならない。保持は Priority.Last の postfix（Sideloader の追加後）。
    //   キャッシュに入れる/返すのは H シーン中だけ。保持は起動中ずっと（名前の一覧だけ）。返すのは毎回コピー。
    //   ★prefix で本体を飛ばしても postfix は必ず走る。Sideloader の postfix は h/list/ で mod バンドル全件を
    //     StartsWith + List.Contains で照合する（約46ms/体位変更）。キャッシュは Sideloader の追加後＝何も足さないので、
    //     キャッシュから返したときだけ Sideloader の postfix を飛ばす（_servedFromCache）。
    internal static class BundleLookupCache
    {
        private const string HarmonyId = "KK_HLoadCache.BundleLookup";
        private const string SideloaderId = "com.bepis.bepinex.sideloader";

        [ThreadStatic] private static bool _servedFromCache;

        private static ConfigEntry<bool> _enabled;
        private static ManualLogSource   _log;
        private static Func<bool>        _hostEnabled;
        private static Harmony           _harmony;
        private static bool              _patched;
        private static bool              _loggedOthers;
        private static bool              _inH;

        private static readonly Dictionary<string, string[]>     _names = new Dictionary<string, string[]>();
        private static readonly Dictionary<string, List<string>> _lists = new Dictionary<string, List<string>>();
        private static int _nameHits, _nameLoads, _listHits, _listLoads;

        internal static void Init(ConfigFile cfg, ManualLogSource log, Func<bool> hostEnabled, GameObject host)
        {
            _log = log;
            _hostEnabled = hostEnabled;
            _enabled = cfg.Bind("Cache", "Bundle Lookups", true,
                "Remember which asset bundles exist in a folder and which assets each bundle contains during H scenes, "
                + "instead of opening every list bundle again to look up a data table (breast physics, fluids, metaballs etc.). "
                + "Kept until the game is closed (names only). Turning this off clears the cache. Applies immediately.");
            host.AddComponent<BundleLookupCacheMonitor>();
        }

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
            var names = AccessTools.Method(typeof(AssetBundleCheck), "GetAllAssetName",
                new[] { typeof(string), typeof(bool), typeof(string), typeof(bool) });
            var list = AccessTools.Method(typeof(CommonLib), "GetAssetBundleNameListFromPath",
                new[] { typeof(string), typeof(bool) });
            var start = AccessTools.Method(typeof(HSceneProc), "Start");
            var destroy = AccessTools.Method(typeof(HSceneProc), "OnDestroy");
            if (names == null || list == null || start == null || destroy == null)
            {
                _log.LogWarning($"[Cache] フック先が見つからない（GetAllAssetName={names != null} GetAssetBundleNameListFromPath={list != null}"
                    + $" HSceneProc.Start={start != null} OnDestroy={destroy != null}）→ バンドル検索のキャッシュは無効（本体の動作のまま）。要調査");
                _patched = true; // 毎フレーム再試行しない
                return;
            }
            _harmony.Patch(names,
                prefix:  new HarmonyMethod(typeof(BundleLookupCache), nameof(NamesPrefix)),
                postfix: new HarmonyMethod(typeof(BundleLookupCache), nameof(NamesPostfix)));
            _harmony.Patch(list,
                prefix:  new HarmonyMethod(typeof(BundleLookupCache), nameof(ListPrefix)),
                postfix: new HarmonyMethod(typeof(BundleLookupCache), nameof(ListPostfix)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(BundleLookupCache), nameof(ListFinalizer)));
            PatchSideloaderPostfixes(list);
            _harmony.Patch(start,   prefix:  new HarmonyMethod(typeof(BundleLookupCache), nameof(HSceneStartPrefix)));
            _harmony.Patch(destroy, postfix: new HarmonyMethod(typeof(BundleLookupCache), nameof(HSceneDestroyPostfix)));
            _patched = true;
            _inH = UnityEngine.Object.FindObjectOfType<HSceneProc>() != null;
            _log.LogInfo($"[Cache] バンドル検索のキャッシュ ON（H シーン中={_inH}・保持 中身 {_names.Count} / 一覧 {_lists.Count} 件）");
            if (!_loggedOthers)
            {
                _loggedOthers = true;
                LogOtherPatches(names, "AssetBundleCheck.GetAllAssetName");
                LogOtherPatches(list, "CommonLib.GetAssetBundleNameListFromPath");
            }
        }

        private static void Unpatch()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
            _patched = false;
            if (_names.Count + _lists.Count > 0)
                _log.LogInfo($"[Cache] バンドル検索のキャッシュ OFF → 保持 中身 {_names.Count} / 一覧 {_lists.Count} 件を破棄");
            _names.Clear();
            _lists.Clear();
        }

        private static bool Active => _inH && Plugin.IsEnabled;

        // isAllCheck はキーに入れない（取ってくる元が違うだけで返す名前は同じ）
        private static string NamesKey(string bundle, bool withExt, string manifest)
            => (string.IsNullOrEmpty(manifest) ? "" : manifest) + "|" + bundle + "|" + (withExt ? "1" : "0");

        private static string ListKey(string path, bool subdir) => path + "|" + (subdir ? "1" : "0");

        // ── フック ──

        private static void HSceneStartPrefix()
        {
            _inH = true;
            _nameHits = _nameLoads = _listHits = _listLoads = 0;
        }

        private static void HSceneDestroyPostfix()
        {
            _inH = false;
            _log.LogInfo($"[Cache] H シーン終了: バンドルの中身 ヒット {_nameHits} / 読み込み {_nameLoads} / 保持 {_names.Count} 件・"
                + $"バンドル一覧 ヒット {_listHits} / 読み込み {_listLoads} / 保持 {_lists.Count} 件（起動中保持）");
        }

        private static bool NamesPrefix(string assetBundleName, bool _WithExtension, string manifestAssetBundleName,
                                        ref string[] __result, out bool __state)
        {
            __state = false; // true = キャッシュから返した（postfix で記録しない）
            if (!Active || string.IsNullOrEmpty(assetBundleName)) return true;
            if (!_names.TryGetValue(NamesKey(assetBundleName, _WithExtension, manifestAssetBundleName), out var arr)) return true;
            __result = (string[])arr.Clone();
            __state = true;
            _nameHits++;
            return false;
        }

        private static void NamesPostfix(string assetBundleName, bool _WithExtension, string manifestAssetBundleName,
                                         string[] __result, bool __state)
        {
            if (__state || !Active || string.IsNullOrEmpty(assetBundleName)) return;
            if (__result == null || __result.Length == 0) return; // 想定外の結果は保持しない（毎回本体に任せる）
            _names[NamesKey(assetBundleName, _WithExtension, manifestAssetBundleName)] = (string[])__result.Clone();
            _nameLoads++;
        }

        private static bool ListPrefix(string path, bool subdirCheck, ref List<string> __result, out bool __state)
        {
            __state = false;
            if (!Active || string.IsNullOrEmpty(path)) return true;
            if (!_lists.TryGetValue(ListKey(path, subdirCheck), out var l)) return true;
            __result = new List<string>(l); // 呼び出し側が並べ替える＝コピー
            __state = true;
            _servedFromCache = true; // この後の Sideloader の postfix を飛ばす（ListPostfix/ListFinalizer で戻す）
            _listHits++;
            return false;
        }

        // Priority.Last: Sideloader が mod のバンドルを足した後の結果を保持する
        private static void ListPostfix(string path, bool subdirCheck, List<string> __result, bool __state)
        {
            _servedFromCache = false;
            if (__state || !Active || string.IsNullOrEmpty(path)) return;
            if (__result == null || __result.Count == 0) return; // 想定外の結果は保持しない（毎回本体に任せる）
            _lists[ListKey(path, subdirCheck)] = new List<string>(__result);
            _listLoads++;
        }

        // 例外で postfix が走らなかったときもフラグを残さない
        private static void ListFinalizer() => _servedFromCache = false;

        // Sideloader の postfix 本体に付ける prefix。キャッシュから返したときだけ飛ばす
        private static bool SideloaderPostfixPrefix() => !_servedFromCache;

        private static void PatchSideloaderPostfixes(MethodBase list)
        {
            var info = Harmony.GetPatchInfo(list);
            int n = 0;
            if (info != null)
            {
                foreach (var p in info.Postfixes)
                {
                    if (p.owner != SideloaderId || p.PatchMethod == null) continue;
                    _harmony.Patch(p.PatchMethod, prefix: new HarmonyMethod(typeof(BundleLookupCache), nameof(SideloaderPostfixPrefix)));
                    n++;
                }
            }
            if (n == 0)
                _log.LogWarning("[Cache] GetAssetBundleNameListFromPath に Sideloader の postfix が見つからない → 照合の省略は無効"
                    + "（キャッシュ自体は動く・v0.4.0 と同じ動作）。Sideloader の版が変わった可能性・要調査");
            else
                _log.LogInfo($"[Cache] Sideloader の postfix {n} 件を、キャッシュから返したときに飛ばす");
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

    internal class BundleLookupCacheMonitor : MonoBehaviour
    {
        private void Update() => BundleLookupCache.SyncPatches();

        private void OnDestroy()
        {
            // 後始末はゲートしない（CLAUDE.md）。パッチを残さない。
            BundleLookupCache.SyncPatchesOff();
        }
    }
}
