# KeepMeAlive

KeepMeAlive adds a second chance after lethal damage in **SPT 4.1.x**. Instead of dying outright, your PMC enters a timed critical state and can be revived before bleeding out.

Each release is a BepInEx client plugin plus an SPT server mod. The server manages gameplay settings and revive lives and cooldowns; the client provides the downed state and revive interactions.

## Pick a build

| Build | For | Needs Fika | Has |
| --- | --- | --- | --- |
| [KeepMeAliveSolo](KeepMeAliveSolo/README.md) | Single-player | No | Downed state and self-revive |
| [KeepMeAliveFika](KeepMeAliveFika/README.md) | Co-op | Yes | Everything in Solo, plus teammate drag, stim and defibrillator revives, and team medical |

Install only one of them. Both use the same mod ID and files, so they cannot run side by side.

Each build has its own README with the full install, play, and configuration details. Release page texts are in `Forge.md` in each build's folder.

## Shared localization

All KMA client mods (KeepMeAlive, KMA Body Drag, KMA Quick Wheel) install into the one `BepInEx/plugins/KeepMeAlive` folder and share `BepInEx/plugins/KeepMeAlive/Localizations`: one `<code>.json` per language (EFT language code, e.g. `ru`) holding every mod's keys. Each mod reads only the keys it uses and ignores the rest. The master text is `Localization/en.json` in this repo; new keys from any KMA mod go there too.

## Credits

These projects are what made this possible.
- Original revival mod: **KaikiNoodles**
- Bring Me To Life: **thuynguyentrungdang**
- Spectate UI: **Zephyros** (Fika build)
- Testing: **constance68w**
- SPT-AKI and Fika development teams

KeepMeAlive is not affiliated with or endorsed by Battlestate Games.

## License

MIT. See [LICENSE](LICENSE).
