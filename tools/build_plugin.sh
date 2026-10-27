#!/usr/bin/env bash
# Build PlayerModelReplacer.dll against the game's own managed assemblies.
set -e

ROOT='G:/美丽水世界模型替换'
GAME='D:/Program Files/Steam/steamapps/common/Subnautica'
MONO='/d/Program Files/Unity 2019.4.36f1c1/Editor/Data/MonoBleedingEdge/bin/mono.exe'
CSC='/d/Program Files/Unity 2019.4.36f1c1/Editor/Data/MonoBleedingEdge/lib/mono/4.5/csc.exe'
MANAGED="$GAME/Subnautica_Data/Managed"

"$MONO" "$CSC" \
  -nologo -target:library -platform:x64 -optimize+ -debug- -codepage:65001 \
  -out:"$ROOT/build/plugin/PlayerModelReplacer.dll" \
  -reference:"$GAME/BepInEx/core/BepInEx.dll" \
  -reference:"$GAME/BepInEx/core/0Harmony.dll" \
  -reference:"$MANAGED/Assembly-CSharp.dll" \
  -reference:"$MANAGED/Assembly-CSharp-firstpass.dll" \
  -reference:"$MANAGED/UnityEngine.dll" \
  -reference:"$MANAGED/UnityEngine.CoreModule.dll" \
  -reference:"$MANAGED/UnityEngine.PhysicsModule.dll" \
  -reference:"$MANAGED/UnityEngine.ImageConversionModule.dll" \
  -reference:"$MANAGED/UnityEngine.InputLegacyModule.dll" \
  -reference:"$MANAGED/UnityEngine.AnimationModule.dll" \
  -reference:"$MANAGED/UnityEngine.IMGUIModule.dll" \
  -reference:"$MANAGED/UnityEngine.TextRenderingModule.dll" \
  -reference:"$MANAGED/UnityEngine.UI.dll" \
  -reference:"$MANAGED/UnityEngine.UIModule.dll" \
  -reference:"$MANAGED/UnityEngine.UnityWebRequestModule.dll" \
  -reference:"$MANAGED/UnityEngine.UnityWebRequestTextureModule.dll" \
  "$ROOT/src/PlayerModelReplacer/PlayerModelReplacer.cs" \
  "$ROOT/src/PlayerModelReplacer/PmxModel.cs" \
  "$ROOT/src/PlayerModelReplacer/LongHairGeometry.cs" \
  "$ROOT/src/PlayerModelReplacer/ClothCollisionPose.cs" \
  "$ROOT/src/PlayerModelReplacer/PdaModelTab.cs" \
  "$ROOT/src/PlayerModelReplacer/MmsConsole.cs" \
  "$ROOT/src/PlayerModelReplacer/ConsentGate.cs" \
  "$ROOT/src/PlayerModelReplacer/AdaptiveHeldIk.cs" \
  "$ROOT/src/PlayerModelReplacer/CinematicArmGeometry.cs" \
  "$ROOT/src/PlayerModelReplacer/CinematicCompatibility.cs" \
  "$ROOT/src/PlayerModelReplacer/NeckCapGeometry.cs"

ls -la "$ROOT/build/plugin/PlayerModelReplacer.dll"
