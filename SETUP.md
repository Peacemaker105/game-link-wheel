# Setup guide: STAR WARS Galactic Racer with VN Motion and Game Link Wheel

You set this up in two halves, in this order: motion first (UE4SS and VN Motion's mod), then wheel force feedback (Game
Link Wheel). The wheel needs the motion half working.

The game has no telemetry of its own. UE4SS, a mod loader for Unreal games, lets VN Motion's small mod read your vehicle
every frame. The mod hands each frame to **Game Link**, a helper the VN Motion dashboard starts with motion. Game Link
passes it to the dashboard, which moves the rig, and, with wheel force feedback ticked, to **Game Link Wheel**, which
drives your wheel's motor and the game's controls.

```
Galactic Racer + UE4SS + VN Motion's mod ──(named pipe)──▶ Game Link ──(UDP 4126)──▶ VN Motion dashboard ──▶ the rig
                                                               └──(UDP 4127)──▶ Game Link Wheel ──▶ the wheelbase
                       ◀──────────(virtual Xbox pad, ViGEmBus)──────────────────────┘
VN Motion dashboard ──(UDP 4128: heartbeat, estop, quit)──▶ Game Link Wheel
```

## What you need

- **A VN Motion dashboard with Game Link** (1.2.8 or later).
- **STAR WARS Galactic Racer** on the PC that runs the rig.
- **A Nexus Mods account** (the free one is enough), to download UE4SS.
- For the wheel: **a force-feedback wheel that works in other games**, its base's **rated torque** in Nm (from its
  maker's specifications), and its **rotation, lock to lock** (from its own software).
- For the wheel: **ViGEmBus**, the virtual Xbox pad driver (Part 4).

> **Game Link Wheel is a preview: it has not driven a real wheel yet.** On its first run, set a low Master strength,
> keep your hands clear of the wheel for the motor test, and keep its **KILL FFB** button within reach.

## Part 1: Set up UE4SS

