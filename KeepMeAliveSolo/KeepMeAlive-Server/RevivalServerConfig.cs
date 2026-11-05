using System.Text.Json.Serialization;

namespace KeepMeAlive.Server;

//====================[ RevivalServerConfig ]====================
public sealed class RevivalServerConfig
{
    // Must match KeepMeAlive-Core SyncedServerConfigStore.SupportedSchemaVersion.
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public RevivalGameplayConfig Gameplay { get; set; } = new();

    //====================[ Normalize ]====================
    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;

        Gameplay ??= new RevivalGameplayConfig();
        Gameplay.Normalize();
    }
}

//====================[ RevivalGameplayConfig ]====================
public sealed class RevivalGameplayConfig
{
    // Settings for the downed state itself and the lives pool.
    public DownedConfig Downed { get; set; } = new();
    // Self revive settings.
    public StimReviveConfig Self { get; set; } = StimReviveConfig.CreateSelfDefaults();
    // Template ids of the stims that Self revive uses up when RequireStim is on; the first one the player
    // carries is used. Empty uses the game's Adrenaline stim, which the client knows by default.
    public List<string> StimItemIds { get; set; } = [];
    // Pre-list single id: read once, moved to the front of StimItemIds, then dropped from the file.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StimItemId { get; set; }
    public ProtectionConfig Protection { get; set; } = new();
    public DevelopmentGameplayConfig Development { get; set; } = new();

    public void Normalize()
    {
        StimItemIds = new[] { StimItemId }.Concat(StimItemIds ?? [])
            .Select(id => (id ?? "").Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        StimItemId = null;

        Downed ??= new DownedConfig();
        Self ??= StimReviveConfig.CreateSelfDefaults();
        Protection ??= new ProtectionConfig();
        Development ??= new DevelopmentGameplayConfig();

        Downed.Normalize();
        Self.Normalize();
        Protection.Normalize();
    }
}

//====================[ DownedConfig ]====================
public sealed class DownedConfig
{
    public float CriticalStateSeconds { get; set; } = 180f;
    public bool ApplyContusionOnDowned { get; set; } = true;
    public bool ApplyStunOnDowned { get; set; } = true;
    public float DownedMovementSpeedPercent { get; set; } = 50f;
    public bool BlockUiWhenDowned { get; set; } = true;
    public bool UnconsciousOnDowned { get; set; } = false;
    public int MaxLivesPerRaid { get; set; } = 5;

    public void Normalize()
    {
        CriticalStateSeconds = Math.Max(1f, CriticalStateSeconds);
        DownedMovementSpeedPercent = Math.Clamp(DownedMovementSpeedPercent, 0f, 100f);
        MaxLivesPerRaid = Math.Max(1, MaxLivesPerRaid);
    }
}

//====================[ ReviveTypeConfig ]====================
// What a revive type owns: whether it is on, how long it takes, what it costs, and what the revived player gets
// (restore, invulnerability, cooldown, effects). Item requirements live on the subclasses.
public abstract class ReviveTypeConfig
{
    public bool Enabled { get; set; } = true;
    public float HoldSeconds { get; set; } = 5f;
    public int LivesCost { get; set; } = 1;
    // Lets this revive go ahead with fewer lives left than LivesCost (but at least one); it spends the rest, so the
    // player's next down cannot be revived.
    public bool AllowInsufficientLives { get; set; }
    public bool RestoreBodyParts { get; set; } = true;
    public BodyRestorePercentConfig RestorePercent { get; set; } = BodyRestorePercentConfig.CreateNeutralDefaults();
    public bool RemoveLightBleeds { get; set; }
    public bool RemoveHeavyBleeds { get; set; }
    public bool RemoveLegFractures { get; set; }
    public bool RemoveArmFractures { get; set; }
    public float InvulnerabilityDurationSeconds { get; set; } = 5f;
    public float InvulnerabilitySpeedPercent { get; set; } = 100f;
    public float CooldownSeconds { get; set; }
    public bool ApplyContusionOnRevive { get; set; } = true;
    public float ContusionDurationSeconds { get; set; }
    public bool ApplyPainOnRevive { get; set; }
    public float PainDurationSeconds { get; set; } = 30f;

