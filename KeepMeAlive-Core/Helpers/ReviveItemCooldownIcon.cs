//====================[ Imports ]====================
using System;
using System.Collections;
using System.Reflection;
using Comfort.Common;
using EFT.UI;
using UnityEngine;

namespace KeepMeAlive.Helpers
{
    //====================[ ReviveItemCooldownIcon ]====================
    // Injects the ReviveItemCooldown icon sprite into EFT's EffectIcons registry.
    //
    // WHY NOT OnAfterDeserialize: StaticIcons is deserialized during Unity's asset-loading
    // phase, before BepInEx plugins load — the method has already fired by the time our plugin
    // is registered. Called instead from raid start (RaidStartupTasks). If hard settings are
    // still loading, injection completes asynchronously when their load finishes.
    internal static class ReviveItemCooldownIcon
    {
        private static bool _injected;
        private static bool _hardSettingsLoadStarted;
        private static bool _hardSettingsLoadCompleted;
        private static Sprite _cachedSprite;

        public static void EnsureIconInjected()
        {
            try
            {
                if (_injected)
                    return;

                if (!_hardSettingsLoadCompleted)
                {
                    StartHardSettingsLoad();
                    return;
                }

                var iconDict = EFTHardSettings.Instance?.StaticIcons?.EffectIcons?.EffectIcons;
                if (iconDict == null)
                {
                    Plugin.LogSource.LogWarning("[ReviveItemCooldownIcon] EffectIcons dict not yet available.");
                    return;
                }

                if (_cachedSprite == null)
                    _cachedSprite = LoadReviveItemSprite();

                if (_cachedSprite == null)
                {
                    Plugin.LogSource.LogWarning("[ReviveItemCooldownIcon] Custom sprite unavailable; using fallback.");
                    foreach (var s in iconDict.Values)
                        if (s != null) { _cachedSprite = s; break; }
                }

                if (_cachedSprite == null)
                {
                    Plugin.LogSource.LogError("[ReviveItemCooldownIcon] No sprite available — icon will not show.");
                    _injected = true;
                    return;
                }

                iconDict[typeof(IReviveItemCooldown)] = _cachedSprite;
                Plugin.LogSource.LogInfo(
                    $"[ReviveItemCooldownIcon] ReviveItemCooldown icon injected (dict size now {iconDict.Count}).");
                _injected = true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[ReviveItemCooldownIcon] EnsureIconInjected error: {ex}");
            }
        }

        private static void StartHardSettingsLoad()
        {
            if (_hardSettingsLoadStarted)
                return;

            if (Plugin.StaticCoroutineRunner == null)
            {
                Plugin.LogSource.LogWarning(
                    "[ReviveItemCooldownIcon] Coroutine runner unavailable; deferring EFTHardSettings.Load.");
                return;
            }

            _hardSettingsLoadStarted = true;
            Plugin.StaticCoroutineRunner.StartCoroutine(LoadHardSettingsAndInject());
        }

        private static IEnumerator LoadHardSettingsAndInject()
        {
            var loadTask = EFTHardSettings.Load();
            while (!loadTask.IsCompleted)
                yield return null;

            if (loadTask.IsFaulted || loadTask.IsCanceled)
            {
                Plugin.LogSource.LogError("[ReviveItemCooldownIcon] EFTHardSettings.Load failed or was canceled.");
                _hardSettingsLoadStarted = false;
                yield break;
            }

            _hardSettingsLoadCompleted = true;
            EnsureIconInjected();
        }

        private static Sprite LoadReviveItemSprite()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                const string resource = "KeepMeAlive.Resources.revive_item_cooldown.png";

                using var stream = asm.GetManifestResourceStream(resource);
                if (stream == null)
                {
                    Plugin.LogSource.LogWarning(
                        $"[ReviveItemCooldownIcon] Embedded resource '{resource}' not found.");
                    return null;
                }

                byte[] bytes;
                using (var br = new System.IO.BinaryReader(stream))
                    bytes = br.ReadBytes((int)stream.Length);

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(bytes))
                {
                    Plugin.LogSource.LogWarning("[ReviveItemCooldownIcon] Texture2D.LoadImage returned false.");
                    return null;
                }

                Plugin.LogSource.LogDebug(
                    $"[ReviveItemCooldownIcon] Loaded reviveItem PNG: {tex.width}x{tex.height}");
                return Sprite.Create(
                    tex,
                    new Rect(0f, 0f, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[ReviveItemCooldownIcon] LoadReviveItemSprite error: {ex}");
                return null;
            }
        }
    }
}
