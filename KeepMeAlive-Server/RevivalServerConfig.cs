namespace KeepMeAlive.Server;

//====================[ RevivalServerConfig ]====================
public sealed class RevivalServerConfig
{
    // Must match KeepMeAlive-Core SyncedServerConfigStore.SupportedSchemaVersion.
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public RevivalItemConfig RevivalItem { get; set; } = new();
    public RevivalGameplayConfig Gameplay { get; set; } = new();

    //====================[ Normalize ]====================
    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;

        RevivalItem ??= new RevivalItemConfig();
        Gameplay ??= new RevivalGameplayConfig();

        RevivalItem.Normalize();
        Gameplay.Normalize();
    }
}

//====================[ RevivalItemConfig ]====================
public sealed class RevivalItemConfig
{
    public string TemplateId { get; set; } = TraderConstants.RevivalItemTemplateId;
    // On by default. Existing items retain their saved footprint until they are moved.
    public bool ResizeTo2x1 { get; set; } = true;
    public TradingConfig Trading { get; set; } = new();

    public void Normalize()
    {
        TemplateId = string.IsNullOrWhiteSpace(TemplateId)
            ? TraderConstants.RevivalItemTemplateId
            : TemplateId.Trim();

        Trading ??= new TradingConfig();
        Trading.Normalize();
    }
}

//====================[ TradingConfig ]====================
public sealed class TradingConfig
{
    // Trader name (e.g. "Therapist") or trader id.
    public string Trader { get; set; } = "Therapist";
    public int AmountRoubles { get; set; } = 200000;
    public int LoyaltyLevel { get; set; } = 2;

    public void Normalize()
    {
        Trader = string.IsNullOrWhiteSpace(Trader) ? "Therapist" : Trader.Trim();
        AmountRoubles = Math.Max(1, AmountRoubles);
        LoyaltyLevel = Math.Clamp(LoyaltyLevel, 1, 4);
    }
}

//====================[ RevivalGameplayConfig ]====================
public sealed class RevivalGameplayConfig
{
    public RevivalMechanicsConfig Revival { get; set; } = new();
    public PostReviveConfig PostRevive { get; set; } = new();
    public ProtectionConfig Protection { get; set; } = new();
    public TeamHealingConfig TeamHealing { get; set; } = new();
    public DevelopmentGameplayConfig Development { get; set; } = new();

    public void Normalize()
    {
        Revival ??= new RevivalMechanicsConfig();
        PostRevive ??= new PostReviveConfig();
        Protection ??= new ProtectionConfig();
        TeamHealing ??= new TeamHealingConfig();
        Development ??= new DevelopmentGameplayConfig();

        Revival.Normalize();
        PostRevive.Normalize();
        Protection.Normalize();
        TeamHealing.Normalize();
    }
}

//====================[ RevivalMechanicsConfig ]====================
public sealed class RevivalMechanicsConfig
{
    public bool EnableSelfRevive { get; set; } = true;
    public bool EnableTeamRevive { get; set; } = true;
    public float SelfReviveHoldSeconds { get; set; } = 2f;
    public float TeamReviveHoldSeconds { get; set; } = 2f;
    public float SelfReviveProgressSeconds { get; set; } = 10f;
    public float TeamReviveProgressSeconds { get; set; } = 5f;
    public bool ConsumeReviveItemOnSelfRevive { get; set; } = true;
    public bool ConsumeReviveItemOnTeamRevive { get; set; } = false;
    public float CriticalStateSeconds { get; set; } = 180f;
    public bool RestoreVitalsOnDowned { get; set; } = true;
    public bool ApplyContusionOnDowned { get; set; } = true;
    public bool ApplyStunOnDowned { get; set; } = true;
    public float MaxStunSeconds { get; set; } = 20f;
    public float DownedMovementSpeedPercent { get; set; } = 50f;
    public bool BlockUiWhenDowned { get; set; } = true;
    public bool UnconsciousOnDowned { get; set; } = false;
    public int MaxLivesPerRaid { get; set; } = 5;
    public int SelfReviveLivesCost { get; set; } = 3;
    public int TeamReviveLivesCost { get; set; } = 1;

    public void Normalize()
    {
        SelfReviveHoldSeconds = Math.Max(0.1f, SelfReviveHoldSeconds);
        TeamReviveHoldSeconds = Math.Max(0.1f, TeamReviveHoldSeconds);
        SelfReviveProgressSeconds = Math.Max(3f, SelfReviveProgressSeconds);
        TeamReviveProgressSeconds = Math.Max(3f, TeamReviveProgressSeconds);
        CriticalStateSeconds = Math.Max(1f, CriticalStateSeconds);
        MaxStunSeconds = Math.Max(0f, MaxStunSeconds);
        DownedMovementSpeedPercent = Math.Clamp(DownedMovementSpeedPercent, 0f, 100f);
        MaxLivesPerRaid = Math.Max(1, MaxLivesPerRaid);
        SelfReviveLivesCost = Math.Max(0, SelfReviveLivesCost);
        TeamReviveLivesCost = Math.Max(0, TeamReviveLivesCost);
    }
}

