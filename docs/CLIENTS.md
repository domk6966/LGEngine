# Using LGEngine from your own app

LGEngine draws Apple-style "liquid glass" over the Windows desktop. Your app describes a piece of glass (a *surface*), gives it
the picture to show through the glass (text, icons), and tells it where to go. The engine reads the screen behind it, bends,
frosts and lights it, and keeps it on screen with its own arrival, hover and leaving motion.

Two ways to run it:

| mode | where the glass is drawn | what you ship |
|---|---|---|
| `InProcess` | a render thread inside your process | `LGEngine.dll` |
| `Host` | the shared per-user host process `LGEngineHost.exe` (one for all apps; started on demand) | `LGEngine.Client.dll` + `LGEngineHost.exe` |
| `Auto` (default) | the host if it can be reached or started, otherwise in-process | both |

`UiThread` also exists: the glass runs on your UI thread's message loop (for comparisons and tests).

`LGEngine.Client.dll` has no engine inside, so with it every mode connects to the host: `Auto`, `InProcess` and `UiThread`
all act as `Host`. Reference `LGEngine.dll` instead if you want the in-process modes or the fallback.

Requirements: Windows 10/11, .NET Framework 4.x. The engine is CPU-only today (a GPU backend is a paused prototype in `gpu\`).

## Build

```powershell
.\build.ps1            # bin\LGEngine.dll, the bench (LGBench.exe) and the host (LGEngineHost.exe)
.\build-client.ps1     # bin\LGEngine.Client.dll (the client library, built with /define:LGCLIENT)
```

Both use the C# compiler that ships with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`); no SDK.
Reference `LGEngine.dll` (in-process) or `LGEngine.Client.dll` (host) from your project, with `System.Drawing`.

## Starting the host

You don't start it yourself. `GlassService.Connect` in `Host` or `Auto` mode looks for the host's pipe; if no host answers,
it starts `LGEngineHost.exe` (from `GlassConnectOptions.HostPath`, or beside the assembly that contains `GlassService`), waits
for it and connects. `HostPath` must be a full path (a relative one throws `ArgumentException`).

`Connect` blocks while it does this: up to about 5 s on a cold start, longer if the host is busy. Call it at startup or off
your UI thread's critical path. Otherwise it never throws.

If no host can be reached or started at `Connect`:
- `Auto` (with `LGEngine.dll`) falls back to the in-process render thread; `glass.Transport` then reads
  `"render thread (host unavailable)"`.
- `Host` mode stays offline for the life of that service: every surface it makes raises `Failed` with
  `GlassFailure.TransportFailed`. Dispose it and `Connect` again later to retry.

`glass.Transport` names what you got: `"host pipe"`, `"render thread"` or `"ui thread"`. `glass.ModeUsed` gives the mode.

The host:

- runs once per user and session (a second copy exits at once);
- accepts only the current user's processes (the pipe and shared memory are locked to the current user + SYSTEM);
- exits 10 s after its last client leaves;
- `LGEngineHost.exe --probe` exits 0 at once (useful to check that Windows lets it run, e.g. under Smart App Control).

If the host dies after you connected, your service raises `Lost` and starts the host again: at once, then with a growing
pause (up to about a minute) if it keeps failing. It then re-creates every surface with its last state and raises
`ServiceRestarted`. You don't need to do anything, but you can show a hint while it is lost.

`Lost` and `ServiceRestarted` are raised on the `SynchronizationContext` that was current when you called `Connect` (your
UI thread in WinForms or WPF), otherwise on an engine thread.

## Connect and make glass

```csharp
using LGEngine;

GlassService glass = GlassService.Connect(new GlassConnectOptions { Mode = GlassMode.Auto });
// glass.Transport says what it got ("host pipe", or "render thread (host unavailable)").

GlassSpec spec = new GlassSpec();
spec.Kind = GlassKind.Card;                 // Card = a notification-style panel; Lens = a rounder, thicker pill (the OSD)
spec.Size = new Size(720, 280);             // the card, in screen pixels
spec.Place = new Point(2400, 40);           // its top left, in screen pixels
spec.Label = "MyApp glass";                 // required: the window title
GlassSurface card = glass.Create(spec);

card.Shown += delegate { /* the glass is on screen */ };
card.Failed += delegate(object s, GlassFailedEventArgs e) { /* e.Code (GlassFailure.*), e.Text; the surface is dead */ };

card.SetContents(pixels, 720, 280, Rectangle.Empty);   // premultiplied ARGB, exactly the card's size
card.Show();
...
card.Hide();       // animated leaving; Show() brings it back
card.Dispose();    // gone
glass.Dispose();   // at exit (disposes every surface)
```

Events are raised on the `SynchronizationContext` that was current when you called `Create` (your UI thread in WinForms or
WPF), otherwise on an engine thread.

