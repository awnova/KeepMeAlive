**KeepMeAlive Solo gives you a second chance after lethal damage.** Instead of dying outright, your PMC enters a timed critical state. You start each raid with **5 lives**. Crawl to safety and try to revive yourself before you bleed out.

Based on the original revival mod by **KaikiNoodles** and **kobethuy**.

> *This is the **solo** version, built for single-player SPT with no co-op. It has no teammate drag, teammate revive, defibrillator, or team medical. Playing with friends? Use the Fika version of KeepMeAlive instead. Don't install both.*

## KeepMeAlive Solo {.tabset}

### Overview

**Going down**  
Lethal damage puts your PMC into a critical state instead of killing you outright. You crawl, stunned and bleeding, while the clock runs down. If your bleed-out timer runs out, you die.

![Downed and bleeding out](https://github.com/awnova/KeepMeAlive/releases/download/v1.0.0/BleedingOut.Forge.webp)

**Self-revive**  
Find a spot to hide, then hold **F** to revive yourself. Your PMC uses a stim during the hold. By default this takes **8 seconds** and uses **1 life**. When it finishes you get back up with part of your health restored, heavy bleeds stopped, and a short window of reduced damage and slower movement.

**Lives**  
Every revive spends lives from your per-raid pool. Run out and your next down is final.

**Giving up**  
If there's no way out, hold **Backspace** for two seconds while downed to die right away.

### Features

* **Choose your recovery rules:** Set how much health each revive restores, which injuries it treats, how long the revive takes, and whether it grants temporary invulnerability.
* **Manage lives per raid:** Set the starting lives pool and the cost of a revive.
* **Set the bleed-out window:** Choose how long a downed PMC has before the timer runs out.
* **Allow headshots to be fatal:** Configure the chance that a headshot kills outright.
* **Require a stim:** Optionally make self-revive need a stim in your inventory, and optionally use it up.
* **Adjust the crawl speed:** Set how fast you move while downed.
* **Translate it:** On-screen text is localized, and you can add your own languages.

### Configuration

KeepMeAlive creates two config files the first time you start the server and a client:

* **Server:** `SPT_Runtime/user/mods/KeepMeAlive/config.json` contains gameplay settings. Restart the server after editing it.
* **Client:** `BepInEx/config/com.awnova.keepmealive.cfg` contains options that only affect your game.

#### Client options

| Setting | Default | Description |
| --- | --- | --- |
| Self Revival Key | `F` | Hold while downed to try to revive yourself. |
| Give Up Key | `Backspace` | Hold for two seconds while downed to die right away. |
| Enable Tinnitus Ring | On | Plays the ringing sound when you’re downed. Turn it off to mute it. |
| Language | Auto | Language for on-screen text. Auto follows your game language. |

To add a language, place a `<code>.json` file in `BepInEx/plugins/KeepMeAlive/Localizations`, named by the game's language code, and restart the game.

#### Server options

The tables below show the server settings, their defaults, and what they control. Revive settings are under `Gameplay.Self`.

**Downed state — `Gameplay.Downed`**

| Setting | Default | Description |
| --- | --- | --- |
| `MaxLivesPerRaid` | 5 | Lives you start each raid with. Revives spend lives. |
| `CriticalStateSeconds` | 180 | Time before you bleed out. |
| `DownedMovementSpeedPercent` | 50 | Crawl speed as a percentage of normal movement speed. |
| `ApplyContusionOnDowned` | On | Applies contusion when you go down. |
| `ApplyStunOnDowned` | On | Stuns you when you go down. |
| `BlockUiWhenDowned` | On | Prevents opening the inventory and menus while downed. |
| `UnconsciousOnDowned` | Off | Darkens the screen as if you have passed out. |

**Self-revive — `Gameplay.Self`**

| Setting | Default | Description |
| --- | --- | --- |
| `Enabled` | On | Turns self-revive on or off. |
| `HoldSeconds` | 8 | How long the revive hold takes. |
| `LivesCost` | 1 | Lives spent by the revive. |
| `AllowInsufficientLives` | Off | When enabled, a revive can proceed with fewer lives than its cost, spending the remaining lives. You can’t be revived after your next down. |
| `RestoreBodyParts` | On | Whether to restore body-part health. |
| `RestorePercent` | Head 20%, everything else 35% | How much to restore for the head, chest, stomach, arms, and legs. |
| `RemoveLightBleeds` | Off | Stops light bleeds. |
| `RemoveHeavyBleeds` | On | Stops heavy bleeds. |
| `RemoveLegFractures`, `RemoveArmFractures` | Off | Fixes leg and arm fractures. |
| `InvulnerabilityDurationSeconds` | 10 | How long you’re protected from damage after reviving. |
| `InvulnerabilitySpeedPercent` | 60 | Movement speed during protection; 100 is normal speed. |
| `CooldownSeconds` | 240 | How long before you can self-revive again. |
| `ApplyContusionOnRevive`, `ContusionDurationSeconds` | On, 10 | Whether to apply a contusion after a revive and how long it lasts. |
| `ApplyPainOnRevive`, `PainDurationSeconds` | On, 30 | Whether to apply pain after a revive and how long it lasts. |

**Required items**

| Setting | Default | Description |
| --- | --- | --- |
| `RequireStim` | Off | Requires you to carry a stim to self-revive. |
| `ConsumeStim` | Off | Consumes the stim when the revive starts. Only applies if `RequireStim` is on. |

`Gameplay.StimItemIds` is a list of stim item IDs that self-revive can use; the first one you carry is used. Leave it empty to use the game’s Adrenaline stim.

**Protection — `Gameplay.Protection`**

| Setting | Default | Description |
| --- | --- | --- |
| `BlockDeathInCritical` | On | Prevents lethal damage from killing you while downed or recovering. |
| `EnableGodMode` | On | Prevents damage while downed. |
| `EnableGhostMode` | On | Stops bots from targeting you while downed. |
| `DeathFromHeadshotChance` | 0 | Chance a headshot kills outright instead of downing you: 0 is never, 0.25 is 25%, and 1 is always. |

#### Debugging

`Gameplay.Development.DebugMode` is off by default. Turn it on to enable extra logging when reporting a bug.

### Installation

1. Download the release.
2. Drop the `BepInEx` and `SPT_Runtime` folders into your SPT installation, keeping the folder structure.
3. Start the server and a client once to create the KeepMeAlive config files.

**Requires SPT 4.1.x.** Don't run this alongside the Fika version of KeepMeAlive.

### Credits

* Original revival mod: **KaikiNoodles**
* Bring Me To Life: **kobethuy**
* Design input: **ArthurWolf-Rayet**
* Testing: **constance68w**

### Support Me

I was told I should add my Ko-fi. Please don't donate money you need!

[https://ko-fi.com/awnova](https://ko-fi.com/awnova)

{.endtabset}
