<#
.SYNOPSIS
  Compiles tests/abi/layout_probe.c (written by eng/Generator) with MSVC and records the C struct layouts
  as tests/abi/<rid>.json. WGpuFury.Tests compares them with the C# structs field by field.

.EXAMPLE
  pwsh eng/run-abi-probe.ps1               # win-x64 and win-x86
  pwsh eng/run-abi-probe.ps1 -Arch x64
  pwsh eng/run-abi-probe.ps1 -Arch arm64   # on an arm64 machine (the probe has to run)
#>
param([ValidateSet('x64', 'x86', 'arm64')][string[]] $Arch = @('x64', 'x86'))

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$env:PATH += ";$(Split-Path $vswhere)"

$out = Join-Path ([System.IO.Path]::GetTempPath()) "wgpufury-abi-$PID"
New-Item -ItemType Directory -Force $out | Out-Null
try {
    foreach ($a in $Arch) {
        $rid = "win-$a"
        # The headers are the same for every RID; use whichever build is present.
        $include = @("native\$rid\include", 'native\win-x64\include', 'native\win-arm64\include') |
            ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $include) { throw "No headers under native\; run: dotnet run eng/build-native.cs" }
        $tools = if ($a -eq 'arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
        $vs = & $vswhere -latest -products '*' -requires $tools -property installationPath
        if (-not $vs) { throw "MSVC (C++ build tools, $tools) not found." }
        $vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars$(@{ x64 = '64'; x86 = '32'; arm64 = 'arm64' }[$a]).bat"
        $exe = Join-Path $out "probe-$a.exe"
        $src = Join-Path $root 'tests\abi\layout_probe.c'
        cmd /c "`"$vcvars`" >nul && cl /nologo /W3 /std:c11 /I`"$include`" `"$src`" /Fo`"$out\\`" /Fe`"$exe`"" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "cl failed for $rid" }
        $json = Join-Path $root "tests\abi\$rid.json"
        (& $exe) -join "`n" | Set-Content -NoNewline -Encoding utf8NoBOM $json
        Add-Content -Encoding utf8NoBOM $json "`n"
        Write-Host "wrote $json"
    }
}
finally {
    Remove-Item -Recurse -Force $out
}
