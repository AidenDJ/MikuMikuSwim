param([string]$GamePath='D:\Program Files\Steam\steamapps\common\Subnautica')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $GamePath 'BepInEx\core\Mono.Cecil.dll')
$records=@()
foreach($label in @('before','after')) {
    $path=if($label -eq 'before'){Join-Path $root 'build\performance-019\before\PlayerModelReplacer.dll'}else{Join-Path $root 'build\adaptive-ik\PlayerModelReplacer.dll'}
    $assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
    try {
        $type=$assembly.MainModule.Types | Where-Object FullName -eq 'MikuPlayerReplacer.MikuPlayerReplacerPlugin'
        foreach($name in @('PushSampleOutOfBody','PushOutOfSlices','ShellOffset')) {
            $method=$type.Methods | Where-Object Name -eq $name
            $calls=@($method.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'UnityEngine.Transform' })
            $records += [pscustomobject]@{build=$label;method=$name;transformCallSites=$calls.Count}
            # Dead-centre fallback still reads the strand root; body-transform
            # reads inside the nested sample/collider loops must be gone.
            $expected=@{PushSampleOutOfBody=2;PushOutOfSlices=1;ShellOffset=0}
            if($label -eq 'after' -and $calls.Count -ne $expected[$name]){throw "Unexpected transform reads in $name"}
        }
        $solve=$type.Methods | Where-Object Name -eq 'SimulateCloth'
        $shell=@($solve.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'PrepareShellOffsets' })
        if($label -eq 'after' -and $shell.Count -ne 1){throw 'Shell refresh is missing'}
    } finally { $assembly.Dispose() }
}
$records | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'build\performance-019\il-checks.json') -Encoding UTF8
$records | Format-Table -AutoSize
Write-Output 'PERFORMANCE_IL_CHECKS=PASS'
