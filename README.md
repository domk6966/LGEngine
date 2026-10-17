# LGEngine

The rendering engine behind Liquid Glass: Apple-style "liquid glass" surfaces for Windows (notifications, the
volume/brightness indicator, a search box, a dock), drawn over the live desktop.

C# 5 on .NET Framework 4.x. No SDK, no packages: it builds with the C# compiler that ships with Windows.

## Build

    powershell -ExecutionPolicy Bypass -File .\build.ps1

Makes `bin\LGEngine.dll` (the engine) and `bin\LGBench.exe` (an off-screen benchmark and picture maker;
`bin\LGBench.exe -help`).

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
- Background service, so other apps can drive the engine: in progress; design and build plan in `docs\SERVICE.md`
  (section 12). In the engine now: the whole per-window glass runtime, moved out of the app: the layered window
  (`src\GlassLayer.cs`), the backdrop tap and scheduling (`src\GlassPeek.cs`), rendering and presenting
  (`src\GlassPresenter.cs`) and the show/hide/hover animation (`src\GlassAnimator.cs`). Still to come: the public
  surface API (`GlassService` / `GlassSurface`, in-process first; built, not yet reviewed, so not in this export), a
  render thread with vblank pacing, and the host process + client library over a named pipe with shared memory, with
  measured call-to-screen latency.
- GPU (Direct3D 11) backend: paused prototype in `gpu\` (see `gpu\PROGRESS.md`); not wired in.

## License

Not chosen yet.
