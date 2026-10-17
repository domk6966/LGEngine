# LGEngine as a per-user background service (design only, no code yet)

Status: DESIGN, build started. Written 2026-10-16 alongside the GlassProfile work (HANDOFF section 8). Section 9 says how to get there
without a flag day; section 12 is the step-by-step build plan and what is done.

Why: other apps will use the engine (an OSD app, a notification app, the GPU backend later). Each of them wants glass on the screen,
and each would otherwise run its own screen duplication, render thread, layered windows and (later) D3D11 device. One shared host
does that once, paced to the display, with the lowest latency and zero idle cost. The apps that use it contain no glass code: they
describe glass (shape, look, contents, place, motion) and the host draws it.

## 1. Process model

- `LGEngineHost.exe`: one per user session, single instance by a named mutex (`Local\LGEngineHost.<protocol major>`); a second copy
  exits at once. Started on demand by the client library (first `Connect`), not at logon; it stays while any surface exists and for
  10 s after the last client leaves, then exits (nothing resident when no glass is used).
- The host OWNS everything that must be shared or that touches the screen:
  - the one screen feed (`ScreenFeed`: Desktop Duplication per output, the screenshot-safe peek fallback), started only while a surface
    is visible and stopped when none is;
  - the render thread, paced by the display's vblank (DwmFlush / waitable timer while animating, an event wait when idle);
  - every glass layered window (created and presented on the render thread, so a present never crosses a process and a stalled
    client cannot stall the screen);
  - later, the one D3D11 device of the GPU backend and its shared-texture pool;
  - the baked-map cache (`GlassMap.Make`, keyed by Signature including the profile hash): two apps asking for the same glass share one
    bake.
- The host never sends screen pixels to anyone. A client sees only a light/dark flag per surface (`AppearanceChanged`).
- LiquidGlassGuard.exe stays a separate, tiny watchdog (it restores windows the app hid if the process that hid them dies). In the new
  model whoever hides a Windows window registers a restore token with the guard (the host for windows it hides itself, a client app for
  its own), so a crash of either is still undone.

## 2. Client library: LGEngine.Client.dll

Plain .NET Framework 4 assembly, no WinForms dependency, shipped beside each app (side by side by version; the newest host wins, see 8).

```
GlassService   Connect() -> GlassService   (starts the host if needed; throws nothing: returns a service in "offline" state)
               Create(GlassSpec) -> GlassSurface
GlassSpec      Shape (card | lens | custom numbers), Look (GlassLook incl. Elevation), Profile (GlassProfile string, "" = Today),
               Scale (display scale), Size, Place
GlassSurface   SetContents(IntPtr pixels | byte[] , w, h, dirty)  premultiplied BGRA into the surface's section; latest wins
               SetPlace(rect[, animate]) / SetSize / SetOpacity / SetDeform(Deform) / SetMotion(GlassMotion)
               SetInteraction(hover, press, region)       the host follows the global pointer itself (see 5)
               SetLook(look, profile)                     rebuilds through the map cache
               Show(style, ms) / Hide(style, ms) / Dispose()
Events         Shown, Hidden, AppearanceChanged(light|dark), PolicyChanged(fps cap, motion on/off), Failed(code, text), ServiceRestarted
```

Every call returns immediately (it writes the surface's state block and, if the host is asleep, signals one event); none blocks on the
host. Events are raised on the creator's SynchronizationContext. `Failed` carries a code (bad spec, quota, device lost, backend
unavailable); a failed surface stays valid and silent, never throws on later calls.

## 3. Transport

Three pieces, each used for what it is good at:

1. Control channel (rare, small, versioned): a named pipe `\\.\pipe\LGEngine.<sid hash>.<protocol major>`, message mode, overlapped.
   - ACL: a DACL allowing only the current user's SID (plus SYSTEM); `FILE_FLAG_FIRST_PIPE_INSTANCE`, `PIPE_REJECT_REMOTE_CLIENTS`;
     the host checks `GetNamedPipeClientProcessId` is in the same session.
   - Frame: `magic 'LGE1' u32, version u16, type u16, length u32 (<= 64 KB), seq u32` + payload. Messages: Hello{min,max version,
     client name} / HelloAck{chosen version, engine version, backend}, Create, Destroy, SetLook, Resize (new contents section generation),
     Show, Hide, Stats, and the host's events back.
   - EVERY message is validated before use: length against the frame and the maximum, enums against their ranges, numbers against
     sane limits (sizes <= 4096 x 4096, scale 1..4, look sliders in range), strings UTF-8 <= 256 bytes, the profile string <= 2400
     characters and parsed with `GlassProfile.Parse` (anything invalid becomes Today). Unknown types get an error reply, not a crash.
     Per-client quotas: 64 surfaces, 256 MB of contents, 200 control messages per second.