    public void Normalize()
    {
        HoldSeconds = Math.Max(0.1f, HoldSeconds);
        LivesCost = Math.Max(0, LivesCost);

        RestorePercent ??= BodyRestorePercentConfig.CreateNeutralDefaults();
        RestorePercent.Normalize();

        InvulnerabilityDurationSeconds = Math.Max(0f, InvulnerabilityDurationSeconds);
        // The client applies this as a movement speed limit, which cannot raise speed above normal.
        InvulnerabilitySpeedPercent = float.IsFinite(InvulnerabilitySpeedPercent)
            ? Math.Clamp(InvulnerabilitySpeedPercent, 0f, 100f)
            : 100f;
        CooldownSeconds = Math.Max(0f, CooldownSeconds);
        ContusionDurationSeconds = Math.Max(0f, ContusionDurationSeconds);
        PainDurationSeconds = Math.Max(0f, PainDurationSeconds);
    }
}

//====================[ StimReviveConfig ]====================
// Self revive: the player may be required to carry a stim.
public sealed class StimReviveConfig : ReviveTypeConfig
{
    // The player must carry a stim.
    public bool RequireStim { get; set; }
    // ConsumeStim deletes the stim as the revive starts; it only applies while RequireStim is on.
    public bool ConsumeStim { get; set; }

    public static StimReviveConfig CreateSelfDefaults() => new()
    {
        Enabled = true,
        HoldSeconds = 8f,
        LivesCost = 3,
        RequireStim = false,
        ConsumeStim = false,
        RestoreBodyParts = true,
        RestorePercent = BodyRestorePercentConfig.CreateNeutralDefaults(),
        RemoveLightBleeds = false,
        RemoveHeavyBleeds = true,
        RemoveLegFractures = false,
        RemoveArmFractures = false,
        InvulnerabilityDurationSeconds = 10f,
        InvulnerabilitySpeedPercent = 60f,
        CooldownSeconds = 240f,
        ApplyContusionOnRevive = true,
        ContusionDurationSeconds = 10f,
        ApplyPainOnRevive = true,
        PainDurationSeconds = 30f
    };
}

//====================[ BodyRestorePercentConfig ]====================
public sealed class BodyRestorePercentConfig
{
    public float Head { get; set; }
    public float Chest { get; set; }
    public float Stomach { get; set; }
    public float Arms { get; set; }
    public float Legs { get; set; }

    public static BodyRestorePercentConfig CreateNeutralDefaults() => new()
    {
        Head = 20f,
        Chest = 35f,
        Stomach = 35f,
        Arms = 35f,
        Legs = 35f
    };

    public void Normalize()
    {
        Head = Math.Clamp(Head, 0f, 100f);
        Chest = Math.Clamp(Chest, 0f, 100f);
        Stomach = Math.Clamp(Stomach, 0f, 100f);
        Arms = Math.Clamp(Arms, 0f, 100f);
        Legs = Math.Clamp(Legs, 0f, 100f);
    }
}

//====================[ ProtectionConfig ]====================
public sealed class ProtectionConfig
{
    public bool BlockDeathInCritical { get; set; } = true;
    public bool EnableGodMode { get; set; } = true;
    public bool EnableGhostMode { get; set; } = true;
    // Chance (0-1) that a headshot which would down a player kills them outright instead; 0 turns it off.
    public float DeathFromHeadshotChance { get; set; }

    public void Normalize()
    {
        DeathFromHeadshotChance = Math.Clamp(DeathFromHeadshotChance, 0f, 1f);
    }
}

//====================[ DevelopmentGameplayConfig ]====================
public sealed class DevelopmentGameplayConfig
{
    // Turns on the client's debug logging and the revive and self-revive traces.
    public bool DebugMode { get; set; }
}
