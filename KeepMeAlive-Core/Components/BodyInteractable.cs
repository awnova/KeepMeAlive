//====================[ Imports ]====================
using System;
using System.Collections;
using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using UnityEngine;

namespace KeepMeAlive.Components
{
    //====================[ BodyInteractable ]====================
    public class BodyInteractable : InteractableObject
    {
        private static SyncedGameplayConfig Cfg => SyncedServerConfigStore.Config.Gameplay;

        //====================[ Nested Types ]====================
        // Marker on each bone-parented collider child.
        // BodyProxyFindInteractablePatch routes raycast hits on it to the owner.
        public sealed class BodyInteractableProxy : MonoBehaviour
        {
            public BodyInteractable Owner;
        }

        //====================[ Fields ]====================
        public Player Revivee { get; set; }

        private readonly List<Collider> _colliders = new List<Collider>();
        // Proxy GameObjects live under the revivee's bone transforms, not under this object,
        // so they must be destroyed explicitly.
        private readonly List<GameObject> _proxyObjects = new List<GameObject>();
        private bool _collidersEnabled;
        private bool _isLootScreenOpen;
        private GamePlayerOwner _lootOwner;

        // Track action changes to avoid resetting the selection.
        private GamePlayerOwner _viewerOwner;
        private int _shownSignature;
        private float _nextSignaturePoll;
        private float _nextInteractablePoll;
        private bool _needsTeamHeal;
        private const float SignaturePollInterval = 0.25f;


        //====================[ Bone Collider Filter ]====================
        // Covered by HeadCommon, so not mirrored.
        private static readonly HashSet<EBodyPartColliderType> HeadSubParts = new HashSet<EBodyPartColliderType>
        {
            EBodyPartColliderType.ParietalHead,
            EBodyPartColliderType.BackHead,
            EBodyPartColliderType.Ears,
            EBodyPartColliderType.Eyes,
            EBodyPartColliderType.Jaw,
        };

        // Fixed world-space padding keeps thin hitboxes easy to select.
        private const float MirroredColliderPadding = 0.05f;

        //====================[ Unity Lifecycle ]====================
        private void Awake()
        {
            gameObject.layer = LayerMask.NameToLayer("Interactive");
        }

        private void Update()
        {
            if (Revivee == null || _colliders.Count == 0) return;

            // Bots never expose revival/team-heal interactables, and a dead teammate's corpse
            // (same GameObject) must fall back to vanilla looting.
            if (!PlayerUtils.IsAliveHuman(Revivee))
            {
                Destroy(gameObject);
                return;
            }

            // Disable bone colliders while the loot screen is open.
            if (_isLootScreenOpen)
            {
                ClearLookHint();
                SetCollidersEnabled(false);
                return;
            }

            RMSession.TryGetPlayerState(Revivee.ProfileId, out var st);
            bool downedInteractable = st != null && st.State == RMState.BleedingOut && !st.IsBeingDragged;
            if (!downedInteractable && Time.time >= _nextInteractablePoll)
            {
                _nextInteractablePoll = Time.time + SignaturePollInterval;
                _needsTeamHeal = Cfg.TeamHealing.Enabled && (st == null || !st.IsCritical) && PatientNeeds.NeedsAny(Revivee);
            }

            bool hasInteractions = downedInteractable || ((st == null || !st.IsCritical) && _needsTeamHeal);
            SetCollidersEnabled(hasInteractions);
            if (!hasInteractions)
            {
                ClearLookHint();
                return;
            }

            UpdateLookHint();

            // While the team-heal wheel is open on this body its menu stays empty (BuildActions).
            if (!TeamHealWheel.IsOpenFor(Revivee.ProfileId)) RefreshListIfActionsChanged();
        }

        // The bottom-strip banner while the local player looks at this body: what a tap and a hold of interact do, and the
        // inventory key's loot.
        private bool _hintShown;
        private float _nextHintRefresh;

