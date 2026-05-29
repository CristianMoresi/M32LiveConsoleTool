# M32 Live Console Tool

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)

M32 Live Console Tool is a **mixing-automation** tool for the **Midas M32 /
Behringer X32**. It is not a remote control or a backup utility: it actively works
the mix in real time and does three things — an **automatic level-mixer** that holds
every channel and bus to a target level you set, **automatic clip protection**, and
**tempo-synced effect delays**. It talks to the console over OSC/UDP, and runs
against the real hardware or a built-in simulator.

> **Note** — M32 and X32 are trademarks of Music Tribe. This is an independent,
> unofficial tool and is not affiliated with or endorsed by Music Tribe.

## What it does

### Auto-leveling

A target-based automixer for the whole desk. You give each watched channel or bus a
target level; the app compares its moving average against that target and moves the
fader to hold it there — if the level drifts above the target it lowers the fader, if
it drifts below it raises it. The correction speed is configurable, with separate up
and down rates, and the moves are slew-rate-limited so they are gradual rather than
sudden. It runs on all watched channels and buses at once.

It does nothing while a channel is muted or below a silence gate, ignores large drops
(an intentional cut, not drift), and stands down as soon as the engineer moves that
fader, resuming only once the fader has settled.

### Clip protection

An anti-clip safety net. It watches each channel's post-trim level and, once it
confirms the channel is clipping (over the threshold for a few consecutive samples),
it reduces the gain at the source feeding that channel — the local head amp or the
AES50 stage box — so the channel stops clipping. The reduction is larger the further
the peak went over the threshold. It only attenuates; it never raises the gain back
on its own.

### Tempo-synced delays

It detects the tempo (from a click track, a percussive channel, or manual tap tempo)
and sets the console's delay slots to a chosen rhythmic figure — a dotted eighth, for
example — recomputing the time whenever the tempo changes. It first asks the console
which effect each FX slot holds, so it writes the time to that effect's correct
parameter, and it leaves slots set to a fixed manual time untouched.

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
and lock-guarded snapshots for the meter history, so the OSC receive thread and
the module worker threads never block each other on the critical path.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/) (or the .NET 10 runtime to run a
  published build).
- For real hardware: an M32/X32 reachable on the local network, with OSC enabled
  on the standard ports (10023 send, 10024 receive).
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
meters, and reflects fader writes back into its meters — so you can watch the
AutoLeveler converge in real time. It also models the channel head amp gain and
injects a short clip excursion into the post-trim every few seconds, so the clip
protection engages and you can watch its reductions tame the level. It still does
not model the gate or compressor.

## Using the dashboard

The screen shows a live dashboard with a one-line menu at the bottom:

```
M32 Live Console Tool   [SIMULATION]
  Time: 21:45:03   Up: 00:42:15   Mixer: 127.0.0.1 (simulated)  [receiving meters]
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
[N]channels [P]references [S]ave [L]oad [0]release-all [Q]uit
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
tempo, recalibrate targets, manage channels, save/load presets, or `[0]` to release
all automatic control at once. Each module's submenu also edits its parameters live
(the leveler's up/down speed has presets), with no need to touch the config file.
The channel marked `*` is the one the BPM detector reads its tempo from. Every input
flow can be cancelled with `Esc`. The session is saved automatically on exit
(including Ctrl+C and window close).

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

## Testing

```bash
dotnet test
```

The xUnit suite covers the OSC encode/decode round-trip (including malformed-blob
rejection), the fader/dB/beat math, the power-domain level average, the delay-slot
logic, the FX effect catalog and delay-type resolution, the mixer-IP validation,
fader-feedback and mute handling, tap-tempo BPM detection and freshness tracking,
the domain value objects, module parameter validation, the repair of out-of-range
or duplicated loaded configurations, the preset-name validation, and an end-to-end
test of the control loop and effect detection against the simulator.

## Known limitations

- The simulator models the fader, the head amp gain and a periodic clip excursion
  (enough to exercise leveling, clip protection and the UI), but not the gate or
  compressor, so those two are not exercised in simulation.

## License

[MIT](LICENSE) © Cristian Moresi

## Acknowledgements

The OSC command set was reverse-engineered by the community; the
[unofficial X32/M32 OSC protocol documentation by Patrick-Gilles Maillot](https://github.com/pmaillot/X32-Behringer)
is the reference behind the addresses and meter bundles used here.
