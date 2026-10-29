using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace KK_HPerformance
{
    // フレームの重さを部品別に測る（v0.5.0）。キャラ（ゲスト）を増やすと FPS が落ちるのに CPU/GPU 使用率に余裕がある
    // ＝メインスレッド1本の頭打ち。その1本を誰がどれだけ使っているかを、本体のキャラ処理・各プラグインの毎フレーム処理に
    // 時計を差して集計する。挙動は一切変えない。専用 Harmony インスタンスで手動パッチし、OFF で Unpatch＝負荷 0。
    //   (a) BepInEx/plugins の各アセンブリの MonoBehaviour.Update/LateUpdate/FixedUpdate/OnGUI を自動で全部
    //   (b) 本体のキャラ処理（揺れ骨・IK・ChaControl 更新・表情/目線/首・ペニス向き・HSceneProc）を名前指定
    //   (c) フレーム時間とキャラ数
    // 集計は自分時間（フックしたメソッドの入れ子は親から引く）。コルーチンは測らない（未計測に入る）。
    internal static class FrameProfiler
    {
        private const string HarmonyId = "KK_HPerformance.FrameProfiler";

        private static ConfigEntry<bool>  _enabled;
        private static ConfigEntry<float> _interval;
        private static ConfigEntry<int>   _topN;
        private static ManualLogSource    _log;
        private static Func<bool>         _hostEnabled;
        private static Harmony            _harmony;
        private static bool               _patched;

        private sealed class Stat
        {
            internal string Name;
            internal string Asm;
            internal long   SelfTicks;
            internal int    Calls;
            internal readonly HashSet<int> Instances = new HashSet<int>();
        }

        private struct Entry { internal long Start; internal long Child; }

        private static readonly Dictionary<MethodBase, Stat> _stats = new Dictionary<MethodBase, Stat>();
        private static readonly Stack<Entry> _stack = new Stack<Entry>();
        private static readonly Stopwatch _sw = Stopwatch.StartNew();
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        private static int    _frames;
        private static double _frameMsSum;
        private static float  _windowStart;

        // (b) 本体のキャラ処理（型名, メソッド名）。見つからなければ LogDebug で除外。
        private static readonly string[][] GameTargets =
        {
            new[] { "DynamicBone", "Update" }, new[] { "DynamicBone", "LateUpdate" }, new[] { "DynamicBone", "FixedUpdate" },
            new[] { "DynamicBone_Ver01", "Update" }, new[] { "DynamicBone_Ver01", "LateUpdate" }, new[] { "DynamicBone_Ver01", "FixedUpdate" },
            new[] { "DynamicBone_Ver02", "Update" }, new[] { "DynamicBone_Ver02", "LateUpdate" }, new[] { "DynamicBone_Ver02", "FixedUpdate" },
            new[] { "RootMotion.FinalIK.SolverManager", "Update" },
            new[] { "RootMotion.FinalIK.SolverManager", "LateUpdate" },
            new[] { "RootMotion.FinalIK.SolverManager", "FixedUpdate" },
            new[] { "Manager.Character", "Update" },
            new[] { "Manager.Character", "LateUpdate" },
            new[] { "ChaControl", "UpdateForce" },
            new[] { "ChaControl", "LateUpdateForce" },
            new[] { "FaceBlendShape", "LateUpdate" },
            new[] { "EyeLookController", "LateUpdate" },
            new[] { "NeckLookControllerVer2", "LateUpdate" },
            new[] { "Lookat_dan", "LateUpdate" },
            new[] { "HSceneProc", "Update" },
            new[] { "HSceneProc", "LateUpdate" },
            // v0.8.0: メタボール精液（射精エフェクト「詳細」）
            new[] { "UpdateMeta", "LateUpdate" },
            new[] { "FinalIKAfter", "LateUpdate" },
            new[] { "MetaCollider", "Update" },
            new[] { "MetaCollider", "FixedUpdate" },
            new[] { "SkinnedCollisionHelper", "LateUpdate" },   // 当たり判定メッシュの毎フレーム作り直し（Rankou #428d の切り分け）
        };

        private static readonly string[] AutoMethodNames = { "Update", "LateUpdate", "FixedUpdate", "OnGUI" };

        internal static void Init(ConfigFile cfg, ManualLogSource log, Func<bool> hostEnabled, GameObject host)
        {
            _log = log;
            _hostEnabled = hostEnabled;
            _enabled = cfg.Bind("Debug", "Frame Profiler", false,
                "Diagnostic: measure how much of each frame goes to each part (the game's character processing such as hair/skirt physics and IK, "
                + "and every plugin's per-frame updates), and log a summary every few seconds together with the FPS and the number of characters. "
                + "For finding out why the FPS drops when more characters are on screen. Measuring itself costs a little FPS. "
                + "Does not change any behavior. Applies immediately.");
            _interval = cfg.Bind("Debug", "Frame Profiler Report Interval (sec)", 5f,
                new ConfigDescription("How often the summary is logged.", new AcceptableValueRange<float>(1f, 60f)));
            _topN = cfg.Bind("Debug", "Frame Profiler Top N", 20,
                new ConfigDescription("How many of the heaviest methods are listed in each summary.", new AcceptableValueRange<int>(5, 100)));
            host.AddComponent<FrameProfilerMonitor>();
        }

        internal static bool Active => _patched;
        internal static float Interval => _interval != null ? _interval.Value : 5f;

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
            var pre = new HarmonyMethod(typeof(FrameProfiler), nameof(Enter)) { priority = Priority.First };
            var fin = new HarmonyMethod(typeof(FrameProfiler), nameof(Exit)) { priority = Priority.Last };
            _stats.Clear();
            _stack.Clear();

            int game = 0, gameMiss = 0;
            foreach (var t in GameTargets)
            {
                var type = AccessTools.TypeByName(t[0]);
                var m = type != null ? AccessTools.DeclaredMethod(type, t[1], Type.EmptyTypes) : null;
                if (m == null) { gameMiss++; _log.LogDebug($"[FrameProf] 本体の {t[0]}.{t[1]} は無い → 除外"); continue; }
                if (TryHook(m, pre, fin, $"{t[0]}.{t[1]}")) game++;
            }

            int auto = 0, asms = 0, failed = 0;
            string pluginsDir = Path.GetFullPath(BepInEx.Paths.PluginPath);
            var own = typeof(FrameProfiler).Assembly;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == own) continue;
                string loc;
                try { loc = asm.Location; } catch { continue; }
                if (string.IsNullOrEmpty(loc)) continue;
                string full;
                try { full = Path.GetFullPath(loc); } catch { continue; }
                if (!full.StartsWith(pluginsDir, StringComparison.OrdinalIgnoreCase)) continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                catch { continue; }
                bool any = false;
                foreach (var type in types)
                {
                    if (type == null || type.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(type)) continue;
                    foreach (var name in AutoMethodNames)
                    {
                        MethodInfo m;
                        try { m = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null); }
                        catch { continue; }
                        if (m == null || m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                        if (TryHook(m, pre, fin, null)) { auto++; any = true; } else failed++;
                    }
                }
                if (any) asms++;
            }

            _patched = true;
            ResetWindow();
            _log.LogInfo($"[FrameProf] 計測 ON（本体 {game} メソッド{(gameMiss > 0 ? $"・無し {gameMiss}" : "")}／プラグイン {asms} アセンブリ {auto} メソッド"
                + $"{(failed > 0 ? $"・フック失敗 {failed}" : "")}）。{Interval:F0} 秒ごとに集計を出す");
        }

        private static bool TryHook(MethodBase m, HarmonyMethod pre, HarmonyMethod fin, string label)
        {
            try
            {
                _harmony.Patch(m, prefix: pre, finalizer: fin);
                var dt = m.DeclaringType;
                _stats[m] = new Stat
                {
                    Name = label ?? $"{dt?.FullName}.{m.Name}",
                    Asm  = dt != null ? dt.Assembly.GetName().Name : "?",
                };
                return true;
            }
            catch (Exception e)
            {
                _log.LogDebug($"[FrameProf] フック失敗 {m.DeclaringType?.FullName}.{m.Name}: {e.Message}");
                return false;
            }
        }

        private static void Unpatch()
        {
            try { _harmony?.UnpatchSelf(); }
            catch (Exception e) { _log.LogWarning($"[FrameProf] フック解除で例外（要調査）: {e.Message}"); }
            _patched = false;
            _stack.Clear();
            _log.LogInfo("[FrameProf] 計測 OFF（フックを全て解除）");
        }

        private static void Enter()
        {
            _stack.Push(new Entry { Start = _sw.ElapsedTicks, Child = 0 });
        }

        private static Exception Exit(Exception __exception, MethodBase __originalMethod, object __instance)
        {
            if (_stack.Count == 0) return __exception;
            var e = _stack.Pop();
            long total = _sw.ElapsedTicks - e.Start;
            long self = total - e.Child;
            if (_stack.Count > 0)
            {
                var parent = _stack.Pop();
                parent.Child += total;
                _stack.Push(parent);
            }
            if (__originalMethod != null && _stats.TryGetValue(__originalMethod, out var st))
            {
                st.SelfTicks += self;
                st.Calls++;
                if (__instance is UnityEngine.Object uo) st.Instances.Add(uo.GetInstanceID());
            }
            return __exception;
        }

        // ── Monitor から ──
        internal static void OnFrame(double frameMs)
        {
            _frames++;
            _frameMsSum += frameMs;
            // 例外等で入れ子が崩れたら捨てる（フレーム境界ではスタックは空のはず）
            if (_stack.Count > 0) _stack.Clear();
            if (Time.unscaledTime - _windowStart >= Interval) Report();
        }

        private static void ResetWindow()
        {
            _frames = 0;
            _frameMsSum = 0;
            _windowStart = Time.unscaledTime;
            foreach (var s in _stats.Values) { s.SelfTicks = 0; s.Calls = 0; s.Instances.Clear(); }
        }

        private static void Report()
        {
            if (_frames <= 0) { ResetWindow(); return; }
            double sec = Time.unscaledTime - _windowStart;
            double avgFrame = _frameMsSum / _frames;
            int chars = 0;
            try { chars = UnityEngine.Object.FindObjectsOfType<ChaControl>().Count(c => c != null && c.isActiveAndEnabled); } catch { }

            double measured = 0;
            var byAsm = new Dictionary<string, double>();
            foreach (var s in _stats.Values)
            {
                if (s.Calls == 0) continue;
                double perFrame = s.SelfTicks * TicksToMs / _frames;
                measured += perFrame;
                byAsm.TryGetValue(s.Asm, out double a);
                byAsm[s.Asm] = a + perFrame;
            }
            var sb = new System.Text.StringBuilder();
            sb.Append($"[FrameProf] {sec:F1}秒 フレーム {_frames}（{_frames / sec:F1}fps・平均 {avgFrame:F1}ms）キャラ {chars}人");
            sb.Append($"\n  計測合計 {measured:F2}ms/F（未計測 {Math.Max(0, avgFrame - measured):F2}ms＝描画命令・スキニング・アニメーション評価・コルーチン等）");
            sb.Append("\n  アセンブリ別: ");
            sb.Append(string.Join(" / ", byAsm.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} {kv.Value:F2}").ToArray()));
            sb.Append("\n  上位:");
            foreach (var s in _stats.Values.Where(x => x.Calls > 0).OrderByDescending(x => x.SelfTicks).Take(_topN != null ? _topN.Value : 20))
            {
                double perFrame = s.SelfTicks * TicksToMs / _frames;
                int inst = s.Instances.Count;
                string per = inst > 1 ? $"・{inst}個・{perFrame / inst:F3}ms/個" : "";
                sb.Append($"\n    {s.Name} {perFrame:F2}ms/F（{(double)s.Calls / _frames:F1}回/F{per}）[{s.Asm}]");
            }
            _log.LogInfo(sb.ToString());
            ResetWindow();
        }
    }

    // フレーム境界（最初に回る Update）で前フレームの長さを積み、間隔ごとに集計を出す。
    [DefaultExecutionOrder(-32000)]
    internal class FrameProfilerMonitor : MonoBehaviour
    {
        private void Update()
        {
            FrameProfiler.SyncPatches();
            if (!FrameProfiler.Active) return;
            FrameProfiler.OnFrame(Time.unscaledDeltaTime * 1000.0);
        }

        private void OnDestroy()
        {
            // 後始末はゲートしない（CLAUDE.md）。パッチを残さない。
            if (FrameProfiler.Active) FrameProfiler.SyncPatchesOff();
        }
    }
}