        private void UpdateLookHint()
        {
            var viewer = _viewerOwner != null ? _viewerOwner.Player : null;
            bool looking = viewer != null && !_isLootScreenOpen && !TeamHealWheel.IsOpen && ReferenceEquals(viewer.InteractableObject, this);
            if (!looking)
            {
                ClearLookHint();
                return;
            }

            if (Time.time < _nextHintRefresh) return;
            _nextHintRefresh = Time.time + SignaturePollInterval;

            RMSession.TryGetPlayerState(Revivee.ProfileId, out var st);
            bool support = st != null && !RMSession.IsPlayerCritical(viewer.ProfileId)
                           && st.State == RMState.BleedingOut && !st.IsBeingDragged && !st.IsBeingRevived && !st.IsSelfReviving
                           && !CrouchHoldState.IsActive(viewer);
            bool loot = CanLoot(viewer);
            if (!support && !loot)
            {
                ClearLookHint();
                return;
            }

            int cost = 0;
            var revive = support ? ReviveHint(viewer, Revivee, st, out cost) : Hud.StripState.Hidden;
            Hud.SetLookHint(support && DownedDragController.CanStartDrag(viewer, Revivee, st), revive, cost, loot);
            _hintShown = true;
        }

        private void ClearLookHint()
        {
            if (!_hintShown) return;
            _hintShown = false;
            Hud.ClearLookHint();
        }

        // Refresh the list when its actions change.
        private void RefreshListIfActionsChanged()
        {
            if (_viewerOwner == null || Time.time < _nextSignaturePoll) return;
            _nextSignaturePoll = Time.time + SignaturePollInterval;

            var viewer = _viewerOwner.Player;
            if (viewer == null || !ReferenceEquals(viewer.InteractableObject, this)) return;

            if (Signature(BuildActions(_viewerOwner)) != _shownSignature) SetStateUpdateTime();
        }

        internal static int Signature(AvailableInteractionState state)
        {
            unchecked
            {
                int hash = 17;
                foreach (var action in state.Actions)
                {
                    hash = hash * 31 + (action.Name?.GetHashCode() ?? 0);
                    hash = hash * 31 + (action.Disabled ? 1 : 0);
                }
                return hash * 31 + state.Actions.Count;
            }
        }

        //====================[ Setup / Attachment ]====================
        public static BodyInteractable AttachToPlayer(Player player)
        {
            if (player == null) return null; 
            
            // Prevent multiple attachments if PlayerId is set multiple times
            if (player.gameObject.GetComponentInChildren<BodyInteractable>() != null) return null;

            // I wait for bones and Fika attachment in a coroutine.
            Plugin.StaticCoroutineRunner.StartCoroutine(WaitForBonesAndBuild(player));
            return null; // I cache the result after attachment.
        }

        private static IEnumerator WaitForBonesAndBuild(Player player)
        {
            // Wait for bones to be ready and Profile to be fully assigned; stop if the player
            // object is destroyed (left the raid) while I wait.
            while (player != null && (player.PlayerBones == null || player.PlayerBones.RootJoint == null || player.Profile == null || string.IsNullOrEmpty(player.ProfileId)))
            {
                yield return null;
            }
            if (player == null) yield break;
            
            // Wait slightly after bones are ready (allow Fika UI to spawn)
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            if (player == null) yield break;

            // Confirm the player type and owner before attaching the interactable.
            if (player.IsYourPlayer || !PlayerUtils.IsHuman(player)) yield break;

            // Another attach may have finished during the wait.
            if (player.gameObject.GetComponentInChildren<BodyInteractable>() != null) yield break;

            // Root GO holds the BodyInteractable component; proxy hits on the bone colliders resolve to it.
            var go = new GameObject("Body Interactable");
            go.transform.SetParent(player.gameObject.transform, false);
            go.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            go.transform.localRotation = Quaternion.identity;
            go.layer = LayerMask.NameToLayer("Interactive");

            var bi = go.AddComponent<BodyInteractable>();
            bi.Revivee = player;

            int interactiveLayer = LayerMask.NameToLayer("Interactive");

            var bodyParts = player.PlayerBones.BodyPartColliders;
            bool hasHeadCommon = false;
            foreach (BodyPartCollider bpc in bodyParts)
            {
                if (bpc != null && bpc.Collider != null && bpc.BodyPartColliderType == EBodyPartColliderType.HeadCommon)
                {
                    hasHeadCommon = true;
                    break;
                }
            }

            // Clone body hitboxes onto their bone transforms (excluding armor plates).
            var mirrored = new HashSet<Collider>();
            foreach (BodyPartCollider bpc in bodyParts)
            {
                if (bpc == null || bpc.Collider == null) continue;
                if (bpc is ArmorPlateCollider) continue;
                if (hasHeadCommon && HeadSubParts.Contains(bpc.BodyPartColliderType)) continue;
                if (!mirrored.Add(bpc.Collider)) continue;

                var childGo = new GameObject($"BI_{bpc.BodyPartColliderType}");
                childGo.transform.SetParent(bpc.Collider.transform, false);
                childGo.transform.localPosition = Vector3.zero;
                childGo.transform.localRotation = Quaternion.identity;
                childGo.layer = interactiveLayer;

                Collider cloned = CloneColliderShape(bpc.Collider, childGo);
                if (cloned == null) { UnityEngine.Object.Destroy(childGo); continue; }
                cloned.isTrigger = false;
                cloned.enabled = false;

                var proxy = childGo.AddComponent<BodyInteractableProxy>();
                proxy.Owner = bi;

                bi._colliders.Add(cloned);
                bi._proxyObjects.Add(childGo);
            }

            Gameplay.Downed.BodyInteractableRuntime.Register(player.ProfileId, bi);
            go.SetActive(true);

            RevivalDebugLog.LogDebug($"[BodyInteractable] Created {bi._colliders.Count} mirrored colliders for player {player.ProfileId}");
        }

