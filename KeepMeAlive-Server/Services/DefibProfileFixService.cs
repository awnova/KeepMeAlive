//====================[ Imports ]====================
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Servers;

namespace KeepMeAlive.Server.Services;

//====================[ DefibProfileFixService ]====================
// The game saves Resource.Value = 0 on every barter item, so defibs saved before they had uses would load as empty.
// Runs on each start (idempotent): drops those zero values and caps values above the configured uses.
[Injectable(InjectionType.Singleton)]
public class DefibProfileFixService(ISptLogger<DefibProfileFixService> logger, SaveServer saveServer, RevivalConfigService configService)
{
    //====================[ Lifecycle ]====================
    public async Task FixAllProfilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            int maxUses = configService.Config.Defib.Uses;
            int profilesChanged = 0;
            int itemsFixed = 0;

            foreach (var (sessionId, profile) in saveServer.GetProfiles())
            {
                int fixedHere = FixCharacter(profile?.CharacterData?.PmcData, maxUses) + FixCharacter(profile?.CharacterData?.ScavData, maxUses);
                if (fixedHere == 0) continue;

                profilesChanged++;
                itemsFixed += fixedHere;
                await saveServer.SaveProfileAsync(sessionId, cancellationToken);
            }

            logger.Info($"[KeepMeAlive.Server] Defib uses check: fixed {itemsFixed} defib(s) across {profilesChanged} profile(s).");
        }
        catch (Exception ex)
        {
            logger.Error($"[KeepMeAlive.Server] Defib profile check failed: {ex.Message}");
        }
    }

    //====================[ Fix Helpers ]====================
    private static int FixCharacter(PmcData? character, int maxUses)
    {
        var items = character?.Inventory?.Items;
        if (items is null) return 0;

        int fixedCount = 0;
        foreach (var item in items)
        {
            if (!string.Equals(item.Template.ToString(), TraderConstants.DefibTemplateId, StringComparison.OrdinalIgnoreCase)) continue;

            var resource = item.Upd?.Resource;
            if (resource is null) continue;

            double? value = resource.Value;
            if (value is null || double.IsNaN(value.Value) || value.Value < 1d)
            {
                // No Resource entry means a full defib; a used-up one is removed in game, so zero only ever means legacy.
                item.Upd!.Resource = null;
                fixedCount++;
            }
            else if (value.Value > maxUses)
            {
                resource.Value = maxUses;
                fixedCount++;
            }
        }

        return fixedCount;
    }
}
