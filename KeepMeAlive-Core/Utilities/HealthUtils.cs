using EFT;

namespace KeepMeAlive.Utilities
{
    internal static class HealthUtils
    {
        // All seven anatomical body parts (excludes EBodyPart.Common).
        public static readonly EBodyPart[] RealBodyParts =
        {
            EBodyPart.Head,
            EBodyPart.Chest,
            EBodyPart.Stomach,
            EBodyPart.LeftArm,
            EBodyPart.RightArm,
            EBodyPart.LeftLeg,
            EBodyPart.RightLeg
        };

        // The parts EFT's ActiveHealthController.method_24 kills on when destroyed. EFT's own VitalBodyParts list also holds
        // the stomach, but a destroyed stomach never kills; it is a surgery-restorable part like a limb.
        public static readonly EBodyPart[] LethalBodyParts =
        {
            EBodyPart.Head,
            EBodyPart.Chest
        };

        // Applies a contusion without producing the tinnitus ring.
        public static void ApplyContusionSilent(Player player, float durationSeconds, float strength = 1f)
        {
            if (durationSeconds <= 0f || player?.ActiveHealthController is not { } hc)
            {
                return;
            }

            GodMode.RunWithDamageEnabled(player, () =>
                TinnitusScope.Run(() => hc.DoContusion(durationSeconds, strength)));
        }
    }
}

