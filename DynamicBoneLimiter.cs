using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace KK_HLoadCache
{
    // v0.6.0: 揺れ骨（DynamicBone / _Ver01 / _Ver02）の「追いつき計算」を1フレームN回までに制限する。
    //   本体 UpdateDynamicBones(t) は経過時間を貯めて 1/UpdateRate(=60) 秒ごとに1回計算し、1フレーム最大3回まで追いつく
    //   ＝FPS が 30 を切ると計算が2〜3倍に増えてさらに落ちる悪循環（v0.5.0 の計測: 19人で 19.4ms/F・0.043ms/個）。
    //   prefix で t を「このフレームの計算回数が上限を超えない長さ」に切り詰めるだけ（計算式・衝突判定は触らない）。
    //   60fps 以上ではもともと1回以下＝変化なし。低FPS時は揺れの時間がゆっくり進む（副作用）。
    internal static class DynamicBoneLimiter
    {
        private const string HarmonyId = "KK_HLoadCache.DynamicBone";

        private static ConfigEntry<int> _maxSteps;
        private static ManualLogSource  _log;
        private static Func<bool>       _hostEnabled;
        private static Harmony          _harmony;
        private static bool             _patched;

        private static AccessTools.FieldRef<DynamicBone, float>       _v0Rate, _v0Time;
        private static AccessTools.FieldRef<DynamicBone_Ver01, float> _v1Rate, _v1Time;
        private static AccessTools.FieldRef<DynamicBone_Ver02, float> _v2Rate, _v2Time;

        internal static void Init(ConfigFile cfg, ManualLogSource log, Func<bool> hostEnabled, GameObject host)
        {
            _log = log;
            _hostEnabled = hostEnabled;
            _maxSteps = cfg.Bind("Performance", "Dynamic Bone Max Steps", 1,
                new ConfigDescription(
                    "Hair / skirt / breast physics normally runs 60 times per second and, when the FPS drops, catches up by running "
                    + "up to 3 times in one frame - which makes a low FPS even lower when many characters are on screen. "
                    + "This caps how many times it may run per frame. 1 = no catching up (fastest; below 60 FPS the swaying slows down a little). "
                    + "2 = catch up to 30 FPS. 3 = game default (no change). Has no effect while the game runs at 60 FPS or more. Applies immediately.",
                    new AcceptableValueRange<int>(1, 3)));
            host.AddComponent<DynamicBoneLimiterMonitor>();
        }

        internal static int MaxSteps => _maxSteps != null ? _maxSteps.Value : 3;

        // 毎フレーム（Monitor から）: Config / 全体スイッチの状態にパッチを合わせる。
        internal static void SyncPatches()
        {
            bool want = MaxSteps < 3 && (_hostEnabled == null || _hostEnabled());
            if (want == _patched) return;
            if (want) Patch(); else Unpatch();
        }

        internal static void SyncPatchesOff() { if (_patched) Unpatch(); }
        internal static bool Active => _patched;

        private static void Patch()
        {
            if (_harmony == null) _harmony = new Harmony(HarmonyId);
            int n = 0;
            n += TryPatch(typeof(DynamicBone), nameof(PrefixV0), () =>
            {
                _v0Rate = AccessTools.FieldRefAccess<DynamicBone, float>("m_UpdateRate");
                _v0Time = AccessTools.FieldRefAccess<DynamicBone, float>("m_Time");
            });
            n += TryPatch(typeof(DynamicBone_Ver01), nameof(PrefixV1), () =>
            {
                _v1Rate = AccessTools.FieldRefAccess<DynamicBone_Ver01, float>("m_UpdateRate");
                _v1Time = AccessTools.FieldRefAccess<DynamicBone_Ver01, float>("m_Time");
            });
            n += TryPatch(typeof(DynamicBone_Ver02), nameof(PrefixV2), () =>
            {
                _v2Rate = AccessTools.FieldRefAccess<DynamicBone_Ver02, float>("UpdateRate");
                _v2Time = AccessTools.FieldRefAccess<DynamicBone_Ver02, float>("UpdateTime");
            });
            _patched = true;
            _log.LogInfo($"[DynBone] 揺れ骨の追いつき計算を 1フレーム {MaxSteps} 回までに制限（対象 {n}/3 型）");
        }

        private static int TryPatch(Type type, string prefix, Action bindFields)
        {
            try
            {
                bindFields();
                var target = AccessTools.DeclaredMethod(type, "UpdateDynamicBones", new[] { typeof(float) });
                if (target == null) throw new MissingMethodException(type.Name, "UpdateDynamicBones(float)");
                _harmony.Patch(target, prefix: new HarmonyMethod(typeof(DynamicBoneLimiter), prefix));
                return 1;
            }
            catch (Exception e)
            {
                _log.LogWarning($"[DynBone] {type.Name} の制限を当てられない（{e.Message}）→ この型は本体の動作のまま。要調査");
                return 0;
            }
        }

        private static void Unpatch()
        {
            try { _harmony?.UnpatchSelf(); }
            catch (Exception e) { _log.LogWarning($"[DynBone] フック解除で例外（要調査）: {e.Message}"); }
            _patched = false;
            _log.LogInfo("[DynBone] 揺れ骨の制限を解除（本体の動作）");
        }

        // 1フレームの計算回数 = floor((貯まった時間 + t) × rate)。これが上限 N を超えない t に切り詰める。
        private static float Clamp(float t, float rate, float time)
        {
            if (rate <= 0f || t <= 0f) return t;
            float maxT = (MaxSteps + 0.999f) / rate - time;
            if (t > maxT) t = maxT > 0f ? maxT : 0f;
            return t;
        }

        private static void PrefixV0(DynamicBone __instance, ref float t)
            => t = Clamp(t, _v0Rate(__instance), _v0Time(__instance));

        private static void PrefixV1(DynamicBone_Ver01 __instance, ref float t)
            => t = Clamp(t, _v1Rate(__instance), _v1Time(__instance));

        private static void PrefixV2(DynamicBone_Ver02 __instance, ref float _deltaTime)
            => _deltaTime = Clamp(_deltaTime, _v2Rate(__instance), _v2Time(__instance));
    }

    internal class DynamicBoneLimiterMonitor : MonoBehaviour
    {
        private void Update() => DynamicBoneLimiter.SyncPatches();

        private void OnDestroy()
        {
            // 後始末はゲートしない（CLAUDE.md）。パッチを残さない。
            if (DynamicBoneLimiter.Active) DynamicBoneLimiter.SyncPatchesOff();
        }
    }
}
