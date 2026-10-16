using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace KK_HLoadCache
{
    // 段階 3c: 表の文字列（h/list 等）の読み込みをキャッシュする。
    //   GlobalMethod.LoadAllListText / LoadAllListTextFromList は毎回フォルダ内の全バンドルを列挙し、
    //   各バンドルのアセット名を照合→開く→TextAsset を読む→閉じる、を繰り返す（1回 約30ms・体位変更1回で 10〜14 回）。
    //   結果は引数とバンドルの中身だけで決まる（バンドル一覧は起動時に Sideloader が確定）＝同じ引数なら同じ結果。
    //   ★GetAssetBundleNameListFromPath はキャッシュしない: Sideloader が postfix で mod のバンドルを足すので、
    //     prefix で横取りすると二重に足される。
    //   キャッシュに入れる/返すのは H シーン中だけ。保持は起動中ずっと（短い文字列だけ・数 MB 程度）。
    internal static class ListTextCache
    {
        private const string HarmonyId = "KK_HLoadCache.ListText";

        private static ConfigEntry<bool> _enabled;
        private static ManualLogSource   _log;
        private static Func<bool>        _hostEnabled;
        private static Harmony           _harmony;
        private static bool              _patched;
        private static bool              _inH;

        private static readonly Dictionary<string, string>       _text = new Dictionary<string, string>();
        private static readonly Dictionary<string, List<string>> _list = new Dictionary<string, List<string>>();
        private static int _hits, _loads;

        internal static void Init(ConfigFile cfg, ManualLogSource log, Func<bool> hostEnabled, GameObject host)
        {
            _log = log;
            _hostEnabled = hostEnabled;
            _enabled = cfg.Bind("Cache", "List Texts", true,
                "Keep the H data tables (reaction, sound, attachment lists etc.) in memory during H scenes instead of "
                + "re-reading them from every list file each time a position starts or changes. "
                + "Kept until the game is closed (text only, a few MB). Turning this off clears the cache. Applies immediately.");
            host.AddComponent<ListTextCacheMonitor>();
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
            var argTypes = new[] { typeof(string), typeof(string), typeof(List<string>) };
            var text = AccessTools.Method(typeof(GlobalMethod), "LoadAllListText", argTypes);
            var list = AccessTools.Method(typeof(GlobalMethod), "LoadAllListTextFromList", argTypes);
            var start = AccessTools.Method(typeof(HSceneProc), "Start");
            var destroy = AccessTools.Method(typeof(HSceneProc), "OnDestroy");
            if (text == null || list == null || start == null || destroy == null)
            {
                _log.LogWarning($"[Cache] フック先が見つからない（LoadAllListText={text != null} LoadAllListTextFromList={list != null}"
                    + $" HSceneProc.Start={start != null} OnDestroy={destroy != null}）→ 表のキャッシュは無効（本体の動作のまま）。要調査");
                _patched = true; // 毎フレーム再試行しない
                return;
            }
            _harmony.Patch(text,
                prefix:  new HarmonyMethod(typeof(ListTextCache), nameof(TextPrefix)),
                postfix: new HarmonyMethod(typeof(ListTextCache), nameof(TextPostfix)));
            _harmony.Patch(list,
                prefix:  new HarmonyMethod(typeof(ListTextCache), nameof(ListPrefix)),
                postfix: new HarmonyMethod(typeof(ListTextCache), nameof(ListPostfix)));
            _harmony.Patch(start,   prefix:  new HarmonyMethod(typeof(ListTextCache), nameof(HSceneStartPrefix)));
            _harmony.Patch(destroy, postfix: new HarmonyMethod(typeof(ListTextCache), nameof(HSceneDestroyPostfix)));
            _patched = true;
            _inH = UnityEngine.Object.FindObjectOfType<HSceneProc>() != null;
            _log.LogInfo($"[Cache] 表のキャッシュ ON（H シーン中={_inH}・保持 {_text.Count + _list.Count} 件）");
        }

        private static void Unpatch()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
            _patched = false;
            int n = _text.Count + _list.Count;
            if (n > 0) _log.LogInfo($"[Cache] 表のキャッシュ OFF → 保持 {n} 件を破棄");
            _text.Clear();
            _list.Clear();
        }

        private static string Key(string folder, string file, List<string> omit)
            => folder + "|" + file + "|" + (omit == null ? "" : string.Join(",", omit.ToArray()));

        private static bool Active => _inH && Plugin.IsEnabled;

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
            _log.LogInfo($"[Cache] H シーン終了: 表 ヒット {_hits} / 読み込み {_loads} / 保持 {_text.Count + _list.Count} 件（起動中保持）");
        }

        private static bool TextPrefix(string _assetbundleFolder, string _strLoadFile, List<string> _OmitFolderName,
                                       ref string __result, out bool __state)
        {
            __state = false; // true = キャッシュから返した（postfix で記録しない）
            if (!Active) return true;
            if (!_text.TryGetValue(Key(_assetbundleFolder, _strLoadFile, _OmitFolderName), out var s)) return true;
            __result = s;
            __state = true;
            _hits++;
            return false;
        }

        private static void TextPostfix(string _assetbundleFolder, string _strLoadFile, List<string> _OmitFolderName,
                                        string __result, bool __state)
        {
            if (__state || !Active || __result == null) return;
            _text[Key(_assetbundleFolder, _strLoadFile, _OmitFolderName)] = __result;
            _loads++;
        }

        private static bool ListPrefix(string _assetbundleFolder, string _strLoadFile, List<string> _OmitFolderName,
                                       ref List<string> __result, out bool __state)
        {
            __state = false;
            if (!Active) return true;
            if (!_list.TryGetValue(Key(_assetbundleFolder, _strLoadFile, _OmitFolderName), out var l)) return true;
            __result = new List<string>(l); // 呼び出し側が書き換えてもキャッシュに響かないようコピー
            __state = true;
            _hits++;
            return false;
        }

        private static void ListPostfix(string _assetbundleFolder, string _strLoadFile, List<string> _OmitFolderName,
                                        List<string> __result, bool __state)
        {
            if (__state || !Active || __result == null) return;
            _list[Key(_assetbundleFolder, _strLoadFile, _OmitFolderName)] = new List<string>(__result);
            _loads++;
        }
    }

    internal class ListTextCacheMonitor : MonoBehaviour
    {
        private void Update() => ListTextCache.SyncPatches();

        private void OnDestroy()
        {
            // 後始末はゲートしない（CLAUDE.md）。パッチを残さない。
            ListTextCache.SyncPatchesOff();
        }
    }
}