        //====================[ Collider Helpers ]====================
        private static Collider CloneColliderShape(Collider source, GameObject target)
        {
            // Padding is in world metres; convert to the bone's local space.
            Vector3 scale = source.transform.lossyScale;
            float sx = Mathf.Max(Mathf.Abs(scale.x), 0.0001f);
            float sy = Mathf.Max(Mathf.Abs(scale.y), 0.0001f);
            float sz = Mathf.Max(Mathf.Abs(scale.z), 0.0001f);
            float maxScale = Mathf.Max(sx, Mathf.Max(sy, sz));
            float pad = MirroredColliderPadding;

            if (source is BoxCollider box)
            {
                var clone = target.AddComponent<BoxCollider>();
                clone.center = box.center;
                clone.size = box.size + new Vector3(2f * pad / sx, 2f * pad / sy, 2f * pad / sz);
                return clone;
            }
            if (source is SphereCollider sphere)
            {
                var clone = target.AddComponent<SphereCollider>();
                clone.center = sphere.center;
                clone.radius = sphere.radius + pad / maxScale;
                return clone;
            }
            if (source is CapsuleCollider capsule)
            {
                var clone = target.AddComponent<CapsuleCollider>();
                clone.center = capsule.center;
                // Capsule radius uses the larger cross-axis scale.
                float axisScale = capsule.direction == 0 ? sx : capsule.direction == 1 ? sy : sz;
                float radialScale = capsule.direction == 0 ? Mathf.Max(sy, sz)
                                  : capsule.direction == 1 ? Mathf.Max(sx, sz)
                                  : Mathf.Max(sx, sy);
                clone.radius = capsule.radius + pad / radialScale;
                clone.height = capsule.height + 2f * pad / axisScale;
                clone.direction = capsule.direction;
                return clone;
            }
            return null;
        }

        // Use triggers while the body is ragdolled to avoid proxy collisions with its bones.
        public void SetRagdollInert(bool inert)
        {
            for (int i = _colliders.Count - 1; i >= 0; i--)
            {
                if (_colliders[i] == null) { _colliders.RemoveAt(i); continue; }
                _colliders[i].isTrigger = inert;
            }
        }

        // Skips the collider pass when the state is unchanged.
        private void SetCollidersEnabled(bool enabled)
        {
            if (_collidersEnabled == enabled) return;
            _collidersEnabled = enabled;

            for (int i = _colliders.Count - 1; i >= 0; i--)
            {
                if (_colliders[i] == null) { _colliders.RemoveAt(i); continue; }
                _colliders[i].enabled = enabled;
            }
        }

        //====================[ Logic & Interaction ]====================

        // The interact key is pressed when the action runs and released to cancel the hold, so Support starts a short
        // hold: let go inside it and it was a tap; still held when it ends and it becomes the revive hold.
        private const float SupportTapWindow = 0.3f;

        // Dragging the body is a Team revive (stim jab); anything else is a Defib revive.
        internal static ReviveSource SourceFor(Player reviver, RMPlayer reviveeState) =>
            RevivePacketHandlers.TeamSourceFor(reviveeState, reviver.ProfileId);

        internal static bool CanTeamRevive(Player reviver, Player revivee, RMPlayer reviveeState) =>
            ReviveHint(reviver, revivee, reviveeState, out _) == Hud.StripState.Available;

