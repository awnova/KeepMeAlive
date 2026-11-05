//====================[ Imports ]====================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InputSystem;
using UnityEngine;

namespace KeepMeAlive.Gameplay.Downed
{
    //====================[ DownedMovementController ]====================
    internal static class DownedMovementController
    {
        //====================[ Ignored-Command Infrastructure ]====================
        private static HashSet<ECommand> _ignoredCommands;
        private static bool _reflectionFailed;

        //====================[ Stance Commands ]====================
        private static readonly ECommand[] StanceCommands =
        {
            ECommand.ToggleDuck,
            ECommand.ToggleProne,
            ECommand.NextWalkPose,
            ECommand.PreviousWalkPose,
            ECommand.Jump,
            ECommand.RestorePose
        };

        //====================[ Weapon-Select Commands ]====================
        private static readonly ECommand[] WeaponSelectCommands =
        {
            ECommand.SelectFirstPrimaryWeapon,
            ECommand.SelectSecondPrimaryWeapon,
            ECommand.SelectSecondaryWeapon,
            ECommand.SelectKnife,
            ECommand.QuickSelectSecondaryWeapon,
            ECommand.SelectFastSlot4,
            ECommand.SelectFastSlot5,
            ECommand.SelectFastSlot6,
            ECommand.SelectFastSlot7,
            ECommand.SelectFastSlot8,
            ECommand.SelectFastSlot9,
            ECommand.SelectFastSlot0,
            ECommand.QuickKnifeKick,
            // Quick-throw puts a grenade in hand via SetItemInHands, which the firearm Proceed block
            // does not cover.
            ECommand.ThrowGrenade,
            ECommand.PressThrowGrenade
        };

