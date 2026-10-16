using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace KK_HLoadCache
{
    // 行為開始・体位変更で画面全体が止まる原因の計測（KK_Rankou #401/#401b から移設。バニラでも起きる）。
    //   本体の読み込み経路（AssetBundleManager / ChaControl.LoadAnimation / HSceneProc.ChangeAnimator）と
    //   その内側の表の読み込み・解析を直接フックするので、本体のキャラも他プラグインのキャラも同じ物差しで測れる。
    //   挙動は一切変えない。専用 Harmony インスタンスで手動パッチし、OFF で Unpatch＝負荷 0。
    internal static class LoadProfiler
    {
        private const string HarmonyId = "KK_HLoadCache.Profiler";

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int>  _hitchMs;
        private static ConfigEntry<int>  _minLogMs;
        private static ManualLogSource   _log;
        private static Func<bool>        _hostEnabled;
        private static Harmony           _harmony;
        private static bool              _patched;

        internal static void Init(ConfigFile cfg, ManualLogSource log, Func<bool> hostEnabled, GameObject host)
        {
            _log = log;
            _hostEnabled = hostEnabled;
            _enabled = cfg.Bind("Profiler", "Enabled", false,
                "Diagnostic: log how long asset bundle / animation loading takes and break down frames that freeze "
                + "(e.g. when an H position starts or changes). Covers the game's own characters and characters driven by other plugins. "
                + "Does not change any behavior. Applies immediately.");
            _hitchMs = cfg.Bind("Profiler", "Hitch Threshold (ms)", 100,
                new ConfigDescription("A frame longer than this is reported with a breakdown of what was measured in it.",
                    new AcceptableValueRange<int>(20, 5000)));
            _minLogMs = cfg.Bind("Profiler", "Min Log (ms)", 5,
                new ConfigDescription("Individual loads shorter than this are not logged (they still count in the frame breakdown).",
                    new AcceptableValueRange<int>(0, 1000)));
            host.AddComponent<LoadProfilerMonitor>();
        }

        // 毎フレーム（Monitor から）: Config / 全体スイッチの状態にパッチを合わせる。
        internal static void SyncPatches()
        {
            bool want = _enabled != null && _enabled.Value && (_hostEnabled == null || _hostEnabled());
            if (want == _patched) return;
            if (want) Patch(); else Unpatch();
        }

        internal static int HitchMs => _hitchMs != null ? _hitchMs.Value : 100;
        internal static bool Active => _patched;

        private static void Patch()
        {
            if (_harmony == null) _harmony = new Harmony(HarmonyId);
            var pre = new HarmonyMethod(typeof(LoadProfiler), nameof(Enter)) { priority = Priority.First };
            int n = 0;
            n += Hook(AccessTools.Method(typeof(AssetBundleManager), "LoadAssetBundleInternal",
                         new[] { typeof(string), typeof(bool), typeof(string) }), pre, nameof(ExitOpen), "AssetBundleManager.LoadAssetBundleInternal");
            n += Hook(AccessTools.Method(typeof(AssetBundleManager), "LoadAsset",
                         new[] { typeof(string), typeof(string), typeof(Type), typeof(string) }), pre, nameof(ExitAsset), "AssetBundleManager.LoadAsset");
            n += Hook(AccessTools.Method(typeof(AssetBundleManager), "UnloadAssetBundle",
                         new[] { typeof(string), typeof(bool), typeof(string), typeof(bool) }), pre, nameof(ExitUnload), "AssetBundleManager.UnloadAssetBundle");
            n += Hook(AccessTools.Method(typeof(ChaControl), "LoadAnimation",
                         new[] { typeof(string), typeof(string), typeof(string) }), pre, nameof(ExitAnim), "ChaControl.LoadAnimation");
            n += Hook(AccessTools.Method(typeof(HSceneProc), "ChangeAnimator"), pre, nameof(ExitChange), "HSceneProc.ChangeAnimator");
            int d = PatchDetails(pre);
            _patched = true;
            _stack.Clear();
            Frame.Reset();
            _log.LogInfo($"[LoadProf] 計測 ON（フック {n}/5・内訳 {d} メソッド）");
            LogOtherPatches(AccessTools.Method(typeof(HSceneProc), "ChangeAnimator"), "HSceneProc.ChangeAnimator");
        }

        private static int Hook(MethodBase target, HarmonyMethod pre, string finalizer, string label)
        {
            if (target == null)
            {
                _log.LogWarning($"[LoadProf] フック先 {label} が見つからない → この項目は計測しない。要調査");
                return 0;
            }
            var fin = new HarmonyMethod(typeof(LoadProfiler), finalizer) { priority = Priority.Last };
            _harmony.Patch(target, prefix: pre, finalizer: fin);
            return 1;
        }

        internal static void SyncPatchesOff() { if (_patched) Unpatch(); }

        // ── #401b: 内訳（名前指定のメソッド群） ──
        // 本体 ChangeAnimator の中で毎回走るデータ読み込み/解析と、KK_Rankou のゲストの行為準備。
        // ★型は名前で引く＝KK_Rankou を参照しない（入っていなければその項目だけ除外）。
        //   見つからない本体の型/メソッドは LogWarning でその項目だけ除外。同名の多重定義は全部当てる。
        private static readonly string[][] DetailTargets =
        {
            // 本体（HSceneProc.ChangeAnimator 内）
            new[] { "YureCtrl", "LoadAllExcel" },
            new[] { "HitCollisionEnableCtrl", "LoadText" },
            new[] { "HitCollisionEnableCtrl", "Init" },
            new[] { "ParentObjectCtrl", "LoadText" },
            new[] { "Lookat_dan", "LoadList" },
            new[] { "HandCtrl", "LoadReactionList" },
            new[] { "MotionIK", "LoadData" },
            new[] { "MotionIK", "Calc" },
            new[] { "ItemObject", "LoadItem" },
            new[] { "SiruPasteCtrl", "Load" },
            new[] { "HSeCtrl", "Load" },
            new[] { "MetaballCtrl", "Load" },
            new[] { "HSprite", "MainSpriteChange" },
            new[] { "HSprite", "InitHeroine" },
            new[] { "HSceneProc", "SetClothStateStartMotion" },
            new[] { "ChaControl", "LoadHitObject" },
            new[] { "ChaControl", "ReleaseHitObject" },
            new[] { "DynamicBoneReferenceCtrl", "Proc" },
            // 上書きコントローラの組み立て（ChangeAnimator その他の切り分け・#3）
            new[] { "Illusion.Utils+Animator", "SetupAnimatorOverrideController" },
            // 表の読み込みの共通関数（各表メソッドの「集める」と「解析」の切り分け・#3）
            new[] { "GlobalMethod", "LoadAllListText" },
            new[] { "GlobalMethod", "LoadAllListTextFromList" },
            new[] { "CommonLib", "GetAssetBundleNameListFromPath" },
            new[] { "AssetBundleCheck", "IsFile" },
            // 他プラグインの ChangeAnimator フック（任意。未導入なら除外＝警告なし・#3）
            new[] { "AnimationLoader.SwapAnim", "SwapAnimation" },
            new[] { "AnimationLoader.SwapAnim", "SetupAnimatorOverrideController" },
            new[] { "KK_BetterPenetration.KK_BetterPenetration", "HSceneProc_PreChangeAnimator" },
            new[] { "KK_BetterPenetration.KK_BetterPenetration", "HSceneProc_PostChangeAnimator" },
            // KK_Rankou のゲストの行為準備（任意。未導入なら除外＝警告なし）
            new[] { "KK_Rankou.Actions.PairAction", "SetupSex" },
            new[] { "KK_Rankou.Actions.PairAction", "SetupHoushi" },
            new[] { "KK_Rankou.Actions.PairAction", "SetupLesbian" },
            new[] { "KK_Rankou.Actions.PairAction", "ApplyHAnimParams" },
            new[] { "KK_Rankou.Actions.MffAction", "Setup" },
            new[] { "KK_Rankou.Chara.CharaAnimCtrl", "PlayPairAnim" },
            new[] { "KK_Rankou.Chara.CharaAnimCtrl", "ApplyAnimCloth" },
            new[] { "KK_Rankou.Chara.ClothingCtrl", "ApplyMaleUndress" },
            new[] { "KK_Rankou.Chara.GuestMaleDriver", "Begin" },
            new[] { "KK_Rankou.Chara.GuestMaleDriver", "SetupLookDan" },
            new[] { "KK_Rankou.Chara.GuestVoiceDriver", "Begin" },
            new[] { "KK_Rankou.Chara.GuestVoiceDriver", "SetupYure" },
            new[] { "KK_Rankou.Chara.GuestVoiceDriver", "SetupEyeNeck" },
            new[] { "KK_Rankou.Chara.PairMotionIKDriver", "Setup" },
        };
        private static readonly Dictionary<MethodBase, string> _labels = new Dictionary<MethodBase, string>();
        private static readonly Dictionary<string, double> _named = new Dictionary<string, double>();   // フレーム内 self ms

        private static int PatchDetails(HarmonyMethod pre)
        {
            var fin = new HarmonyMethod(typeof(LoadProfiler), nameof(ExitNamed)) { priority = Priority.Last };
            int n = 0;
            _labels.Clear();
            foreach (var t in DetailTargets)
            {
                var type = AccessTools.TypeByName(t[0]);
                if (type == null)
                {
                    if (IsOptional(t[0])) _log.LogDebug($"[LoadProf] {t[0]} が無い（そのプラグインが未導入）→ {t[1]} は計測しない");
                    else _log.LogWarning($"[LoadProf] 内訳の型 {t[0]} が見つからない → {t[1]} は計測しない。要調査");
                    continue;
                }
                int hit = 0;
                foreach (var m in AccessTools.GetDeclaredMethods(type))
                {
                    if (m.Name != t[1] || m.IsAbstract || m.IsGenericMethodDefinition) continue;
                    if (TryPatch(m, $"{type.Name}.{m.Name}", pre, fin)) hit++;
                }
                if (hit == 0)
                {
                    // 他プラグインは版によって名前が変わり得る＝LogDebug。本体は想定外なので LogWarning
                    if (IsOptional(t[0])) _log.LogDebug($"[LoadProf] {t[0]}.{t[1]} が無い（そのプラグインの版違い？）→ 計測しない");
                    else _log.LogWarning($"[LoadProf] 内訳のメソッド {t[0]}.{t[1]} が見つからない → 計測しない。要調査");
                }
                n += hit;
            }
            // HActionBase.MotionChange は各モード（愛撫/奉仕/挿入…）の派生クラスで上書きされている＝派生ごとに当てる
            var hab = AccessTools.TypeByName("HActionBase");
            if (hab != null)
            {
                foreach (var type in hab.Assembly.GetTypes())
                {
                    if (!hab.IsAssignableFrom(type)) continue;
                    var m = AccessTools.DeclaredMethod(type, "MotionChange");
                    if (m == null || m.IsAbstract) continue;
                    if (TryPatch(m, $"{type.Name}.MotionChange", pre, fin)) n++;
                }
            }
            else _log.LogWarning("[LoadProf] HActionBase が見つからない → MotionChange は計測しない。要調査");
            return n;
        }

        // 他プラグインの型（入っていなくても正常）
        private static readonly string[] OptionalPrefixes = { "KK_Rankou.", "AnimationLoader.", "KK_BetterPenetration." };
        private static bool IsOptional(string typeName)
        {
            foreach (var p in OptionalPrefixes) if (typeName.StartsWith(p)) return true;
            return false;
        }

        private static bool TryPatch(MethodBase m, string label, HarmonyMethod pre, HarmonyMethod fin)
        {
            try
            {
                _labels[m] = label;
                _harmony.Patch(m, prefix: pre, finalizer: fin);
                return true;
            }
            catch (Exception e)
            {
                _labels.Remove(m);
                _log.LogWarning($"[LoadProf] 内訳 {label} のパッチ失敗 → 除外: {e.Message}");
                return false;
            }
        }

        // ChangeAnimator に他プラグインが付けているフックの持ち主（その分は「ChangeAnimatorその他」に入る）
        private static void LogOtherPatches(MethodBase m, string label)
        {
            if (m == null) return;
            var info = Harmony.GetPatchInfo(m);
            if (info == null) return;
            var owners = new List<string>();
            foreach (var p in info.Prefixes)    if (p.owner != HarmonyId) owners.Add(p.owner + "(prefix)");
            foreach (var p in info.Postfixes)   if (p.owner != HarmonyId) owners.Add(p.owner + "(postfix)");
            foreach (var p in info.Transpilers) if (p.owner != HarmonyId) owners.Add(p.owner + "(transpiler)");
            foreach (var p in info.Finalizers)  if (p.owner != HarmonyId) owners.Add(p.owner + "(finalizer)");
            _log.LogInfo($"[LoadProf] {label} に付いている他のフック: {(owners.Count > 0 ? string.Join(", ", owners.ToArray()) : "なし")}");
        }

        private static Exception ExitNamed(Exception __exception, MethodBase __originalMethod)
        {
            Leave(out double total, out double self);
            string label;
            if (__originalMethod == null || !_labels.TryGetValue(__originalMethod, out label)) label = __originalMethod?.Name ?? "?";
            double cur;
            _named.TryGetValue(label, out cur);
            _named[label] = cur + self;
            Note(label, total);
            if (ShouldLog(total)) _log.LogDebug($"[LoadProf] {Indent}{label} 計 {total:F1}ms（自分 {self:F1}ms）");
            return __exception;
        }

        // 内訳（名前指定メソッドの self）の上位 N 件。1ms 未満は省く。
        private static string NamedTop(int n)
        {
            if (_named.Count == 0) return "";
            var list = new List<KeyValuePair<string, double>>(_named);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder(" 内訳:");
            int k = 0;
            foreach (var kv in list)
            {
                if (k >= n || kv.Value < 1.0) break;
                sb.Append($" {kv.Key} {kv.Value:F0}");
                k++;
            }
            return k > 0 ? sb.ToString() : "";
        }

        private static void Unpatch()
        {
            _harmony?.UnpatchSelf();
            _patched = false;
            _stack.Clear();
            _log.LogInfo("[LoadProf] 計測 OFF");
        }

        // ── 入れ子の計測 ──
        // LoadAnimation ⊃ LoadAsset ⊃ LoadAssetBundleInternal（開く）、LoadAnimation ⊃ UnloadAssetBundle、
        // ChangeAnimator ⊃ 全部、と入れ子になる。各フックの「自分の分（self = total − 子の合計）」を種別ごとに足すと、
        // フレーム内の内訳が二重計上なしに出る。finalizer は例外時も走る＝スタックが崩れない。
        private struct Entry { public long Start; public long Child; }
        private static readonly Stack<Entry> _stack = new Stack<Entry>();

        internal struct FrameSums
        {
            public double Open, Asset, Unload, Anim, Change;   // self ms
            public double TopTotal;                            // 最外の合計 ms（未計測＝フレーム − これ）
            public int    Count;
            public string Worst; public double WorstMs;
            public void Reset() { this = default(FrameSums); _named.Clear(); }
        }
        internal static FrameSums Frame;

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static void Enter()
        {
            _stack.Push(new Entry { Start = Stopwatch.GetTimestamp(), Child = 0 });
        }

        // 戻り値: (total ms, self ms)。最外なら TopTotal に足す。
        private static void Leave(out double total, out double self)
        {
            if (_stack.Count == 0) { total = self = 0; return; }
            var e = _stack.Pop();
            long t = Stopwatch.GetTimestamp() - e.Start;
            total = Ms(t);
            self  = Ms(t - e.Child);
            if (_stack.Count > 0)
            {
                var p = _stack.Pop(); p.Child += t; _stack.Push(p);
            }
            else Frame.TopTotal += total;
            Frame.Count++;
        }

        private static void Note(string what, double total)
        {
            if (total > Frame.WorstMs) { Frame.WorstMs = total; Frame.Worst = what; }
        }

        private static bool ShouldLog(double total) => total >= (_minLogMs != null ? _minLogMs.Value : 5);
        private static string Indent => new string(' ', _stack.Count * 2);

        private static Exception ExitOpen(Exception __exception, string assetBundleName)
        {
            Leave(out double total, out double self);
            Frame.Open += self;
            if (ShouldLog(total)) _log.LogDebug($"[LoadProf] {Indent}開く {assetBundleName} {total:F1}ms");
            return __exception;
        }

        private static Exception ExitAsset(Exception __exception, string assetBundleName, string assetName)
        {
            Leave(out double total, out double self);
            Frame.Asset += self;
            Note($"LoadAsset {assetName}", total);
            if (ShouldLog(total)) _log.LogDebug($"[LoadProf] {Indent}LoadAsset {assetBundleName} : {assetName} {total:F1}ms（展開 {self:F1}ms）");
            return __exception;
        }

        private static Exception ExitUnload(Exception __exception, string assetBundleName)
        {
            Leave(out double total, out double self);
            Frame.Unload += self;
            if (ShouldLog(total)) _log.LogDebug($"[LoadProf] {Indent}閉じる {assetBundleName} {total:F1}ms");
            return __exception;
        }

        private static Exception ExitAnim(Exception __exception, ChaControl __instance, string assetName)
        {
            Leave(out double total, out double self);
            Frame.Anim += self;
            string who = __instance != null ? __instance.name : "?";
            Note($"LoadAnimation {who} {assetName}", total);
            if (ShouldLog(total)) _log.LogDebug($"[LoadProf] {Indent}LoadAnimation {who} {assetName} 計 {total:F1}ms（当て直し {self:F1}ms）");
            return __exception;
        }

        private static Exception ExitChange(Exception __exception, HSceneProc.AnimationListInfo _nextAinmInfo)
        {
            Leave(out double total, out double self);
            Frame.Change += self;
            string nm = _nextAinmInfo != null ? _nextAinmInfo.nameAnimation : "?";
            Note($"ChangeAnimator {nm}", total);
            if (ShouldLog(total)) _log.LogDebug($"[LoadProf] {Indent}ChangeAnimator（本体の体位変更）'{nm}' 計 {total:F1}ms（読み込み以外 {self:F1}ms）");
            return __exception;
        }

        internal static void ReportFrame(double frameMs)
        {
            double un = frameMs - Frame.TopTotal;
            _log.LogInfo($"[LoadProf] ヒッチ {frameMs:F0}ms：開く {Frame.Open:F0} / 展開 {Frame.Asset:F0} / 閉じる {Frame.Unload:F0}"
                + $" / LoadAnimation当て直し {Frame.Anim:F0} / ChangeAnimatorその他 {Frame.Change:F0} / 未計測 {un:F0}"
                + $"（件数 {Frame.Count}{(Frame.Worst != null ? $"・最大 {Frame.Worst} {Frame.WorstMs:F0}ms" : "")}）"
                + NamedTop(14));
        }
    }

    // 最初に回る Update で「前フレーム開始→今」の長さを見て、閾値超えなら内訳を出してリセットする。
    // （その間に走った全ての読み込みが Frame に積まれている）
    [DefaultExecutionOrder(-32000)]
    internal class LoadProfilerMonitor : MonoBehaviour
    {
        private void Update()
        {
            LoadProfiler.SyncPatches();
            if (!LoadProfiler.Active) return;
            double frameMs = Time.unscaledDeltaTime * 1000.0;
            if (frameMs >= LoadProfiler.HitchMs) LoadProfiler.ReportFrame(frameMs);
            LoadProfiler.Frame.Reset();
        }

        private void OnDestroy()
        {
            // 後始末はゲートしない（CLAUDE.md）。パッチを残さない。
            if (LoadProfiler.Active) LoadProfiler.SyncPatchesOff();
        }
    }
}
