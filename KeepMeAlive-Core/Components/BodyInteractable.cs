//====================[ Imports ]====================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using KeepMeAlive.Components;
using KeepMeAlive.Features;
using KeepMeAlive.Fika;
using KeepMeAlive.Helpers;
using UnityEngine;

namespace KeepMeAlive.Components
{
    //====================[ BodyInteractable ]====================
    public class BodyInteractable : InteractableObject
    {
        private static SyncedGameplayConfig Cfg => SyncedServerConfigStore.Config.Gameplay;

        //====================[ Nested Types ]====================
        // Lightweight marker placed on each bone-parented collider child.
        // The game's raycast hits the child and routes actions back to the owner.
        public sealed class BodyInteractableProxy : InteractableObject
        {
            public BodyInteractable Owner;
        }

        //====================[ Fields ]====================
        public Player Revivee { get; set; }
        public bool HasActivePicker { get; set; }
        
        private readonly List<Collider> _colliders = new List<Collider>();
        // Proxy GameObjects live under the revivee's bone transforms, not under this object,
        // so they must be destroyed explicitly.
        private readonly List<GameObject> _proxyObjects = new List<GameObject>();
        private readonly List<BodyInteractableProxy> _proxies = new List<BodyInteractableProxy>();
        private MedPickerInteractable _activeMedPicker;
        private bool _isLootScreenOpen;
        private GamePlayerOwner _lootOwner;

        public static float ReviveHoldTime => RevivePolicy.GetHoldDuration(ReviveSource.Team);

        //====================[ Bone Collider Whitelist ]====================
        private static readonly HashSet<EBodyPartColliderType> ColliderWhitelist = new HashSet<EBodyPartColliderType>
        {
            // Chest
            EBodyPartColliderType.RibcageUp,
            EBodyPartColliderType.RibcageLow,
            EBodyPartColliderType.RightSideChestUp,
            EBodyPartColliderType.LeftSideChestUp,
            EBodyPartColliderType.RightSideChestDown,
            EBodyPartColliderType.LeftSideChestDown,
            EBodyPartColliderType.SpineTop,
            EBodyPartColliderType.NeckFront,
            EBodyPartColliderType.NeckBack,
            // Stomach
            EBodyPartColliderType.Pelvis,
            EBodyPartColliderType.PelvisBack,
            EBodyPartColliderType.SpineDown,
            // Upper Arms
            EBodyPartColliderType.LeftUpperArm,
            EBodyPartColliderType.RightUpperArm,
            // Upper Legs
            EBodyPartColliderType.LeftThigh,
            EBodyPartColliderType.RightThigh,
        };

        // Slightly expand mirrored colliders to bias interaction raycasts toward body hitboxes.
        private const float MirroredColliderInflation = 1.08f;

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
            if (Revivee.IsAI || Revivee.AIData?.IsAI == true || Revivee.HealthController == null || !Revivee.HealthController.IsAlive)
            {
                Destroy(gameObject);
                return;
            }

            // Disable bone colliders when an overlay (picker / loot screen) is active
            if (HasActivePicker || _isLootScreenOpen)
            {
                SetCollidersEnabled(false);
                return;
            }

            SetCollidersEnabled(true);

            // Advance StateUpdateTime so the interaction wheel refreshes as eligibility changes.
            BumpProxyStateUpdateTime();
        }

        private void BumpProxyStateUpdateTime()
        {
            for (int i = 0; i < _proxies.Count; i++)
            {
                if (_proxies[i] != null) _proxies[i].SetStateUpdateTime();
            }
        }

        //====================[ Setup / Attachment ]====================
        public static BodyInteractable AttachToPlayer(Player player)
        {
            if (player == null) return null; 
            
            // Prevent multiple attachments if PlayerId is set multiple times
            if (player.gameObject.GetComponentInChildren<BodyInteractable>() != null) return null;

            // We use a coroutine to wait for bones, and also to wait until after Fika attaches
            Plugin.StaticCoroutineRunner.StartCoroutine(WaitForBonesAndBuild(player));
            return null; // Return null synchronously, we cache it dynamically later anyway
        }

