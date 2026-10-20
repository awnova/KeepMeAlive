//====================[ Imports ]====================
using Fika.Core.Networking.LiteNetLib.Utils;

namespace KeepMeAlive.Networking.Fika.Packets
{
    //====================[ Core State Packets ]====================
    public struct BleedingOutPacket : INetSerializable
    {
        public string playerId;
        public float timeRemaining;
        public int livesRemaining;
        // How the body went down (RMPlayer.DownFallKind...): the fall to play, the world pelvis and root yaw it starts
        // from, and where the pelvis lies from the root. Fika sends none of it, and each observer sees the player
        // later than the others do.
        public int fallKind;
        public float pelvisX, pelvisY, pelvisZ;
        public float yaw;
        public float offsetX, offsetZ;

        public void Deserialize(NetDataReader reader)
        {
            playerId = reader.GetString();
            timeRemaining = reader.GetFloat();
            livesRemaining = reader.GetInt();
            fallKind = reader.GetInt();
            pelvisX = reader.GetFloat();
            pelvisY = reader.GetFloat();
            pelvisZ = reader.GetFloat();
            yaw = reader.GetFloat();
            offsetX = reader.GetFloat();
            offsetZ = reader.GetFloat();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(playerId ?? string.Empty);
            writer.Put(timeRemaining);
            writer.Put(livesRemaining);
            writer.Put(fallKind);
            writer.Put(pelvisX);
            writer.Put(pelvisY);
            writer.Put(pelvisZ);
            writer.Put(yaw);
            writer.Put(offsetX);
            writer.Put(offsetZ);
        }
    }

    public struct RevivedPacket : INetSerializable
    {
        public string playerId;
        public string reviverId;

        public void Deserialize(NetDataReader reader)
        {
            playerId = reader.GetString();
            reviverId = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(playerId ?? "");
            writer.Put(reviverId ?? "");
        }
    }

    public struct PlayerStateResetPacket : INetSerializable
    {
        public string playerId;
        public bool isDead;
        public float cooldownSeconds;

        public void Deserialize(NetDataReader reader)
        {
            playerId = reader.GetString();
            isDead = reader.GetBool();
            cooldownSeconds = reader.GetFloat();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(playerId ?? "");
            writer.Put(isDead);
            writer.Put(cooldownSeconds);
        }
    }

    // Periodic state heartbeat broadcast by players in a non-None state. Sent every ~5s and on transitions for late-joiners.
    public struct PlayerStateResyncPacket : INetSerializable
    {
        public string playerId;
        public int    state;                 // RMState cast to int
        public float  criticalTimer;
        public float  invulTimer;
        public float  cooldownTimer;
        public string reviverId;
        public int    reviveRequestedSource; // ReviveSource cast to int (0=Self, 1=Team)
        public int    livesRemaining;
        public string draggerId;             // Owner's current dragger (empty when released)
        public bool   limp;                  // Whether the player is still limp
        public bool   hasLyingOffset;        // Whether the owner knows where the pelvis lies from the root
        public float  lyingOffsetX, lyingOffsetZ;
        public bool   hasDropRest;           // Where the body was put down after a drag (a late joiner missed the release)
        public float  dropX, dropZ, dropSpineYaw;
        public bool   dropOnBack;

        public void Deserialize(NetDataReader reader)
        {
            playerId              = reader.GetString();
            state                 = reader.GetInt();
            criticalTimer         = reader.GetFloat();
            invulTimer            = reader.GetFloat();
            cooldownTimer         = reader.GetFloat();
            reviverId             = reader.GetString();
            reviveRequestedSource = reader.GetInt();
            livesRemaining        = reader.GetInt();
            draggerId             = reader.GetString();
            limp                  = reader.GetBool();
            hasLyingOffset        = reader.GetBool();
            lyingOffsetX          = reader.GetFloat();
            lyingOffsetZ          = reader.GetFloat();
            hasDropRest           = reader.GetBool();
            dropX                 = reader.GetFloat();
            dropZ                 = reader.GetFloat();
            dropSpineYaw          = reader.GetFloat();
            dropOnBack            = reader.GetBool();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(playerId ?? "");
            writer.Put(state);
            writer.Put(criticalTimer);
            writer.Put(invulTimer);
            writer.Put(cooldownTimer);
            writer.Put(reviverId ?? "");
            writer.Put(reviveRequestedSource);
            writer.Put(livesRemaining);
            writer.Put(draggerId ?? "");
            writer.Put(limp);
            writer.Put(hasLyingOffset);
            writer.Put(lyingOffsetX);
            writer.Put(lyingOffsetZ);
            writer.Put(hasDropRest);
            writer.Put(dropX);
            writer.Put(dropZ);
            writer.Put(dropSpineYaw);
            writer.Put(dropOnBack);
        }
    }

