using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace KK_HPerformance
{
    // v0.7.0: 画面に映っていないキャラの揺れ骨（DynamicBone / _Ver01 / _Ver02）を止める。
    //   v0.5.0 の計測で揺れ骨がメインスレッドの最大の消費者（19人で 9〜19ms/F）。映っていないキャラの分は誰にも見えない。
    //   キャラごとに毎フレーム、体の描画範囲（rendBody.bounds＋余白）がメインカメラの視錐台に入るかを判定し、
    //   0.5 秒続けて映っていなければ、その持ち主の揺れ骨の Update/LateUpdate を prefix で飛ばす（揺れ骨の enabled/重みは触らない）。
    //   映ったら ResetParticlesPosition で今の姿勢に合わせ直してから再開（止めている間に動いていても髪が跳ねない）。
    //   非表示のキャラ（visibleAll=false＝愛撫中の男・待機中の 3P 席等）も止める。
    internal static class DynamicBoneCulling
    {
        private const string HarmonyId = "KK_HPerformance.DynamicBoneCulling";
        private const float  HideDelay = 0.5f;     // 映っていない状態がこれだけ続いたら止める
        private const float  RescanSec = 1f;       // キャラ一覧の取り直し間隔
        private const float  LogSec    = 10f;

        private static ConfigEntry<bool>  _enabled;
        private static ConfigEntry<float> _margin;
        private static ConfigEntry<float> _maxDist;
        private static ManualLogSource    _log;
        private static Func<bool>         _hostEnabled;
        private static Harmony            _harmony;
        private static bool               _patched;

        private sealed class CharState
        {
            internal ChaControl Cha;
            internal bool  Culled;
            internal float LastVisible;
        }

        private static readonly Dictionary<int, ChaControl> _owner   = new Dictionary<int, ChaControl>();   // 揺れ骨 → 持ち主
        private static readonly Dictionary<int, CharState>  _chars   = new Dictionary<int, CharState>();    // キャラ → 状態
        private static readonly HashSet<int>                _culled  = new HashSet<int>();                  // 止めているキャラ
        private static ChaControl[] _list = new ChaControl[0];
        private static float _nextRescan, _nextLog;
        private static int   _lastLoggedCulled = -1;

        internal static void Init(ConfigFile cfg, ManualLogSource log, Func<bool> hostEnabled, GameObject host)
        {
            _log = log;
            _hostEnabled = hostEnabled;
            _enabled = cfg.Bind("Performance", "Dynamic Bone Off-Screen Culling", true,
                "Pause hair / skirt / breast physics of characters that are not on screen (or hidden), and resume it as soon as they "
                + "come into view. Characters outside the camera view cost nothing that way. Applies immediately.");
            _margin = cfg.Bind("Performance", "Culling Screen Margin (m)", 0.5f,
                new ConfigDescription("Extra space around each character's body when checking whether it is on screen, so hair or a skirt "
                    + "reaching past the body still counts as visible.", new AcceptableValueRange<float>(0f, 3f)));
            _maxDist = cfg.Bind("Performance", "Culling Max Distance (m)", 0f,
                new ConfigDescription("Also pause characters farther than this from the camera. 0 = off (only off-screen characters are paused).",
                    new AcceptableValueRange<float>(0f, 100f)));
            host.AddComponent<DynamicBoneCullingMonitor>();
        }

        internal static bool Active => _patched;

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
            var pre = new HarmonyMethod(typeof(DynamicBoneCulling), nameof(SkipPrefix));
            int n = 0;
            foreach (var t in new[] { typeof(DynamicBone), typeof(DynamicBone_Ver01), typeof(DynamicBone_Ver02) })
            {
                var up   = AccessTools.DeclaredMethod(t, "Update", Type.EmptyTypes);
                var late = AccessTools.DeclaredMethod(t, "LateUpdate", Type.EmptyTypes);
                var rst  = AccessTools.DeclaredMethod(t, "ResetParticlesPosition", Type.EmptyTypes);
                if (up == null || late == null || rst == null)
                {
                    _log.LogWarning($"[DynBone] {t.Name} の Update/LateUpdate/ResetParticlesPosition が見つからない"
                        + $"（{up != null}/{late != null}/{rst != null}）→ この型は画面外でも止めない。要調査");
                    continue;
                }
                try
                {
                    _harmony.Patch(up, prefix: pre);
                    _harmony.Patch(late, prefix: pre);
                    n++;
                }
                catch (Exception e) { _log.LogWarning($"[DynBone] {t.Name} の停止フックに失敗（{e.Message}）→ この型は止めない。要調査"); }
            }
            _patched = true;
            _owner.Clear(); _chars.Clear(); _culled.Clear();
            _nextRescan = 0f; _lastLoggedCulled = -1;
            _log.LogInfo($"[DynBone] 画面外のキャラの揺れ骨を止める（対象 {n}/3 型・余白 {_margin.Value:F1}m・距離 {(_maxDist.Value > 0f ? $"{_maxDist.Value:F0}m" : "なし")}）");
        }

        private static void Unpatch()
        {
            // 止めていたキャラを今の姿勢に合わせ直してから本体の動作へ戻す
            foreach (var st in _chars.Values)
                if (st.Culled) ResetBones(st.Cha);
            try { _harmony?.UnpatchSelf(); }
            catch (Exception e) { _log.LogWarning($"[DynBone] 停止フックの解除で例外（要調査）: {e.Message}"); }
            _patched = false;
            _owner.Clear(); _chars.Clear(); _culled.Clear();
            _log.LogInfo("[DynBone] 画面外の揺れ骨の停止を解除（本体の動作）");
        }

        // 揺れ骨の Update/LateUpdate の前: 持ち主が止まっていれば本体を飛ばす
        private static bool SkipPrefix(Component __instance)
        {
            if (_culled.Count == 0 || (object)__instance == null) return true;
            int id = __instance.GetInstanceID();
            if (!_owner.TryGetValue(id, out var cha))
            {
                cha = __instance.GetComponentInParent<ChaControl>();
                _owner[id] = cha;
            }
            if ((object)cha == null) return true;
            return !_culled.Contains(cha.GetInstanceID());
        }

        // ── Monitor から毎フレーム ──
        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now >= _nextRescan)
            {
                _nextRescan = now + RescanSec;
                try { _list = UnityEngine.Object.FindObjectsOfType<ChaControl>(); } catch { _list = new ChaControl[0]; }
                Prune();
            }

            var cam = Camera.main;
            Plane[] planes = cam != null ? GeometryUtility.CalculateFrustumPlanes(cam) : null;
            float margin = _margin.Value, maxDist = _maxDist.Value;

            foreach (var cha in _list)
            {
                if (cha == null) continue;
                int id = cha.GetInstanceID();
                if (!_chars.TryGetValue(id, out var st)) { st = new CharState { Cha = cha, LastVisible = now }; _chars[id] = st; }

                bool visible = true;   // カメラが無い・体の描画が無いキャラは止めない
                if (planes != null && cha.rendBody != null)
                {
                    if (!cha.visibleAll) visible = false;
                    else
                    {
                        var b = cha.rendBody.bounds;
                        b.Expand(margin * 2f);
                        visible = GeometryUtility.TestPlanesAABB(planes, b);
                        if (visible && maxDist > 0f && (b.center - cam.transform.position).sqrMagnitude > maxDist * maxDist) visible = false;
                    }
                }

                if (visible)
                {
                    st.LastVisible = now;
                    if (st.Culled)
                    {
                        st.Culled = false;
                        _culled.Remove(id);
                        ResetBones(cha);
                    }
                }
                else if (!st.Culled && now - st.LastVisible >= HideDelay)
                {
                    st.Culled = true;
                    _culled.Add(id);
                }
            }

            if (now >= _nextLog)
            {
                _nextLog = now + LogSec;
                if (_culled.Count != _lastLoggedCulled)
                {
                    _lastLoggedCulled = _culled.Count;
                    _log.LogDebug($"[DynBone] 画面外で揺れ骨を停止 {_culled.Count}/{_list.Length} 人");
                }
            }
        }

        // 消えたキャラ・揺れ骨の記録を外す
        private static void Prune()
        {
            List<int> dead = null;
            foreach (var kv in _chars) if (kv.Value.Cha == null) (dead ?? (dead = new List<int>())).Add(kv.Key);
            if (dead != null) foreach (var k in dead) { _chars.Remove(k); _culled.Remove(k); }
            dead = null;
            foreach (var kv in _owner) if (kv.Value == null) (dead ?? (dead = new List<int>())).Add(kv.Key);
            if (dead != null) foreach (var k in dead) _owner.Remove(k);
        }

        private static void ResetBones(ChaControl cha)
        {
            if (cha == null) return;
            try
            {
                foreach (var d in cha.GetComponentsInChildren<DynamicBone>(true)) d.ResetParticlesPosition();
                foreach (var d in cha.GetComponentsInChildren<DynamicBone_Ver01>(true)) d.ResetParticlesPosition();
                foreach (var d in cha.GetComponentsInChildren<DynamicBone_Ver02>(true)) d.ResetParticlesPosition();
            }
            catch (Exception e) { _log.LogWarning($"[DynBone] 揺れ骨の合わせ直しで例外（{cha.name}）: {e.Message}。要調査"); }
        }
    }

    [DefaultExecutionOrder(-31000)]
    internal class DynamicBoneCullingMonitor : MonoBehaviour
    {
        private void Update()
        {
            DynamicBoneCulling.SyncPatches();
            if (DynamicBoneCulling.Active) DynamicBoneCulling.Tick();
        }

        private void OnDestroy()
        {
            // 後始末はゲートしない（CLAUDE.md）。パッチを残さない。
            if (DynamicBoneCulling.Active) DynamicBoneCulling.SyncPatchesOff();
        }
    }
}
