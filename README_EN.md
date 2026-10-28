# MMS — MikuMikuSwim

A BepInEx 5 plugin for **Subnautica** that replaces the player character with a full MMD
(PMX) model. It is not a camera-attached prop: the plugin reads the game player's skeleton
at runtime and retargets 55 bones (legs, arms, all fingers), so swimming, walking, arm and
finger animations follow the game character.

Version **0.1.1** — bug-fix release. Fixes a stray skirt-hem spike on some YYB models by
excluding root-level rig control bones (`センター先` / `Y先`) from automatic cloth inference.

> **No models are included.** You supply your own PMX models and textures.
> MMS only loads them; each model remains subject to its author's own license.

## Requirements

- Subnautica on Steam, version **1.22.83031** (Unity 2019.4.36, 64-bit, Mono).
- **BepInEx 5.4.x (x64 / Unity Mono)**. The easiest install is
  [Tobey's BepInEx Pack for Subnautica](https://www.nexusmods.com/subnautica/mods/1108).
- Confirm the game generates `BepInEx\LogOutput.log` before installing MMS.

## Installation

1. Close Subnautica.
2. Extract the `BepInEx` folder from this archive into the game root, merging folders:
   `...\steamapps\common\Subnautica\BepInEx\...`.
   After that you should have:

   ```
   Subnautica\BepInEx\plugins\PlayerModelReplacer\PlayerModelReplacer.dll
   Subnautica\BepInEx\plugins\PlayerModelReplacer\MMS_logo.png
   Subnautica\BepInEx\plugins\PlayerModelReplacer\MMS_pda_icon.png
   Subnautica\BepInEx\plugins\PlayerModelReplacer\Models\README.txt
   ```

3. Put each model in its own folder under `Models\`, for example:

   ```
   Models\MyModel\
       model.pmx
       tex\...
   ```

   Textures are looked up by the relative paths written inside the PMX; if that fails,
   by file name. Multiple PMX files in one folder become separate catalog entries.
4. Start the game. The **MMS notice** window appears on first run. Read it, then tick
   *Don't show again* and choose **I agree** (我同意). Choosing **I refuse** (我拒绝) quits
   the game.
5. Load a save. The model is built when you enter `player_view`.

## Controls

| Key | Action |
| --- | --- |
| **V** (configurable) | Open the PDA **Model** page |
| **F2** (configurable) | MMS console (master switch, key rebinding, rebuild) |
| **F5** | Cycle first person → over-the-shoulder third person → front-facing second person |
| **Numpad 4/6, 8/2, +/-** | Move the third/second-person camera; **5** resets |

The Model page previews a model before applying it, lets you hide individual materials,
toggle hair/skirt/clothing/accessory/water physics, adjust hair stiffness and other
sliders, and mute the player's own damage/jump/breathing sounds. Settings are stored per
model in `BepInEx\config\com.miku.subnautica.playerreplacer.cfg`.

The in-game interface is currently **Simplified Chinese**. The screenshots and this quick
start explain the main controls; see the Chinese `README.md` for the full manual.

## Model licensing

Before using any model, read the README or terms that came with that model (credit
requirements, modification rules, commercial-use restrictions, prohibited uses). Model
copyright and permissions belong to the model's author. **Do not redistribute models
through this mod, and do not upload models you do not have permission to share.**

## Notes and limitations

- MMS does not bundle models, textures, voice files, or game files.
- Model proportions differ from the game's dive suit; hand thickness and some grips may
  show slight intersections. Hair and skirts use a simplified cloth simulation.
- The plugin is built for the game version above. A game update may require a rebuild.
- Not affiliated with Crypton Future Media. Hatsune Miku and other character names are
  trademarks of their respective owners.

## Uninstall

Delete the `Subnautica\BepInEx\plugins\PlayerModelReplacer\` folder.

## License

Plugin source code is released under the MIT License; see [LICENSE](LICENSE).
BepInEx is a separate project under LGPL-2.1. PMX 2.1 field layout reference:
[OpenMMD specification](https://github.com/dskjal/OpenMMD/blob/main/docs/specification/pmx2.1-specification.md).
