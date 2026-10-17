# Builds the Liquid Glass engine on its own: LGEngine\bin\LGEngine.dll (the engine) and LGEngine\bin\LGBench.exe
# (its off-screen benchmark), with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
# Run:  .\build.ps1          then e.g.  .\bin\LGBench.exe -help
# Also LGEngine\bin\LGEngineHost.exe (the host process: src\*.cs + host\HostMain.cs, no window). -HostOnly [-HostName <file>] builds only that
# (tools\ship8.ps1 uses it).
# (The app itself compiles the engine's sources straight into LiquidGlass.exe: see ..\build.ps1.)
param([switch]$NoBench, [switch]$HostOnly, [string]$HostName = 'LGEngineHost.exe')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'
$bench = Join-Path $root 'bench'
$out = Join-Path $root 'bin'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe from .NET Framework 4.x was not found.' }
$fx = Split-Path $csc
$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll' | ForEach-Object { "/r:$(Join-Path $fx $_)" }

New-Item -ItemType Directory -Force $out | Out-Null
$hostDir = Join-Path $root 'host'
if ($HostOnly) {
    $hexe = Join-Path $out $HostName
    & $csc /nologo /noconfig /target:winexe /optimize+ /platform:anycpu /warn:4 "/out:$hexe" $refs (Join-Path $src '*.cs') (Join-Path $hostDir '*.cs')
    if ($LASTEXITCODE -ne 0) { throw "Host compile failed (csc exit code $LASTEXITCODE)." }
    "Built $hexe"
    return
}
$dll = Join-Path $out 'LGEngine.dll'
& $csc /nologo /noconfig /target:library /optimize+ /platform:anycpu /warn:4 "/out:$dll" $refs (Join-Path $src '*.cs')
if ($LASTEXITCODE -ne 0) { throw "Engine compile failed (csc exit code $LASTEXITCODE)." }
"Built $dll"

if (-not $NoBench) {
    $exe = Join-Path $out 'LGBench.exe'
    & $csc /nologo /noconfig /target:exe /optimize+ /platform:anycpu /warn:4 "/out:$exe" $refs "/r:$dll" (Join-Path $bench '*.cs')
    if ($LASTEXITCODE -ne 0) { throw "Benchmark compile failed (csc exit code $LASTEXITCODE)." }
    "Built $exe"
}

if (Test-Path (Join-Path $hostDir 'HostMain.cs')) {
    $hexe = Join-Path $out 'LGEngineHost.exe'
    & $csc /nologo /noconfig /target:winexe /optimize+ /platform:anycpu /warn:4 "/out:$hexe" $refs (Join-Path $src '*.cs') (Join-Path $hostDir '*.cs')
    if ($LASTEXITCODE -ne 0) { throw "Host compile failed (csc exit code $LASTEXITCODE)." }
    "Built $hexe"
}