## The calls

All of them return at once, can be called from any thread, and coalesce: the engine draws the newest state at the next
frame (latest wins; there is no queue to fill).

| call | what it does |
|---|---|
| `SetContents(px, w, h, dirty)` | the picture under the glass: premultiplied ARGB, `w`x`h` = the card size, transparent where the glass shows; `dirty` = the part that changed (empty = all) |
| `SetSize(size)` | a new card size; the old contents are dropped until the next `SetContents` at the new size |
| `SetPlace(point, animate)` | move the card (`animate`: glide instead of jump) |
| `SetOpacity(0..1)` | fade the whole glass |
| `SetLook(look, profile)` | darkness, blur, refraction, rim, shadow, roundness, intensity, elevation (`GlassLook`, each 0..100) and an optional profile (a cross-section string or a preset name: "Tighter rim", "Thin lens", "Pillow", "Flat pane") |
| `SetMotion(style, inMs, outMs)` | the arrival/leaving: 0 none, 1 fade, 2 slide, 3 zoom, 4 slide + zoom, 5 drop, 6 pop, 7 melt, 8 ripple; 60..1200 ms |
| `SetInteraction(hover, region)` | swell and follow the pointer while it is inside `region` (card pixels; empty = the whole card) |
| `SetDeform(deform)` | your own bend (zoom, lean, swell) instead of the engine's while it isn't `Deform.None` |
| `Show()` / `Hide()` | arrive / leave with the motion above |

### In-process extras (`LGEngine.dll`, modes `InProcess` and `UiThread`)

Liquid Glass's own notifications and volume/brightness indicator use these. Over the host they throw
`NotSupportedException`.

| call | what it does |
|---|---|
| `SetSize(size, px, flow)` | a new size and its picture in one step (never a blank frame); `flow`: a lens flows to the new width (260 ms) |
| `Hide(false)` | gone at once, no leaving motion (`Hide(true)` = `Hide()`) |
| `SetMotion(style, in, out, zoom, liquid, bounce, travel[, indicatorStyle])` | every number of the motion settings |
| `SetUpdates(fps, adapt, savePower)` | change the background rate, adapting and power saving while it runs |
| `SetPointerArea(rect)` | where the pointer counts as on the glass, in card pixels; may reach past the card |
| `SetSlosh(style, amount)` / `Kick(way)` | a lens's lean/bounce/pulse when its level moves (+1 up, -1 down) |
| `SetBusy(bool)` | the owner is animating the contents: keep reading the background as it moves |
| `SetButtons(GlassButton[], carry)` | glass buttons drawn in the contents that light up and press under the pointer |
| `Light`, `LightMoved` | how light the glass is now (0..1), to tint your contents; `BeforeRender` (UI-thread mode) for an exact per-frame tint |

`GlassSpec` also has `KeepSpare` (keep a spare glass map for quick size switches), `AnimTravel` (slide distance in points)
and `PeekOnExit` (keep reading the background during the leaving motion).

`GlassSpec` also has `Scale` (0 = the monitor's), `InShots` (show in screenshots), `SavePower` (fewer frames on battery),
`Fps` (how often the background is re-read, default 15; 0 = frozen), `Adapt` (light glass over light backgrounds),
`Hover`, `AnimStyle/AnimIn/AnimOut/AnimZoom/AnimLiquid/AnimBounce`.

Other events: `Hidden`, `AppearanceChanged` (the glass turned light or dark: switch your text colour), and
`RequestRejected` (host mode only: the host refused a request without killing the surface, e.g. a `SetSize` over your
memory quota; the surface stays at its last size, so set a size that fits before the next `SetContents`).

## Limits and errors

- A value out of range (look sliders, elevation, scale, place beyond +-100000, a label over 256 UTF-8 bytes, a profile over
  2400 chars) throws `ArgumentException` at the call, in every mode.
- Bad contents (null, wrong size, a dirty rectangle outside the card) raise `Failed` with `GlassFailure.BadContents`.
- Host mode: up to 16 clients per user; each has a memory quota (about 20 bytes per card pixel in total) and a call rate;
  calls over the rate are slowed and coalesced, not failed. Only protocol errors end a connection.
- Latency: a call reaches the screen within one display refresh plus about 1 ms (measured by `LGBench -contract -host`).

## Check your setup

```powershell
.\bin\LGBench.exe -contract            # the API in-process (render thread + UI thread), off screen
.\bin\LGBench.exe -contract -loopback  # the same over a real pipe inside one process
.\bin\LGBench.exe -contract -host      # the same against the installed LGEngineHost.exe, with latency p50/p99
```

Each prints numbered checks and a `contract: N/N checks OK` line. Nothing is shown on your screen (test glass is parked far
off the desktop).

Design and internals: `docs\SERVICE.md`.
