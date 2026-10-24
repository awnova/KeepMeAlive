//====================[ Imports ]====================
using System;
using EFT;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using UnityEngine;

namespace KeepMeAlive.Networking.Fika
{
    //====================[ TeamHealPacketHandlers ]====================
    // Receivers for the team-heal packets: the request, the patient's answer when the use starts, and when it ends.
    // Kept separate from RevivePacketHandlers since team healing is not part of the revival state machine.
    internal static class TeamHealPacketHandlers
    {
        //====================[ Registration ]====================
        public static void Register(IFikaNetworkManager manager)
        {
            manager.RegisterPacket<TeamHealPacket, NetPeer>(OnTeamHeal);
            manager.RegisterPacket<TeamHealResultPacket, NetPeer>(OnTeamHealResult);
            manager.RegisterPacket<TeamHealUseEndedPacket, NetPeer>(OnTeamHealUseEnded);
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

            Plugin.StaticCoroutineRunner.StartCoroutine(TeamHealPatient.ApplyIncoming(patient, packet.healerId, packet.itemId));
        }

        //====================[ TeamHealResult ]====================
        private static void OnTeamHealResult(TeamHealResultPacket packet, NetPeer peer)
        {
            var local = ModUtils.GetYourPlayer();
            if (local == null || local.ProfileId != packet.healerId) return;

            TeamMedical.OnHealResult(packet.itemId, packet.success, packet.reason, packet.useSeconds);
        }

        //====================[ TeamHealUseEnded ]====================
        private static void OnTeamHealUseEnded(TeamHealUseEndedPacket packet, NetPeer peer)
        {
            var local = ModUtils.GetYourPlayer();
            if (local == null || local.ProfileId != packet.healerId) return;

            TeamMedical.OnHealEnded(packet.itemId, packet.completed);
        }
    }
}