        //====================[ Reflection Accessor ]====================
        private static HashSet<ECommand> GetIgnoredCommands()
        {
            if (_ignoredCommands != null) return _ignoredCommands;
            if (_reflectionFailed) return null;

            try
            {
                var field = typeof(PlayerOwner).GetField("ignoredCommands",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (field == null)
                {
                    Plugin.LogSource.LogError("[DownedMovement] ignoredCommands field not found via reflection");
                    _reflectionFailed = true;
                    return null;
                }
                _ignoredCommands = field.GetValue(null) as HashSet<ECommand>;
                if (_ignoredCommands == null)
                {
                    Plugin.LogSource.LogError("[DownedMovement] ignoredCommands field was null");
                    _reflectionFailed = true;
                }
                return _ignoredCommands;
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[DownedMovement] ignoredCommands reflection error: {ex.Message}");
                _reflectionFailed = true;
                return null;
            }
        }

        private static void AddIgnored(ECommand[] commands)
        {
            var set = GetIgnoredCommands();
            if (set == null) return;
            for (int i = 0; i < commands.Length; i++) set.Add(commands[i]);
        }

        private static void RemoveIgnored(ECommand[] commands)
        {
            var set = GetIgnoredCommands();
            if (set == null) return;
            for (int i = 0; i < commands.Length; i++) set.Remove(commands[i]);
        }

        //====================[ Public API ]====================

        // Sets the player prone and blocks stance-changing inputs.
        public static void ForceProne(Player player)
        {
            try
            {
                if (player?.MovementContext == null || !player.IsYourPlayer) return;

                var mc = player.MovementContext;
                mc.EnableSprint(false);

                ApplyPronePose(player, mc);

                AddIgnored(StanceCommands);
                HookProneGuard(player, mc);

                // The setter silently refuses when CanProne is false (e.g. on a pipe or slope).
                if (!IsProne(mc)) StartProneRestore();

                RevivalDebugLog.LogDebug($"[DownedMovement] ForceProne applied for {player.ProfileId}");
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedMovement] ForceProne error: {ex.Message}"); }
        }

        // Removes stance-blocking commands; the caller restores the pose if needed.
        public static void ReleaseProne(Player player)
        {
            if (player == null || !player.IsYourPlayer) return;
            try
            {
                UnhookProneGuard();
                RemoveIgnored(StanceCommands);
                RevivalDebugLog.LogDebug($"[DownedMovement] ReleaseProne applied for {player.ProfileId}");
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedMovement] ReleaseProne error: {ex.Message}"); }
        }

        //====================[ Prone Guard ]====================
        // Event-driven: pose/state changes queue a restore. The restore only writes when the player is
        // not prone, and falls back to a throttled retry while the game refuses prone (CanProne false).
        private const float ProneRetryInterval = 0.25f;
        private static Player _guardPlayer;
        private static MovementContext _guardContext;
        private static Coroutine _restoreRoutine;

        private static bool IsProne(MovementContext mc) => mc.IsInPronePose && mc.PoseLevel <= 0.001f;

        // Same sequence as the initial force; the Prone() call is skipped when already prone because it toggles.
        private static void ApplyPronePose(Player player, MovementContext mc)
        {
            try
            {
                if (!mc.IsInPronePose) player.CurrentManagedState?.Prone();
            }
            catch { }

            SnapProne(mc);
        }

        // Straight to the bottom of prone.
        private static void SnapProne(MovementContext mc)
        {
            mc.SetPoseLevel(0f, true);
            mc.IsInPronePose = true;
        }

        private static void HookProneGuard(Player player, MovementContext mc)
        {
            if (ReferenceEquals(_guardContext, mc)) return;
            UnhookProneGuard();

            _guardPlayer = player;
            _guardContext = mc;
            mc.OnPoseChanged += OnGuardPoseChanged;
            mc.OnStateChanged += OnGuardStateChanged;
        }

        private static void UnhookProneGuard()
        {
            if (_guardContext != null)
            {
                _guardContext.OnPoseChanged -= OnGuardPoseChanged;
                _guardContext.OnStateChanged -= OnGuardStateChanged;
            }
            _guardContext = null;
            _guardPlayer = null;

            if (_restoreRoutine != null && Plugin.StaticCoroutineRunner != null)
                Plugin.StaticCoroutineRunner.StopCoroutine(_restoreRoutine);
            _restoreRoutine = null;
        }

        private static void OnGuardPoseChanged(int _)
        {
            if (_guardContext != null && !IsProne(_guardContext)) StartProneRestore();
        }

        private static void OnGuardStateChanged(EPlayerState previous, EPlayerState next)
        {
            if (_guardContext != null && !IsProne(_guardContext)) StartProneRestore();
        }

        private static void StartProneRestore()
        {
            if (_restoreRoutine != null || _guardPlayer == null || Plugin.StaticCoroutineRunner == null) return;
            _restoreRoutine = Plugin.StaticCoroutineRunner.StartCoroutine(RestoreProneRoutine());
        }

        // I defer writes until the game's pose/state transition ends.
        private static IEnumerator RestoreProneRoutine()
        {
            yield return null;

            var wait = new WaitForSeconds(ProneRetryInterval);
            while (_guardPlayer != null && _guardContext != null)
            {
                var player = _guardPlayer;
                var mc = _guardContext;

                if (player.MovementContext != mc || !RMSession.TryGetPlayerState(player.ProfileId, out var st) || !st.IsCritical)
                    break;

                if (IsProne(mc)) break;

                try { ApplyPronePose(player, mc); }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogWarning($"[DownedMovement] Prone restore failed: {ex.Message}");
                    break;
                }

                if (IsProne(mc)) break;
                yield return wait; // still refused: retry until CanProne allows it
            }

            _restoreRoutine = null;
        }

        // Empties the player's hands and blocks weapon-select inputs.
        public static void ForceEmptyHands(Player player)
        {
            try
            {
                if (player == null || !player.IsYourPlayer) return;

                player.SetEmptyHands(null);
                AddIgnored(WeaponSelectCommands);
                HookHandsGuard(player);
                // After the guard hook, which stops any watchdog when it hooks a new player.
                ArmDropWatchdog(player, "downed");

                RevivalDebugLog.LogDebug($"[DownedMovement] ForceEmptyHands applied for {player.ProfileId}");
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedMovement] ForceEmptyHands error: {ex.Message}"); }
        }

        // Removes weapon-select blocks; the caller equips a weapon if needed.
        public static void ReleaseEmptyHands(Player player)
        {
            if (player == null || !player.IsYourPlayer) return;
            try
            {
                UnhookHandsGuard();
                RemoveIgnored(WeaponSelectCommands);
                RevivalDebugLog.LogDebug($"[DownedMovement] ReleaseEmptyHands applied for {player.ProfileId}");
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedMovement] ReleaseEmptyHands error: {ex.Message}"); }
        }

