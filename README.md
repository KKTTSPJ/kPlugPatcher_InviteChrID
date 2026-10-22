# kPlugPatcher_InviteChrID

English / [日本語](README_ja.md)

A BepInEx plugin for Koikatu and Koikatsu Party that works around bugs in **kPlug / kPlugVR** (by Katarsys) and in related game code, mainly `NullReferenceException`s, an orgasm loop, a stuck invite menu and high memory use.
It does not change kPlug's files; it patches the running code with Harmony.
(The name comes from its first feature: inviting the same character card more than once.)

**For the kPlug author:** [docs/KPLUG_ISSUES.md](docs/KPLUG_ISSUES.md) lists every issue with the exception stack, the cause, and a suggested fix inside kPlug, plus smaller exceptions that the patch does not touch.
The patch and the document are MIT-licensed, so any part of them can be taken into kPlug.

- Games: `Koikatu` / `KoikatuVR` (`kPlugPatcher_InviteChrID.dll`), `Koikatsu Party` / `Koikatsu Party VR` (`kPlugPatcher_InviteChrID_KKP.dll`)
- kPlug versions: 3.5.6 (tested in game, normal and VR) and 3.6.3 including the 2026-10-19 update (patches applied in normal and VR; the newer API is detected by type, not by version number)
- Version: 1.17.1

## What it fixes

| Problem | Workaround |
|---|---|
| Inviting the same card twice breaks leaving / name-based actions; a card with the main girl's name is not listed | Copy and rename the invited card in memory ("Name (1)"); show same-name cards |
| Invite does nothing for the rest of the H scene | Find the main girl's destroyed bones again before `SpotManager.AdaptCaliber` |
| NRE every frame after a coordinate reload or a swap (`ColDefsManager`, `BindersCtrl`, `BonesCtrl`, `BellyCtrl`) | Remove destroyed entries / find the bones again (since 1.11.2 also right after a previous repair, e.g. when the coordinate button is clicked quickly) |
| Orgasm repeats while the gauge is empty (game `ParentObjectCtrl`, game `MotionIK` with null IK targets from kPlug) | Skip destroyed items / null targets (game patches) |
| NRE every frame in ahegao for characters without a face material | Skip only the material call |
| Several GB of memory per H scene (invite menu loads every card fully) | Keep a light card in the list and load the full card when inviting |
| Frame rate drops with many invited girls (DynamicBone) | Stop invited girls' DynamicBones and cap updates to 1 per frame |
| After a swap, faces do not match the lines (face list not reloaded) | Reload the face list together with the voice list |
| HVoiceCaching 0.3: swap exceptions, 3P stuck at loading | Clear the dictionaries the way the game does (game patches) |
| VR: several invited girls cannot be kissed at once; a girl stays in kiss state | Optional multi-girl kiss; end stuck kisses |
| VR (kPlugVR 3.599+): the new invite menu loads every card fully at each character select (250 cards: about 20 s and +11 to 13 GB; out of memory the second time) | Keep light cards in the list and load the full card when inviting (VR) |
| Invite menu face images are kept at the full card size with a CPU copy, and stay after H (about 250 cards: 300 MB GPU + 300 MB CPU) | Shrink them to the button size, drop the CPU copy, destroy them when H ends (normal mode; VR menu: shrink) |
| VR: transparent clothes of characters behind the kPlugVR menu are drawn over it, and the invite menu flickers | Draw the characters' transparent parts first (VR) |
| `cf_j_kokan` scale becomes 0 / negative / NaN during insertion (kPlug `KokanBehavior`), with a Unity warning every frame | Skip the broken step and repair the value |
| Frame rate drops with many characters: DynamicBone collision checks recompute every collider for every particle (game) | Compute the colliders once per DynamicBone and reuse them; the results are the same (game patch) |

Each workaround can be turned off in the config. Diagnostics (logging only) are off by default.
Tested in game: 1.13.0 to 1.15.0 in KK VR, 1.16.0 in KK normal mode and VR, 1.16.1 and 1.17.0 in KK normal mode. Not tested: 1.16.1 in VR, Koikatsu Party, and the 1.17.1 change (VR menu draw order with the flicker diagnostics off).

## Install

1. Install kPlug and BepInEx 5.
2. Copy the DLL for your game from the [Releases](../../releases) ZIP to `BepInEx\plugins\kPlugPatcher_InviteChrID\` (only the one for your game; the other one would skip itself anyway).

Settings are in `BepInEx\config\kkttspj.kk.kplugpatcher.cfg` (Koikatsu Party: `kkttspj.kkp.kplugpatcher.cfg`). See [README_ja.md](README_ja.md) §7 for the full list.
The settings file of 1.10.x (`com.your.kk.kplugpatcher.cfg` / `com.your.kkp.kplugpatcher.cfg`) is copied to the new name on the first start of 1.11.0.

## Build

Place this folder directly under the game folder and run `build_and_deploy_invite.ps1` (needs the .NET SDK). It builds both `Release_KK` and `Release_KKP`.
The project references `..\..\BepInEx\core\` and `..\..\Koikatu_Data\Managed\` (`..\..\Koikatsu Party_Data\Managed\` for KKP). It does not reference kPlug; kPlug types are found by reflection.
The DLLs are copied to `..\Deploy\kPlugPatcher_InviteChrID\`.

## License

MIT ([LICENSE](LICENSE)). No warranty.
kPlug is a separate work by its author and is not included here. Game files, BepInEx and other plugins are not included either.
