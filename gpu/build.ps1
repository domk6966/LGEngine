# Builds LGEngine\gpu\LGGpu.exe, the GPU Liquid Glass prototype and its test rig, with the C#
# compiler that ships with Windows (.NET Framework 4.x); and, when ..\bin\LGEngine.dll has been
# built (..\build.ps1), LGCpuRef.exe, which draws the same test cards with the CPU engine for
# comparison. Run:  .\build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe from .NET Framework 4.x was not found.' }
$fx = Split-Path $csc
$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll' | ForEach-Object { "/r:$(Join-Path $fx $_)" }

$exe = Join-Path $root 'LGGpu.exe'
$sources = 'Native.cs', 'Gfx.cs', 'Glass.cs', 'Surface.cs', 'Engine.cs', 'TestArt.cs', 'Program.cs' | ForEach-Object { Join-Path $root $_ }
& $csc /nologo /noconfig /target:exe /optimize+ /platform:anycpu /unsafe /warn:3 "/out:$exe" $refs $sources
if ($LASTEXITCODE -ne 0) { throw "Compile failed (csc exit code $LASTEXITCODE)." }
"Built $exe"

$dll = Join-Path (Split-Path $root) 'bin\LGEngine.dll'
if (Test-Path $dll) {
    $ref = Join-Path $root 'LGCpuRef.exe'
    $refSources = 'CpuRef.cs', 'TestArt.cs', 'Program.cs', 'Native.cs', 'Gfx.cs', 'Glass.cs', 'Surface.cs', 'Engine.cs' | ForEach-Object { Join-Path $root $_ }
    & $csc /nologo /noconfig /target:exe /optimize+ /platform:anycpu /unsafe /warn:3 /main:LGGpu.CpuRef "/out:$ref" $refs "/r:$dll" $refSources
    if ($LASTEXITCODE -ne 0) { throw "CpuRef compile failed (csc exit code $LASTEXITCODE)." }
    Copy-Item $dll (Join-Path $root 'LGEngine.dll') -Force
    "Built $ref"
} else { "(no ..\bin\LGEngine.dll yet: LGCpuRef.exe not built)" }
