# Builds the Liquid Glass client library on its own: LGEngine\bin\LGEngine.Client.dll = GlassService / GlassSurface and the host
# pipe transport (no engine, no in-process render thread: /define:LGCLIENT compiles those out). Auto = host only; offline when no
# host answers. Smallest file list that compiles; same csc and style as build.ps1.
# Run:  .\build-client.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'
$out = Join-Path $root 'bin'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe from .NET Framework 4.x was not found.' }
$fx = Split-Path $csc
$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll' | ForEach-Object { "/r:$(Join-Path $fx $_)" }

$files = @(
    'GlassService.cs', 'GlassSurface.cs', 'GlassTransport.cs', 'PipeTransport.cs',
    'GlassProtocol.cs', 'GlassSection.cs', 'GlassLatency.cs', 'GlassLook.cs', 'GlassWarp.cs'
) | ForEach-Object { Join-Path $src $_ }

New-Item -ItemType Directory -Force $out | Out-Null
$dll = Join-Path $out 'LGEngine.Client.dll'
& $csc /nologo /noconfig /target:library /optimize+ /platform:anycpu /warn:4 /define:LGCLIENT "/out:$dll" $refs $files
if ($LASTEXITCODE -ne 0) { throw "Client compile failed (csc exit code $LASTEXITCODE)." }
"Built $dll"