        // What the look hint shows for the revive: Hidden when it isn't offered here at all, otherwise Available or why it
        // can't be done (NeedsStim, NeedsDefib, NoLives). cost is in the downed teammate's lives.
        internal static Hud.StripState ReviveHint(Player reviver, Player revivee, RMPlayer reviveeState, out int cost)
        {
            cost = 0;
            if (reviver == null || revivee == null || reviveeState == null) return Hud.StripState.Hidden;
            if (reviveeState.State != RMState.BleedingOut) return Hud.StripState.Hidden;
            if (reviveeState.IsBeingRevived || reviveeState.IsSelfReviving || reviveeState.SelfReviveAwaitingAuth) return Hud.StripState.Hidden;
            if (BodyRagdoll.IsFalling(revivee.ProfileId)) return Hud.StripState.Hidden;
            if (RMSession.IsPlayerCritical(reviver.ProfileId)) return Hud.StripState.Hidden;
            if (reviver.MovementContext != null && reviver.MovementContext.IsInPronePose) return Hud.StripState.Hidden;
            if (!DownedDragController.IsInRange(reviver, revivee)) return Hud.StripState.Hidden;

            var source = SourceFor(reviver, reviveeState);
            if (!RevivePolicy.IsEnabled(source)) return Hud.StripState.Hidden;
            cost = RevivePolicy.LivesCost(source);
            switch (RevivePolicy.MissingItem(source, reviver))
            {
                case RevivePolicy.RequiredItem.Stim: return Hud.StripState.NeedsStim;
                case RevivePolicy.RequiredItem.Defib: return Hud.StripState.NeedsDefib;
            }
            return reviveeState.LivesRemaining < cost ? Hud.StripState.NoLives : Hud.StripState.Available;
        }

        public void OnSupport(GamePlayerOwner owner)
        {
            if (Revivee is null || owner?.Player is null) return;
            if (CrouchHoldState.IsActive(owner.Player)) return;

            // Standing still is needed to hold; otherwise the press is a tap.
            if (!CrouchHoldState.Begin(owner.Player, SupportTapWindow, held => OnSupportWindowEnd(owner, held)))
                DownedDragController.StartDrag(owner, Revivee);
        }

        private void OnSupportWindowEnd(GamePlayerOwner owner, bool held)
        {
            if (Revivee == null || owner?.Player == null || RMSession.IsPlayerCritical(owner.Player.ProfileId)) return;
            if (!held) { DownedDragController.StartDrag(owner, Revivee); return; }
            RequestRevive(owner);
        }

        // Starts the revive hold. A missing item or too few lives is already on the look hint or the dragger's strip.
        public void RequestRevive(GamePlayerOwner owner)
        {
            if (Revivee == null || owner?.Player == null || RMSession.IsPlayerCritical(owner.Player.ProfileId)) return;

            var st = RMSession.GetPlayerState(Revivee.ProfileId);
            if (st.State != RMState.BleedingOut) return;
            if (st.IsBeingRevived || st.IsSelfReviving || st.SelfReviveAwaitingAuth) return;
            if (!DownedDragController.IsLocalHoldingBody)
            {
                if (BodyRagdoll.IsFalling(Revivee.ProfileId)) return;
                if (owner.Player.MovementContext != null && owner.Player.MovementContext.IsInPronePose) return;
                if (!DownedDragController.IsInRange(owner.Player, Revivee)) return;
            }
            var source = SourceFor(owner.Player, st);
            if (!RevivePolicy.IsEnabled(source))
                VFX_UI.Text(Color.red, PlayerFacingMessages.Revive.Disabled);
            else if (RevivePolicy.HasRequiredItem(source, owner.Player) && st.LivesRemaining >= RevivePolicy.LivesCost(source))
                OnRevive(owner, source);
        }

        public void OnRevive(GamePlayerOwner owner, ReviveSource source)
        {
            if (Revivee is null || owner?.Player is null) return;

            // A hold is already running; a second press must not start another.
            if (CrouchHoldState.IsActive(owner.Player)) return;

            var handler = new ReviveCompleteHandler
            {
                owner = owner,
                targetId = Revivee.ProfileId,
                reviverId = owner.Player.ProfileId,
                source = source
            };

            float holdTime = RevivePolicy.GetHoldDuration(source);
            // A body in the reviver's hands is already in front of them: turning to face it would swing it out of their grip.
            Player facing = DownedDragController.IsLocalHoldingBody ? null : Revivee;
            if (!CrouchHoldState.Begin(owner.Player, holdTime, handler.Complete, facing))
            {
                VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.CannotReviveWhileMoving);
                return;
            }

