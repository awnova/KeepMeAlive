//====================[ Imports ]====================
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using KeepMeAlive.Server.Services;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers.Http;

namespace KeepMeAlive.Server.Http;

//====================[ RaidLifecycleListener ]====================
// Runs ahead of SPT's own listener (lower TypePriority) for the two local-raid lifecycle routes,
// then forwards the request to it after normalizing end-raid damage-history type values:
//   /client/match/local/start - every Fika peer calls this for its own session before loading
//   /client/match/local/end   - every Fika peer submits its own raid result
// Revival state and lives are reset server-side for the calling session on both routes.
[Injectable(TypePriority = 1)]
public class RaidLifecycleListener(
    SptHttpListener sptHttpListener,
    RevivalStateService stateService,
    SessionProfileResolver profileResolver) : IHttpListener
{
    //====================[ Constants ]====================
    private const string StartPath = "/client/match/local/start";
    private const string EndPath = "/client/match/local/end";

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

    //====================[ Listener API ]====================
    public bool CanHandle(HttpContext context)
    {
        if (context.Request.Method != "POST") return false;
        var path = context.Request.Path.Value;
        return string.Equals(path, StartPath, StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, EndPath, StringComparison.OrdinalIgnoreCase);
    }

    public async Task HandleAsync(MongoId sessionId, HttpContext context, CancellationToken cancellationToken = default)
    {
        stateService.ResetForRaid(profileResolver.GetOwnedProfileIds(sessionId));

        if (string.Equals(context.Request.Path.Value, EndPath, StringComparison.OrdinalIgnoreCase))
        {
            await FixEndRaidBodyAsync(context.Request);
        }

        await sptHttpListener.HandleAsync(sessionId, context, cancellationToken);
    }

    //====================[ End-Raid Damage History Normalization ]====================
    // Converts numeric DamageHistory Type values to their EDamageType names before SPT handles the request.
    private static async Task FixEndRaidBodyAsync(HttpRequest request)
    {
        bool isCompressed = !request.Headers.TryGetValue("requestcompressed", out var cv) || cv != "0";

        string body;
        if (isCompressed)
        {
            await using var zlibStream = new ZLibStream(request.Body, CompressionMode.Decompress);
            using var reader = new StreamReader(zlibStream, Encoding.UTF8);
            body = await reader.ReadToEndAsync();
        }
        else
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            body = await reader.ReadToEndAsync();
        }

        var bodyBytes = Encoding.UTF8.GetBytes(FixDamageHistoryTypes(body));
        request.Body = new MemoryStream(bodyBytes);
        request.ContentLength = bodyBytes.Length;
        request.Headers["requestcompressed"] = "0";
    }

    private static string FixDamageHistoryTypes(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null) return json;

            var damageHistory = GetNode(root, "results", "profile", "Stats", "Eft", "DamageHistory");
            if (damageHistory is null) return json;

            bool changed = false;
            if (GetProp(damageHistory, "BodyParts") is JsonObject bodyPartsObj)
                foreach (var (_, partNode) in bodyPartsObj)
                    if (partNode is JsonArray entries)
                        foreach (var entry in entries)
                            changed |= FixTypeField(entry as JsonObject);

            changed |= FixTypeField(GetProp(damageHistory, "LethalDamage") as JsonObject);

            return changed ? root.ToJsonString() : json;
        }
        catch
        {
            return json;
        }
    }

    //====================[ JSON Helpers ]====================
    private static bool FixTypeField(JsonObject? obj)
    {
        if (obj is null) return false;
        if (GetProp(obj, "Type") is not JsonValue typeVal || !typeVal.TryGetValue<long>(out long intValue)) return false;

        obj["Type"] = JsonValue.Create(ResolveTypeName(intValue));
        return true;
    }

    private static JsonNode? GetNode(JsonNode root, params string[] path)
    {
        JsonNode? current = root;
        foreach (var key in path)
        {
            current = GetProp(current, key);
            if (current is null) return null;
        }
        return current;
    }

    private static JsonNode? GetProp(JsonNode? node, string name)
    {
        if (node is not JsonObject obj) return null;
        if (obj[name] is { } exact) return exact;
        foreach (var kvp in obj)
            if (string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        return null;
    }

    //====================[ Type Resolver ]====================
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