2. Per-surface shared memory (hot, no syscalls): one named section `Local\LGEngine.S.<id>.<nonce>` created by the host, DACL current
   user, mapped by both sides.
   - State block (seqlock: the writer makes the counter odd, writes, makes it even; the reader retries until it sees the same even value
     twice, bounded): place, size, opacity, deform, motion, flags, `callStamp` (client QPC at the call, for latency accounting).
   - Contents: two premultiplied BGRA buffers (double-buffered) with a per-buffer sequence and a dirty rect; the client writes the back
     buffer and flips `front` with an interlocked exchange; the host reads the front one. As in HANDOFF 4.5: the new back buffer is missing
     the front's last dirty rect, so the client library re-copies it on the next publish (dirty forwarding). A resize is a new section
     generation announced on the control channel (rare). The host bounds-checks width, height and rect against the section size every
     frame; it never trusts a pointer, only offsets into a section it mapped itself.
   - GPU backend: contents are a DXGI shared texture (an NT handle duplicated into the client over the control channel) with a keyed
     mutex (the client acquires key 0, draws, releases key 1; the host acquires key 1 for its frame, releases key 0); the dirty rect stays
     in the state block.
3. Wake-ups (no polling anywhere): one auto-reset event per direction.
   - client -> host: one shared auto-reset event `HostWake` (all clients signal the same one; the host then scans the surfaces' sequence
     numbers, which is bounded by the surface count and costs nothing next to a frame);
   - host -> client: one auto-reset event per client `ClientWake`, signalled when the host has put an event on that client's ring (a
     small ring in the client's own section; the pipe is the fallback for large or rare ones). The client library's event thread waits on it.
   A signal is only sent when the other side is (or may be) asleep; while the host is animating it is already awake every vblank.

## 4. Latency budget

Target: a client call is on screen within one vblank plus about 1 ms, for the calls an animation or an OSD makes (place, opacity,
deform, motion, contents).

| stage | cost | note |
|---|---|---|
| client publishes state / contents | ~0.1 us / one write per dirty row | seqlock block, no syscall |
| wake the host | 2-5 us SetEvent, 50-100 us to run | only if it was asleep; 1 ms timer resolution requested while animating |
| coalesce | 0 | latest wins: the host reads the newest state and contents when it renders, never a queue |
| shade | motion-only frame: ~1 ms osd, ~8 ms card (CPU, @2); contents-only: 0.9 ms osd, 5.4 ms card (LGBench); GPU target < 1.5 ms | per-frame cost does not depend on the profile (HANDOFF 8) |
| present | ~0.3 ms (UpdateLayeredWindow, dirty rect) | on the render thread |
| scan-out | next vblank | DWM composes at vblank |

Scheduling rule: when woken, the host starts at once if `now + EMA(frame cost) < next vblank - 1 ms`; otherwise it waits for the vblank
event and starts then (one frame late at worst, and only for work that cannot fit before it). The EMA is per surface kind, so the host
wakes "cost + 1 ms" before the vblank while a surface animates. So the common case is: call -> same vblank; worst case one vblank +
the render cost of that frame. Measured, not assumed: every state block carries `callStamp`; the host records (present return time,
`DwmGetCompositionTimingInfo` qpcVBlank) per frame and publishes p50/p99 of call-to-vblank in the stats section (LGBench `-service`
already measures wake-ups and jitter in-process; the same rig is pointed at the host).

Hover and press motion (GlassMotion.Swell / Lean / press spring) is computed INSIDE the host from the global pointer: the client only
declares the interaction region and options (`SetInteraction`); no round trip per mouse move. The host polls `GetCursorPos` (and the
button state) once per vblank only while an interactive surface is visible and the pointer is near it or a spring is still settling;
with the pointer far away it checks at 4 Hz, and with no interactive surface visible it does not check at all (layered click-through
windows get no mouse messages, so this is the cheapest correct way; no global hook).