            Hud.ShowHold(Hud.HoldKind.ReviveTeammate, holdTime, PlayerUtils.GetPlayerDisplayName(Revivee.ProfileId));
            // Claims locally too (no loopback), so this machine also drops any drag of the body.
            FikaBridge.ClaimTeamHelp(Revivee.ProfileId, owner.Player.ProfileId);
            // The defibrillator comes out for the hold (the weapon comes back when it ends), only with the claim:
            // observers start it from the claim, so a refused one would show it on this screen alone. A body the reviver
            // is dragging stays in their hands (the defibrillator would break the drag stance): the right hand jabs a stim
            // into its shoulder instead (DraggerArms), and the body is dropped when the revive is done.
            var reviveeState = RMSession.GetPlayerState(Revivee.ProfileId);
            if (reviveeState.CurrentReviverId == owner.Player.ProfileId)
            {
                if (source == ReviveSource.Team) DraggerArms.BeginStim(owner.Player, holdTime);
                else DefibHands.Begin(owner.Player, holdTime);
            }
        }

        // Called on each list build; records what the list shows.
        public AvailableInteractionState GetActions(GamePlayerOwner owner)
        {
            var actions = BuildActions(owner);
            _viewerOwner = owner;
            _shownSignature = Signature(actions);
            _nextSignaturePoll = Time.time + SignaturePollInterval;
            return actions;
        }

        private AvailableInteractionState BuildActions(GamePlayerOwner owner)
        {
            var actions = new AvailableInteractionState();

            if (Revivee == null || owner?.Player == null) return actions;
            // Empty result -> TryRouteActions returns false -> vanilla corpse interactions.
            if (!PlayerUtils.IsAliveHuman(Revivee)) return actions;
            if (RMSession.IsPlayerCritical(owner.Player.ProfileId)) return actions;

            RMSession.TryGetPlayerState(Revivee.ProfileId, out var reviveeState);

            // I expose no actions during revive progress.
            if (reviveeState?.State == RMState.Reviving) return actions;

            // The team-heal wheel open on this body replaces its menu.
            if (TeamHealWheel.IsOpenFor(Revivee.ProfileId)) return actions;

            // A body in someone's hands has no menu: the dragger revives it from their own prompt (DownedDragController), and
            // nobody else can step in.
            if (reviveeState != null && reviveeState.IsBeingDragged) return actions;

            if (reviveeState?.State == RMState.BleedingOut)
            {
                // Support: tap drags, hold revives.
                actions.Actions.Add(new InteractionAction
                {
                    Action = () => OnSupport(owner),
                    Name = PlayerFacingMessages.Interaction.SupportAction,
                    Disabled = !DownedDragController.CanStartDrag(owner.Player, Revivee, reviveeState) && !CanTeamRevive(owner.Player, Revivee, reviveeState)
                });
                // Looting is on the inventory key (DownedLootInventoryKeyPatch), not in this menu.
            }
            else if (Cfg.TeamHealing.Enabled && PatientNeeds.NeedsAny(Revivee))
            {
                // Interact: opens the team-heal wheel; greyed out when the healer carries nothing for any of their needs.
                actions.Actions.Add(new InteractionAction
                {
                    Action = () => TeamHealWheel.Open(owner.Player, Revivee),
                    Name = PlayerFacingMessages.Interaction.InteractAction,
                    Disabled = !TeamMedical.HasAnythingToGive(owner.Player, Revivee)
                });
            }

            return actions;
        }

        // Whether the looter's inventory key opens this teammate's loot instead of their own inventory. Not while the body is
        //====================[ Loot Downed ]====================
        // in someone's hands, the looter is holding one or mid-hold, or the team-heal wheel is open (its keys close it).
        internal bool CanLoot(Player looter)
        {
            if (!Cfg.TeamHealing.AllowLootDownedPlayers || _isLootScreenOpen) return false;
            if (Revivee == null || looter == null || !looter.HealthController.IsAlive || !PlayerUtils.IsAliveHuman(Revivee)) return false;
            if (RMSession.IsPlayerCritical(looter.ProfileId) || CrouchHoldState.IsActive(looter)) return false;
            if (TeamHealWheel.IsOpen || DownedDragController.IsLocalHoldingBody) return false;
            return RMSession.TryGetPlayerState(Revivee.ProfileId, out var st) && st.State == RMState.BleedingOut && !st.IsBeingDragged;
        }

