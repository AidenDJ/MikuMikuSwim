param(
    [string]$GamePath = 'D:\Program Files\Steam\steamapps\common\Subnautica',
    [string]$MonoPath = 'D:\Program Files\Unity 2019.4.36f1c1\Editor\Data\MonoBleedingEdge'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$game = $GamePath
$mono = $MonoPath
$managed = Join-Path $game 'Subnautica_Data\Managed'
$output = Join-Path $root 'build\adaptive-ik\PlayerModelReplacer.dll'
New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
$refs = @('Assembly-CSharp', 'Assembly-CSharp-firstpass', 'UnityEngine',
    'UnityEngine.CoreModule', 'UnityEngine.PhysicsModule', 'UnityEngine.ImageConversionModule',
    'UnityEngine.InputLegacyModule', 'UnityEngine.AnimationModule', 'UnityEngine.IMGUIModule',
    'UnityEngine.TextRenderingModule', 'UnityEngine.UI', 'UnityEngine.UIModule',
    'UnityEngine.UnityWebRequestModule', 'UnityEngine.UnityWebRequestTextureModule')
$arguments = @('-nologo', '-target:library', '-platform:x64', '-optimize+', '-debug-', '-codepage:65001', "-out:$output")
$arguments += "-reference:$game\BepInEx\core\BepInEx.dll"
$arguments += "-reference:$game\BepInEx\core\0Harmony.dll"
foreach ($ref in $refs) { $arguments += "-reference:$managed\$ref.dll" }
$arguments += @(Get-ChildItem -LiteralPath (Join-Path $root 'src\PlayerModelReplacer') -Filter '*.cs' | ForEach-Object FullName)
& "$mono\bin\mono.exe" "$mono\lib\mono\4.5\csc.exe" @arguments
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
Get-FileHash -LiteralPath $output -Algorithm SHA256