## 5. Idle, power, priority

- Zero wake-ups when idle: no surface visible = no timers, the render thread blocked in `MsgWaitForMultipleObjectsEx`, the feed stopped,
  the pointer check off. A visible but unchanging surface costs nothing either (no frames while the motion is neutral and nothing
  changed, as today).
- EcoQoS (`SetProcessInformation(ProcessPowerThrottling)`) on whenever nothing animates; off, with the render thread at AboveNormal, only
  while a frame is being driven (and back after ~500 ms of calm).
- Battery policy is centralised in the host: it reads the power state (battery, battery saver, `SavePower`-style preference sent by the
  client per surface) once and applies one policy to all surfaces (frame cap, motion neutral, backdrop update rate), then tells the
  clients (`PolicyChanged`). Clients may ask; the host decides. No app runs its own battery logic.

## 6. Failure model

- Client death or hang: the host holds each client's pipe and process handle (a registered wait on the handle); on break it destroys that
  client's surfaces (a 120 ms fade if they were visible), unmaps their sections and frees their quota. A hung client (stops publishing)
  just keeps its last picture; it cannot block the render thread (the host never waits on a client).
- Host crash: the client library sees the pipe break, marks every surface `Lost`, and reconnects with backoff (100 ms, 400 ms, 1 s,
  then every 2 s), starting the host again on demand. When it is back the library raises `ServiceRestarted`, re-creates every surface from the
  spec it retained, and republishes the last contents buffer and state, so the app need only repaint if it wants to. Nothing is left on
  screen from the dead host (its windows die with it); the guard restores whatever was hidden.
- Backend failure (device lost, duplication access lost): the host rebuilds the backend itself (CPU fallback), raises `Failed` /
  `PolicyChanged` only if the look changed.

## 7. Security summary

Current-user-only pipe and sections, same-session check, every message and every section field validated and bounds-checked, quotas,
no screen pixels leave the host, no code or paths from clients are ever loaded or opened (a profile is a string parsed by a
bounded parser, nothing else). A client can at worst draw its own glass wrongly.

## 8. Versioning

