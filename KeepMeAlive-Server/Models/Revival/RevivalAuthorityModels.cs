using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Utils;

namespace KeepMeAlive.Server.Models.Revival;

//====================[ RevivalState ]====================
public enum RevivalState
{
    None = 0,
    BleedingOut = 1,
    Reviving = 2,
    Revived = 3,
    CoolDown = 4
}

//====================[ RevivalSourceKind ]====================
public enum RevivalSourceKind
{
    Self = 0,
    Team = 1
}

//====================[ RevivalAuthorityRequest ]====================
// Body of every /keepmealive/state/* request. Property names match the client's
// Newtonsoft (PascalCase) serialization.
public record RevivalAuthorityRequest : IRequestData
{
    [JsonPropertyName("PlayerId")]
    public string PlayerId { get; set; } = string.Empty;

    [JsonPropertyName("ReviverId")]
    public string ReviverId { get; set; } = string.Empty;

    [JsonPropertyName("Source")]
    public string Source { get; set; } = string.Empty;
}

//====================[ RevivalStateEntry ]====================
public record RevivalStateEntry
{
    public string PlayerId { get; init; } = string.Empty;
    public RevivalState State { get; set; } = RevivalState.None;
    public RevivalSourceKind Source { get; set; } = RevivalSourceKind.Self;
    public string ReviverId { get; set; } = string.Empty;
    public long LastUpdatedUnixSeconds { get; set; }
    public long CooldownUntilUnixSeconds { get; set; }
    public int LivesRemaining { get; set; }
}

//====================[ RevivalAuthorityResponse ]====================
public record RevivalAuthorityResponse
{
    public bool Success { get; init; }
    public RevivalDeniedCode DenialCode { get; init; } = RevivalDeniedCode.None;
    public string Reason { get; init; } = string.Empty;
    public RevivalStateEntry? State { get; init; }
}

//====================[ RevivalDeniedCode ]====================
public enum RevivalDeniedCode
{
    None = 0,
    Cooldown = 1,
    InvalidState = 2,
    NotDowned = 3,
    CompleteInvalidState = 4,
    ServerError = 5,
    FeatureDisabled = 6,
    OutOfLives = 7,
    NotAuthorized = 8,
    BadRequest = 9
}
