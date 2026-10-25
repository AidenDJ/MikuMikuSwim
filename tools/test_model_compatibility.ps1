param(
    [Parameter(Mandatory=$true)][string]$ModelPath,
    [string]$GamePath = 'D:\Program Files\Steam\steamapps\common\Subnautica',
    [string]$MonoPath = 'D:\Program Files\Unity 2019.4.36f1c1\Editor\Data\MonoBleedingEdge'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$managed = Join-Path $GamePath 'Subnautica_Data\Managed'
$outputDir = Join-Path $root 'build\compatibility-test'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$output = Join-Path $outputDir 'ModelCompatibilityProbe.exe'
$arguments = @('-nologo', '-codepage:65001', "-out:$output",
    "-reference:$managed\UnityEngine.dll", "-reference:$managed\UnityEngine.CoreModule.dll",
    (Join-Path $root 'src\PlayerModelReplacer\PmxModel.cs'),
    (Join-Path $PSScriptRoot 'ModelCompatibilityProbe.cs'))
& "$MonoPath\bin\mono.exe" "$MonoPath\lib\mono\4.5\csc.exe" @arguments
if ($LASTEXITCODE -ne 0) { throw 'Compatibility probe compilation failed' }
# This older Mono cannot execute an EXE whose own location contains some CJK
# characters. Run from a temporary ASCII-named folder; read the PMX path as UTF-8.
$probeDir = Join-Path $env:TEMP ('mms-compat-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probeDir | Out-Null
try {
    Copy-Item -LiteralPath $output -Destination $probeDir
    foreach ($assembly in @('UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.SharedInternalsModule.dll')) {
        Copy-Item -LiteralPath (Join-Path $managed $assembly) -Destination $probeDir
    }
    $manifest = Join-Path $probeDir 'model-path.txt'
    [IO.File]::WriteAllText($manifest, (Resolve-Path -LiteralPath $ModelPath).Path,
        (New-Object Text.UTF8Encoding($false)))
    & "$MonoPath\bin\mono.exe" (Join-Path $probeDir 'ModelCompatibilityProbe.exe') ('@' + $manifest) --verify
    if ($LASTEXITCODE -ne 0) { throw 'Model conversion/weight compatibility checks failed' }
} finally {
    $resolvedProbe = [IO.Path]::GetFullPath($probeDir)
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    if (!$resolvedProbe.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        !([IO.Path]::GetFileName($resolvedProbe).StartsWith('mms-compat-'))) {
        throw 'Unexpected temporary directory; cleanup withheld'
    }
    Remove-Item -LiteralPath $resolvedProbe -Recurse -Force
}
