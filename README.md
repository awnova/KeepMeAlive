# KeepMeAlive

KeepMeAlive adds a second chance after lethal damage in **SPT 4.1.x**. Your PMC can enter a timed critical state and be revived by you or a teammate before bleeding out. Co-op revives use Fika.

The release includes a BepInEx client plugin and an SPT server mod. The server manages gameplay settings, revive lives and cooldowns, and the revival item trader offer. The client provides the downed state and revive interactions.

Based on the original revival mod by **KaikiNoodles** and **thuynguyentrungdang**.

## Features

- Self and teammate revives, with configurable timing, lives, and item use.
- Configurable recovery after revival, including health restoration, status effects, and temporary invulnerability.
- Teammate medical assistance for health, bleeds, fractures, comfort effects, and nutrition.
- Configurable downed movement, protections, and hardcore behavior.

## Requirements

- SPT 4.1.x
- Fika on the server and each co-op client

Disable Fika's built-in revive system in the server configuration; it conflicts with KeepMeAlive.

## Install

Drop both the `BepInEx` and `SPT_Runtime` folders from the release into your SPT installation, keeping their folder structure.

KeepMeAlive creates its server and client configuration files the first time each is started.

## Play

When downed, hold the self-revive key (default `F`) to attempt a self-revive. A teammate looking at your downed PMC gets two options: **Support** and **Loot**. Tapping Support starts a drag; holding it starts a defibrillator revive. The server controls revive timing, item requirements, life costs, and recovery effects.

When you go down, your body falls limp. Move to recover and crawl while downed. Teammates see your body fall; your own player is not ragdolled.

To give up, hold the configured key (default `Backspace`) for two seconds while downed and not being revived.

A teammate can tap **Support** on your downed PMC to drag you along. The dragger crouches, puts away their weapon, and holds you by the shoulders. Dragging ends if they stand, go prone, take something in hand, turn away from you, get too far away, go down, or your body rolls out of their hands. A revive by anyone else also ends the drag, and a body being dragged has no interaction menu for anyone. The dragger can revive the body they are holding by holding the interact key (shown on the bottom strip as "Hold [key] Revive"): their right hand takes a stim pen from their chest and holds it in your shoulder for the whole revive while the left keeps hold, then they put you down when it is done (a cancelled revive just puts the pen away and the drag carries on). You stay limp where you are released until you move.

The default revive item is the item with template ID `5c052e6986f7746b207bc3c9`. By default, the server offers it from Therapist for 200,000 roubles at loyalty level 2.

## Configuration

Server gameplay settings are in `user/mods/KeepMeAlive/config.json` inside `SPT_Runtime`. The server creates this file with defaults and shares its settings with clients. Restart the server after editing it.

Client-only options, including the self-revive and give-up keys and tinnitus audio, are in `BepInEx/config/com.awnova.keepmealive.cfg`.

## Troubleshooting

- **Revive options are unavailable:** Check the server log for `[SyncConfig]` warnings and confirm `SPT_Runtime/user/mods/KeepMeAlive/config.json` exists.
- **Self-revive is unavailable:** Check that it is enabled in the server config, you have enough lives, and you carry the configured item.
- **Teammate revive does not work:** Check that team revive is enabled, the reviver meets the configured requirements, and Fika's built-in revive system is disabled.

## Build from source

Run `build.ps1` to build both projects in Release mode. The package is assembled under `Build/KeepMeAlive`, and the client plugin is also copied to the configured SPT install. The client project expects SPT at `C:\SPT` by default; set the `SPTBaseDir` MSBuild property to your SPT client installation path when building elsewhere.

## Credits
These projects are what made this possible.
- Original revival mod: **KaikiNoodles**
- Bring Me To Life: **thuynguyentrungdang**
- SPT-AKI and Fika development teams

KeepMeAlive is not affiliated with or endorsed by Battlestate Games.

## License

MIT. See [LICENSE](LICENSE).
