//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using EFT;
using EFT.CameraControl;
using EFT.HealthSystem;
using HarmonyLib;
using KeepMeAlive.Components;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Features
{
    //====================[ DownedScreenEffects ]====================
    // Local-only screen treatment while downed, driven by RMPlayer.CriticalTimer:
    //   - tunnel vision and the low-health blend, added to EFT's own EffectsController accumulators
    //     as stub health effects;
    //   - a red screen edge, drawn by the BloodOnScreen patches (DownedBloodVignettePatches);
    //   - grayscale that deepens as the timer runs down.
    // The vignette and red edge ease down slightly between the heartbeat's lub and dub.
    // Only this class's own stubs are ever removed; EFT's health effects are left alone.
    internal static class DownedScreenEffects
    {
        //====================[ Constants ]====================
        private const float BlendFadeSeconds = 1.5f;
        private const float RebindInterval = 0.5f;

        // Tick runs every frame while downed; if it stops for this long without a Stop() the
        // player has left the downed state by a path that did not clean up, so effects self-clear.
        private const float StaleSeconds = 2f;

        // Grayscale climbs at this multiple of the elapsed bleed-out fraction (clamped to full grey).
        private const float GrayscaleRate = 1.5f;

        // Effect strength ramps from the first value to the second as the bleed-out timer runs down.
        private const float TunnelStrengthStart = 0.35f;
        private const float TunnelStrengthEnd = 0.9f;
        private const float RedEdgeDarknessStart = 22f;
        private const float RedEdgeDarknessEnd = 40f;

        // heartbeat_loop.wav is a lub-dub: the lub peaks ~0.05 s and the dub ~0.29 s into the clip.
        private const float LubTime = 0.05f;
        private const float DubTime = 0.29f;

        //====================[ State ]====================
        private static readonly AccessTools.FieldRef<EffectsController, List<EffectsController.EffectAccumulator>> AccumulatorsRef =
            AccessTools.FieldRefAccess<EffectsController, List<EffectsController.EffectAccumulator>>("_effectAccumulators");

        private static EffectsController _controller;
        private static EffectsController.CC_FastVignetteAccumulator _vignette;
        private static EffectsController.CC_BlendAccumulator _blend;
        private static readonly DownedHealthEffect _tunnelStub = new DownedHealthEffect(typeof(ITunnelVision));
        private static readonly DownedHealthEffect _blendStub = new DownedHealthEffect(typeof(ILowEdgeHealth));
        private static bool _active;
        private static float _lastTick;
        private static float _nextRebind;

        // Grayscale runs EFT's own DesaturateEffect shader and ramp through a private material.
        private static DesaturateEffect _desatProvider;
        private static Material _grayMaterial;
        private static float _grayAmount;
        private static Texture2D _dimRamp;
        private static float _dimRampScale;
        private static readonly int RampTexId = Shader.PropertyToID("_RampTex");
        private static readonly int DesatWeatherId = Shader.PropertyToID("_DesatWeather");
        private static readonly int DesatHealthId = Shader.PropertyToID("_DesatHealth");
        private static readonly int DesatMaskId = Shader.PropertyToID("_DesatMask");
        private static readonly int RampOffsetId = Shader.PropertyToID("_RampOffset");
        private static readonly int RadiusId = Shader.PropertyToID("_Radius");
        private static readonly int RadiusFalloffId = Shader.PropertyToID("_RadiusFalloff");
        private static readonly int NightVisionRatioId = Shader.PropertyToID("_NightVisionRatio");

        // Read by DownedBloodVignettePatches.
        public static bool IsActive => _active;
        public static bool DownedBloodActive { get; private set; }
        public static float DownedBloodDarkness { get; private set; }
        public static bool GrayscaleActive => DownedBloodActive && _grayAmount > 0.001f && _desatProvider != null;

        //====================[ Public API ]====================
        public static void Start(Player player)
        {
            if (player == null || !player.IsYourPlayer || !KeepMeAliveSettings.ENABLE_DOWNED_SCREEN_EFFECTS.Value) return;
            _active = true;
            _lastTick = Time.unscaledTime;
            _nextRebind = 0f;
            _grayAmount = 0f;
        }

        // Called from the render/accumulator patches, which run every frame regardless of the
        // player's state, so a missed Stop() can never leave the effects on.
        public static void ExpireIfStale()
        {
            if (!_active || Time.unscaledTime - _lastTick <= StaleSeconds) return;
            Plugin.LogSource.LogWarning("[DownedScreenEffects] Downed tick stopped without a Stop(); clearing screen effects.");
            Stop();
        }

        public static void Stop()
        {
            try
            {
                _active = false;
                DownedBloodActive = false;
                _grayAmount = 0f;
                Detach();
                _controller = null;
                _vignette = null;
                _blend = null;
                _desatProvider = null;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedScreenEffects] Stop error: {ex.Message}");
            }
        }

        // Called every frame from DownedStateController.TickDowned for the local player.
        public static void Tick(RMPlayer st)
        {
            if (!_active || st == null) return;
            _lastTick = Time.unscaledTime;
            try
            {
                // EFT can replace the player camera, so rebind whenever the cached controller is gone.
                if (_controller == null)
                {
                    if (Time.unscaledTime < _nextRebind) return;
                    _nextRebind = Time.unscaledTime + RebindInterval;
                    if (!Bind()) return;
                }

                float total = Mathf.Max(1f, SyncedServerConfigStore.Config.Gameplay.Revival.CriticalStateSeconds);
                float t = Mathf.Clamp01(1f - st.CriticalTimer / total); // 0 at the start of bleed-out, 1 at the end

                // Full strength, easing down to the configured minimum between the lub and the dub.
                float mult = Mathf.Lerp(1f, KeepMeAliveSettings.DOWNED_PULSE_MIN_STRENGTH.Value, BeatPulse());

                _tunnelStub.Strength = Mathf.Lerp(TunnelStrengthStart, TunnelStrengthEnd, t) * mult;
                _blendStub.Strength = t;
                DownedBloodDarkness = Mathf.Lerp(RedEdgeDarknessStart, RedEdgeDarknessEnd, t) * mult;
                _grayAmount = KeepMeAliveSettings.ENABLE_DOWNED_GRAYSCALE.Value
                    ? Mathf.Clamp01(KeepMeAliveSettings.DOWNED_GRAYSCALE_STRENGTH.Value * GrayscaleRate * t)
                    : 0f;
                DownedBloodActive = true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedScreenEffects] Tick error: {ex.Message}");
            }
        }

        // True when acc is the fast-vignette accumulator this class is contributing to.
        public static bool OwnsVignette(EffectsController.CC_FastVignetteAccumulator acc) =>
            _active && acc != null && ReferenceEquals(acc, _vignette);

        //====================[ Grayscale ]====================
        // Returns a temporary RT holding a desaturated copy of source (release it with
        // RenderTexture.ReleaseTemporary), or null when grayscale is unavailable.
        public static RenderTexture GrayscaleCopy(RenderTexture source)
        {
            if (!GrayscaleActive || source == null) return null;
            try
            {
                if (_grayMaterial == null)
                    _grayMaterial = new Material(_desatProvider.shader) { hideFlags = HideFlags.HideAndDontSave };

                // Same inputs as DesaturateEffect.OnRenderImage, driving only the health channel.
                var p = _desatProvider;
                _grayMaterial.SetTexture(RampTexId, DesaturateRamp(p));
                _grayMaterial.SetFloat(DesatWeatherId, 0f);
                _grayMaterial.SetFloat(DesatHealthId, Mathf.Clamp01(_grayAmount));
                _grayMaterial.SetVector(RampOffsetId, new Vector4(p.rampOffsetR, p.rampOffsetG, p.rampOffsetB, 0f));
                _grayMaterial.SetFloat(DesatMaskId, 0f);
                _grayMaterial.SetFloat(RadiusId, 1f);
                _grayMaterial.SetFloat(RadiusFalloffId, p.RadiusFalloff);
                var cam = CameraManager.Instance;
                _grayMaterial.SetFloat(NightVisionRatioId, cam != null && cam.NightVision != null && cam.NightVision.On ? 0f : 1f);

                var desc = source.descriptor;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                var tmp = RenderTexture.GetTemporary(desc);
                Graphics.Blit(source, tmp, _grayMaterial);
                return tmp;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedScreenEffects] GrayscaleCopy error: {ex}");
                _desatProvider = null; // stop retrying until the next bind
                return null;
            }
        }

        // The desaturate shader maps luminance through a grey lookup ramp. Scaling that ramp by
        // (1 - dim) darkens the desaturated part of the image, so brightness falls in step with
        // the grayscale fade at no extra render cost. Returns EFT's own ramp when dimming is off.
        private static Texture DesaturateRamp(DesaturateEffect provider)
        {
            var source = provider.textureRamp;
            float scale = 1f - KeepMeAliveSettings.DOWNED_DIM_STRENGTH.Value;
            if (source == null || scale >= 0.999f) return source;

            int width = Mathf.Max(2, source.width);
            if (_dimRamp != null && _dimRamp.width == width && Mathf.Approximately(_dimRampScale, scale)) return _dimRamp;

            if (_dimRamp == null || _dimRamp.width != width)
            {
                if (_dimRamp != null) UnityEngine.Object.Destroy(_dimRamp);
                _dimRamp = new Texture2D(width, 1, TextureFormat.RGBAHalf, false, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave
                };
            }

            var pixels = new Color[width];
            for (int i = 0; i < width; i++)
            {
                float v = i / (float)(width - 1) * scale;
                pixels[i] = new Color(v, v, v, 1f);
            }
            _dimRamp.SetPixels(pixels);
            _dimRamp.Apply(false);
            _dimRampScale = scale;
            return _dimRamp;
        }

        //====================[ Heartbeat pulse ]====================
        // 0 = full strength, 1 = deepest point of the dip. The dip is a single raised-cosine
        // between the lub and the dub of each beat (zero at both sounds, zero slope at both ends),
        // so it eases in and out with no hard edge.
        private static float BeatPulse()
        {
            if (!HeartbeatEffect.TryGetBeatAge(out float age, out float interval, out float prevInterval)) return 0f;
            age -= KeepMeAliveSettings.DOWNED_PULSE_OFFSET_MS.Value * 0.001f;

            // The dip windows never overlap; the neighbouring beats only matter when the
            // visual offset shifts the current time across a beat boundary.
            float pulse = GapDip(age);
            if (prevInterval > 0f) pulse = Mathf.Max(pulse, GapDip(age + prevInterval));
            return Mathf.Max(pulse, GapDip(age - interval));
        }

        private static float GapDip(float age)
        {
            if (age <= LubTime || age >= DubTime) return 0f;
            float u = (age - LubTime) / (DubTime - LubTime);
            return 0.5f - 0.5f * Mathf.Cos(u * Mathf.PI * 2f);
        }

        //====================[ Binding ]====================
        private static bool Bind()
        {
            var controller = CameraManager.Instance?.EffectsController;
            if (controller == null) controller = UnityEngine.Object.FindObjectOfType<EffectsController>();
            if (controller == null) return false;

            EffectsController.CC_FastVignetteAccumulator vignette = null;
            EffectsController.CC_BlendAccumulator blend = null;
            foreach (var acc in AccumulatorsRef(controller))
            {
                if (acc is EffectsController.CC_FastVignetteAccumulator v) vignette = v;
                else if (acc is EffectsController.CC_BlendAccumulator b && Array.IndexOf(b.ValidTypes, typeof(ILowEdgeHealth)) >= 0) blend = b;
            }
            if (vignette == null) return false;

            Detach();
            _controller = controller;
            _vignette = vignette;
            _blend = blend;
            _desatProvider = controller.GetComponent<DesaturateEffect>();
            if (_desatProvider == null)
                Plugin.LogSource.LogWarning("[DownedScreenEffects] No DesaturateEffect on the player camera; grayscale disabled.");

            vignette.AddEffect(_tunnelStub);
            if (blend != null)
            {
                bool first = blend.ActiveEffects.Count == 0;
                blend.AddEffect(_blendStub);
                // The blend ramps over a fixed 10 s; shorten it when this is the effect starting it.
                if (first) blend._inTime = Time.time + BlendFadeSeconds;
            }
            return true;
        }

        // Removes this class's stubs from the bound accumulators. The camera components may
        // already be destroyed, so each removal is best-effort.
        private static void Detach()
        {
            try
            {
                if (_vignette != null && _vignette.ActiveEffects.Contains(_tunnelStub))
                    _vignette.DeleteEffect(_tunnelStub);
            }
            catch (Exception) { }

            try
            {
                if (_blend != null && _blend.ActiveEffects.Contains(_blendStub))
                {
                    _blend.DeleteEffect(_blendStub);
                    if (_blend.ActiveEffects.Count == 0) _blend._inTime = Time.time + BlendFadeSeconds;
                }
            }
            catch (Exception) { }
        }

        //====================[ Stub effect ]====================
        // Minimal IHealthEffect: the accumulators only read Type and CurrentStrength.
        private sealed class DownedHealthEffect : IHealthEffect
        {
            public DownedHealthEffect(Type type) { Type = type; }
            public float Strength { get; set; }

            public Type Type { get; }
            public EBodyPart BodyPart => EBodyPart.Common;
            public EEffectState State => EEffectState.Started;
            public float CurStateTimeLeft => 0f;
            public float WorkStateTime => 0f;
            public float WholeTime => 0f;
            public float CurrentStrength => Strength;
            float IHealthEffect.Strength => Strength;
            public float TimeLeft => 0f;
            public bool Critical => false;
            public EffectDescription[] DisplayableVariations => Array.Empty<EffectDescription>();
            public float OverallDuration => 0f;
            public bool Existing => true;
            public bool Active => true;
            public bool Residual => false;
            public bool WasPaused => false;
            public void AddWholeTime(float deltaTime) { }
            public void Propagate() { }
        }
    }
}
