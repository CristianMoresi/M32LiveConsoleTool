# M32 Live Console Tool

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![CI](https://github.com/CristianMoresi/M32LiveConsoleTool/actions/workflows/ci.yml/badge.svg)](https://github.com/CristianMoresi/M32LiveConsoleTool/actions/workflows/ci.yml)
[![Release](https://github.com/CristianMoresi/M32LiveConsoleTool/actions/workflows/release.yml/badge.svg)](https://github.com/CristianMoresi/M32LiveConsoleTool/actions/workflows/release.yml)

M32 Live Console Tool is a **mixing-automation** tool for the **Midas M32 /
Behringer X32**. It is not a remote control or a backup utility — it runs three
automations on the live mix:

- **Auto-leveling** — keeps every channel and bus at a target level you set.
- **Auto anti-clipping** — when a channel clips, it automatically pulls the gain down.
- **Auto delays** — keeps the effect delays automatically synced to the tempo.

It talks to the console over OSC/UDP and runs against the real hardware or a
built-in simulator.

> **Note** — M32 and X32 are trademarks of Music Tribe. This is an independent,
> unofficial tool and is not affiliated with or endorsed by Music Tribe.

## What it does

### Auto-leveling

A target-based automixer for the whole desk. You give each watched channel or bus a
target level; the app compares its moving average against that target and moves the
fader to hold it there — if the level drifts above the target it lowers the fader, if
it drifts below it raises it. The correction speed is configurable, with separate up
and down rates, and the moves are slew-rate-limited so they are gradual rather than
sudden. Each watched channel and bus can opt into leveling independently and has its own maximum fader level.

It does nothing while a channel is muted or below a silence gate, ignores large drops
(an intentional cut, not drift), and stands down as soon as the engineer moves that
fader, resuming only once the fader has settled — and, if the move happened during a
pause, only once there is real signal again, so it never mistakes silence for the new
target level.

### Auto anti-clipping

An anti-clip safety net. It watches each channel's post-trim level and, once it
confirms the channel is clipping (over the threshold for a few consecutive samples),
it reduces the gain at the source feeding that channel — the local head amp or the
AES50 stage box — so the channel stops clipping. The reduction is larger the further
the peak went over the threshold, and is applied on the console's own gain grid
(0.5 dB head amp steps, 0.25 dB trim steps). It only attenuates; it never raises the
gain back on its own. It waits for confirmed routing, gain and mute state, follows
digital trim as well as analog head amps, and shares gain state and cooldown across
channels fed by the same physical preamp. A configurable accumulated-reduction limit
(default 24 dB) bounds each resource; the strictest limit of its watched channels wins.
The reduction counter starts again when the session is resynchronized or the routing,
connection or device configuration is reset; it is not a lifetime attenuation budget.
If the gain is already at its physical minimum and the channel
still clips, it raises a single clear warning — manual action required — instead of
logging reductions that no longer reduce anything.

### Auto delays (tempo-synced)

It detects the tempo (from a click track, a percussive channel, or manual tap tempo)
and sets the console's delay slots to a chosen rhythmic figure — a dotted eighth, for
example — recomputing the time whenever the tempo changes. It first asks the console
which effect each FX slot holds, so it writes the time to that effect's correct
parameter. Manual slots keep their configured fixed time. Unknown, unsupported or
stale effect identities suspend writes. Automatic times require tempo evidence from
the last four seconds; old tempos remain visible but are not used to configure a new
slot. Effective times are deduplicated, and unconfirmed writes are retried.

### Supporting pieces

Around the three features above: a built-in **M32 simulator** to run and develop with
no hardware; a color terminal **dashboard** that shows the live state and lets you
edit every parameter on the fly; a **panic key** that hands full manual control back
at once; **automatic re-subscription** if the console link drops; and session
**persistence** with named presets.

## How it works

The app speaks OSC over two UDP ports (10023 to the console, 10024 back). It
subscribes to the console's meter bundles, decodes the binary blobs, and feeds a
short rolling history per device. Three independent modules act on that history:

- The **AutoLeveler** compares each device's moving average to its target and
  writes small, slew-limited fader corrections.
- The **AntiClipper** reacts to confirmed peaks on a channel's post-trim and
  attenuates its preamp.
- The **BpmDetector** infers tempo from transients and drives the
  **DelayController**, which updates the console's effects-delay slots. The
  DelayController queries each FX slot's effect type (`/fx/N/type`) so it writes
  the time to the correct parameter, and can be re-queried live when the engineer
  swaps an effect mid-show.

To tell its own corrections apart from manual moves, the app subscribes to the
console's `/xremote` parameter feedback and seeds each fader's real position and
mute state at startup. When the engineer moves a fader, the leveler detects it and
stands down for a stabilization window before re-capturing the new target; a muted
channel is left untouched by both the leveler and the clip protection.

### Architecture

```
Osc/             OSC wire protocol (OscMessage), UDP client, meter map, simulator
Domain/          AudioDevice (abstract) -> MixerChannel / Bus, MeterSample, descriptors
Modules/         MeterReader, AutoLeveler, AntiClipper, BpmDetector, DelayController
Infrastructure/  FaderMath, SessionConfig, ShowLogger, AppPaths, enums
Ui/              AppOrchestrator (wiring), ConsoleDashboard, ConsoleMenu, FirstRunWizard
Program.cs       Entry point and ordered startup/shutdown
```

The domain layer never touches the network: modules own the OSC client and decide
when to send. Concurrency uses a copy-on-write pattern for the hot device lists
and a bounded history with sequence numbers. Averages read the retained samples
without allocating a history copy. Clip and transient detection consume every new
sample, including peaks that arrive between worker polls.

Observed state and outgoing control decisions share a short critical section in the
OSC transport. This makes panic and connection changes atomic with respect to writes.
Changing peer or receiving routing/scene notifications invalidates observed state;
periodic queries recover lost replies. Only the selected IPv4 address and source port
can supply feedback. Queries continue while control is released. Disk logging uses a
bounded background queue so file I/O does not hold the control or dashboard locks.

## Download

Grab a ready-to-run executable for your platform from the
[latest release](https://github.com/CristianMoresi/M32LiveConsoleTool/releases/latest)
— a single file with the .NET runtime embedded, so there is nothing to install and
no dependencies.

| Platform | File |
| --- | --- |
| Windows · x64 | `…-win-x64.zip` |
| Linux · x64 | `…-linux-x64.tar.gz` |
| macOS · Apple Silicon | `…-osx-arm64.tar.gz` |
| macOS · Intel | `…-osx-x64.tar.gz` |

Unzip it (Windows) or extract it with `tar -xzf` (Linux/macOS) and run
`M32LiveConsoleTool` from a terminal — it is a console application, not a windowed
one. The binaries are not code-signed, so the first launch needs one extra step:

- **Windows** — SmartScreen may ask you to confirm via **More info → Run anyway**.
- **macOS** — clear Gatekeeper's quarantine flag with
  `xattr -d com.apple.quarantine M32LiveConsoleTool`.

Each release also ships a `SHA256SUMS.txt` you can check your download against.

## Requirements

- To run a release binary: nothing — the .NET runtime is embedded in the
  executable. Download, extract and run.
- To build from source: the [.NET 10 SDK](https://dotnet.microsoft.com/).
- For real hardware: an M32/X32 reachable on the local network, with OSC enabled
  on the standard ports (10023 send, 10024 receive), using an IPv4 literal.
- Works on Windows, macOS and Linux. On Windows 10+ the dashboard enables ANSI
  virtual-terminal processing automatically.

## Build and run

```bash
git clone https://github.com/CristianMoresi/M32LiveConsoleTool.git
cd M32LiveConsoleTool
dotnet run -c Release
```

On first launch a short wizard asks for the mixer IP, the OSC ports, and which
channels and buses to watch. At every startup the app asks whether to run in
**simulation mode** (no hardware) or against the real console.

## Simulation mode

Answer `Y` to the simulation prompt and the app points its OSC client at a local
`M32Simulator` instead of a real console. On a first run it skips the setup wizard
and loads a small demo session (a few channels and buses), so the dashboard comes
up populated right away. The simulator answers subscriptions, streams synthetic
meters, and models a common input → gain/trim → fader signal chain. Gain reductions
therefore affect both post-trim and post-fader meters. Routing, shared head amps,
digital trim, mute, gain quantization, FX parameter storage and subscription expiry
are modeled. Tests can use a fixed input and drop selected replies deliberately.
The interactive demo adds periodic clip excursions. Gates and compressors remain
outside the simulator model.

## Using the dashboard

The screen shows a live dashboard with a one-line menu at the bottom:

```
M32 Live Console Tool   [SIMULATION]
  Time: 21:45:03   Up: 00:42:15   Mixer: 127.0.0.1 (simulated)  [LIVE]
  AutoLeveler [ON]   Anti-Clip [ON]  BpmDetector [ON]    BPM: 128.0

Watched channels and buses:
  # Typ Name           LvlAvg Target Preamp Src   M  Level
  1 Ch  Lead Vocal      -18.2  -18.0   0.0  HA0      [########|        ]
  2 Ch  Kick            -15.0  -15.0   0.0  HA1      [##########|      ]
  3 Bus Drums           -16.1  -16.0   ---  ---      [#########|       ]

Recent events:
  [21:44:58] [WRN] AntiClipper|Kick|reduction|0.0dB|-3.0dB|peak=+1.2dBFS|...

Configured delay slots:
  Slot  Effect              Mode        Figure / ms
   1    Stereo Delay        Automatic   DottedEighth          375ms
   2    3-Tap Delay         Manual      Manual                 80ms

[A]uto-level Anti-[C]lip [B]PM [D]elay [X]clear-slot [F]x-refresh [T]ap [R]ecalibrate
[N]channels [P]references [S]ave [L]oad [0]release-all [F12]PANIC [Q]uit
```

The menu legend at the bottom wraps to as many rows as the window width needs, so it
is never truncated, and the whole screen — including the menu — re-flows cleanly when
the terminal is resized. If the window is shrunk below a usable size, a clear
"terminal too small" notice is shown (with the minimum and current size) until it is
enlarged again, instead of a broken layout.

Rows are colored by what the tool is doing to each channel (amber = manual override,
cyan = auto-leveling, red = clip protection just acted) and the level bar by how
close it is to clipping (green/amber/red). Press the bracketed key to toggle a
module, configure a delay slot, re-detect the effects loaded in the FX slots, tap
tempo, recalibrate targets, manage channels or save/load presets. **F12 releases
control from every submenu, text field, notice and timed capture**, and cancels the
current form. `[0]` remains a shortcut on the main menu. Enabling a module explicitly
rearms it; re-enabling only the leveler does not rearm delays after panic. Each module's submenu also edits its parameters live
(the leveler's up/down speed has presets), with no need to touch the config file.
Use `[N]` → `[E]` to edit a device's target, leveling/protection participation,
maximum fader and gain-reduction budget; the screen lists channels sharing its gain.
The header shows `LIVE`, `SYNC`, `NO SIGNAL` or `RELEASED`; `?` in a row means state
is still unknown, `S` means stale meters and `-` means leveling is excluded.
Preferences → `[V]` shows traffic, rejected packets, failed sends and log drops.

The BPM submenu includes a figure-times table — every rhythmic figure converted to
milliseconds at the current tempo, with values beyond the console's 3000 ms cap
marked. The channel marked `*` is the one the BPM detector reads its tempo from.
Every input flow can be cancelled with `Esc` and every notice is dismissed with a
key press, at your own pace. The session is saved automatically on exit (including
Ctrl+C and window close). A failed save is reported as a failure, without announcing
that the session was saved.

## Persistence

Files live in the standard per-user application-data folder
(`%APPDATA%\M32LiveConsoleTool\` on Windows,
`~/Library/Application Support/M32LiveConsoleTool/` on macOS,
`~/.config/M32LiveConsoleTool/` on Linux):

- `last_session.json` — the last session (including recalibrated targets),
  restored on launch.
- `presets/` — named configurations.
- `logs/` — one dated event-log file per run (e.g. `2026-05-29_21-45-03.log`);
  the most recent 50 are kept and older ones are pruned automatically.

Presets show a summary of changes before application. They apply their exact module
flags, meter window, device options and delay slots while preserving the venue's
network settings. Files are written to a temporary sibling, flushed and replaced
atomically. Null collections, invalid entries and non-finite parameters are repaired
or rejected before affecting runtime state. Simulation never replaces the saved real
session.

## Testing

```bash
dotnet test
```

The xUnit suite covers the OSC encode/decode round-trip (including malformed-blob
rejection), the meter-blob parser's corrupt-packet guards, the fader/dB/beat math,
the power-domain level average, the delay-slot logic, the FX effect catalog and
delay-type resolution, the mixer-IP validation, fader-feedback and mute handling,
tap-tempo BPM detection and freshness tracking, the domain value objects, module
parameter validation, the repair of corrupt or hand-edited configurations
(out-of-range values, invalid delay slots, bad IP), the leveler's silence-safe
target re-capture after a manual move, the session/preset persistence round-trip,
the preset-name validation, and an end-to-end test of the control loop and effect
detection against the simulator.

Every pull request and branch push runs the suite on Windows, Linux and macOS.
Release builds wait for the same matrix. CI retains TRX and coverage artifacts and
smoke-tests a native self-contained executable on each runner. `global.json` stays
on stable .NET 10 feature bands; dependency and bundled-runtime updates should run
this matrix before release.

The current source also includes a non-interactive, loopback-only diagnostic:

```bash
dotnet run -c Release -- --self-test       # 48 devices, synchronization and panic check
dotnet run -c Release -- --self-test 1800  # 30-minute load profile, JSON metrics
```

A portable executable built from this source accepts the same arguments. The command
returns a nonzero exit code when synchronization or panic validation fails. It does
not load your session or contact a physical console. The audit's matched microbenchmark
is available with `dotnet run --project tests/audit-2026-10-16/AuditProbe.csproj -c Release -- --benchmark`.

See the [original audit](output/auditoria-m32-2026-10-16.markdown) and
[correction record](output/correcciones-m32-2026-10-16.markdown) for findings,
regressions, measurements and remaining hardware checks.

## Known limitations

- Clip protection reacts to metering and OSC feedback; it is not an audio limiter
  and cannot prevent every transient before it occurs.
- Loopback tests do not validate a particular M32/X32 firmware, AES50 stage box,
  DCA/mute group or stereo-link configuration. Those combinations still need a
  supervised hardware session and captured OSC traces.
- OSC does not carry command acknowledgements with unique IDs. Feedback matching
  uses bounded history and state queries; a physical move identical to a recent
  command cannot always be distinguished from its echo.
- The simulator does not model gates, compressors, DCA/mute groups or every routing
  feature of a physical desk. Profiling excludes dashboard rendering and disk logging.

## License

[MIT](LICENSE) © [Cristian Moresi](https://cristianmoresi.com)

## Acknowledgements

The OSC command set was reverse-engineered by the community; the
[unofficial X32/M32 OSC protocol documentation by Patrick-Gilles Maillot](https://github.com/pmaillot/X32-Behringer)
is the reference behind the addresses and meter bundles used here.