    // Owner -> everyone: the owner took their body back from the limp state (moved, or a revive started), with the
    // yaw of their body (root) at that moment. Fika replicates position and look, never body yaw, and an observer's
    // root stops turning while its body is ragdolled; observers roll the body onto this yaw, and onto the root they
    // see the player at (their own replicated copy, which every observer sees with its own delay).
    public struct GetUpPacket : INetSerializable
    {
        public string playerId;
        public float  yaw;

        public void Deserialize(NetDataReader reader)
        {
            playerId = reader.GetString();
            yaw      = reader.GetFloat();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(playerId ?? "");
            writer.Put(yaw);
        }
    }

    //====================[ Revival Action Packets ]====================
    public struct SelfReviveStartPacket : INetSerializable
    {
        public string playerId;

        public void Deserialize(NetDataReader reader)
        {
            playerId = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(playerId ?? "");
        }
    }

    public struct TeamHelpPacket : INetSerializable
    {
        public string reviveeId;
        public string reviverId;

        public void Deserialize(NetDataReader reader)
        {
            reviveeId = reader.GetString();
            reviverId = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(reviveeId ?? "");
            writer.Put(reviverId ?? "");
        }
    }

    public struct TeamCancelPacket : INetSerializable
    {
        public string reviveeId;
        public string reviverId;

        public void Deserialize(NetDataReader reader)
        {
            reviveeId = reader.GetString();
            reviverId = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(reviveeId ?? "");
            writer.Put(reviverId ?? "");
        }
    }

    public struct TeamReviveStartPacket : INetSerializable
    {
        public string reviveeId;
        public string reviverId;

        public void Deserialize(NetDataReader reader)
        {
            reviveeId = reader.GetString();
            reviverId = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(reviveeId ?? "");
            writer.Put(reviverId ?? "");
        }
    }

    //====================[ Team Healing Packets ]====================
    public struct TeamHealPacket : INetSerializable
    {
        public string patientId;
        public string healerId;
        public string itemId;

        public void Deserialize(NetDataReader reader)
        {
            patientId = reader.GetString();
            healerId = reader.GetString();
            itemId = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(patientId ?? "");
            writer.Put(healerId ?? "");
            writer.Put(itemId ?? "");
        }
    }

    // Patient -> healer: whether the requested item was actually applied.
    public struct TeamHealResultPacket : INetSerializable
    {
        public string patientId;
        public string healerId;
        public string itemId;
        public bool success;
        public string reason;

        public void Deserialize(NetDataReader reader)
        {
            patientId = reader.GetString();
            healerId = reader.GetString();
            itemId = reader.GetString();
            success = reader.GetBool();
            reason = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(patientId ?? "");
            writer.Put(healerId ?? "");
            writer.Put(itemId ?? "");
            writer.Put(success);
            writer.Put(reason ?? "");
        }
    }

    //====================[ Drag Packets ]====================
    // Shares drag start and release events with all peers. A release of a lifted body names who will say where it comes
    // to rest (declarerId: whoever let go; empty when nobody will): every peer waits for that word (DropRestPacket).
    public struct DragStatePacket : INetSerializable
    {
        public string reviveeId;
        public string draggerId;
        public bool active;
        public string declarerId;

        public void Deserialize(NetDataReader reader)
        {
            reviveeId = reader.GetString();
            draggerId = reader.GetString();
            active = reader.GetBool();
            declarerId = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(reviveeId ?? "");
            writer.Put(draggerId ?? "");
            writer.Put(active);
            writer.Put(declarerId ?? "");
        }
    }

    // Declarer -> everyone, once the body it let go of has settled on the declarer's own screen: where it lies, the pelvis
    // (level), the spine's yaw (pelvis to shoulders) and whether it lies on its back or its front. Every peer's body eases onto it and the owner's real body goes
    // under it. From the same sender as the release, so it always comes after it.
    public struct DropRestPacket : INetSerializable
    {
        public string reviveeId;
        public string declarerId;
        public float x, z;
        public float spineYaw;
        public bool onBack;

        public void Deserialize(NetDataReader reader)
        {
            reviveeId = reader.GetString();
            declarerId = reader.GetString();
            x = reader.GetFloat();
            z = reader.GetFloat();
            spineYaw = reader.GetFloat();
            onBack = reader.GetBool();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(reviveeId ?? "");
            writer.Put(declarerId ?? "");
            writer.Put(x);
            writer.Put(z);
            writer.Put(spineYaw);
            writer.Put(onBack);
        }
    }

    // Dragger -> everyone, several times a second while dragging: the yaw of the dragger's body. Fika sends a player's
    // look, never their body's yaw, so every other peer's hold frame (DragSession) would face only roughly where the
    // dragger's does. Sent unreliable sequenced: only the latest matters.
    public struct DragHeadingPacket : INetSerializable
    {
        public string draggerId;
        public float yaw;

        public void Deserialize(NetDataReader reader)
        {
            draggerId = reader.GetString();
            yaw = reader.GetFloat();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(draggerId ?? "");
            writer.Put(yaw);
        }
    }
}
