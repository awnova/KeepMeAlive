//====================[ Imports ]====================
using EFT;
using UnityEngine;

namespace KeepMeAlive.Runtime.State
{
    //====================[ Enums ]====================
    public enum ReviveSource { Self = 0, Team = 1, Defib = 2 }

    public enum RMState
    {
        None,
        CoolDown,
        BleedingOut,
        Reviving,
        Revived
    }

    //====================[ RMPlayer ]====================
    public class RMPlayer
    {
        //====================[ State ]====================
        public RMState State { get; set; } = RMState.None;
        public RMState LastObservedState { get; set; } = RMState.None;

        //====================[ Derived Flags ]====================
        public bool IsCritical => State is RMState.BleedingOut or RMState.Reviving;
        public bool IsInvulnerable => State == RMState.Revived;

        //====================[ Runtime Flags ]====================
        public bool KillOverride { get; set; }
        // Tracks whether the local revive progress timer is running.
        public bool IsReviveProgressActive => ReviveProgressCoroutine != null;
        public bool IsBeingRevived { get; set; }
        public bool IsSelfReviving { get; set; }
        public bool IsSilentInventoryAnimActive { get; set; }
        public bool IsSilentReviveBlurActive { get; set; }
        // True while RestorePlayerWeapon's async SetFirstAvailableItem callback is in-flight.
        // Causes the weapon-proceed-block patches to pass through so the equip can complete.
        public bool AllowWeaponEquipForReviveAnim { get; set; }

        // ReviveSource as int: 0 = Self, 1 = Team, 2 = Defib
        //====================[ Session Info ]====================
        public int ReviveRequestedSource { get; set; }
        public string CurrentReviverId { get; set; } = string.Empty;
        // Profile id of the teammate currently dragging this (downed) player; empty when nobody is.
        public string CurrentDraggerId { get; set; } = string.Empty;
        public bool IsBeingDragged => !string.IsNullOrEmpty(CurrentDraggerId);
        // Time.time of the last local change to CurrentDraggerId; a resync from the owner older than the change
        // must not undo it while the change is still in flight.
        public float DragClaimTime { get; set; } = -100f;
        // Time.time of the last team-revive help or cancel applied on this machine; the same guard for the revive
        // claim (CurrentReviverId / IsBeingRevived).
        public float ReviveClaimTime { get; set; } = -100f;
        // Shown as a ragdoll. Set on going down (every peer) and while dragged; the owner clears it once they
        // move and its resync clears it everywhere else.
        public bool IsLimp { get; set; }
        // Time.time the owner went limp (owner only), for the minimum limp time.
        public float LimpStartTime { get; set; }
        // How the body went down, as the owner recorded it (the owner sets it going down, every other peer from
        // BleedingOutPacket), so every peer plays the same fall from the same place. DownFallKind: 0 none (went down
        // already lying), 1 the standing fall, 2 the fall from a crouch. DownFallTime: Time.time it was set (observers:
        // when the packet came).
        public int DownFallKind { get; set; }
        public Vector3 DownPelvis { get; set; }
        public float DownYaw { get; set; }
        public float DownFallTime { get; set; } = -100f;
        // Where the pelvis lies from the root when the body is at rest, level, in the body's own frame (x right, y
        // forward at yaw 0): the body is eased onto the real root plus this. Known from going down (set with the above),
        // or from the owner's resync.
        public Vector2 LyingOffset { get; set; }
        public bool HasLyingOffset { get; set; }
        // After a drag: who let go of the body and will say where it came to rest (empty: nobody will), and when it was let go.
        // Until that word comes, every peer's body lies where its own ragdoll settled.
        public string DropDeclarerId { get; set; } = string.Empty;
        public float DropReleaseTime { get; set; } = -100f;
        // Where it came to rest, as the declarer's own ragdoll settled (DropRestPacket): the pelvis (level) and the pose's
        // facing yaw. Every peer, the owner included, eases its body onto it, and the owner moves the real root under it.
        // Both dropped on a new claim or a new downing.
        public bool HasDropRest { get; set; }
        public Vector3 DropRestPelvis { get; set; }
        public float DropFacingYaw { get; set; }
        // The spine's yaw it lies with (pelvis to shoulders), and whether it lies on its back (the breathing pose) or its
        // front (EFT's prone): every peer's body lies the declarer's way up, whatever its own physics settled it as.
        public float DropSpineYaw { get; set; }
        public bool DropOnBack { get; set; } = true;
        // Observers: the body yaw the owner said they had when they took the body back (GetUpPacket), and the
        // Time.time it came.
        public float GetUpYaw { get; set; }
        public float GetUpTime { get; set; } = -100f;
        // Server-authoritative per-raid lives counter, shared across self- and team-revive.
        public int LivesRemaining { get; set; }

