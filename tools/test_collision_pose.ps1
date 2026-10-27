param([string]$GamePath='D:\Program Files\Steam\steamapps\common\Subnautica')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$managed=Join-Path $GamePath 'Subnautica_Data\Managed'
$mono='D:\Program Files\Unity 2019.4.36f1c1\Editor\Data\MonoBleedingEdge'
$out=Join-Path $root 'build\performance-019'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$arguments=@('-nologo','-codepage:65001',"-out:$out\ClothCollisionPoseProbe.exe","-reference:$managed\UnityEngine.dll","-reference:$managed\UnityEngine.CoreModule.dll",(Join-Path $root 'src\PlayerModelReplacer\ClothCollisionPose.cs'),(Join-Path $PSScriptRoot 'ClothCollisionPoseProbe.cs'))
& "$mono\bin\mono.exe" "$mono\lib\mono\4.5\csc.exe" @arguments
if($LASTEXITCODE -ne 0){throw 'Collision pose probe compilation failed'}
foreach($name in @('UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.SharedInternalsModule.dll')) { Copy-Item -LiteralPath (Join-Path $managed $name) -Destination $out -Force }
& "$out\ClothCollisionPoseProbe.exe"
if($LASTEXITCODE -ne 0){throw 'Collision pose checks failed'}
