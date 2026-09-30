//====================[ Imports ]====================
using KeepMeAlive.Server.Models.Revival;
using KeepMeAlive.Server.Services;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Utils;

namespace KeepMeAlive.Server.Callbacks;

//====================[ RevivalStateCallbacks ]====================
// Handlers for /keepmealive/state/*. The caller's identity comes from the session (cookie),
// never from the request body: mutating routes require the session to own the profile it acts on.
[Injectable]
public class RevivalStateCallbacks(
    RevivalStateService stateService,
    RevivalConfigService configService,
    SessionProfileResolver profileResolver,
    HttpResponseUtil httpResponseUtil,
    ISptLogger<RevivalStateCallbacks> logger)
{
    //====================[ Routes ]====================
    public ValueTask<string> BeginCritical(string url, RevivalAuthorityRequest info, MongoId sessionId) =>
        Handle(url, info, sessionId, owned => RequireOwner(info.PlayerId, owned)
            ?? Ok(stateService.SetBleedingOut(info.PlayerId)));

    public ValueTask<string> RequestReviveStart(string url, RevivalAuthorityRequest info, MongoId sessionId) =>
        Handle(url, info, sessionId, owned =>
        {
            // Self-revive: the downed player's own client. Team revive: the reviver's client.
            string actor = string.IsNullOrEmpty(info.ReviverId) ? info.PlayerId : info.ReviverId;
            return RequireOwner(actor, owned) ?? stateService.TryStartRevive(info.PlayerId, info.ReviverId, info.Source);
        });

    public ValueTask<string> CancelReviveStart(string url, RevivalAuthorityRequest info, MongoId sessionId) =>
        Handle(url, info, sessionId, owned => stateService.CancelReviveStart(info.PlayerId, owned));

    public ValueTask<string> CompleteRevive(string url, RevivalAuthorityRequest info, MongoId sessionId) =>
        Handle(url, info, sessionId, owned => RequireOwner(info.PlayerId, owned)
            ?? stateService.TryCompleteRevive(info.PlayerId));

    public ValueTask<string> EndInvulnerability(string url, RevivalAuthorityRequest info, MongoId sessionId) =>
        Handle(url, info, sessionId, owned => RequireOwner(info.PlayerId, owned)
            ?? Ok(stateService.MarkCooldown(info.PlayerId)));

    public ValueTask<string> Reset(string url, RevivalAuthorityRequest info, MongoId sessionId) =>
        Handle(url, info, sessionId, owned => RequireOwner(info.PlayerId, owned)
            ?? Ok(stateService.Reset(info.PlayerId)));

    public ValueTask<string> Get(string url, RevivalAuthorityRequest info, MongoId sessionId) =>
        Handle(url, info, sessionId, _ => Ok(stateService.Get(info.PlayerId)));

    public ValueTask<string> GetRuntimeConfig(string url, RevivalAuthorityRequest info, MongoId sessionId) =>
        new(httpResponseUtil.NoBody(configService.Config));

    //====================[ Helpers ]====================
    private ValueTask<string> Handle(string url, RevivalAuthorityRequest? info, MongoId sessionId,
        Func<ICollection<string>, RevivalAuthorityResponse> action)
    {
        RevivalAuthorityResponse response;
        try
        {
            response = string.IsNullOrWhiteSpace(info?.PlayerId)
                ? Fail(RevivalDeniedCode.BadRequest, "PlayerId is required")
                : action(profileResolver.GetOwnedProfileIds(sessionId));
        }
        catch (Exception ex)
        {
            logger.Warning($"[KeepMeAlive.Server] Authority route {url} failed: {ex.Message}");
            response = Fail(RevivalDeniedCode.ServerError, "Server error");
        }

        return new ValueTask<string>(httpResponseUtil.NoBody(response));
    }

    private RevivalAuthorityResponse? RequireOwner(string profileId, ICollection<string> owned)
    {
        if (owned.Contains(profileId)) return null;

        logger.Warning($"[KeepMeAlive.Server] Rejected request acting for {profileId}: caller does not own that profile.");
        return Fail(RevivalDeniedCode.NotAuthorized, "Not authorized for this player");
    }

    private static RevivalAuthorityResponse Ok(RevivalStateEntry state) => new() { Success = true, State = state };

    private static RevivalAuthorityResponse Fail(RevivalDeniedCode code, string reason) =>
        new() { Success = false, DenialCode = code, Reason = reason };
}