Galactic Racer needs its own build of UE4SS:
[UE4SS Galactic Racer Edition](https://www.nexusmods.com/starwarsgalacticracer/mods/2) on Nexus Mods. It is the UE4SS
loader plus this game's signature and settings files.

1. **Close the game.**
2. **Download the zip.** Open the UE4SS Galactic Racer Edition page, log in, open **Files** and download its file.
3. **Find the game's `Win64` folder**, the one with `SWGR-Win64-Shipping.exe` in it. In Steam, right-click the game,
   choose **Manage → Browse local files**, then open `Griffin\Binaries\Win64`. In Steam's default library it is:
   `C:\Program Files (x86)\Steam\steamapps\common\STAR WARS Galactic Racer\Griffin\Binaries\Win64`
4. **Extract everything in the zip into that `Win64` folder**, as it comes. Next to `SWGR-Win64-Shipping.exe` you now
   have `dwmapi.dll` and a `ue4ss` folder, with a `Mods` folder inside it.
5. **Start the game once to its main menu, then close it.** UE4SS writes its log, `ue4ss\UE4SS.log`, as the game
   starts: if that file is there, UE4SS ran.

- **If the game hangs as it starts,** Windows is probably stopping the game from writing into the `ue4ss` folder.
  Right-click `ue4ss`, choose **Properties → Security → Edit**, select your user and tick **Allow: Modify**.
- **After a game update,** UE4SS may need its matching new version before the mod works again. Close the game, extract
  the new zip over the same `Win64` folder and choose **Replace**. Never delete the `ue4ss` folder: your mods live in it.
- **Single-player only.** VN Motion's mod only reads the game, but keep UE4SS out of the game's online races.

## Part 2: Install VN Motion's mod

The dashboard's Install box puts the mod in: it copies the `VNMGalacticRacer` folder into `ue4ss\Mods` and adds the
line `VNMGalacticRacer : 1` to `ue4ss\Mods\mods.txt`.

1. **Close the game.** The box won't install while it runs.
2. In the dashboard, select the **STAR WARS Galactic Racer** tile.
3. Make sure **Use UEVR telemetry (VR)** is unticked.
4. The Install box finds the game in Steam by itself. If it doesn't, press **Locate the game…** and choose the `Win64`
   folder from Part 1.
5. If the box shows **Get UE4SS for Galactic Racer** instead of Install, it hasn't found `ue4ss\Mods`: finish Part 1,
   then press **Check again**.
6. Press **Install**.
7. Start the game to its main menu and open `ue4ss\UE4SS.log`. It should say `[VNMGalacticRacer] loaded`.

## Part 3: Motion

1. Select the **STAR WARS Galactic Racer** tile and **start motion**. Game Link starts and waits for the game; the rig
   stays at neutral.
2. **Start the game** and race, single-player.

Motion eases in when you take control of a vehicle. It eases out in menus, pause, cinematics, on foot, after the finish,
and while the vehicle is upside down or nose straight up or down. The **Game Link UDP port** in the panel is 4126:
change it only if another program holds that port.

## Part 4: Install ViGEmBus and Game Link Wheel

1. **Install ViGEmBus.** Download `ViGEmBus_1.22.0_x64_x86_arm64.exe` from
   [its releases page](https://github.com/nefarius/ViGEmBus/releases/latest) and run it. Restart Windows if it asks.
2. **Stop motion.** Game Link Wheel installs only while motion is stopped.
3. In the Galactic Racer panel, under **Wheel force feedback**, tick **Use my wheel, with force feedback (Game Link
   Wheel)**.
4. Press **Install**. The dashboard downloads Game Link Wheel from this repository's release and keeps it only if its
   SHA-256 matches the one the dashboard pins. It checks the copy again before every start.

The copy goes in `%LOCALAPPDATA%\vn-motion-dashboard\game-link-wheel`. Game Link Wheel's settings, profiles and
recordings live in `%APPDATA%\vn-motion-dashboard\game-link-wheel`.

## Part 5: Set up the wheel (first time only)

Do the wheel's setup with the rig still: open Game Link Wheel on its own, with motion stopped. Its settings are the same
ones the dashboard's copy uses later.

**In the wheelbase's own software**, note the wheel's **rotation, lock to lock**, and find the base's **rated torque**.
That software's own force strength also scales what Game Link Wheel sends.

**Open Game Link Wheel:** paste `%LOCALAPPDATA%\vn-motion-dashboard\game-link-wheel` into File Explorer's address bar and
double-click `GameLinkWheel-<version>.exe`. On the first run the **Game Link Wheel setup** window opens by itself:

1. **1 of 4: Your wheel.** Pick your base under **Force feedback wheelbase** (**Look again** if it isn't listed). Enter
   its **Rated torque (Nm)**; if you're not sure, leave 0 and the force starts weak. Enter the **Rotation, lock to lock
   (°)**. Click **Detect steering** and turn the wheel to the right, about a quarter turn.
2. **2 of 4: Motor test.** Take your hands off the wheel and click **Run motor test**. The wheel turns a little to one
   side for a moment. Until this test has run, it sends no force at all.
3. **3 of 4: Pedals.** Press each pedal all the way down and let it go, as it asks. No pedals: **No pedals**.
4. **4 of 4: Controls.** For each game function it names, press the button you want for it, on the wheel or any
   controller. **Skip** keeps what is there. If no pedal drives a trigger (RT, LT), bind a paddle to it.
5. **All set:** check the summary and click **Finish**.

**Then set the strength for the first drive.** On the **Force feedback** tab, set **Master** to 0.30. Master 1.00 peaks at
about 3.3 Nm on any base whose torque you entered (3.9 Nm × 0.85), so 0.30 is about 1 Nm. Raise it in small steps later.

**Know where KILL FFB is** before you drive: the button at the top of the window. Keep your hands off the wheel when you
use **Force center**: it turns the wheel to the centre and holds it there with full force.

**Close Game Link Wheel when you're done.** Only one copy can drive the wheel, and the dashboard starts its own with
motion.

## Part 6: Race with the wheel

1. **Unplug or switch off any real Xbox controller.** The game listens only to player 1.
2. Select the **STAR WARS Galactic Racer** tile, with **Wheel force feedback** ticked, and **start motion**. The
   dashboard checks Game Link Wheel's copy and starts it.
3. **Start the game** and race.

The right profile loads by itself for each vehicle class. The force mutes in menus, pause and after the finish. On foot,
the wheel's pad is held still, so walk with the keyboard.

**What stops the force:**

- **Stopping motion:** the dashboard sends `estop` and `quit`; Game Link Wheel holds its force at zero and closes itself,
  letting go of the wheelbase.
- **The rig's E-stop,** on a controller that reports its E-stop chain: the force goes to zero and stays there until motion
  starts again.
- **The dashboard freezing:** Game Link Wheel misses its heartbeat and holds its force at zero within 1.5 s. **The
  dashboard closing or crashing:** Game Link Wheel closes itself the same way.
- **KILL FFB**, in its window, at any time.

**Force feedback off, the wheel still steering:** untick **FFB enabled** on the **Force feedback** tab. That choice is
saved; profiles never turn it back on.

## Troubleshooting

| What you see | What to do |
| --- | --- |
| The game hangs as it starts | Let your user write to the `ue4ss` folder (Part 1). |
| The Install box says the game was not found | **Locate the game…** and choose its `Griffin\Binaries\Win64` folder. |
| The Install box shows **Get UE4SS for Galactic Racer** | UE4SS isn't in: finish Part 1, then **Check again**. |
| No `[VNMGalacticRacer] loaded` in `UE4SS.log` | Check `dwmapi.dll` sits beside `SWGR-Win64-Shipping.exe` and `ue4ss\Mods\mods.txt` has `VNMGalacticRacer : 1`. After a game update, update UE4SS too. |
| `telemetry field(s) unreadable` in `UE4SS.log` | A game update renamed something the mod reads: watch for a VN Motion update. |
| "another Game Link already owns the game's pipe" | A second VN Motion is running on this PC: close it. |
| "Game Link Wheel is not installed" | Stop motion and press **Install** in the wheel box. |
| "the downloaded copy failed its check" | **Remove** it, then **Install** again. |
| "Game Link Wheel closed at once" | Another copy is open: close it. Only one can drive the wheel. |
| The wheel doesn't steer in the game | Is ViGEmBus installed? Is a real Xbox controller holding player 1? |
| No force | Run the motor test, enter the rotation, check **FFB enabled**, KILL FFB and Master. If the base isn't found, close its own software and press **Look again**. |
| Every force pushes the wheel away from the centre | The motor direction is wrong: hands off, run **Motor test** again. |
| Windows SmartScreen warns about the exe | It isn't code-signed. The copy in `%LOCALAPPDATA%\vn-motion-dashboard\game-link-wheel` is the one the dashboard checked. |

## Turning it off and removing it

- **Wheel off:** untick **Wheel force feedback** in the dashboard. It applies the next time motion starts.
- **Game Link Wheel out:** press **Remove** in the wheel box, with motion stopped. Its settings stay in
  `%APPDATA%\vn-motion-dashboard\game-link-wheel`; delete that folder to forget them too.
- **ViGEmBus out:** uninstall it from Windows Settings → Apps.
- **VN Motion's mod out:** press **Remove** in the Install box, with the game closed.
- **UE4SS out:** delete `dwmapi.dll` and the `ue4ss` folder from the game's `Win64` folder. That removes every UE4SS mod.
- **Back to VR:** tick **Use UEVR telemetry (VR)** in the dashboard.
