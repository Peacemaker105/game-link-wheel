# Game Link Wheel

Force feedback and wheel controls for **STAR WARS: Galactic Racer** on any DirectInput force-feedback wheel.

It is a fork of [SWGR Wheel Bridge](https://github.com/CCappsDevelopment/galactic-racer-moza-wheel-bridge) 0.1 by
CCappsDevelopment (MIT), which did this for MOZA bases through MOZA's SDK. This fork reads the wheel, pedals and buttons
through DirectInput instead, so it works with any wheelbase that Windows sees as a force-feedback game controller. The
force model, the virtual pad and the recorded laps are the bridge's work.

> **0.1.0 is a preview. It has not driven a real wheel yet.** It is tested offline: 50 tests, four recorded laps.
> On a first run, set a low Master strength, keep your hands clear of the wheel for the motor test, and keep the
> **KILL FFB** button within reach.

## What it does

- **Controls.** It reads every attached DirectInput controller: wheel, pedals, shifter, button box. The game has no
  wheel support, so Game Link Wheel drives it through a virtual Xbox 360 pad (ViGEmBus). Setup walks you through it:
  pick the force-feedback base, enter its rated torque and its rotation, run the motor test, then press each pedal and
  each button you want.
- **Force feedback.** It works out the forces from the game's telemetry, in layers: centring with speed, aligning
  torque from slip, cornering load, damping, collisions by side, scrapes, road texture, landings, engine and boost
  buzz, and the game's own rumble. The forces are tuned per vehicle class (land speeder, speeder bike, skim speeder,
  podracer), and the four profiles are built into the exe.

## What it needs

- Windows 10 or 11, 64-bit.
- [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases/latest), for the virtual pad. Its author retired it in
  2023; the last release still installs and works.
- A force-feedback wheel that works in other games.
- The game's telemetry. That comes from VN Motion's UE4SS mod for Galactic Racer, which sends one VNSG frame per game
  frame. VN Motion's Game Link forwards each frame over UDP to `127.0.0.1:4127`. The frame's layout is in `src/Vnsg.cs`.
  [VN Motion](https://vn-motion.com)'s dashboard installs the mod and starts both programs.

## With the VN Motion dashboard

In the STAR WARS Galactic Racer panel, tick **Wheel force feedback** and press **Install**. The dashboard downloads
`GameLinkWheel-<version>.exe` from this repository's release. It keeps the copy only if its SHA-256 matches the one the
dashboard pins, checks it again before every start, and starts it with motion.

The dashboard keeps a link to Game Link Wheel on UDP `127.0.0.1:4128`:

- a heartbeat every 0.5 s. Without one for 1.5 s, Game Link Wheel holds the force at zero.
- `estop` when motion stops or the rig's E-stop chain opens. The force fades to zero within 100 ms.

## On its own

```
GameLinkWheel.exe                  the window (Setup opens on the first run)
GameLinkWheel.exe --console        the text dashboard
  --port <n>          telemetry UDP port (4127)
  --control-port <n>  the control link (4128): estop, resume, hb, status
  --dashboard         a heartbeat on the control link is required; without one the force is held at zero
  --parent <pid>      end when that process ends
GameLinkWheel.exe --list-devices | --replay <recording.tsv[.gz]> [profile.json] | --ffb-selftest | --idle-probe | --version
```

Settings, profiles and recordings are kept in `%APPDATA%\vn-motion-dashboard\game-link-wheel`. Set
`VNM_GAMELINK_WHEEL_HOME` to keep them somewhere else; the tests do.

## Safety

- **No force before two steps in Setup:** the motor test has found the motor's direction, and you have entered the
  wheel's rotation (lock to lock).
- **Strength from the base's rated torque.** At Master 1.00 the force peaks at about 3.9 Nm × 0.85 on any base: the
  profiles were tuned on a 3.9 Nm wheel. A base whose torque is not entered is taken as 25 Nm, so it is driven weakest.
- **The motor test pushes about 0.47 Nm** on any base whose torque is entered.
- **An `estop` on the control link** fades every force to zero within 100 ms, including the motor test and force centre.
  Nothing on the link can raise the force.
- **Picking another base** resets the motor direction and the strength.

## Build and test

You need the .NET 10 SDK (10.0.404 built the 0.1.0 release).

```
dotnet run -c Release --project tests              the offline suite: "50 passed, 0 failed"
dotnet publish src/GameLinkWheel.csproj -c Release -r win-x64 --self-contained true -o out
```

The suite needs no wheel, game, ViGEmBus or dashboard. It replays the four laps in `tests/recordings`.

The release exe is meant to be rebuilt byte for byte from this source with the same SDK:

- the csproj maps the source folder to `/_/` and turns Source Link off;
- `.gitattributes` pins CRLF line endings, since the exe carries each source file's SHA-256 and the profiles' bytes.

Compare your build's SHA-256 with the one on the release page.

## Licence and credit

MIT: see `LICENSE`, the bridge's notice, kept unchanged. This fork's changes are released under the same licence. The
repository's first commit is the bridge's code exactly as published (`fb3e573a1c`), so `git diff` against it shows every
change. Comments that cite `changes/NNNN` refer to VN Motion's internal change notes.