        //====================[ Downed Hands ]====================
        // A hands change refused while downed ends on empty hands, the way the game answers an item it can't take
        // (Player.SetInHands falls back to SetEmptyHands). Refusing without it leaves no controller at all when the
        // caller destroyed the old one first, as other mods' restores do (DestroyController, then
        // TrySetLastEquippedWeapon).
        public static void RefuseHandsChange(Player player, Callback<IHandsController> callback)
        {
            if (player.HandsController is Player.EmptyHandsController emptyHands)
            {
                callback?.Invoke(new Result<IHandsController>(emptyHands));
                return;
            }

            player.SetEmptyHands(result => callback?.Invoke(result.Complete
                ? new Result<IHandsController>(result.Value)
                : new Result<IHandsController>(null, result.Error)));
            ArmDropWatchdog(player, "refused hands change");
        }

        //====================[ Drop Watchdog ]====================
        // A firearm dropped mid-shot only hides on the shot's fire-end event. If that never comes the hands change
        // stalls and later equips are skipped as the same item. Past the timeout I fast-forward, as the game does on death.
        private const float DropWatchdogSeconds = 1.5f;
        private static Coroutine _dropWatchdog;

        private static void ArmDropWatchdog(Player player, string reason)
        {
            if (player == null || !player.IsYourPlayer || Plugin.StaticCoroutineRunner == null) return;
            StopDropWatchdog();
            _dropWatchdog = Plugin.StaticCoroutineRunner.StartCoroutine(DropWatchdog(player, reason));
        }

        private static void StopDropWatchdog()
        {
            if (_dropWatchdog != null && Plugin.StaticCoroutineRunner != null)
                Plugin.StaticCoroutineRunner.StopCoroutine(_dropWatchdog);
            _dropWatchdog = null;
        }

        // The old controller is gone. The game's queue may hold the change a bit longer, which isn't a stall.
        private static bool HasEmptyHands(Player player) =>
            PlayerUtils.IsEmptyHanded(player) && player.HandsController != null && !player.HandsController.Destroyed;

        // Bleeding out only; the revive draws the weapon on purpose.
        private static bool WantsEmptyHands(Player player) =>
            PlayerUtils.IsAlive(player)
            && RMSession.TryGetPlayerState(player.ProfileId, out var st)
            && st.State == RMState.BleedingOut;

        private static IEnumerator DropWatchdog(Player player, string reason)
        {
            float elapsed = 0f;
            while (elapsed < DropWatchdogSeconds)
            {
                if (player == null || HasEmptyHands(player) || !WantsEmptyHands(player))
                {
                    _dropWatchdog = null;
                    yield break;
                }
                elapsed += Time.deltaTime;
                yield return null;
            }
            _dropWatchdog = null;

            if (player == null || HasEmptyHands(player) || !WantsEmptyHands(player)) yield break;

            Plugin.LogSource.LogWarning($"[DownedMovement] Hands did not empty within {DropWatchdogSeconds}s ({reason}; {PlayerUtils.DescribeHands(player)}); fast-forwarding the current hands operations");
            PlayerRestorations.FastForward(player);

            // Empty hands asked for again if the fast-forward left anything else.
            yield return null;
            if (player == null || !WantsEmptyHands(player)) yield break;
            Plugin.LogSource.LogInfo($"[DownedMovement] After fast-forward: {PlayerUtils.DescribeHands(player)}");
            if (!PlayerUtils.IsEmptyHanded(player) && PlayerUtils.IsHandsIdle(player))
            {
                try { player.SetEmptyHands(null); }
                catch (Exception ex) { Plugin.LogSource.LogError($"[DownedMovement] Empty-hands retry error: {ex.Message}"); }
            }
        }

        // Backstop for swaps that bypass every hands call I patch: other mods destroy the controller and spawn their
        // own in the same call, so hands still null a frame later means nothing is coming. Event-driven: runs only
        // when the hands change, and only while downed.
        private static Player _handsGuardPlayer;
        private static Coroutine _handsRestoreRoutine;

        private static void HookHandsGuard(Player player)
        {
            if (ReferenceEquals(_handsGuardPlayer, player)) return;
            UnhookHandsGuard();

            _handsGuardPlayer = player;
            player.OnHandsControllerChanged += OnGuardHandsChanged;
        }

        private static void UnhookHandsGuard()
        {
            if (_handsGuardPlayer != null) _handsGuardPlayer.OnHandsControllerChanged -= OnGuardHandsChanged;
            _handsGuardPlayer = null;

            if (_handsRestoreRoutine != null && Plugin.StaticCoroutineRunner != null)
                Plugin.StaticCoroutineRunner.StopCoroutine(_handsRestoreRoutine);
            _handsRestoreRoutine = null;
            StopDropWatchdog();
        }

