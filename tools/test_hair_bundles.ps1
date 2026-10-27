param([string]$GamePath='D:\Program Files\Steam\steamapps\common\Subnautica')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$managed=Join-Path $GamePath 'Subnautica_Data\Managed'
$mono='D:\Program Files\Unity 2019.4.36f1c1\Editor\Data\MonoBleedingEdge'
$out=Join-Path $root 'build\hair-bundles-019'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$arguments=@('-nologo','-codepage:65001','-main:MikuPlayerReplacer.HairBundleProbe',"-out:$out\HairBundleProbe.exe","-reference:$managed\UnityEngine.dll","-reference:$managed\UnityEngine.CoreModule.dll",(Join-Path $root 'src\PlayerModelReplacer\PmxModel.cs'),(Join-Path $root 'src\PlayerModelReplacer\LongHairGeometry.cs'),(Join-Path $PSScriptRoot 'ModelCompatibilityProbe.cs'),(Join-Path $PSScriptRoot 'HairBundleProbe.cs'))
& "$mono\bin\mono.exe" "$mono\lib\mono\4.5\csc.exe" @arguments
if($LASTEXITCODE -ne 0) { throw 'Hair bundle probe compilation failed' }
foreach($name in @('UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.SharedInternalsModule.dll')) { Copy-Item -LiteralPath (Join-Path $managed $name) -Destination $out -Force }
$models=@(Get-ChildItem -LiteralPath (Join-Path $GamePath 'BepInEx\plugins\PlayerModelReplacer\Models') -Filter '*.pmx' -Recurse | ForEach-Object FullName)
[IO.File]::WriteAllLines((Join-Path $out 'models.txt'),$models,(New-Object Text.UTF8Encoding($false)))
& "$out\HairBundleProbe.exe" "$out\models.txt"
if($LASTEXITCODE -ne 0) { throw 'Hair bundle checks failed' }
