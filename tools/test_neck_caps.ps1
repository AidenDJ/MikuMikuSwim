param(
    [Parameter(Mandatory=$true)][string[]]$ModelPath,
    [string]$GamePath = 'D:\Program Files\Steam\steamapps\common\Subnautica',
    [string]$MonoPath = 'D:\Program Files\Unity 2019.4.36f1c1\Editor\Data\MonoBleedingEdge'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$managed = Join-Path $GamePath 'Subnautica_Data\Managed'
$probeDir = Join-Path $env:TEMP ('mms-neck-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probeDir | Out-Null
try {
    $output = Join-Path $probeDir 'NeckCapProbe.exe'
    $arguments = @('-nologo', '-codepage:65001', '-main:MikuPlayerReplacer.NeckCapProbe', "-out:$output",
        "-reference:$managed\UnityEngine.dll", "-reference:$managed\UnityEngine.CoreModule.dll",
        (Join-Path $root 'src\PlayerModelReplacer\PmxModel.cs'),
        (Join-Path $root 'src\PlayerModelReplacer\NeckCapGeometry.cs'),
        (Join-Path $PSScriptRoot 'ModelCompatibilityProbe.cs'),
        (Join-Path $PSScriptRoot 'NeckCapProbe.cs'))
    & "$MonoPath\bin\mono.exe" "$MonoPath\lib\mono\4.5\csc.exe" @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Neck probe compilation failed' }
    foreach ($assembly in @('UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.SharedInternalsModule.dll')) {
        Copy-Item -LiteralPath (Join-Path $managed $assembly) -Destination $probeDir
    }
    $manifest = Join-Path $probeDir 'models.txt'
    $resolvedModels = @($ModelPath | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
    [IO.File]::WriteAllLines($manifest, $resolvedModels, (New-Object Text.UTF8Encoding($false)))
    & "$MonoPath\bin\mono.exe" $output $manifest
    if ($LASTEXITCODE -ne 0) { throw 'Neck geometry checks failed' }
} finally {
    $resolvedProbe = [IO.Path]::GetFullPath($probeDir)
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    if (!$resolvedProbe.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        !([IO.Path]::GetFileName($resolvedProbe).StartsWith('mms-neck-'))) {
        throw 'Unexpected temporary directory; cleanup withheld'
    }
    Remove-Item -LiteralPath $resolvedProbe -Recurse -Force
}