        private static void OnGuardHandsChanged(Player.AbstractHandsController previous, Player.AbstractHandsController next)
        {
            if (next != null || _handsRestoreRoutine != null || _handsGuardPlayer == null || Plugin.StaticCoroutineRunner == null) return;
            _handsRestoreRoutine = Plugin.StaticCoroutineRunner.StartCoroutine(RestoreEmptyHandsRoutine(_handsGuardPlayer));
        }

        private static IEnumerator RestoreEmptyHandsRoutine(Player player)
        {
            yield return null;
            _handsRestoreRoutine = null;

            if (player == null || player.HandsController != null || player.ProcessStatus != Player.EProcessStatus.None) yield break;
            if (!PlayerUtils.IsAlive(player) || !RMSession.IsPlayerCritical(player.ProfileId)) yield break;

            Plugin.LogSource.LogWarning($"[DownedMovement] Hands left empty of any controller while downed; restoring empty hands for {player.ProfileId}");
            try { player.SetEmptyHands(null); }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedMovement] Empty-hands restore error: {ex.Message}"); }
        }

        //====================[ Downed State Application ]====================
        // Applies the downed state by ending active actions, forcing prone and empty hands, and releasing doors and stationary weapons.
        public static void ApplyRevivableState(Player player)
        {
            try
            {
                if (player == null || !player.IsYourPlayer) return;

                CancelLocalManagedActions(player);
                ExitWeaponPostures(player);

                ForceProne(player);
                ForceEmptyHands(player);
                // Going down is voiced by DownedVoiceController.OnLocalDowned.

                var mc = player.MovementContext;
                if (mc == null) return;

                mc.ReleaseDoorIfInteractingWithOne();

                if (mc.StationaryWeapon != null)
                {
                    mc.StationaryWeapon.Unlock(player.ProfileId);
                    if (player.HandsController != null && mc.StationaryWeapon.Item == player.HandsController.Item)
                    {
                        mc.StationaryWeapon.Show();
                        player.ReleaseHand();
                    }
                }

                if (player.IsYourPlayer && Plugin.StaticCoroutineRunner != null)
                {
                    Plugin.StaticCoroutineRunner.StartCoroutine(ReassertProneNextFrame(player));
                }
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedMovement] ApplyRevivableState error: {ex.Message}"); }
        }

        // Puts back what the downed entry took off the local player. The state-change footstep handler is not
        // among it: nothing here detaches it, and Fika replaces the vanilla one on purpose (SetupFikaAudio), so
        // re-adding it would double every step, stop and idle sound.
        public static void ReattachMovementHooks(Player player)
        {
            if (player?.MovementContext == null) return;
            try
            {
                // ReleaseHand (stationary weapon, ApplyRevivableState) unsubscribes the IK store for good, and the arm
                // IK reads what it stores every frame.
                PlayerHooks.AttachWeaponAnimation(player);
            }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[DownedMovement] Re-hook movement events error: {ex.Message}"); }
        }

        // Ensure leaked local input locks from previous raids are cleared on raid start.
        public static void ScrubDownedInputLocks()
        {
            try
            {
                UnhookProneGuard();
                UnhookHandsGuard();
                RemoveIgnored(StanceCommands);
                RemoveIgnored(WeaponSelectCommands);
                // The previous raid's MovementContext is gone; forget the limit it held.
                _appliedSpeedLimit = -1f;
            }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[DownedMovement] ScrubDownedInputLocks error: {ex.Message}"); }
        }

        private static void CancelLocalManagedActions(Player player)
        {
            if (player == null || !player.IsYourPlayer) return;

            try
            {
                bool hasOverride = !ReferenceEquals(player.CurrentManagedState, player.CurrentState);
                player.CurrentManagedState?.Cancel();
                if (hasOverride)
                {
                    player.MovementContext?.ExitOverridenState();
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedMovement] Cancel movement override failed: {ex.Message}");
            }

            try { player.HealthController?.CancelApplyingItem(); }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[DownedMovement] CancelApplyingItem failed: {ex.Message}"); }

            try { player.ActiveHealthController?.RemoveMedEffect(); }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[DownedMovement] RemoveMedEffect failed: {ex.Message}"); }

            try
            {
                if (player.HandsController is Player.MedsController medsController)
                {
                    medsController.Remove();
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedMovement] MedsController.Remove failed: {ex.Message}");
            }
        }

