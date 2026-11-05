//====================[ Imports ]====================
using EFT;
using UnityEngine;

namespace KeepMeAlive.Runtime.State
{
    //====================[ Enums ]====================
    public enum ReviveSource { Self = 0 }

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
        public bool IsSelfReviving { get; set; }
        public bool IsSilentInventoryAnimActive { get; set; }
        public bool IsSilentReviveBlurActive { get; set; }
        // True while RestorePlayerWeapon's async SetFirstAvailableItem callback is in-flight.
        // Causes the weapon-proceed-block patches to pass through so the equip can complete.
        public bool AllowWeaponEquipForReviveAnim { get; set; }

        //====================[ Downed Body ]====================
        // Shown in the downed pose. Set on going down and while self-stimming; cleared once the player moves or revives.
        public bool IsLimp { get; set; }
        // Time.time the player went limp, for the minimum limp time.
        public float LimpStartTime { get; set; }
        // Where the pelvis lies from the root when the body is at rest, level, in the body's own frame (x right, y
        // forward at yaw 0): the body is eased onto the real root plus this.
        public Vector2 LyingOffset { get; set; }
        public bool HasLyingOffset { get; set; }
        // Server-authoritative per-raid lives counter.
        public int LivesRemaining { get; set; }

        //====================[ Timers ]====================
        public float CriticalTimer { get; set; }
        public float InvulnerabilityTimer { get; set; }
        // True once StartInvulnerabilityPeriod has executed restore-side effects for this cycle.
        public bool HasInitializedInvulnerability { get; set; }
        public float CooldownTimer { get; set; }

        //====================[ Stored Values ]====================
        public EDamageType PlayerDamageType { get; set; } = EDamageType.Undefined;

        //====================[ UI Timers ]====================
        // The bleed-out countdown (BleedingOut only; TickDowned reads CriticalTimer from it).
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
        // Clears local self-revive input/UI state.
        public void ClearSelfReviveInput()
        {
            SelfReviveHoldTime = 0f;
            SelfReviveAwaitingAuth = false;
            IsSelfReviving = false;
        }
    }
}
