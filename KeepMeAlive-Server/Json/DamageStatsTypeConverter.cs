//====================[ Imports ]====================
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Utils.Json;

namespace KeepMeAlive.Server.Json;

//====================[ RevivalJsonConverterRegistrator ]====================
// SPT's JsonUtil collects every IJsonConverterRegistrator from DI and adds its converters to the
// global serializer options.
[Injectable]
public class RevivalJsonConverterRegistrator : IJsonConverterRegistrator
{
    public IEnumerable<JsonConverter> GetJsonConverters() => [new DamageStatsTypeConverter()];
}

//====================[ DamageStatsTypeConverter ]====================
// End-raid DamageHistory can carry numeric EDamageType values (EFT writes an undefined/combined
// [Flags] value as a number), but SPT models DamageStats.Type as a string and has no number-to-
// string converter, so /client/match/local/end fails to deserialize. Numeric Type values are
// converted to their EDamageType names; everything else deserializes exactly as SPT would.
public sealed class DamageStatsTypeConverter : JsonConverter<DamageStats>
{
    //====================[ Lookup ]====================
    // EFT.EDamageType - [Flags] enum
    private static readonly Dictionary<long, string> DamageTypeNames = new()
    {
        [1]        = "Undefined",
        [2]        = "Fall",
        [4]        = "Explosion",
        [8]        = "Barbed",
        [16]       = "Flame",
        [32]       = "GrenadeFragment",
        [64]       = "Impact",
        [128]      = "Existence",
        [256]      = "Medicine",
        [512]      = "Bullet",
        [1024]     = "Melee",
        [2048]     = "Landmine",
        [4096]     = "Sniper",
        [8192]     = "Blunt",
        [16384]    = "LightBleeding",
        [32768]    = "HeavyBleeding",
        [65536]    = "Dehydration",
        [131072]   = "Exhaustion",
        [262144]   = "RadExposure",
        [524288]   = "Stimulator",
        [1048576]  = "Poison",
        [2097152]  = "LethalToxin",
        [4194304]  = "Btr",
        [8388608]  = "Artillery",
        [16777216] = "HotGases",
        [33554432] = "ThermobaricExplosion",
        [67108864] = "Environment",
    };

    // SPT's options with this converter removed, per source options (indented / non-indented),
    // so the inner (de)serialization uses SPT's normal handling without recursing back here.
    private readonly ConcurrentDictionary<JsonSerializerOptions, JsonSerializerOptions> _innerOptions = new();

    //====================[ Converter API ]====================
    public override DamageStats? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader);
        if (node is JsonObject obj) FixTypeField(obj);
        return node.Deserialize<DamageStats>(GetInnerOptions(options));
    }

    public override void Write(Utf8JsonWriter writer, DamageStats value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, GetInnerOptions(options));

    private JsonSerializerOptions GetInnerOptions(JsonSerializerOptions options)
        => _innerOptions.GetOrAdd(options, source =>
        {
            var inner = new JsonSerializerOptions(source);
            inner.Converters.Remove(this);
            return inner;
        });

    //====================[ Type Field ]====================
    private static void FixTypeField(JsonObject obj)
    {
        var key = obj.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, "Type", StringComparison.OrdinalIgnoreCase));
        if (key is null) return;
        if (obj[key] is JsonValue typeVal && typeVal.TryGetValue<long>(out long intValue))
            obj[key] = JsonValue.Create(ResolveTypeName(intValue));
    }

    private static string ResolveTypeName(long value)
    {
        if (DamageTypeNames.TryGetValue(value, out var name))
            return name;

        var parts = new List<string>();
        long remaining = value;
        foreach (var (flag, flagName) in DamageTypeNames.OrderByDescending(kv => kv.Key))
        {
            if (remaining == 0) break;
            if ((remaining & flag) == flag)
            {
                parts.Add(flagName);
                remaining &= ~flag;
            }
        }
        return parts.Count > 0 ? string.Join(", ", parts) : value.ToString();
    }
}