        private static IEnumerator WaitForBonesAndBuild(Player player)
        {
            // Wait for bones to be ready and Profile to be fully assigned; stop if the player
            // object is destroyed (left the raid) while we wait.
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
            if (player.IsYourPlayer || player.IsAI || player.AIData?.IsAI == true) yield break;

            // Root GO holds the BodyInteractable component and serves as MedPicker anchor
            var go = new GameObject("Body Interactable");
            go.transform.SetParent(player.gameObject.transform, false);
            go.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            go.transform.localRotation = Quaternion.identity;
            go.layer = LayerMask.NameToLayer("Interactive");

            var bi = go.AddComponent<BodyInteractable>();
            bi.Revivee = player;

            int interactiveLayer = LayerMask.NameToLayer("Interactive");

            // Clone chest+stomach hitbox colliders onto bone transforms
            foreach (BodyPartCollider bpc in player.PlayerBones.BodyPartColliders)
            {
                if (bpc == null || bpc.Collider == null) continue;
                if (!ColliderWhitelist.Contains(bpc.BodyPartColliderType)) continue;

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
                bi._proxies.Add(proxy);
            }

            Features.BodyInteractableRuntime.Register(player.ProfileId, bi);
            go.SetActive(true);

            RevivalDebugLog.LogDebug($"[BodyInteractable] Created {bi._colliders.Count} mirrored colliders for player {player.ProfileId}");
        }

        //====================[ Collider Helpers ]====================
        private static Collider CloneColliderShape(Collider source, GameObject target)
        {
            if (source is BoxCollider box)
            {
                var clone = target.AddComponent<BoxCollider>();
                clone.center = box.center;
                clone.size = box.size * MirroredColliderInflation;
                return clone;
            }
            if (source is SphereCollider sphere)
            {
                var clone = target.AddComponent<SphereCollider>();
                clone.center = sphere.center;
                clone.radius = sphere.radius * MirroredColliderInflation;
                return clone;
            }
            if (source is CapsuleCollider capsule)
            {
                var clone = target.AddComponent<CapsuleCollider>();
                clone.center = capsule.center;
                clone.radius = capsule.radius * MirroredColliderInflation;
                clone.height = capsule.height * MirroredColliderInflation;
                clone.direction = capsule.direction;
                return clone;
            }
            return null;
        }

        private void SetCollidersEnabled(bool enabled)
        {
            for (int i = _colliders.Count - 1; i >= 0; i--)
            {
                if (_colliders[i] == null) { _colliders.RemoveAt(i); continue; }
                if (_colliders[i].enabled != enabled) _colliders[i].enabled = enabled;
            }
        }

        //====================[ Logic & Interaction ]====================

        public void OnRevive(GamePlayerOwner owner)
        {
            if (Revivee is null || owner?.Player is null) return;

            if (!RevivePolicy.IsEnabled(ReviveSource.Team))
            {
                VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.TeamReviveDisabled);
                return;
            }

