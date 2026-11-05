**KeepMeAlive gives you a second chance after lethal damage.** Instead of dying outright, your PMC enters a timed critical state. You start each raid with **5 lives**. Crawl to safety and try to revive yourself, or let a teammate drag you to safety and revive you. Teammates can also help treat your injuries and share food or water while you’re alive.

Based on the original revival mod by **KaikiNoodles** and **kobethuy**.

> ***Fika** is required! Disable Fika’s built-in revive system in the server configuration; it conflicts with KeepMeAlive.*

## KeepMeAlive {.tabset}

### Overview

**Going down**  
Lethal damage puts your PMC into a critical state instead of killing you outright. You can crawl to safety and attempt a self-revive, or wait for a teammate to drag you to safety and revive you. If your bleed-out timer runs out, you die.

![Downed and bleeding out](https://github.com/awnova/KeepMeAlive/releases/download/v1.0.0/BleedingOut.Forge.webp)

**Spectating while downed**  
While you’re down, spectate your teammates. If a teammate reaches your body, they can use **Support** to drag or revive you, or **Loot** to access your inventory.

![Support and Loot options shown to a teammate looking at your downed PMC](https://github.com/awnova/KeepMeAlive/releases/download/v1.0.0/Spectate.revive.forge.webp)

**Dragging a teammate**  
Choose **Support** to grab a downed teammate and pull them along the ground. Get them out of danger or move them into position for a revive.

![Dragging a downed teammate](https://github.com/awnova/KeepMeAlive/releases/download/v1.0.0/downed.and.draggged.Forge.webp)

**Reviving while dragging**  
Stop moving and hold the interact key to revive the teammate you’re dragging with a stim. By default, this uses **2 of their lives**. You keep hold of them during the revive and let them go when it’s done.

![Stim revive while dragging](https://github.com/awnova/KeepMeAlive/releases/download/v1.0.0/DragRevive.Forge.webp)

**Defibrillator revive**  
Use **Support** on a downed PMC to start a defibrillator revive. Hold to use the defib and bring them back. By default, this uses **1 of their lives**. A defib charge is consumed only if `ConsumeDefib` is enabled.

![Defibrillator revive](https://github.com/awnova/KeepMeAlive/releases/download/v1.0.0/DefibRevive.Forge.webp)

**Team medical**  
Help your teammates stay in the fight by treating their injuries, giving them medicine, or sharing food and water.

![Team medical assistance](https://github.com/awnova/KeepMeAlive/releases/download/v1.0.0/TeamHeal.Forge.webp)

### Features

* **Choose your recovery rules:** Set how much health each revive restores, which injuries it treats, how long the revive takes, and whether it grants temporary invulnerability.
* **Manage lives per raid:** Set the starting lives pool and the cost of each revive type.
* **Set the bleed-out window:** Choose how long a downed PMC has before the timer runs out.
* **Allow headshots to be fatal:** Configure the chance that a headshot kills outright.
* **Give up when rescue isn’t an option:** Hold `Backspace` for two seconds while downed.
* **Adjust the crawl speed:** Set how fast you move while downed.

#### The defibrillator

Buy the defibrillator from Therapist. By default, it costs **200,000 roubles at loyalty level 2** and has **5 charges**.

### Configuration

KeepMeAlive creates two config files the first time you start the server and a client:

* **Server:** `SPT_Runtime/user/mods/KeepMeAlive/config.json` contains shared gameplay settings. Restart the server after editing it.
* **Client:** `BepInEx/config/com.keepmealive.fika.cfg` contains options that only affect your game.

#### Client options

| Setting | Default | Description |
| --- | --- | --- |
| Self Revival Key | `F` | Hold while downed to try to revive yourself. |
| Give Up Key | `Backspace` | Hold while downed to die right away. |
| Enable Tinnitus Ring | On | Plays the ringing sound when you’re downed. Turn it off to mute it. |
| Language | Auto | Language for on-screen text. Auto follows your game language. |

To add a language, place a `<code>.json` file in `BepInEx/plugins/KeepMeAlive/Localizations`, named by the game's language code, and restart the game.

#### Server options

The tables below show the server settings, their defaults, and what they control. Revive settings are under `Gameplay.Self`, `Gameplay.Team`, or `Gameplay.Defib`, as indicated.

**Downed state — `Gameplay.Downed`**

| Setting | Default | Description |
| --- | --- | --- |
| `MaxLivesPerRaid` | 5 | Lives each player starts with. Revives spend lives. |
| `CriticalStateSeconds` | 180 | Time before a downed player bleeds out. |
| `DownedMovementSpeedPercent` | 50 | Crawl speed as a percentage of normal movement speed. |
| `ApplyContusionOnDowned` | On | Applies contusion when a player goes down. |
| `ApplyStunOnDowned` | On | Stuns a player when they go down. |
| `BlockUiWhenDowned` | On | Prevents opening the inventory and menus while downed. |
| `UnconsciousOnDowned` | Off | Darkens the screen as if the player has passed out. |

**Revive settings — `Gameplay.Self`, `Gameplay.Team`, `Gameplay.Defib`**

Each revive type has its own copy of these settings.

| Setting | Description |
| --- | --- |
| `Enabled` | Turns this revive type on or off. |
| `HoldSeconds` | How long the revive hold takes. |
| `LivesCost` | Lives spent by the revived player. |
| `AllowInsufficientLives` | Off by default. When enabled, a revive can proceed with fewer lives than its cost, spending the remaining lives. The player can’t be revived after their next down. |
| `RestoreBodyParts`, `RestorePercent` | Whether to restore body-part health and how much to restore for the head, chest, stomach, arms, and legs. |
| `RemoveLightBleeds`, `RemoveHeavyBleeds` | Whether to stop light and heavy bleeds. |
| `RemoveLegFractures`, `RemoveArmFractures` | Whether to fix leg and arm fractures. |
| `InvulnerabilityDurationSeconds` | How long the revived player is protected from damage. |
| `InvulnerabilitySpeedPercent` | Movement speed during protection; 100 is normal speed. |
| `CooldownSeconds` | How long before this player can be revived again. |
| `ApplyContusionOnRevive`, `ContusionDurationSeconds` | Whether to apply a contusion after a revive and how long it lasts. |
| `ApplyPainOnRevive`, `PainDurationSeconds` | Whether to apply pain after a revive and how long it lasts. |

**Default revive values**

| Setting | Self | Team | Defib |
| --- | --- | --- | --- |
| Hold time | 8 s | 6 s | 4 s |
| Lives cost | 3 | 2 | 1 |
| Cooldown | 240 s | 180 s | 90 s |
| Invulnerability | 10 s at 60% speed | 7 s at 80% speed | 5 s at full speed |
| Head / body health restored | 20% / 35% | 30% / 45% | 60% / 75% |
| Bleeds stopped | Heavy | Light and heavy | Light and heavy |
| Fractures fixed | None | None | Legs and arms |

**Required items**

| Setting | Revive type | Default | Description |
| --- | --- | --- | --- |
| `RequireStim` | Self, Team | Off | Requires the reviver to carry a stim. |
| `ConsumeStim` | Self, Team | Off | Consumes the stim when the revive starts. Only applies if `RequireStim` is on. |
| `RequireDefib` | Defib | On | Requires the reviver to carry a defibrillator. |
| `ConsumeDefib` | Defib | Off | Consumes one defib charge per revive. |

`Gameplay.StimItemIds` is a list of stim item IDs that the stim settings can use; the first one the reviver carries is used. Leave it empty to use the game’s Adrenaline stim.

**Protection — `Gameplay.Protection`**

| Setting | Default | Description |
| --- | --- | --- |
| `BlockDeathInCritical` | On | Prevents lethal damage from killing a player while downed or recovering. |
| `EnableGodMode` | On | Prevents damage while downed. |
| `EnableGhostMode` | On | Stops bots from targeting downed players. |
| `DeathFromHeadshotChance` | 0 | Chance a headshot kills outright instead of downing the player: 0 is never, 0.25 is 25%, and 1 is always. |

**Team medical — `Gameplay.TeamHealing`**

| Setting | Default | Description |
| --- | --- | --- |
| `Enabled` | On | Allows teammates to treat each other with medical items, food, and water. |
| `UseTimeMultiplier` | 1 | Treatment speed compared with normal: 2 is twice as slow; 0.5 is twice as fast. |
| `MinHpResourceToDisplay` | 50 | Minimum HP an item must have left to be offered. |
| `NutritionMinDeficitToDisplay` | 0.5 | Minimum hunger or thirst deficit for food or water to be offered. |
| `AllowLootDownedPlayers` | On | Allows teammates to use **Loot** on a downed player’s body. |

**Defibrillator — `Defib`**

| Setting | Default | Description |
| --- | --- | --- |
| `Uses` | 5 | Charges per defibrillator. |
| `ResizeTo2x1` | On | Makes the defibrillator occupy 2×1 inventory slots. |
| `Trading.AmountRoubles` | 200000 | Therapist’s price. |
| `Trading.LoyaltyLevel` | 2 | Loyalty level required to buy it. |

#### Debugging

`Gameplay.Development.DebugMode` is off by default. Turn it on to enable extra logging when reporting a bug.

### Installation

1. Download the release.
2. Drop the `BepInEx` and `SPT_Runtime` folders into your SPT installation, keeping the folder structure.
3. Start the server and a client once to create the KeepMeAlive config files.

**Required dependency:** [Fika](https://github.com/project-fika) on the server, headless host, and every co-op client.

### Credits

* Original revival mod: **KaikiNoodles**
* Bring Me To Life: **kobethuy**
* Design input: **ArthurWolf-Rayet**
* Spectate UI: **Zephyros**
* Testing: **constance68w**

### Support Me

I was told I should add my Ko-fi. Please don't donate money you need!

[https://ko-fi.com/awnova](https://ko-fi.com/awnova)

{.endtabset}
