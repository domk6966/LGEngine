# Builds the Liquid Glass engine on its own: LGEngine\bin\LGEngine.dll (the engine) and LGEngine\bin\LGBench.exe
# (its off-screen benchmark), with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
# Run:  .\build.ps1          then e.g.  .\bin\LGBench.exe -help
# (The app itself compiles the engine's sources straight into LiquidGlass.exe: see ..\build.ps1.)
param([switch]$NoBench)
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