            if (owner.Player.CurrentState is not IdlePlayerState)
            {
                VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.CannotReviveWhileMoving);
                return;
            }

            VFX_UI.ObjectivePanel(Color.cyan, PlayerFacingMessages.Interaction.RevivingObjective, ReviveHoldTime);

            var handler = new ReviveCompleteHandler
            {
                owner = owner,
                targetId = Revivee.ProfileId,
                reviverId = owner.Player.ProfileId
            };

            owner.Player.CurrentManagedState.Plant(true, false, ReviveHoldTime, handler.Complete);
            FikaBridge.SendTeamHelpPacket(Revivee.ProfileId, owner.Player.ProfileId);
        }

        public AvailableInteractionState GetActions(GamePlayerOwner owner)
        {
            var actions = new AvailableInteractionState();

            if (Revivee == null || owner?.Player == null) return actions;
            if (Revivee.IsAI || Revivee.AIData?.IsAI == true) return actions;
            // Empty result -> TryRouteActions returns false -> vanilla corpse interactions.
            if (Revivee.HealthController == null || !Revivee.HealthController.IsAlive) return actions;
            if (RMSession.IsPlayerCritical(owner.Player.ProfileId)) return actions;

            RMSession.TryGetPlayerState(Revivee.ProfileId, out var reviveeState);

            // During active revive progress we intentionally expose no actions.
            // This prevents both revive/search and category picker interactions while settling.
            if (reviveeState?.State == RMState.Reviving) return actions;

            if (reviveeState?.State == RMState.BleedingOut)
            {
                if (RevivePolicy.IsEnabled(ReviveSource.Team))
                {
                    bool canRevive = Cfg.Development.NoReviveItemRequired || ModUtils.HasReviveItem(owner.Player);
                    bool hasLives = reviveeState.LivesRemaining >= Cfg.Revival.TeamReviveLivesCost;
                    actions.Actions.Add(new InteractionAction
                    {
                        Action = () => OnRevive(owner),
                        Name = PlayerFacingMessages.Interaction.ReviveAction,
                        Disabled = !canRevive || !hasLives
                    });
                }

                if (Cfg.TeamHealing.AllowLootDownedPlayers)
                {
                    actions.Actions.Add(new InteractionAction
                    {
                        Action = () => OnLootDowned(owner),
                        Name = PlayerFacingMessages.Interaction.SearchAction,
                        Disabled = false
                    });
                }
            }
            else if (Cfg.TeamHealing.Enabled)
            {
                foreach (MedCategory cat in Enum.GetValues(typeof(MedCategory)))
                {
                    MedCategory captured = cat;
                    bool patientNeeds = TeamMedical.PatientNeedsCategory(Revivee, captured);
                    if (!patientNeeds) continue; // Only show category if the patient needs healing for it

                    bool hasMeds = TeamMedical.HealerHasMedForCategory(owner.Player, captured);
                    actions.Actions.Add(new InteractionAction
                    {
                        Action = () => OpenFilteredMedPicker(owner, captured),
                        Name = CategoryLabel(captured),
                        Disabled = !hasMeds
                    });
                }
            }

            return actions;
        }

        //====================[ Loot Downed ]====================
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
            var owner = _lootOwner;
            EndLootSession();
            try { owner?.CloseInventoryIfOpen(); } catch { }

            ForceClosePicker();
            for (int i = 0; i < _proxyObjects.Count; i++)
            {
                if (_proxyObjects[i] != null) Destroy(_proxyObjects[i]);
            }
            _proxyObjects.Clear();
            _proxies.Clear();
            _colliders.Clear();

            if (Revivee != null)
            {
                BodyInteractableRuntime.Remove(Revivee.ProfileId, this);

                // Clear cached revival state when the remote Player object is removed.
                RMSession.RemovePlayer(Revivee.ProfileId);
            }
        }

        public void OpenFilteredMedPicker(GamePlayerOwner owner, MedCategory category)
        {
            try
            {
                if (Revivee == null || RMSession.IsPlayerCritical(Revivee.ProfileId)) return;

                HasActivePicker = true;
                SetCollidersEnabled(false);

                // Spawn MedPicker with same bounds as the full body collider
                var pickerGo = InteractableBuilder<MedPickerInteractable>.Build(
                    PlayerFacingMessages.Interaction.MedPickerName,
                    Vector3.zero, 
                    new Vector3(0.8f, 1.8f, 0.8f),
                    transform,
                    null,
                    false
                );

                var picker = pickerGo?.GetComponent<MedPickerInteractable>();
                if (picker == null)
                {
                    Plugin.LogSource.LogError("[BodyInteractable] OpenFilteredMedPicker: picker missing");
                    RestoreFromPicker();
                    return;
                }

                picker.Init(owner.Player, Revivee, this, category);
                _activeMedPicker = picker;
                pickerGo.layer = LayerMask.NameToLayer("Interactive");
                
                // Allow it to exist now
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[BodyInteractable] OpenFilteredMedPicker error: {ex.Message}");
                RestoreFromPicker();
            }
        }

        public void RestoreFromPicker()
        {
            _activeMedPicker = null;
            HasActivePicker = false;
            // The Update loop will re-enable our collider on its next tick if conditions are met.
        }

        public void ForceClosePicker()
        {
            if (_activeMedPicker != null)
            {
                try { UnityEngine.Object.Destroy(_activeMedPicker.gameObject); } catch { }
                _activeMedPicker = null;
            }
            HasActivePicker = false;
        }

        private static string CategoryLabel(MedCategory cat)
        {
            return cat switch
            {
                MedCategory.Bleeds => PlayerFacingMessages.Interaction.MedicBleeds,
                MedCategory.Breaks => PlayerFacingMessages.Interaction.MedicBreaks,
                MedCategory.Health => PlayerFacingMessages.Interaction.MedicHealth,
                MedCategory.Comfort => PlayerFacingMessages.Interaction.MedicComfort,
                MedCategory.Nutrition => PlayerFacingMessages.Interaction.MedicNutrition,
                _ => cat.ToString()
            };
        }

        //====================[ ReviveCompleteHandler ]====================
        internal class ReviveCompleteHandler
        {
            public GamePlayerOwner owner;
            public string targetId;
            public string reviverId;

            public void Complete(bool result)
            {
                VFX_UI.HideObjectivePanel();

                if (result)
                {
                    var reviveeState = RMSession.GetPlayerState(targetId);
                    if (reviveeState.State != RMState.BleedingOut)
                    {
                        FikaBridge.SendTeamCancelPacket(targetId, reviverId);
                        VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.ReviveNoLongerPossible);
                        return;
                    }

                    RevivalController.BeginTeamReviveStart(owner.Player, targetId);
                }
                else
                {
                    FikaBridge.SendTeamCancelPacket(targetId, reviverId);
                    VFX_UI.Text(Color.yellow, PlayerFacingMessages.Interaction.ReviveCancelled);
                }
            }
        }
    }
}
