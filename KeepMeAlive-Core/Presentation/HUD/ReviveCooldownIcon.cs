//====================[ Imports ]====================
using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace KeepMeAlive.Presentation.HUD
{
    //====================[ ReviveCooldownIcon ]====================
    // Injects the ReviveCooldown icon sprite into EFT's EffectIcons registry.
    //
    // WHY NOT OnAfterDeserialize: StaticIcons is deserialized during Unity's asset-loading
    // phase, before BepInEx plugins load; the method has fired before I can load this plugin
    // is registered. Called instead from raid start (RaidStartupTasks). If hard settings are
    // still loading, injection completes asynchronously when their load finishes.
    internal static class ReviveCooldownIcon
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
                    Plugin.LogSource.LogWarning("[ReviveCooldownIcon] EffectIcons dict not yet available.");
                    return;
                }

                if (_cachedSprite == null)
                    _cachedSprite = LoadCooldownSprite();

                if (_cachedSprite == null)
                {
                    Plugin.LogSource.LogWarning("[ReviveCooldownIcon] Custom sprite unavailable; using fallback.");
                    foreach (var s in iconDict.Values)
                        if (s != null) { _cachedSprite = s; break; }
                }

                if (_cachedSprite == null)
                {
                    Plugin.LogSource.LogError("[ReviveCooldownIcon] No sprite available — icon will not show.");
                    _injected = true;
                    return;
                }

                iconDict[typeof(IReviveCooldown)] = _cachedSprite;
                Plugin.LogSource.LogInfo(
                    $"[ReviveCooldownIcon] ReviveCooldown icon injected (dict size now {iconDict.Count}).");
                _injected = true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[ReviveCooldownIcon] EnsureIconInjected error: {ex}");
            }
        }

        private static void StartHardSettingsLoad()
        {
            if (_hardSettingsLoadStarted)
                return;

            if (Plugin.StaticCoroutineRunner == null)
            {
                Plugin.LogSource.LogWarning(
                    "[ReviveCooldownIcon] Coroutine runner unavailable; deferring EFTHardSettings.Load.");
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
                Plugin.LogSource.LogError("[ReviveCooldownIcon] EFTHardSettings.Load failed or was canceled.");
                _hardSettingsLoadStarted = false;
                yield break;
            }

            _hardSettingsLoadCompleted = true;
            EnsureIconInjected();
        }

        private static Sprite LoadCooldownSprite()
        {
            try
            {
                return EmbeddedData.ReadSprite("KeepMeAlive.Resources.revive_item_cooldown.png", mipmaps: false);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[ReviveCooldownIcon] Could not load the icon: {ex.Message}");
                return null;
            }
        }
    }
}
