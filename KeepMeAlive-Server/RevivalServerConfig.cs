namespace KeepMeAlive.Server;

//====================[ RevivalServerConfig ]====================
public sealed class RevivalServerConfig
{
    // Must match KeepMeAlive-Core SyncedServerConfigStore.SupportedSchemaVersion.
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DefibConfig Defib { get; set; } = new();
    public RevivalGameplayConfig Gameplay { get; set; } = new();

    //====================[ Normalize ]====================
    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;

        Defib ??= new DefibConfig();
        Gameplay ??= new RevivalGameplayConfig();

        Defib.Normalize();
        Gameplay.Normalize();
    }
}

//====================[ DefibConfig ]====================
public sealed class DefibConfig
{
    // On by default. Existing items retain their saved footprint until they are moved.
    public bool ResizeTo2x1 { get; set; } = true;
    // How many revives one defibrillator provides; shown as the item's "uses" counter. Spent by the Defib revive with ConsumeDefib on.
    public int Uses { get; set; } = 5;
    public TradingConfig Trading { get; set; } = new();

    public void Normalize()
    {
        Uses = Math.Clamp(Uses, 1, 999);
        Trading ??= new TradingConfig();
        Trading.Normalize();
    }
}

//====================[ TradingConfig ]====================
public sealed class TradingConfig
{
    public int AmountRoubles { get; set; } = 200000;
    public int LoyaltyLevel { get; set; } = 2;

    public void Normalize()
    {
        AmountRoubles = Math.Max(1, AmountRoubles);
        LoyaltyLevel = Math.Clamp(LoyaltyLevel, 1, 4);
    }
}

//====================[ RevivalGameplayConfig ]====================
public sealed class RevivalGameplayConfig
{
    // Settings shared by every revive type: the downed state itself and the lives pool.
    public DownedConfig Downed { get; set; } = new();
    // One section per revive type: Self (own stim), Team (a teammate dragging the body, stim jab),
    // Defib (a teammate not dragging the body, defibrillator animation).
    public ReviveTypeConfig Self { get; set; } = ReviveTypeConfig.CreateSelfDefaults();
    public ReviveTypeConfig Team { get; set; } = ReviveTypeConfig.CreateTeamDefaults();
    public ReviveTypeConfig Defib { get; set; } = ReviveTypeConfig.CreateDefibDefaults();
    // Template id of the stim that Self and Team revives use up when their RequireStim is on. Empty uses the game's
    // Adrenaline stim, which the client knows by default.
    public string StimItemId { get; set; } = "";
    public ProtectionConfig Protection { get; set; } = new();
    public TeamHealingConfig TeamHealing { get; set; } = new();
    public DevelopmentGameplayConfig Development { get; set; } = new();

    public void Normalize()
    {
        StimItemId = (StimItemId ?? "").Trim();

        Downed ??= new DownedConfig();
        Self ??= ReviveTypeConfig.CreateSelfDefaults();
        Team ??= ReviveTypeConfig.CreateTeamDefaults();
        Defib ??= ReviveTypeConfig.CreateDefibDefaults();
        Protection ??= new ProtectionConfig();
        TeamHealing ??= new TeamHealingConfig();
        Development ??= new DevelopmentGameplayConfig();

        Downed.Normalize();
        Self.Normalize();
        Team.Normalize();
        Defib.Normalize();
        Protection.Normalize();
        TeamHealing.Normalize();
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
// Everything one revive type owns: whether it is on, how long it takes, what it costs, the defibrillator rules, and what
// the revived player gets (restore, invulnerability, cooldown, effects).
public sealed class ReviveTypeConfig
{
    public bool Enabled { get; set; } = true;
    public float HoldSeconds { get; set; } = 5f;
    public int LivesCost { get; set; } = 1;
    // Defib only: the reviver must carry a defib, and ConsumeDefib spends one of its uses. Self and Team ignore both.
    public bool RequireDefib { get; set; } = true;
    public bool ConsumeDefib { get; set; }
    // Self and Team only: the reviver must carry a stim, which is single-use and deleted when the revive starts.
    public bool RequireStim { get; set; }
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

    public static ReviveTypeConfig CreateSelfDefaults() => new()
    {
        Enabled = true,
        HoldSeconds = 8f,
        LivesCost = 3,
        RequireStim = false,
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

    public static ReviveTypeConfig CreateTeamDefaults() => new()
    {
        Enabled = true,
        HoldSeconds = 6f,
        LivesCost = 2,
        RequireStim = false,
        RestoreBodyParts = true,
        RestorePercent = BodyRestorePercentConfig.CreateTeamDefaults(),
        RemoveLightBleeds = true,
        RemoveHeavyBleeds = true,
        RemoveLegFractures = false,
        RemoveArmFractures = false,
        InvulnerabilityDurationSeconds = 7f,
        InvulnerabilitySpeedPercent = 80f,
        CooldownSeconds = 180f,
        ApplyContusionOnRevive = true,
        ContusionDurationSeconds = 6f,
        ApplyPainOnRevive = true,
        PainDurationSeconds = 20f
    };

    // The premium revive: needs a brought-in defibrillator (kept after use), so it is the fastest and cleanest.
    public static ReviveTypeConfig CreateDefibDefaults() => new()
    {
        Enabled = true,
        HoldSeconds = 4f,
        LivesCost = 1,
        RequireDefib = true,
        ConsumeDefib = false,
        RestoreBodyParts = true,
        RestorePercent = BodyRestorePercentConfig.CreateDefibDefaults(),
        RemoveLightBleeds = true,
        RemoveHeavyBleeds = true,
        RemoveLegFractures = true,
        RemoveArmFractures = true,
        InvulnerabilityDurationSeconds = 5f,
        InvulnerabilitySpeedPercent = 100f,
        CooldownSeconds = 90f,
        ApplyContusionOnRevive = true,
        ContusionDurationSeconds = 3f,
        ApplyPainOnRevive = true,
        PainDurationSeconds = 10f
    };

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

    public static BodyRestorePercentConfig CreateTeamDefaults() => new()
    {
        Head = 30f,
        Chest = 45f,
        Stomach = 45f,
        Arms = 45f,
        Legs = 45f
    };

    public static BodyRestorePercentConfig CreateDefibDefaults() => new()
    {
        Head = 60f,
        Chest = 75f,
        Stomach = 75f,
        Arms = 75f,
        Legs = 75f
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

//====================[ TeamHealingConfig ]====================
public sealed class TeamHealingConfig
{
    public bool Enabled { get; set; } = true;
    public float UseTimeMultiplier { get; set; } = 1f;
    public float MinHpResourceToDisplay { get; set; } = 50f;
    public float NutritionMinDeficitToDisplay { get; set; } = 0.5f;
    public bool AllowLootDownedPlayers { get; set; } = true;

    public void Normalize()
    {
        UseTimeMultiplier = Math.Max(0.01f, UseTimeMultiplier);
        MinHpResourceToDisplay = Math.Max(0f, MinHpResourceToDisplay);
        NutritionMinDeficitToDisplay = Math.Max(0f, NutritionMinDeficitToDisplay);
    }
}

//====================[ DevelopmentGameplayConfig ]====================
public sealed class DevelopmentGameplayConfig
{
    // Turns on the clients' debug logging and the revive, network and self-revive traces.
    public bool DebugMode { get; set; }
}
