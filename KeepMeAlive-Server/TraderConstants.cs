namespace KeepMeAlive.Server;

//====================[ TraderConstants ]====================
public static class TraderConstants
{
    //====================[ Template IDs ]====================
    public const string RevivalItemTemplateId = "5c052e6986f7746b207bc3c9";
    public const string RoubleTemplateId = "5449016a4bdc2d6f028b456f";

    //====================[ Trader Mapping ]====================
    public static readonly IReadOnlyDictionary<string, string> TraderIdByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Prapor"] = "54cb50c76803fa8b248b4571",
        ["Therapist"] = "54cb57776803fa99248b456e",
        ["Fence"] = "579dc571d53a0658a154fbec",
        ["Skier"] = "58330581ace78e27b8b10cee",
        ["Peacekeeper"] = "5935c25fb3acc3127c3d8cd9",
        ["Mechanic"] = "5a7c2eca46aef81a7ca2145d",
        ["Ragman"] = "5ac3b934156ae10c4430e83c",
        ["Jaeger"] = "5c0647fdd443bc2504c2d371",
        ["Lighthousekeeper"] = "638f541a29ffd1183d187f57"
    };
}