        // The game ends both when it takes the hands for the inventory.
        private static void ExitWeaponPostures(Player player)
        {
            var mc = player.MovementContext;
            if (mc == null) return;

            try { if (mc.IsInMountedState) mc.ExitMountedState(); }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[DownedMovement] ExitMountedState failed: {ex.Message}"); }

            try { mc.SetBlindFire(0); }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[DownedMovement] SetBlindFire(0) failed: {ex.Message}"); }
        }

        private static IEnumerator ReassertProneNextFrame(Player player)
        {
            yield return null;

            if (player?.MovementContext == null || !player.IsYourPlayer) yield break;

            var st = RMSession.GetPlayerState(player.ProfileId);
            if (!st.IsCritical) yield break;

            try
            {
                var mc = player.MovementContext;
                if (!IsProne(mc)) SnapProne(mc);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[DownedMovement] ReassertProneNextFrame failed: {ex.Message}");
            }
        }

        //====================[ Speed Limit ]====================
        // Held in the Weight slot: the game rebuilds that one itself (MovementContext.WeightRelatedValuesUpdated
        // removes it and restores the carried-weight limit), so I clear my limit in one call. I re-apply it below
        // if a weight change overwrites it. Other ESpeedLimit causes are set by their triggers and must be preserved.
        private const Player.ESpeedLimit SpeedLimitCause = Player.ESpeedLimit.Weight;
        private const float SpeedLimitReassertInterval = 0.25f;
        private static float _appliedSpeedLimit = -1f; // -1 = no limit applied
        private static float _nextSpeedLimitReassert;

        // Apply downed and post-revive speed limits each tick.
        public static void TickSpeedLimit(Player player, RMPlayer st)
        {
            if (player?.MovementContext == null || !player.IsYourPlayer) return;

            float target = st != null ? GetTargetSpeedLimit(st) : -1f;
            if (target < 0f)
            {
                ClearSpeedLimit(player);
                return;
            }

            if (Mathf.Approximately(target, _appliedSpeedLimit) && Time.time < _nextSpeedLimitReassert) return;

            try
            {
                var mc = player.MovementContext;
                mc.RemoveStateSpeedLimit(SpeedLimitCause);
                // Never looser than the real weight limit this slot replaces.
                float limit = target * mc.MaxSpeed;
                float weightLimit = player.Physical.WalkSpeedLimit;
                if (weightLimit < 1f) limit = Mathf.Min(limit, weightLimit * mc.MaxSpeed);
                mc.AddStateSpeedLimit(limit, SpeedLimitCause);
                _appliedSpeedLimit = target;
                _nextSpeedLimitReassert = Time.time + SpeedLimitReassertInterval;
            }
            catch (Exception ex) { Plugin.LogSource.LogError($"[DownedMovement] TickSpeedLimit error: {ex.Message}"); }
        }

        public static void ClearSpeedLimit(Player player)
        {
            if (player == null || !player.IsYourPlayer || _appliedSpeedLimit < 0f) return;
            _appliedSpeedLimit = -1f;

            try
            {
                var mc = player.MovementContext;
                if (mc == null) return;
                mc.RemoveStateSpeedLimit(SpeedLimitCause);
                mc.WeightRelatedValuesUpdated(); // puts the real weight limit back
            }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[DownedMovement] ClearSpeedLimit error: {ex.Message}"); }
        }

        // Fraction of max speed, or -1 for no limit.
        private static float GetTargetSpeedLimit(RMPlayer st)
        {
            switch (st.State)
            {
                case RMState.BleedingOut:
                    bool frozen = st.IsLimp || st.IsBeingRevived || st.IsSelfReviving || st.SelfReviveAwaitingAuth;
                    return frozen ? 0f : Mathf.Clamp01(SyncedServerConfigStore.Config.Gameplay.Downed.DownedMovementSpeedPercent / 100f);
                case RMState.Reviving:
                    return 0f;
                case RMState.Revived:
                    float mult = PostReviveEffects.GetInvulnSpeedMultiplier((ReviveSource)st.ReviveRequestedSource);
                    return mult < 1f ? Mathf.Clamp01(mult) : -1f;
                default:
                    return -1f;
            }
        }
    }
}
