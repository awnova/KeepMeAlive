//====================[ Imports ]====================
using System;

namespace KeepMeAlive.Helpers
{
    //====================[ Synced Config DTOs ]====================
    // Client mirror of KeepMeAlive-Server RevivalServerConfig; property names must match it.
    // The server normalizes before serving. A fresh instance has default false/zero values.
    // Server-only fields (trading, item resize) are simply ignored on deserialization.
    internal sealed class SyncedRuntimeConfig
    {
        public int SchemaVersion { get; set; }
        public SyncedRevivalItemConfig RevivalItem { get; set; } = new();
        public SyncedGameplayConfig Gameplay { get; set; } = new();
    }

    internal sealed class SyncedRevivalItemConfig
    {
        public string TemplateId { get; set; }
    }

    internal sealed class SyncedGameplayConfig
    {
        public SyncedRevivalMechanicsConfig Revival { get; set; } = new();
        public SyncedPostReviveConfig PostRevive { get; set; } = new();
        public SyncedProtectionConfig Protection { get; set; } = new();
        public SyncedTeamHealingConfig TeamHealing { get; set; } = new();
        public SyncedDevelopmentGameplayConfig Development { get; set; } = new();
    }

    internal sealed class SyncedRevivalMechanicsConfig
    {
        public bool EnableSelfRevive { get; set; }
        public bool EnableTeamRevive { get; set; }
        public float SelfReviveHoldSeconds { get; set; }
        public float TeamReviveHoldSeconds { get; set; }
        public float SelfReviveProgressSeconds { get; set; }
        public float TeamReviveProgressSeconds { get; set; }
        public bool ConsumeReviveItemOnSelfRevive { get; set; }
        public bool ConsumeReviveItemOnTeamRevive { get; set; }
        public float CriticalStateSeconds { get; set; }
        public bool RestoreVitalsOnDowned { get; set; }
        public bool ApplyContusionOnDowned { get; set; }
        public bool ApplyStunOnDowned { get; set; }
        public float MaxStunSeconds { get; set; }
        public float DownedMovementSpeedPercent { get; set; }
        public bool BlockUiWhenDowned { get; set; }
        public bool UnconsciousOnDowned { get; set; }
        public int MaxLivesPerRaid { get; set; }
        public int SelfReviveLivesCost { get; set; }
        public int TeamReviveLivesCost { get; set; }
    }

    internal sealed class SyncedPostReviveConfig
    {
        public SyncedPostReviveSourceConfig Self { get; set; } = new();
        public SyncedPostReviveSourceConfig Team { get; set; } = new();
    }

    internal sealed class SyncedPostReviveSourceConfig
    {
        public bool RestoreBodyParts { get; set; }
        public SyncedBodyRestorePercentConfig RestorePercent { get; set; } = new();
        public bool RemoveBleeds { get; set; }
        public bool RemoveFractures { get; set; }
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
        public bool EnableHardcoreMode { get; set; }
        public bool HardcoreHeadshotsAreFatal { get; set; }
        public float HardcoreCriticalStateChance { get; set; }
    }

    internal sealed class SyncedTeamHealingConfig
    {
        public bool Enabled { get; set; }
        public float InteractRangeMeters { get; set; }
        public float HoldSeconds { get; set; }
        public float UseTimeMultiplier { get; set; }
        public float MinHpResourceToDisplay { get; set; }
        public float NutritionMinDeficitToDisplay { get; set; }
        public bool AllowLootDownedPlayers { get; set; }
    }

    internal sealed class SyncedDevelopmentGameplayConfig
    {
        public bool NoReviveItemRequired { get; set; }
    }

    //====================[ SyncedServerConfigStore ]====================
    // Fetches configuration at plugin load and retries at raid start when it is not loaded.
    // Everything runs on the main thread.
    internal static class SyncedServerConfigStore
    {
        // Must match KeepMeAlive-Server RevivalServerConfig.CurrentSchemaVersion.
        public const int SupportedSchemaVersion = 4;

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