        //====================[ Timers ]====================
        public float CriticalTimer { get; set; }
        public float InvulnerabilityTimer { get; set; }
        // True once StartInvulnerabilityPeriod has executed restore-side effects for this cycle.
        public bool HasInitializedInvulnerability { get; set; }
        public float CooldownTimer { get; set; }
        // Countdown until the next periodic state resync broadcast. -1 sends immediately.
        public float ResyncCooldown { get; set; }
        // Owner only: after the next resync, send one more once a changed claim has settled on every peer.
        public bool ResyncFollowUp { get; set; }
        // Watchdog: counts down from a set value when IsBeingRevived is raised from a TeamHelp packet.
        // Countdown for the active team-revive request while State == BleedingOut.
        public float BeingRevivedWatchdogTimer { get; set; }

        //====================[ Stored Values ]====================
        public EDamageType PlayerDamageType { get; set; } = EDamageType.Undefined;

        // The bleed-out countdown (BleedingOut only; TickDowned reads CriticalTimer from it).
        //====================[ UI Timers ]====================
        public CustomTimer CriticalStateMainTimer { get; set; }
        // The "Reviving" status bar for the revive progress phase.
        public CustomTimer ReviveStatusTimer { get; set; }
        public CustomTimer RevivePromptTimer { get; set; }

        //====================[ Coroutine Handles ]====================
        // Stored so ExitDowned / EnterDowned can cancel the revive-progress coroutine.
        public Coroutine ReviveProgressCoroutine { get; set; }
        // Stored so ExitDowned / PrepareForDeath can cancel the heartbeat cue coroutine
        // as soon as the downed state ends (revived or died).
        public Coroutine HeartbeatCoroutine { get; set; }
        // Owned by RevivePresentation so a stop always kills pending inventory-open steps.
        public Coroutine ReviveEffectsCoroutine { get; set; }
        public Coroutine UnconsciousOpenCoroutine { get; set; }

        //====================[ Input Tracking ]====================
        // Self-revive flow state (hold -> auth -> reviving).
        public float SelfReviveHoldTime { get; set; }
        // Hold threshold reached; server authorization is pending.
        public bool SelfReviveAwaitingAuth { get; set; }
        public int SelfReviveAttemptId { get; set; }
        // -1 when not holding the give-up key; otherwise seconds held.
        public float GiveUpHoldTime { get; set; } = -1f;

        //====================[ Revive Flow Markers ]====================
        // Incremented whenever a new downed cycle begins.
        public int ReviveCycleId { get; set; }
        // Set to ReviveCycleId once restore/finalize side effects are committed.
        public int FinalizedReviveCycleId { get; set; } = -1;
        public bool IsReviveFinalizeCommittedForCurrentCycle => FinalizedReviveCycleId == ReviveCycleId;

        //====================[ Shared Reset Helpers ]====================
        // Clears local self-revive input/UI state. Used whenever a packet hands control of the
        // revive to something else (team help, a fresh reviving transition, a full state reset).
        public void ClearSelfReviveInput()
        {
            SelfReviveHoldTime = 0f;
            SelfReviveAwaitingAuth = false;
            IsSelfReviving = false;
        }

        // Clears the active revive-session bookkeeping (who is reviving whom, from where).
        // Used on a fresh BleedingOut transition and on a full PlayerStateReset.
        public void ClearReviveSession()
        {
            IsBeingRevived = false;
            IsSelfReviving = false;
            CurrentReviverId = string.Empty;
            ReviveRequestedSource = 0;
            BeingRevivedWatchdogTimer = 0f;
        }
    }
}