        public void OnLootDowned(GamePlayerOwner owner)
        {
            if (Revivee == null || owner?.Player == null || _isLootScreenOpen) return;
            if (!RMSession.TryGetPlayerState(Revivee.ProfileId, out var st) || st.State != RMState.BleedingOut) return;

            _isLootScreenOpen = true;
            _lootOwner = owner;
            SetCollidersEnabled(false);

            // Mark all downed player items as searched/known in the viewer's search controller
            // so the loot screen allows interaction (bypasses ISearchableContainer search gate).
            var searchCtrl = owner.Player.InventoryController.SearchController;
            var playerSearch = searchCtrl as PlayerSearchController;
            var playerSearchCtrl = searchCtrl as ActiveSearchController;
            foreach (Item item in Revivee.Equipment.GetAllItemsFromCollection())
            {
                // Mark searchable containers (rigs, backpacks) as searched
                if (item is SearchableItem searchable)
                    playerSearch?._searchedItems.Add(searchable);

                // Mark individual items as temporarily known
                playerSearchCtrl?.SetItemAsTemporaryKnown(item);
            }

            DownedLootGuard.Begin(owner.Player, Revivee);
            RMSession.PlayerStateChanged += OnReviveeStateChanged;

            owner.ShowInventoryScreenLoot(Revivee.Equipment, () =>
            {
                EndLootSession();
                owner.Player.SetInventoryOpened(false);
            });
        }

        private void OnReviveeStateChanged(string playerId, RMState oldState, RMState newState)
        {
            if (Revivee == null || playerId != Revivee.ProfileId) return;
            if (newState == RMState.BleedingOut) return;

            // Teammate is getting up (or died) - the loot session must end immediately.
            var owner = _lootOwner;
            EndLootSession();
            try { owner?.CloseInventoryIfOpen(); }
            catch (Exception ex) { Plugin.LogSource.LogWarning($"[BodyInteractable] CloseInventoryIfOpen failed: {ex.Message}"); }
        }

        private void EndLootSession()
        {
            if (!_isLootScreenOpen) return;
            RMSession.PlayerStateChanged -= OnReviveeStateChanged;
            DownedLootGuard.End();
            _isLootScreenOpen = false;
            _lootOwner = null;
        }

        private void OnDestroy()
        {
            ClearLookHint();
            var owner = _lootOwner;
            EndLootSession();
            try { owner?.CloseInventoryIfOpen(); } catch { }

            if (Revivee != null) TeamHealWheel.CloseFor(Revivee.ProfileId, "teammate's body interactable removed");
            for (int i = 0; i < _proxyObjects.Count; i++)
            {
                if (_proxyObjects[i] != null) Destroy(_proxyObjects[i]);
            }
            _proxyObjects.Clear();
            _colliders.Clear();

            if (Revivee != null)
            {
                BodyInteractableRuntime.Remove(Revivee.ProfileId, this);

                // Clear cached revival state when the remote Player object is removed.
                RMSession.RemovePlayer(Revivee.ProfileId);
            }
        }

        //====================[ ReviveCompleteHandler ]====================
        internal class ReviveCompleteHandler
        {
            public GamePlayerOwner owner;
            public string targetId;
            public string reviverId;
            public ReviveSource source;

            public void Complete(bool result)
            {
                Hud.HideHold();
                // Completed: the shock lands now and the paddles are put away; cancelled: they go at once.
                DefibHands.End(owner.Player, result);

                if (result)
                {
                    var reviveeState = RMSession.GetPlayerState(targetId);
                    if (reviveeState.State != RMState.BleedingOut)
                    {
                        FikaBridge.ReleaseTeamHelp(targetId, reviverId);
                        VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.ReviveNoLongerPossible);
                        return;
                    }

                    RevivalController.BeginAssistedReviveStart(owner.Player, targetId, source);
                }
                else
                {
                    FikaBridge.ReleaseTeamHelp(targetId, reviverId);
                }
            }
        }
    }
}
