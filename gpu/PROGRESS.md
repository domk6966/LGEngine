# LGEngine GPU backend - prototype (paused 2026-10-16)

Stopped on purpose, to save usage. **Nothing here is used by the app yet.** The CPU engine (LGEngine\src) is what runs.

## What exists (about 3,200 lines)
- **Engine.cs:** the surface service. One render thread owns the D3D11 device, the DirectComposition device and every surface.
  - It sleeps on kernel objects when idle (no polling). Work from other threads arrives through a queue and an event, and desktop changes through an event from the feed thread.
  - While animating, it waits on the swap chain's frame-latency object, so presents are paced to the display.
- **Gfx.cs:** the D3D11 device on the adapter that drives the display, textures/views, the shader's constant block (float4 p[32]), raw COM vtable interop (no SharpDX).
- **Glass.cs:** the look/shape/elevation numbers for the shader. The bezel bend tables are worked out exactly as GlassMap does, including the "never fold back" pass.
- **Surface.cs:** one piece of glass on screen: its textures, its window's composition swap chain (or an off-screen target), and its passes.
- **glass.hlsl:** the Liquid Glass shader (SDF shape, frost, lensing bezel, rim light, shadow, contents, elevation).
- **Native.cs:** P/Invoke for windows, DWM and DirectComposition.
- **TestArt.cs:** test scenes.
- **CpuRef.cs / LGCpuRef.exe:** renders the same scenes with the CPU engine for side-by-side comparison.
- **Program.cs / LGGpu.exe:** the test rig.
  - `offscreen --out DIR [--alone] [--edge] [--case NAME] [--scene mixed|white|dark]` saves PNGs.
  - `live --x X --y Y [--w W --h H] [--seconds N] [--anim] [--busy] [--light|--dark] [--elev E] [--lens] [--nolayered] [--two]` shows a card over the desktop and measures it.
  - `busy-only ...` shows the moving test window alone (its own cost).
- **build.ps1:** builds LGGpu.exe and LGCpuRef.exe.

## Where it stopped
Its last note: CPU reference renders of the same scenes were made. The next steps were to compare the toast card and the lens side by side with the GPU renders, and to run the live test again with the latest fixes. So the offscreen GPU renders and a first live run existed. Look and live measurements had not been reported yet.

## To resume
1. Build: `powershell -ExecutionPolicy Bypass -File build.ps1` in this folder.
2. `LGGpu.exe offscreen --out <dir>` and `LGCpuRef.exe` → compare.
3. `LGGpu.exe live --x 2400 --y 40 --seconds 8` → measure CPU, frames and latency. The screen must be unlocked, because Desktop Duplication is denied on the lock screen.
4. Then integrate behind the same surface interface as the CPU service (see LGEngine\README.md once the core work is done).
