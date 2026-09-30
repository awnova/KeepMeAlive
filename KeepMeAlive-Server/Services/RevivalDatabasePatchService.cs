//====================[ Imports ]====================
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace KeepMeAlive.Server.Services;

//====================[ RevivalDatabasePatchService ]====================
[Injectable(InjectionType.Singleton)]
public class RevivalDatabasePatchService(ISptLogger<RevivalDatabasePatchService> logger,
    TradersTable tradersTable, TemplateTable templateTable, LocaleTable localeTable, RevivalConfigService configService)
{
    //====================[ Constants ]====================
    private const string TraderAssortItemId = "60dc0d93a66c41234a80aeff";
    private const string ItemDescription =
        "A portable revive item used to revive yourself or others from critical condition. When in critical state, hold your configured revive key to get a second chance.";

    private static readonly string[] PocketTemplateIds =
    {
        "627a4e6b255f7527fb05a0f6", // standard pockets
        "65e080be269cbd5c5005e529"  // unheard pockets
    };

    //====================[ Lifecycle ]====================
    public void OnPostLoad()
    {
        try
        {
            PatchTraderAssort();
            PatchRevivalItemTemplate();
            PatchItemDescription();
            PatchSpecialSlotFilters();

            logger.Info("[KeepMeAlive.Server] Applied database patches.");
        }
        catch (Exception ex)
        {
            logger.Error($"[KeepMeAlive.Server] Database patch failed: {ex.Message}");
        }
    }

    //====================[ Patch Steps ]====================
    private void PatchTraderAssort()
    {
        var trading = configService.Config.RevivalItem.Trading;
        string traderId = ResolveTraderId(trading.Trader);
        int price = trading.AmountRoubles;
        string templateId = GetReviveTemplateId();

        var trader = tradersTable.GetTrader(traderId);
        if (trader is null)
        {
            logger.Warning($"[KeepMeAlive.Server] Trader not found: {traderId}");
            return;
        }

        var assort = trader.Assort;
        if (assort?.Items is null)
        {
            logger.Warning($"[KeepMeAlive.Server] Trader assort missing for {traderId}");
            return;
        }

        // Remove previous entry (idempotent patching).
        assort.Items.RemoveAll(i => i.Id == TraderAssortItemId);

        assort.Items.Add(new Item
        {
            Id = TraderAssortItemId,
            Template = templateId,
            ParentId = "hideout",
            SlotId = "hideout",
            Upd = new Upd
            {
                UnlimitedCount = true,
                StackObjectsCount = 999999
            }
        });

        assort.BarterScheme[TraderAssortItemId] = new List<List<BarterScheme>>
        {
            new List<BarterScheme>
            {
                new BarterScheme { Count = price, Template = TraderConstants.RoubleTemplateId }
            }
        };
        assort.LoyalLevelItems[TraderAssortItemId] = trading.LoyaltyLevel;

        // The offer is unlimited, so a price below the flea base price lets players buy from the
        // trader and resell on the flea for profit indefinitely.
        if (templateTable.Prices != null && templateTable.Prices.TryGetValue(templateId, out var fleaBase) && price < fleaBase)
        {
            logger.Warning($"[KeepMeAlive.Server] Revive item trader price ({price}) is below its flea base price ({fleaBase:0}); players can resell it for profit.");
        }

        logger.Info($"[KeepMeAlive.Server] Revive item injected into trader assort. Trader={traderId}, Price={price}, LL={trading.LoyaltyLevel}");
    }

    private void PatchRevivalItemTemplate()
    {
        string templateId = GetReviveTemplateId();
        if (!templateTable.Items.TryGetValue(templateId, out var item) || item?.Properties is null)
        {
            logger.Warning($"[KeepMeAlive.Server] Revive item template {templateId} not found.");
            return;
        }

        if (configService.Config.RevivalItem.ResizeTo2x1 && item.Properties.Width != null && item.Properties.Height != null)
        {
            item.Properties.Width = 2;
            item.Properties.Height = 1;
        }
    }

    // The in-game description comes from the "<tpl> Description" locale key, not the template.
    private void PatchItemDescription()
    {
        string key = $"{GetReviveTemplateId()} Description";
        foreach (var (_, lazyLocale) in localeTable.Global)
        {
            lazyLocale.AddTransformer(localeData =>
            {
                if (localeData is not null)
                {
                    localeData[key] = ItemDescription;
                }
                return localeData;
            });
        }
    }

    private void PatchSpecialSlotFilters()
    {
        string reviveTemplateId = GetReviveTemplateId();
        foreach (var templateId in PocketTemplateIds)
        {
            PatchSlotFilters(templateId, reviveTemplateId);
        }
    }

    //====================[ Slot Filter Helpers ]====================
    // Adds the revive item template to SpecialSlot filters, which live in Props.Slots
    // (not Grids) of pocket templates.
    private void PatchSlotFilters(string templateId, string reviveTemplateId)
    {
        if (!templateTable.Items.TryGetValue(templateId, out var template) || template?.Properties?.Slots is null)
        {
            return;
        }

        foreach (var slot in template.Properties.Slots)
        {
            if (slot?.Name is null || !slot.Name.Contains("SpecialSlot", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var filters = slot.Properties?.Filters;
            if (filters is null)
            {
                continue;
            }

            foreach (var filter in filters)
            {
                filter.Filter ??= new HashSet<MongoId>();
                filter.Filter.Add(reviveTemplateId);
            }
        }
    }

    //====================[ Trader Resolution ]====================
    private string ResolveTraderId(string trader)
    {
        if (TraderConstants.TraderIdByName.TryGetValue(trader, out var traderId))
        {
            return traderId;
        }

        if (trader.Length == 24 && tradersTable.GetTrader(trader) is not null)
        {
            return trader;
        }

        logger.Warning($"[KeepMeAlive.Server] Unknown trader '{trader}' in config; falling back to Therapist.");
        return TraderConstants.TraderIdByName["Therapist"];
    }

    private string GetReviveTemplateId() => configService.Config.RevivalItem.TemplateId;
}
