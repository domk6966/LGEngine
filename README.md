# LGEngine

Version 2.7.12.0 - protocol 1.1.

The rendering engine behind Liquid Glass: Apple-style "liquid glass" surfaces for Windows (notifications, the
volume/brightness indicator, a search box, a dock), drawn over the live desktop.

C# 5 on .NET Framework 4.x. No SDK, no packages: it builds with the C# compiler that ships with Windows.

## Build

    powershell -ExecutionPolicy Bypass -File .\build.ps1

Makes `bin\LGEngine.dll` (the engine), `bin\LGEngineHost.exe` (the shared per-user host process) and `bin\LGBench.exe`
(an off-screen benchmark and picture maker; `bin\LGBench.exe -help`). `.\build-client.ps1` makes `bin\LGEngine.Client.dll`,
the small library other apps reference to use the host.

**Using the engine from your own app: `docs\CLIENTS.md`** (start the host, connect, create glass surfaces).

## What it does

- **Ray-traced, baked glass** (`src\GlassTrace.cs`, `src\GlassTraceGeo.cs`): a glass slab floating above the screen,
  with refraction (Snell, per-colour dispersion), Fresnel reflection of a soft studio environment, total internal
  reflection and exact anti-aliasing. It is traced once per shape into per-pixel tables, so a frame only samples the
  live backdrop through them (`src\GlassMap.cs`).
- **Custom contours** (`src\GlassProfile.cs`, `src\GlassSideView.cs`, `src\GlassProfileEditor.cs`): top and bottom
  surface profiles as control points with presets, and a WinForms editor control that shows the engine's real ray
  paths in a side view.
- **Incremental rendering**: only changed backdrop texels, changed cells and dirty rectangles are redone
  (`src\GlassBackdrop.cs`, `src\LayerPresent.cs`).
- **Animated glass** (`src\GlassMotion.cs`): arrival and dismissal, hover bulge, press and ripple. It scales baked
  offsets and light, with no re-trace per frame and zero cost when idle.
- **Live backdrop**: Desktop Duplication feed (`src\ScreenFeed.cs`, `src\Duplicator.cs`).
- **Stats** for monitoring (`src\EngineStats.cs`).

## Status

- CPU backend: complete and in daily use.
- Background service, so other apps can drive the engine: ready on the CPU (how to use it: `docs\CLIENTS.md`; design:
  `docs\SERVICE.md`). `host\HostMain.cs` + `src\GlassHostRunner.cs` = the host process (one per user, current-user-only
  pipe and shared memory, started on demand by the client, exits when idle); `src\GlassHostServer.cs`,
  `src\PipeTransport.cs`, `src\GlassProtocol.cs`, `src\GlassSection.cs` = the wire. Restarts are handled: a client that
  dies is cleaned up by the host; a host that dies is started again and every surface comes back. In the engine now: the whole per-window glass runtime, moved out of the app: the layered window
  (`src\GlassLayer.cs`), the backdrop tap and scheduling (`src\GlassPeek.cs`), rendering and presenting
  (`src\GlassPresenter.cs`) and the show/hide/hover animation (`src\GlassAnimator.cs`). Also in: the public surface
  API (`src\GlassService.cs`, `src\GlassSurface.cs`), run in-process on the engine's own render thread with vblank
  pacing (`src\GlassRenderThread.cs`), with measured latency (`src\GlassLatency.cs`; call to next vblank ~10 ms p50,
  ~19 ms p99 at 60 Hz; `bin\LGBench.exe -contract`, `-contract -loopback`, `-contract -host`, `-hostmain`).
- Liquid Glass's own notifications and volume/brightness indicator are drawn through this public surface API (in process,
  UI-thread mode), proven pixel-identical to the old direct path by `bin\LGBench.exe -surface`.
- GPU (Direct3D 11) backend: paused prototype in `gpu\` (see `gpu\PROGRESS.md`); not wired in.

## License

Not chosen yet.
