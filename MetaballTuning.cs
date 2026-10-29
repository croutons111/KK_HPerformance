using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace KK_HPerformance
{
    // v0.8.0: 射精エフェクト「詳細」（メタボール精液）の負荷を下げる。
    //   本体は弾（Rigidbody）が1つでも動いている間、毎フレーム UpdateMeta.LateUpdate → StaticMetaballSeed.CreateMesh で
    //   メッシュを丸ごと作り直す（マーチングキューブ・メインスレッド・格子ぶんの配列を毎回 new）。弾は MetaCollider.sleepTime
    //   （本体 10 秒）で眠るまで動き続ける＝射精のあと 10 秒以上、毎フレーム重い。さらに CreateMesh は毎回 new Mesh() して
    //   meshFilter.sharedMesh を差し替えるだけで、古いメッシュを Destroy しない（ネイティブメモリのリーク）。
    //   ①古いメッシュの破棄（常時）②作り直しの間引き ③弾が眠るまでの時間の切り詰め ＋ 作り直しの計測要約。
    //   ★本体の処理なので、ヒロインにも、本体の部品を複製して使う他プラグインのキャラにも同じように効く。
    internal static class MetaballTuning
    {
        private const string HarmonyId = "KK_HPerformance.Metaball";
        private const float  SummarySec = 5f;

        private static ConfigEntry<int>   _interval;
        private static ConfigEntry<float> _settle;
        private static ConfigEntry<int>   _cellBudget;
        private const double HeavyBuildMs = 50.0;   // これを超えた作り直しは1件ずつログ
        private static ManualLogSource    _log;
        private static Func<bool>         _hostEnabled;
        private static Harmony            _harmony;
        private static bool               _patched;

        private static AccessTools.FieldRef<UpdateMeta, bool> _fIsCreate;

        // CreateMesh が作ったメッシュ（InstanceID）。これだけを破棄する＝プレハブ元のメッシュ（アセット）は壊さない。
        private static readonly HashSet<int> _ownMeshes = new HashSet<int>();

        private sealed class State { public int LastBuild = -9999; public bool Pending; }
        private static readonly Dictionary<int, State> _states = new Dictionary<int, State>();

        // 計測（数秒ごとの要約）
        private static int    _sumBuilds, _sumSkipped, _sumDestroyed, _maxBalls, _sumCoarsened, _sumGc;
        private static double _sumMs, _maxMs;
        private static float  _maxCells;
        private static float  _windowStart;
        private static int    _gcAtWindow;

        internal static void Init(ConfigFile cfg, ManualLogSource log, Func<bool> hostEnabled, GameObject host)
        {
            _log = log;
            _hostEnabled = hostEnabled;
            _interval = cfg.Bind("Performance", "Metaball Rebuild Interval", 2,
                new ConfigDescription(
                    "When the ejaculation effect is set to 'detailed', the game rebuilds the semen mesh every frame while any drop "
                    + "is still moving, which is heavy. This rebuilds it only every N frames instead. 1 = game default (every frame). "
                    + "2 = half the work; the surface may move slightly less smoothly. A final rebuild always happens when the drops "
                    + "come to rest. Applies immediately.",
                    new AcceptableValueRange<int>(1, 4)));
            _settle = cfg.Bind("Performance", "Metaball Settle Time", 6f,
                new ConfigDescription(
                    "Seconds before a semen drop is frozen in place. The mesh keeps being rebuilt until the last drop freezes, "
                    + "so a shorter time shortens the heavy period. 10 = game default. Lower values make dripping stop sooner. "
                    + "Applies to drops created after the change.",
                    new AcceptableValueRange<float>(2f, 10f)));
            _cellBudget = cfg.Bind("Performance", "Metaball Cell Budget", 0,
                new ConfigDescription(
                    "When a rebuild of the semen mesh would need more grid cells than this, the grid is made coarser for that "
                    + "rebuild only (the surface looks slightly rougher for a moment). Measured rebuilds stay well under 50000 cells, "
                    + "so this rarely matters. 0 = no limit (game default). Applies immediately.",
                    new AcceptableValueRange<int>(0, 1000000)));
            host.AddComponent<MetaballTuningMonitor>();
        }

        private static int   Interval => _interval != null ? _interval.Value : 1;
        private static float Settle   => _settle   != null ? _settle.Value   : 10f;
        private static int   CellBudget => _cellBudget != null ? _cellBudget.Value : 0;

        internal static bool Active => _patched;

        internal static void SyncPatches()
        {
            bool want = _hostEnabled == null || _hostEnabled();
            if (want == _patched) return;
            if (want) Patch(); else Unpatch();
        }

        internal static void SyncPatchesOff() { if (_patched) Unpatch(); }

        private static void Patch()
        {
            if (_harmony == null) _harmony = new Harmony(HarmonyId);
            _patched = true;   // 失敗しても毎フレーム再試行しない（個別に LogWarning 済み）
            int n = 0;
            n += TryPatch("UpdateMeta.LateUpdate", () =>
            {
                _fIsCreate = AccessTools.FieldRefAccess<UpdateMeta, bool>("isCreate");
                var m = AccessTools.DeclaredMethod(typeof(UpdateMeta), "LateUpdate");
                if (m == null) throw new MissingMethodException("UpdateMeta", "LateUpdate");
                _harmony.Patch(m, prefix: new HarmonyMethod(typeof(MetaballTuning), nameof(PrefixLateUpdate)));
            });
            n += TryPatch("StaticMetaballSeed.CreateMesh", () =>
            {
                var m = AccessTools.DeclaredMethod(typeof(StaticMetaballSeed), "CreateMesh");
                if (m == null) throw new MissingMethodException("StaticMetaballSeed", "CreateMesh");
                _harmony.Patch(m, prefix:  new HarmonyMethod(typeof(MetaballTuning), nameof(PrefixCreateMesh)),
                                  postfix: new HarmonyMethod(typeof(MetaballTuning), nameof(PostfixCreateMesh)));
            });
            n += TryPatch("MetaCollider.Start", () =>
            {
                var m = AccessTools.DeclaredMethod(typeof(MetaCollider), "Start");
                if (m == null) throw new MissingMethodException("MetaCollider", "Start");
                _harmony.Patch(m, postfix: new HarmonyMethod(typeof(MetaballTuning), nameof(PostfixColliderStart)));
            });
            // #13: 格子配列6本の使い回し（GC の引き金を断つ）。想定の6か所が見つからなければ Transpiler は元の IL を返す＝本体のまま。
            n += TryPatch("MetaballBuilder.BuildMetaballMesh", () =>
            {
                var m = AccessTools.DeclaredMethod(typeof(MetaballBuilder), "BuildMetaballMesh");
                if (m == null) throw new MissingMethodException("MetaballBuilder", "BuildMetaballMesh");
                _harmony.Patch(m, transpiler: new HarmonyMethod(typeof(MetaballTuning), nameof(TranspileBuild)));
                if (!MetaballArrayPool.Active)
                    throw new InvalidOperationException($"格子配列の new が想定の6か所と一致しない（見つかった {_poolMatched} か所）");
            });
            _windowStart = Time.realtimeSinceStartup;
            _log.LogInfo($"[Metaball] メタボールの負荷対策を有効化（フック {n}/4・作り直し {Interval} フレームごと・弾の停止 {Settle:F1} 秒・格子の予算 {CellBudget} セル・格子配列の使い回し {(MetaballArrayPool.Active ? "ON" : "OFF")}）");
        }

        private static int TryPatch(string label, Action patch)
        {
            try { patch(); return 1; }
            catch (Exception e)
            {
                _log.LogWarning($"[Metaball] {label} にフックできない（{e.Message}）→ この部分は本体の動作のまま。要調査");
                return 0;
            }
        }

        // ── ⑤ 格子配列の使い回し（#13）────────────────────────────────────
        // BuildMetaballMesh の「計算したサイズで new する配列」（直前が mul の newarr）6本を、
        // MetaballArrayPool.Rent*(size, slot) の呼び出しに差し替える。固定長の小配列（int[15]/int[12]）は対象外。
        // 本体は配列の .Length を使わず計算したサイズでのみ添字を回す＝長めの配列を渡しても動作は同じ（静的確認済み）。
        private static int _poolMatched;

        private static IEnumerable<CodeInstruction> TranspileBuild(IEnumerable<CodeInstruction> instructions)
        {
            var src = new List<CodeInstruction>(instructions);
            var dst = new List<CodeInstruction>(src.Count + 12);
            int matched = 0, vecSlot = 0;
            for (int i = 0; i < src.Count; i++)
            {
                var ins = src[i];
                bool sized = ins.opcode == OpCodes.Newarr && i > 0 && src[i - 1].opcode == OpCodes.Mul;
                string rent = null;
                int slot = 0;
                if (sized)
                {
                    var t = ins.operand as Type;
                    if      (t == typeof(float))      rent = nameof(MetaballArrayPool.RentFloat);
                    else if (t == typeof(Vector3))    { rent = nameof(MetaballArrayPool.RentVector3); slot = vecSlot++; }
                    else if (t == typeof(int))        rent = nameof(MetaballArrayPool.RentInt);
                    else if (t == typeof(bool))       rent = nameof(MetaballArrayPool.RentBool);
                    else if (t == typeof(BoneWeight)) rent = nameof(MetaballArrayPool.RentBoneWeight);
                }
                if (rent == null) { dst.Add(ins); continue; }
                var ld = new CodeInstruction(OpCodes.Ldc_I4, slot);
                ld.labels.AddRange(ins.labels);
                ld.blocks.AddRange(ins.blocks);
                dst.Add(ld);
                dst.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MetaballArrayPool), rent)));
                matched++;
            }
            _poolMatched = matched;
            bool ok = matched == 6 && vecSlot == 2;
            MetaballArrayPool.Active = ok;
            return ok ? dst : src;
        }

        private static void Unpatch()
        {
            try { _harmony?.UnpatchSelf(); }
            catch (Exception e) { _log.LogWarning($"[Metaball] フック解除で例外（要調査）: {e.Message}"); }
            _patched = false;
            MetaballArrayPool.Active = false;
            MetaballArrayPool.Release();
            _states.Clear();
            _log.LogInfo("[Metaball] メタボールの負荷対策を解除（本体の動作）");
        }

        // ── ② 作り直しの間引き ────────────────────────────────────────────
        // 本体 UpdateMeta.LateUpdate を同じ判定で置き換える（isCreate＝弾が1つでも動いているか を更新し、
        // 動いている間は N フレームごとに CreateMesh）。isCreate は ConstMetaMesh が読むので毎フレーム更新する。
        // 間引いたまま弾が止まったら、最後に1回だけ作り直す（止まった姿が古いまま残らないように）。
        private static bool PrefixLateUpdate(UpdateMeta __instance)
        {
            bool creating = false;
            var shots = __instance.lstShoot;
            for (int i = 0; i < shots.Count; i++)
                if (shots[i].IsCreate()) { creating = true; break; }
            _fIsCreate(__instance) = creating;

            int id = __instance.GetInstanceID();
            if (!_states.TryGetValue(id, out var st)) _states[id] = st = new State();
            int frame = Time.frameCount;
            if (creating)
            {
                if (frame - st.LastBuild >= Interval) Build(__instance, st, frame);
                else { st.Pending = true; _sumSkipped++; }
            }
            else if (st.Pending)
            {
                Build(__instance, st, frame);   // 止まった瞬間の最終形
            }
            return false;
        }

        private static void Build(UpdateMeta um, State st, int frame)
        {
            st.LastBuild = frame;
            st.Pending = false;
            var seed = um.metaseed;
            if (!seed) return;

            // ④ 格子のセル数（本体 MetaballBuilder と同じ式）を作り直しの前に求め、予算を超える回だけ一時的に粗くする。
            float g0 = seed.gridSize;
            float cells = EstimateCells(seed, seed.GridSize);
            float gUsed = seed.GridSize;
            int budget = CellBudget;
            bool coarsened = budget > 0 && cells > budget && !seed.bAutoGridSize;
            if (coarsened)
            {
                gUsed = CoarserGrid(seed, budget, cells, seed.GridSize);
                seed.gridSize = gUsed;
                _sumCoarsened++;
            }

            int balls = 0;
            try
            {
                foreach (var s in um.lstShoot)
                    if (s.objSourceRoot) balls += s.objSourceRoot.transform.childCount;
            }
            catch { /* 計測のみ */ }

            // #12: 重い回が GC（Boehm の全体回収）によるものかを確かめる＝前後で回収回数を比べる
            int gc0 = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            try { seed.CreateMesh(); }
            finally { if (coarsened) seed.gridSize = g0; }   // 必ず元の細かさへ戻す
            sw.Stop();
            int gcRan = GC.CollectionCount(0) - gc0;
            if (gcRan > 0) _sumGc += gcRan;
            double ms = sw.Elapsed.TotalMilliseconds;
            _sumBuilds++;
            _sumMs += ms;
            if (ms > _maxMs) _maxMs = ms;
            if (balls > _maxBalls) _maxBalls = balls;
            if (cells > _maxCells) _maxCells = cells;
            if (ms > HeavyBuildMs)
                _log.LogDebug(string.Format(
                    "[Metaball] 重い作り直し {0:F1}ms・格子 約{1:F0}セル（gridSize {2:F4}{3}）・弾 {4}個・作り直し中の GC {5}回",
                    ms, cells, g0, coarsened ? string.Format("→この回だけ {0:F4}", gUsed) : "", balls, gcRan));
        }

        // セル数は gridSize の3乗に反比例（端数の +1/×2 があるので最大2回まで詰める）
        private static float CoarserGrid(StaticMetaballSeed seed, int budget, float cells, float g0)
        {
            float g = Mathf.Max(float.Epsilon, g0) * Mathf.Pow(cells / budget, 1f / 3f);
            for (int k = 0; k < 2 && EstimateCells(seed, g) > budget; k++) g *= 1.1f;
            return g;
        }

        // 本体 StaticMetaballSeed.CreateMesh → MetaballBuilder と同じ箱でセル数を見積もる:
        //   箱＝sourceRoot 配下の MetaballNode の（中心±半径）をメッシュのローカル系（中心＝ワールド箱の中心）で囲み、
        //   原点を含み、fixedMin/fixedMax でクランプ（bUseFixedBounds なら fixedBounds）。
        //   セル数＝各軸 (ceil(半幅/g)+1)×2 の積（BuildMetaballMesh の配列の大きさ）。
        private static float EstimateCells(StaticMetaballSeed seed, float g)
        {
            try
            {
                if (g <= 0f || seed.sourceRoot == null || seed.meshFilter == null) return 0f;
                Vector3 size;
                if (seed.bUseFixedBounds) size = seed.fixedBounds.size;
                else
                {
                    _nodes.Clear();
                    seed.sourceRoot.GetComponentsInChildren(true, _nodes);
                    var root = seed.sourceRoot.transform;
                    var wb = new Bounds();
                    bool first = true;
                    foreach (var n in _nodes)
                    {
                        if (n.transform == root) continue;
                        var p = n.transform.position;
                        var r = Vector3.one * n.Radius;
                        if (first) { wb = new Bounds(p, r * 2f); first = false; }
                        else { wb.Encapsulate(p - r); wb.Encapsulate(p + r); }
                    }
                    if (first) return 0f;
                    var mt  = seed.meshFilter.transform;
                    var inv = Quaternion.Inverse(mt.rotation);
                    var sc  = mt.lossyScale;
                    Vector3 lo = Vector3.zero, hi = Vector3.zero;   // 本体は原点（サイズ0）から広げる
                    foreach (var n in _nodes)
                    {
                        if (n.transform == root) continue;
                        var lp = inv * (n.transform.position - wb.center);
                        lp = new Vector3(lp.x / Nz(sc.x), lp.y / Nz(sc.y), lp.z / Nz(sc.z));
                        float r = n.Radius;
                        for (int i = 0; i < 3; i++)
                        {
                            lo[i] = Mathf.Min(lo[i], Mathf.Clamp(lp[i] - r, seed.fixedMin[i], seed.fixedMax[i]));
                            hi[i] = Mathf.Max(hi[i], Mathf.Clamp(lp[i] + r, seed.fixedMin[i], seed.fixedMax[i]));
                        }
                    }
                    size = hi - lo;
                }
                float c = 1f;
                for (int i = 0; i < 3; i++) c *= (Mathf.Ceil(size[i] * 0.5f / g) + 1f) * 2f;
                return c;
            }
            catch { return 0f; }
        }

        private static float Nz(float v) => Mathf.Abs(v) < 1e-6f ? 1f : v;
        private static readonly List<MetaballNode> _nodes = new List<MetaballNode>();

        // ── ① 古いメッシュの破棄 ─────────────────────────────────────────
        private static void PrefixCreateMesh(StaticMetaballSeed __instance, out Mesh __state)
        {
            __state = __instance.meshFilter ? __instance.meshFilter.sharedMesh : null;
        }

        private static void PostfixCreateMesh(StaticMetaballSeed __instance, Mesh __state)
        {
            var now = __instance.meshFilter ? __instance.meshFilter.sharedMesh : null;
            if (now == null || now == __state) return;   // 失敗時は差し替わらない
            _ownMeshes.Add(now.GetInstanceID());
            if (__state != null && _ownMeshes.Remove(__state.GetInstanceID()))
            {
                UnityEngine.Object.Destroy(__state);
                _sumDestroyed++;
            }
        }

        // ── ③ 弾が眠るまでの時間 ──────────────────────────────────────────
        private static void PostfixColliderStart(MetaCollider __instance)
        {
            float s = Settle;
            if (__instance.sleepTime > s) __instance.sleepTime = s;
        }

        // 数秒ごとの要約（作り直しがあった区間だけ）
        internal static void TickSummary()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _windowStart < SummarySec) return;
            if (_sumBuilds > 0 || _sumSkipped > 0)
                _log.LogDebug(string.Format(
                    "[Metaball] {0:F0}秒: 作り直し {1}回（間引き {2}回・粗くした {8}回）平均 {3:F2}ms 最大 {4:F2}ms・弾 最大{5}個・格子 最大約{6:F0}セル・古いメッシュ破棄 {7}個・作り直し中の GC {9}回／区間全体の GC {10}回・格子配列 使い回し {11}回／新規確保 {12}回",
                    now - _windowStart, _sumBuilds, _sumSkipped, _sumBuilds > 0 ? _sumMs / _sumBuilds : 0.0, _maxMs,
                    _maxBalls, _maxCells, _sumDestroyed, _sumCoarsened, _sumGc, GC.CollectionCount(0) - _gcAtWindow,
                    MetaballArrayPool.Reused, MetaballArrayPool.Fresh));
            _windowStart = now;
            _sumBuilds = _sumSkipped = _sumDestroyed = _maxBalls = _sumCoarsened = _sumGc = 0;
            _gcAtWindow = GC.CollectionCount(0);
            MetaballArrayPool.Reused = MetaballArrayPool.Fresh = 0;
            _sumMs = _maxMs = 0;
            _maxCells = 0f;
            if (_states.Count > 64) _states.Clear();   // 破棄済みの組の掃除（次のフレームで作り直される）
        }
    }

    // #13: BuildMetaballMesh の格子配列の使い回し。メインスレッドの1呼び出し内でしか使われない（本体の局所変数）
    //   ＝1組を共有してよい。毎回、必要な長さまで 0 クリアして「new した直後」と同じ状態で渡す。
    //   大きすぎる格子（MaxCells 超）は本体どおり新規確保（巨大な配列を抱え込まない）。
    public static class MetaballArrayPool
    {
        private const int MaxCells = 300000;

        internal static bool Active;
        internal static int  Reused, Fresh;   // 作り直し単位（float 配列で数える＝1回の作り直しに1本）

        private static float[]      _f;
        private static readonly Vector3[][] _v = new Vector3[2][];
        private static int[]        _i;
        private static bool[]       _b;
        private static BoneWeight[] _w;

        internal static void Release() { _f = null; _v[0] = _v[1] = null; _i = null; _b = null; _w = null; }

        private static T[] Rent<T>(ref T[] buf, int n, int cap)
        {
            if (n > cap) return new T[n];
            if (buf == null || buf.Length < n) buf = new T[n];
            else Array.Clear(buf, 0, n);
            return buf;
        }

        public static float[] RentFloat(int n, int slot)
        {
            if (n > MaxCells) Fresh++; else Reused++;
            return Rent(ref _f, n, MaxCells);
        }

        public static Vector3[] RentVector3(int n, int slot) => Rent(ref _v[slot & 1], n, MaxCells);
        public static int[]     RentInt(int n, int slot)     => Rent(ref _i, n, MaxCells * 3);
        public static bool[]    RentBool(int n, int slot)    => Rent(ref _b, n, MaxCells);
        public static BoneWeight[] RentBoneWeight(int n, int slot) => Rent(ref _w, n, MaxCells);
    }

    internal class MetaballTuningMonitor : MonoBehaviour
    {
        private void Update()
        {
            MetaballTuning.SyncPatches();
            if (MetaballTuning.Active) MetaballTuning.TickSummary();
        }

        private void OnDestroy()
        {
            // 後始末はゲートしない（CLAUDE.md）。パッチを残さない。
            if (MetaballTuning.Active) MetaballTuning.SyncPatchesOff();
        }
    }
}
