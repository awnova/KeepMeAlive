//====================[ Imports ]====================
using System;
using EFT;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using KeepMeAlive.Fika.Packets;
using KeepMeAlive.Features;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Fika
{
    //====================[ TeamHealPacketHandlers ]====================
    // Receivers for the team-heal request/result packet pair. Kept separate from
    // RevivePacketHandlers since team healing is not part of the revival state machine.
    internal static class TeamHealPacketHandlers
    {
        //====================[ Registration ]====================
        public static void Register(IFikaNetworkManager manager)
        {
            manager.RegisterPacket<TeamHealPacket, NetPeer>(OnTeamHeal);
            manager.RegisterPacket<TeamHealResultPacket, NetPeer>(OnTeamHealResult);
        }

        //====================[ TeamHeal ]====================
        private static void OnTeamHeal(TeamHealPacket packet, NetPeer peer)
        {
            RevivalDebugLog.LogNetworkTrace($"[Packet] TeamHeal: {packet.healerId} healing {packet.patientId} with item {packet.itemId}");

            // Show notification on all machines.
            try
            {
                string healerDisplay  = ModUtils.GetPlayerDisplayName(packet.healerId);
                string patientDisplay = ModUtils.GetPlayerDisplayName(packet.patientId);
                PlayerMessageRouter.Notify(
                    Color.green,
                    PlayerFacingMessages.TeamHeal.StartedBy(healerDisplay, patientDisplay),
                    MessageAudience.InvolvedPlayers,
                    packet.healerId,
                    packet.patientId);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[VFX_UI] TeamHeal notify failed: {ex.Message}");
            }

            // Only the patient applies the item; the HpResource drain on
            // the healer's item is synced back to the healer by Fika's health sync (vanilla behavior
            // for using someone else's meds), so every machine ends up consistent.
            Player patient = ModUtils.GetPlayerById(packet.patientId);
            if (patient == null || !patient.IsYourPlayer) return;

            Plugin.StaticCoroutineRunner.StartCoroutine(TeamMedical.ApplyIncomingTeamHeal(patient, packet.healerId, packet.itemId));
        }

        //====================[ TeamHealResult ]====================
        private static void OnTeamHealResult(TeamHealResultPacket packet, NetPeer peer)
        {
            var local = ModUtils.GetYourPlayer();
            if (local == null || local.ProfileId != packet.healerId) return;

            TeamMedical.OnHealResult(packet.itemId, packet.success, packet.reason);
        }
    }
}
