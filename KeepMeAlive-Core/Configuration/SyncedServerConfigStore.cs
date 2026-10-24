//====================[ Imports ]====================
using System;

namespace KeepMeAlive.Configuration
{
    //====================[ Synced Config DTOs ]====================
    // Client mirror of KeepMeAlive-Server RevivalServerConfig; property names must match it.
    // The server normalizes before serving. A fresh instance has default false/zero values.
    // Server-only fields (trading, item resize) are simply ignored on deserialization.
    internal sealed class SyncedRuntimeConfig
    {
        public int SchemaVersion { get; set; }
        public SyncedGameplayConfig Gameplay { get; set; } = new();
    }

    // The defibrillator is a fixed part of the mod; must match KeepMeAlive-Server TraderConstants.DefibTemplateId.
    internal static class DefibIds
    {
        public const string TemplateId = "5c052e6986f7746b207bc3c9";
    }

    // The stim a Self or Team revive uses up when the server asks for one: the game's Adrenaline unless the server
    // config names another template.
    internal static class StimIds
    {
        public const string DefaultTemplateId = "5c10c8fd86f7743d7d706df3";

        public static string TemplateId
        {
            get
            {
                string configured = SyncedServerConfigStore.Config.Gameplay.StimItemId;
                return string.IsNullOrWhiteSpace(configured) ? DefaultTemplateId : configured.Trim();
            }
        }
    }

    internal sealed class SyncedGameplayConfig
    {
        public string StimItemId { get; set; } = "";
        public SyncedDownedConfig Downed { get; set; } = new();
        public SyncedReviveTypeConfig Self { get; set; } = new();
        public SyncedReviveTypeConfig Team { get; set; } = new();
        public SyncedReviveTypeConfig Defib { get; set; } = new();
        public SyncedProtectionConfig Protection { get; set; } = new();
        public SyncedTeamHealingConfig TeamHealing { get; set; } = new();
        public SyncedDevelopmentGameplayConfig Development { get; set; } = new();
    }

    internal sealed class SyncedDownedConfig
    {
        public float CriticalStateSeconds { get; set; }
        public bool ApplyContusionOnDowned { get; set; }
        public bool ApplyStunOnDowned { get; set; }
        public float DownedMovementSpeedPercent { get; set; }
        public bool BlockUiWhenDowned { get; set; }
        public bool UnconsciousOnDowned { get; set; }
        public int MaxLivesPerRaid { get; set; }
    }

    internal sealed class SyncedReviveTypeConfig
    {
        public bool Enabled { get; set; }
        public float HoldSeconds { get; set; }
        public int LivesCost { get; set; }
        public bool RequireDefib { get; set; }
        public bool ConsumeDefib { get; set; }
        public bool RequireStim { get; set; }
        public bool RestoreBodyParts { get; set; }
        public SyncedBodyRestorePercentConfig RestorePercent { get; set; } = new();
        public bool RemoveLightBleeds { get; set; }
        public bool RemoveHeavyBleeds { get; set; }
        public bool RemoveLegFractures { get; set; }
        public bool RemoveArmFractures { get; set; }
        public float InvulnerabilityDurationSeconds { get; set; }
        public float InvulnerabilitySpeedPercent { get; set; }
        public float CooldownSeconds { get; set; }
        public bool ApplyContusionOnRevive { get; set; }
        public float ContusionDurationSeconds { get; set; }
        public bool ApplyPainOnRevive { get; set; }
        public float PainDurationSeconds { get; set; }
    }

    internal sealed class SyncedBodyRestorePercentConfig
    {
        public float Head { get; set; }
        public float Chest { get; set; }
        public float Stomach { get; set; }
        public float Arms { get; set; }
        public float Legs { get; set; }
    }

    internal sealed class SyncedProtectionConfig
    {
        public bool BlockDeathInCritical { get; set; }
        public bool EnableGodMode { get; set; }
        public bool EnableGhostMode { get; set; }
        public float DeathFromHeadshotChance { get; set; }
    }

    internal sealed class SyncedTeamHealingConfig
    {
        public bool Enabled { get; set; }
        public float UseTimeMultiplier { get; set; }
        public float MinHpResourceToDisplay { get; set; }
        public float NutritionMinDeficitToDisplay { get; set; }
        public bool AllowLootDownedPlayers { get; set; }
    }

    internal sealed class SyncedDevelopmentGameplayConfig
    {
        public bool DebugMode { get; set; }
    }

    //====================[ SyncedServerConfigStore ]====================
    // Fetches configuration at plugin load and retries at raid start when it is not loaded.
    // Everything runs on the main thread.
    internal static class SyncedServerConfigStore
    {
        // Must match KeepMeAlive-Server RevivalServerConfig.CurrentSchemaVersion.
        public const int SupportedSchemaVersion = 1;

        private const string Route = "/keepmealive/state/get-runtime-config";

        // Default false/zero values until the server configuration is loaded.
        public static SyncedRuntimeConfig Config { get; private set; } = new();
        public static bool IsLoaded { get; private set; }

        //====================[ Lifecycle ]====================
        public static void Load() => TryFetch("plugin-load");

        // Fetches configuration when it has not yet been loaded.
        public static void EnsureLoaded(string reason)
        {
            if (!IsLoaded) TryFetch(reason);
        }

        //====================[ Internal Helpers ]====================
        private static void TryFetch(string reason)
        {
            try
            {
                var config = ModUtils.ServerRoute<SyncedRuntimeConfig>(Route, new object());
                // SchemaVersion 0 indicates that the response did not contain a runtime config.
                if (config == null || config.SchemaVersion == 0)
                {
                    Warn(reason, "no config in server response; is the KeepMeAlive server mod installed?");
                    return;
                }

                if (config.SchemaVersion != SupportedSchemaVersion)
                {
                    Warn(reason, $"schema {config.SchemaVersion}, expected {SupportedSchemaVersion}; update the client and server mods together");
                    return;
                }

                Config = config;
                IsLoaded = true;
                Plugin.LogSource?.LogInfo($"[SyncConfig] Loaded server config ({reason}).");
            }
            catch (Exception ex)
            {
                Warn(reason, ex.Message);
            }
        }

        private static void Warn(string reason, string detail) =>
            Plugin.LogSource?.LogWarning($"[SyncConfig] Server config unavailable ({reason}): {detail}. KeepMeAlive features disabled (fail-closed).");
    }
}