Protocol version negotiated in Hello; additive changes only inside a major version (new message types, new trailing fields with defaults);
the host supports the previous minor. The mutex, pipe and section names carry the protocol MAJOR. An app with a newer client than the
running host asks the old host to exit when idle (`ShutdownWhenIdle`) and starts its own copy from its own folder; the host executable is
copied to `%LOCALAPPDATA%\LGEngine\<version>\` on first use so an app's update never replaces a running host.

## 9. Migration: no flag day

1. Phase 0, today: the engine runs in the app's process (GlassOverlay / SearchGlass own their windows and timing).
2. Phase 1: put the SURFACE API of section 2 in front of it: `IGlassTransport` with an `InProcessTransport` (the render-thread host of
   HANDOFF 4.5, the thread, `IGlassBackend`, immutable `SurfaceProps`, double-buffered contents). The app moves to the API one piece at a
   time (SearchGlass first, as 4.5 says), behaviour unchanged. Contract tests drive the API, not the classes behind it.
3. Phase 2: `LGEngineHost.exe` = the same render-thread host in its own process; `PipeTransport` implements the same interface (control
   pipe + sections + events). `Connect()` prefers the host and falls back to the in-process transport (debugging, host unavailable). The
   same contract test suite runs against both transports, plus LGBench `-service` against the host (wake-ups, jitter, idle cost, latency
   histogram).
4. Phase 3: the D3D11 backend lives in the host (shared textures with keyed mutexes); CPU stays as the fallback.
5. The other app (OSD and the rest) is written against the client library from day one; it works the same in-process (phase 1) and
   through the host (phase 2).

### Why this replaces HANDOFF section 4 step 5's choice

Step 5 chose "an in-process render thread" over "a helper process" for ONE app, for three reasons: IPC, duplicated memory, and cross-process
HWND ownership, and "a helper cannot beat an in-process thread". The requirement changed: several apps will draw glass at once. In-process
would give each of them its own screen duplication, render thread, vblank pacing and (later) GPU device, and their presents would fight for the
same pixels; one shared host does each of those once. The three costs step 5 feared are designed out: windows live in the host (no
cross-process HWND), contents travel through one shared-memory write with dirty rects (the backdrop never leaves the host, so nothing is
duplicated), state costs no syscall (seqlock block), and the wake-up is one event. The step-5 work is not thrown away: it is exactly the
in-process transport of phase 1, and the host is that same render-thread host moved into its own process. In-process stays as the fallback.

## 10. Where GlassProfile and the side view sit

- `GlassProfile` is plain, immutable data with a string form (`top=...;bot=...;slab=...;fillet=...`). It crosses the boundary as that
  string in Create / SetLook (<= 2400 characters, parsed in the host with the bounded `GlassProfile.Parse`; invalid = Today). Its short hash
  is part of the map Signature, so surfaces from different apps with the same profile share one bake.
- `GlassProfileEditor` (WinForms) and `GlassSideView` (the cross-section and rays, from the engine's own ray trace) live in the CLIENT
  side: the editor hosts in any app, computes locally from the same engine code, and only the profile string goes to the host. The
  editor works with the host down. HelloAck carries the host's engine version; if it differs from the client's, an editor can say its
  rays are approximate.
- The stats (`EngineStats`) become a read-only shared section the host publishes (frame times, call-to-vblank p50/p99, wake-ups, backend),
  which the Engine page, or any dashboard, reads without a round trip.

## 11. To measure before building

Wake-up latency of an auto-reset event at AboveNormal under load; seqlock retry rate with a 60 Hz writer; the cost of a second
`ScreenFeed` tap versus per-surface duplication; call-to-vblank p99 for the OSD kind through a loopback pipe transport (build the loopback
in phase 1 and measure there before any process exists).

## 12. Build plan: the cut through GlassOverlay (2026-10-16)

What src\Glass.cs (GlassOverlay, 3.5k lines) mixes. Runtime (moves): the layered window, the WinForms Timer scheduling, the feed tap
with the peek / self-change / burst logic, the vis / slide / fade / morph / hover springs and GlassMotion, the map cache, Adapt
(appearance, even), Render / Present, EngineStats. App (stays): finding the pop-up and reading its contents (WindowReader, FindCard,
Key / MarkInk), the indicator memory, the slider and slosh, the glass-button glow, the taskbar's TaskbarGlass. Frame() (lines
1381-1897) interleaves both; the coupling to cut: card / contentW,H / readW,H (app -> runtime), lastContentChange / contentSoonAt (app
-> scheduler), the slider fields read by the peek's "level busy", slosh / morphAt (app -> deform), the preview fields, PaintGlow
painting onto the shaded output after Render, barStrip for row presents, and the give-up rules (Yielded) that read both.

LGBench -check and sweep7 never run GlassOverlay; a step that touches it is proven only by verify.ps1 (0 px, nothing else moved) plus
a silent lgtoast capture and the OSD shown through the app's own preview (never the keys). Each step ships alone, no visible change.

1. DONE 2026-10-16: the app uses the engine's ScreenFeed / Duplicator (its copies are in retired\src-2026-10-16); the layered window
   is the engine's GlassLayer (was GlassOverlay.LayerWindow).
2. FrameClock (engine) replaces each overlay's Timer: RequestIn(ms) (earliest wins, as `timer.Interval = min(...)` does today),
   Kick(), Stop(). First on the UI thread over the same Timer (identical timing); later vblank-paced on the render thread (section 4)
   behind the same API.
3. DONE 2026-10-16 (steps 1 and 3 shipped as proj12): GlassPeek (engine): the tap, the step out of screenshots, the self-change filter, bursts, looks and the GlassBackdrop, as one
   class (Frame 1666-1820, NoteOwnChange, DrawnGap, RingChanged, Off, SetCapt, EndBurst, OnFeedThread / OnFeedArrived, CloseTap).
   In per frame: the card on screen, the GlassLayer, the intervals, `busy` (today's level-busy, as a bool), live wanted. Out: the
   backdrop buffer, a changed flag, the counters.
4. 4a DONE 2026-10-16 (proj13): LGEngine\src\GlassPresenter.cs = out buffers, map cache (FitMap), Adapt, Render, Present,
   counters; hooks for the glow, warp and taskbar dirty rows. 4b DONE (proj14): LGEngine\src\GlassAnimator.cs = Animate /
   SetVisible / MotionNow / hover / press / vis / slide / Stretch / ApplyMotion. Steps 5 and 7 DONE (proj15, 21:09).
   Target, GlassSurfaceCore (engine, internal): map cache (SignatureFor / Make / spare / primed), Adapt, Animate, Render / Present (dirty
   rect, row strip), EngineStats. In: immutable SurfaceProps {place, card size, scale, opacity target, deform (slosh and morph arrive
   as a Deform), motion, look, shape + profile, interaction} + contents (premultiplied, dirty rect) + an OVERLAY plane composited
   after shading (the glow: replaces PaintGlow's write into outDib) + a row-strip hint (knob-only frames). Largest step.
5. DONE 2026-10-16 (proj15, with step 7; two Opus reviews' 19 findings fixed). Design (files flat in LGEngine\src; the build
   globs src\*.cs only):
   - GlassService becomes a sealed class: static Connect(GlassConnectOptions) (Mode Auto | InProcess | Host; phase 1 always
     in-process), Create(GlassSpec) -> GlassSurface, Dispose; static SelfTest() kept for LGBench.
   - GlassSpec: Kind (Card | Lens), Look, Profile string, Scale, Size (card px), Place (card top-left on screen), InShots,
     SavePower, Fps, Adapt, Label (window title: test windows are labelled).
   - GlassSurface: SetContents(int[] premultiplied, w, h, dirty) copied into a double buffer (latest wins), SetPlace(Point,
     animate), SetSize, SetOpacity, SetDeform, SetMotion, SetInteraction(hover, region), SetLook(look, profile), Show, Hide,
     Dispose. Events Shown, Hidden, AppearanceChanged(light), Failed(code, text); a failed surface never throws again. Every
     call stamps Stopwatch ticks (callStamp) into an immutable SurfaceProps copy, then wakes the transport.
   - IGlassTransport { Open(spec, events) -> surface channel; Wake; Name }. InProcessTransport: one SurfaceRuntime per surface
     on the creator's UI thread, one WinForms Timer per transport (step 7 swaps in the render thread behind the same interface).
   - SurfaceRuntime (internal) = GlassLayer + GlassPeek + GlassPresenter + GlassAnimator. Frame: latest props + contents ->
     FitMap -> animate -> peek.Step -> Adapt -> Render -> Present -> latency sample -> schedule (peek.Want; stop when still).
   - Latency: per surface a ring of 256 samples of (present return - callStamp) and (next vblank from
     DwmGetCompositionTimingInfo - callStamp); p50 / p99 in EngineStats and on the surface.
   - LGBench `-contract`: a labelled surface placed off the desktop (x = -10000, silent), Show -> Shown, a burst of 200
     SetPlace / SetOpacity / SetContents calls, latency p50 / p99 printed, Hide -> Hidden, Dispose; invalid input (null or
     oversized contents, dirty out of range, garbage profile, calls after Dispose) -> Failed, never an exception.
   The public API of section 2 over IGlassTransport, InProcessTransport = steps 2-4 on the UI thread; events Ready / Failed / Yielded
   as today. LGBench `-contract` drives only the API.
6. Move the users: SearchGlass first (own timer, present and ScreenGrab; simplest), then the OSD, then notifications, then the taskbar
   (TaskbarGlass becomes a surface kind "bar" whose renderer runs in the engine; last, it is the largest).
7. The render thread: InProcessTransport runs 2-4 on one engine thread (HANDOFF 4.5), vblank-paced, windows created there. Measure
   section 11 with a loopback transport. DONE 2026-10-16 (proj15): GlassRenderThread.cs (STA, MsgWaitForMultipleObjectsEx + a
   high-resolution waitable timer, zero wakeups idle); client-woken frames run at once, animation frames (NextMs <= 20 ms) snap to
   the nearest vblank, peek gaps never earlier than asked; GlassMode.UiThread keeps the step-5 path for comparison. Step 2
   (FrameClock) folded into it. Measured (LGBench -contract, 60 Hz): queue 0.2 / 2.2 ms, present 1.7 / 3.7 ms, call -> next
   vblank 9.9 / 18.6 ms (p50 / p99); -service ~60 frames/s, no gap > 22 ms, idle wakeups 0.
Phase 2 after 7: LGEngineHost.exe + LGEngine.Client.dll + PipeTransport as sections 1-8. The host is a second exe, so ship8's Smart
App Control test-run must cover it too (extend ship8 then). Guessed size: steps 2 and 3 one session each, 4 two, 5-6 one or two, 7
one, phase 2 two or three.
