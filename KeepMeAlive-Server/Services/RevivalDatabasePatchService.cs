//====================[ Imports ]====================
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using System.Collections;
using System.Reflection;

namespace KeepMeAlive.Server.Services;

//====================[ RevivalDatabasePatchService ]====================
[Injectable(InjectionType.Singleton)]
public class RevivalDatabasePatchService(ISptLogger<RevivalDatabasePatchService> logger,
    DatabaseServer databaseServer, RevivalConfigService configService)
{
    //====================[ Constants ]====================
    private const string TraderAssortItemId = "60dc0d93a66c41234a80aeff";
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
            dynamic tables = databaseServer.GetTables();
            if (tables is null)
            {
                logger.Warning("[KeepMeAlive.Server] Database tables are null; skipping DB patch.");
                return;
            }

            PatchTraderAssort(tables);
            PatchRevivalItemTemplate(tables);
            PatchSpecialSlotFilters(tables);

            logger.Info("[KeepMeAlive.Server] Applied database patches.");
        }
        catch (Exception ex)
        {
            logger.Error($"[KeepMeAlive.Server] Database patch failed: {ex.Message}");
        }
    }

    //====================[ Patch Steps ]====================
    private void PatchTraderAssort(dynamic tables)
    {
        string traderId = ResolveTraderId(configService.Config.RevivalItem.Trading.Trader);
        int price = Math.Max(1, configService.Config.RevivalItem.Trading.AmountRoubles);
        string templateId = GetReviveTemplateId();

        dynamic trader = tables.Traders[traderId];
        if (trader is null)
        {
            logger.Warning($"[KeepMeAlive.Server] Trader not found: {traderId}");
            return;
        }

        if (trader.Assort == null)
        {
            logger.Warning($"[KeepMeAlive.Server] Trader assort missing for {traderId}");
            return;
        }

        if (!configService.Config.RevivalItem.Trading.EnableTraderOffer)
        {
            logger.Info("[KeepMeAlive.Server] Trader offer injection disabled via config.");
            return;
        }

        // Remove previous entry (idempotent patching). SPT 4.0 uses PascalCase (Id, Tpl).
        var items = (System.Collections.IList)trader.Assort.Items;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var x = items[i];
            var id = x?.GetType().GetProperty("Id")?.GetValue(x)?.ToString();
            if (id == TraderAssortItemId) items.RemoveAt(i);
        }

        // Create new assort item. SPT 4.0 uses strongly-typed models with Id, Tpl (PascalCase).
        if (items.Count == 0) { logger.Warning("[KeepMeAlive.Server] Trader assort has no items; cannot add reviveItem."); return; }
        Type? itemType = items[0]?.GetType();
        if (itemType == null) return;
        var newItem = Activator.CreateInstance(itemType);
        if (newItem != null)
        {
            var t = newItem.GetType();
            SetFirstExistingMemberValue(t, newItem, new[] { "Id", "_id" }, TraderAssortItemId);
            SetFirstExistingMemberValue(t, newItem, new[] { "Template", "Tpl", "_tpl", "TemplateId" }, templateId);
            SetFirstExistingMemberValue(t, newItem, new[] { "ParentId", "parentId" }, "hideout");
            SetFirstExistingMemberValue(t, newItem, new[] { "SlotId", "slotId" }, "hideout");
            EnsureItemUpd(newItem);

            if (HasValidTpl(newItem))
            {
                items.Add(newItem);
            }
            else
            {
                logger.Warning("[KeepMeAlive.Server] Skipping trader assort inject: could not set valid _tpl/Tpl on item model.");
            }
        }
        else
        {
            logger.Warning("[KeepMeAlive.Server] Could not create assort item instance.");
        }

        // Barter/price setup
        var barterKey = ConvertDictionaryKey(trader.Assort.BarterScheme, TraderAssortItemId);
        trader.Assort.BarterScheme[barterKey] = BuildBarterSchemeValue(trader.Assort.BarterScheme, price, TraderConstants.RoubleTemplateId);

        var loyalKey = ConvertDictionaryKey(trader.Assort.LoyalLevelItems, TraderAssortItemId);
        trader.Assort.LoyalLevelItems[loyalKey] = 2;

        // Legacy parity: log the injected assort entry details for easier diagnostics.
        logger.Info($"[KeepMeAlive.Server] Revive Item injected into trader assort. Trader={traderId}, ItemId={TraderAssortItemId}, Price={price}");
    }

    private void PatchRevivalItemTemplate(dynamic tables)
    {
        string templateId = GetReviveTemplateId();
        dynamic item = tables.Templates.Items[templateId];
        if (item == null) { logger.Warning("[KeepMeAlive.Server] Revive Item template not found."); return; }

        var props = GetFirstExistingPropertyValue(item, new[] { "Props", "_props" });
        if (props == null) { logger.Warning("[KeepMeAlive.Server] Revive Item template has no Props."); return; }

        int price = Math.Max(1, configService.Config.RevivalItem.Trading.AmountRoubles);
        try { props.CreditsPrice = price; } catch { /* CreditsPrice may not exist */ }
        try { props.Description = "A portable revive item used to revive yourself or others from critical condition. When in critical state, use your configured revive key to get a second chance."; } catch { }
        try { if (props.Width != null && props.Height != null) { props.Width = 2; props.Height = 1; } } catch { }
        try
        {
            var bg = GetFirstExistingPropertyValue(props, new[] { "BackgroundColor", "backgroundColor" });
            if (bg == null)
            {
                var propsType = props.GetType();
                SetFirstExistingMemberValue(propsType, props, new[] { "BackgroundColor", "backgroundColor" }, "red");
            }
        }
        catch
        {
            // optional visual property
        }
    }

    private void PatchSpecialSlotFilters(dynamic tables)
    {
        try
        {
            // SpecialSlot1/2/3 live in Props.Slots (not Grids) of pocket templates.
            // Explicitly target Slots so we don't accidentally patch Grids (pocket1-4) which use category filters.
            var slotsOnly = new[] { "Slots", "slots" };
            foreach (var templateId in PocketTemplateIds)
            {
                PatchSlotFilters(tables, templateId, slotsPropertyNames: slotsOnly,
                    slotSlotNameOrId: "SpecialSlot", slotLabel: "SpecialSlot");
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[KeepMeAlive.Server] Special-slot patch warning: {ex.Message}");
        }
    }

    //====================[ Slot Filter Helpers ]====================
    /// <summary>
    /// Adds the reviveItem template to slot filters. When <paramref name="slotSlotNameOrId"/> is set,
    /// only patches slots whose Id or Name contains that string (e.g. "SpecialSlot").
    /// </summary>
    /// <param name="slotsPropertyNames">Property names to try for slots array. Use ["Slots","slots"] for SpecialSlots; null for default.</param>
    private void PatchSlotFilters(dynamic tables, string templateId, string[]? slotsPropertyNames, string? slotSlotNameOrId, string slotLabel)
    {
        dynamic template = tables.Templates.Items[templateId];
        if (template == null)
        {
            return;
        }

        var props = GetFirstExistingPropertyValue(template, new[] { "Props", "_props" });
        if (props == null)
        {
            return;
        }

        var propNames = slotsPropertyNames ?? new[] { "Slots", "slots", "Grids", "grids" };
        var slotsObj = GetFirstExistingPropertyValue(props, propNames);
        IEnumerable? slotsEnum = slotsObj as IList;
        if (slotsEnum == null && slotsObj is IDictionary dict)
        {
            slotsEnum = (IEnumerable)dict.Values;
        }
        if (slotsEnum == null)
        {
            return;
        }

        int idx = 0;
        foreach (var slot in slotsEnum)
        {
            if (idx >= 12) break; // Reasonable limit for slots
            if (slot == null)
            {
                idx++;
                continue;
            }

            if (!string.IsNullOrEmpty(slotSlotNameOrId))
            {
                var slotId = GetFirstExistingPropertyValue(slot, new[] { "Id", "_id" })?.ToString() ?? "";
                var slotName = GetFirstExistingPropertyValue(slot, new[] { "Name", "_name" })?.ToString() ?? "";
                if (!slotId.Contains(slotSlotNameOrId, StringComparison.OrdinalIgnoreCase) &&
                    !slotName.Contains(slotSlotNameOrId, StringComparison.OrdinalIgnoreCase))
                {
                    idx++;
                    continue;
                }
            }

            var slotProps = GetFirstExistingPropertyValue(slot, new[] { "Props", "_props" });
            if (slotProps == null)
            {
                idx++;
                continue;
            }

            var filtersObj = GetFirstExistingPropertyValue(slotProps, new[] { "Filters", "filters" });
            if (filtersObj is not IList filters || filters.Count == 0)
            {
                idx++;
                continue;
            }

            var firstFilter = filters[0];
            if (firstFilter == null)
            {
                idx++;
                continue;
            }

            var filterListObj = GetFirstExistingPropertyValue(firstFilter, new[] { "Filter", "filter" });
            if (filterListObj is not IList filterList)
            {
                idx++;
                continue;
            }

            bool exists = false;
            string reviveTemplateId = GetReviveTemplateId();
            foreach (var entry in filterList)
            {
                if (string.Equals(entry?.ToString(), reviveTemplateId, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                var elementType = filterList.GetType().GetGenericArguments().Length > 0
                    ? filterList.GetType().GetGenericArguments()[0]
                    : typeof(string);
                filterList.Add(ConvertToTargetType(elementType, reviveTemplateId));
                logger.Info($"[KeepMeAlive.Server] Added reviveItem to {slotLabel} slot filter. Template={templateId}, SlotIndex={idx}");
            }

            idx++;
        }
    }

    //====================[ Reflection Helpers ]====================
    private static string ResolveTraderId(string traderName)
    {
        if (TraderConstants.TraderIdByName.TryGetValue(traderName, out var traderId))
        {
            return traderId;
        }

        return TraderConstants.TraderIdByName["Therapist"];
    }

    private string GetReviveTemplateId()
    {
        string configured = configService.Config.RevivalItem.TemplateId;
        return string.IsNullOrWhiteSpace(configured)
            ? TraderConstants.RevivalItemTemplateId
            : configured;
    }

    private static object ConvertDictionaryKey(object dictionary, string key)
    {
        var dictType = dictionary.GetType();
        var genericArgs = dictType.GetGenericArguments();
        var keyType = genericArgs.Length >= 1 ? genericArgs[0] : typeof(string);
        return ConvertToTargetType(keyType, key);
    }

    private static object ConvertToTargetType(Type targetType, object value)
    {
        if (value == null)
        {
            return value!;
        }

        var nonNullable = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (nonNullable.IsInstanceOfType(value))
        {
            return value;
        }

        if (nonNullable == typeof(string))
        {
            return value.ToString() ?? string.Empty;
        }

        // SPT 4.0 uses strongly-typed Mongo ID wrappers with varying names.
        if (nonNullable.Name.Contains("Mongo", StringComparison.OrdinalIgnoreCase))
        {
            var str = value.ToString() ?? string.Empty;
            if (!IsValidMongoIdString(str))
            {
                return value;
            }

            var ctor = nonNullable.GetConstructor([typeof(string)]);
            if (ctor != null)
            {
                return ctor.Invoke([str]);
            }

            var parse = nonNullable.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
            if (parse != null)
            {
                return parse.Invoke(null, [str]) ?? value;
            }

            var implicitOp = nonNullable.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
            if (implicitOp != null)
            {
                return implicitOp.Invoke(null, [str]) ?? value;
            }
        }

        try
        {
            return Convert.ChangeType(value, nonNullable);
        }
        catch
        {
            return value;
        }
    }

    private static object BuildBarterSchemeValue(object barterDictionary, int price, string tplId)
    {
        var dictType = barterDictionary.GetType();
        var dictArgs = dictType.GetGenericArguments();
        if (dictArgs.Length < 2)
        {
            // Fallback to the old shape if generic type metadata is unavailable.
            return new[]
            {
                new[]
                {
                    new
                    {
                        count = price,
                        _tpl = tplId
                    }
                }
            };
        }

        var valueType = dictArgs[1]; // Expected: List<List<BarterScheme>>
        var outerList = Activator.CreateInstance(valueType) as IList;
        if (outerList == null)
        {
            throw new InvalidOperationException("Unable to instantiate barter scheme container.");
        }

        var outerItemType = valueType.GetGenericArguments().Length > 0 ? valueType.GetGenericArguments()[0] : null; // List<BarterScheme>
        if (outerItemType == null)
        {
            throw new InvalidOperationException("Unable to determine inner barter list type.");
        }

        var innerList = Activator.CreateInstance(outerItemType) as IList;
        if (innerList == null)
        {
            throw new InvalidOperationException("Unable to instantiate inner barter list.");
        }

        var barterType = outerItemType.GetGenericArguments().Length > 0 ? outerItemType.GetGenericArguments()[0] : null; // BarterScheme
        if (barterType == null)
        {
            throw new InvalidOperationException("Unable to determine barter item type.");
        }

        var barterItem = Activator.CreateInstance(barterType)
            ?? throw new InvalidOperationException("Unable to instantiate barter item.");

        SetFirstExistingMemberValue(barterType, barterItem, ["Count", "count"], price);
        SetFirstExistingMemberValue(barterType, barterItem, ["Template", "Tpl", "_tpl"], tplId);

        innerList.Add(barterItem);
        outerList.Add(innerList);
        return outerList;
    }

    private static void SetFirstExistingMemberValue(Type type, object instance, string[] memberNames, object value)
    {
        foreach (var memberName in memberNames)
        {
            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanWrite)
            {
                var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
                if (field == null || field.IsInitOnly)
                {
                    continue;
                }

                try
                {
                    field.SetValue(instance, ConvertToTargetType(field.FieldType, value));
                    return;
                }
                catch
                {
                    continue;
                }
            }

            try
            {
                prop.SetValue(instance, ConvertToTargetType(prop.PropertyType, value));
                return;
            }
            catch
            {
                continue;
            }
        }
    }

    private static object? GetFirstExistingPropertyValue(object instance, string[] propertyNames)
    {
        if (instance == null)
        {
            return null;
        }

        var type = instance.GetType();
        foreach (var propertyName in propertyNames)
        {
            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanRead)
            {
                return prop.GetValue(instance);
            }

            var field = type.GetField(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                return field.GetValue(instance);
            }
        }

        return null;
    }

    private static void EnsureItemUpd(object item)
    {
        var itemType = item.GetType();
        var updProp = itemType.GetProperty("Upd", BindingFlags.Public | BindingFlags.Instance);
        if (updProp == null || !updProp.CanRead)
        {
            return;
        }

        var upd = updProp.GetValue(item);
        if (upd == null && updProp.CanWrite)
        {
            upd = Activator.CreateInstance(updProp.PropertyType);
            updProp.SetValue(item, upd);
        }

        if (upd == null)
        {
            return;
        }

        var updType = upd.GetType();
        SetFirstExistingMemberValue(updType, upd, new[] { "UnlimitedCount", "unlimitedCount" }, true);
        SetFirstExistingMemberValue(updType, upd, new[] { "StackObjectsCount", "stackObjectsCount" }, 999999);
    }

    //====================[ Validation ]====================
    private static bool HasValidTpl(object item)
    {
        var tpl = GetFirstExistingPropertyValue(item, new[] { "Template", "Tpl", "_tpl", "TemplateId" });
        var asString = tpl?.ToString();
        return IsValidMongoIdString(asString);
    }

    private static bool IsValidMongoIdString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 24)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool isHex = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}