//====================[ PostReviveConfig ]====================
public sealed class PostReviveConfig
{
    public PostReviveSourceConfig Self { get; set; } = PostReviveSourceConfig.CreateSelfDefaults();
    public PostReviveSourceConfig Team { get; set; } = PostReviveSourceConfig.CreateTeamDefaults();

    public void Normalize()
    {
        Self ??= PostReviveSourceConfig.CreateSelfDefaults();
        Team ??= PostReviveSourceConfig.CreateTeamDefaults();

        Self.Normalize();
        Team.Normalize();
    }
}

//====================[ PostReviveSourceConfig ]====================
public sealed class PostReviveSourceConfig
{
    public bool RestoreBodyParts { get; set; } = true;
    public BodyRestorePercentConfig RestorePercent { get; set; } = BodyRestorePercentConfig.CreateNeutralDefaults();
    public bool RemoveBleeds { get; set; }
    public bool RemoveFractures { get; set; }
    public float InvulnerabilityDurationSeconds { get; set; } = 5f;
    public float InvulnerabilitySpeedPercent { get; set; } = 100f;
    public float CooldownSeconds { get; set; }
    public bool ApplyContusionOnRevive { get; set; } = true;
    public float ContusionDurationSeconds { get; set; }
    public bool ApplyPainOnRevive { get; set; }
    public float PainDurationSeconds { get; set; } = 30f;

    public static PostReviveSourceConfig CreateSelfDefaults() => new()
    {
        RestoreBodyParts = true,
        RestorePercent = BodyRestorePercentConfig.CreateNeutralDefaults(),
        RemoveBleeds = false,
        RemoveFractures = false,
        InvulnerabilityDurationSeconds = 5f,
        InvulnerabilitySpeedPercent = 100f,
        CooldownSeconds = 240f,
        ApplyContusionOnRevive = true,
        ContusionDurationSeconds = 10f,
        ApplyPainOnRevive = true,
        PainDurationSeconds = 30f
    };

    public static PostReviveSourceConfig CreateTeamDefaults() => new()
    {
        RestoreBodyParts = true,
        RestorePercent = BodyRestorePercentConfig.CreateTeamDefaults(),
        RemoveBleeds = true,
        RemoveFractures = true,
        InvulnerabilityDurationSeconds = 5f,
        InvulnerabilitySpeedPercent = 100f,
        CooldownSeconds = 180f,
        ApplyContusionOnRevive = true,
        ContusionDurationSeconds = 5f,
        ApplyPainOnRevive = false,
        PainDurationSeconds = 30f
    };

    public void Normalize()
    {
        RestorePercent ??= BodyRestorePercentConfig.CreateNeutralDefaults();
        RestorePercent.Normalize();

        InvulnerabilityDurationSeconds = Math.Max(0f, InvulnerabilityDurationSeconds);
        InvulnerabilitySpeedPercent = float.IsFinite(InvulnerabilitySpeedPercent)
            ? Math.Clamp(InvulnerabilitySpeedPercent, 0f, 200f)
            : 100f;
        CooldownSeconds = Math.Max(0f, CooldownSeconds);
        ContusionDurationSeconds = Math.Max(0f, ContusionDurationSeconds);
        PainDurationSeconds = Math.Max(0f, PainDurationSeconds);
    }
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
        Head = 0f,
        Chest = 35f,
        Stomach = 35f,
        Arms = 35f,
        Legs = 35f
    };

    public static BodyRestorePercentConfig CreateTeamDefaults() => new()
    {
        Head = 50f,
        Chest = 50f,
        Stomach = 50f,
        Arms = 50f,
        Legs = 50f
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
    public bool EnableGodMode { get; set; }
    public bool EnableGhostMode { get; set; } = true;
    public bool EnableHardcoreMode { get; set; }
    public bool HardcoreHeadshotsAreFatal { get; set; }
    public float HardcoreCriticalStateChance { get; set; } = 0.75f;

    public void Normalize()
    {
        HardcoreCriticalStateChance = Math.Clamp(HardcoreCriticalStateChance, 0f, 1f);
    }
}

//====================[ TeamHealingConfig ]====================
public sealed class TeamHealingConfig
{
    public bool Enabled { get; set; } = true;
    public float HoldSeconds { get; set; } = 1f;
    public float UseTimeMultiplier { get; set; } = 1f;
    public float MinHpResourceToDisplay { get; set; } = 50f;
    public float NutritionMinDeficitToDisplay { get; set; } = 0.5f;
    public bool AllowLootDownedPlayers { get; set; } = true;

    public void Normalize()
    {
        HoldSeconds = Math.Max(0.1f, HoldSeconds);
        UseTimeMultiplier = Math.Max(0.01f, UseTimeMultiplier);
        MinHpResourceToDisplay = Math.Max(0f, MinHpResourceToDisplay);
        NutritionMinDeficitToDisplay = Math.Max(0f, NutritionMinDeficitToDisplay);
    }
}

//====================[ DevelopmentGameplayConfig ]====================
public sealed class DevelopmentGameplayConfig
{
    public bool NoReviveItemRequired { get; set; }
}
