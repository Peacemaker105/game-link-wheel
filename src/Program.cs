using System.Diagnostics;
using WheelBridge;

// Game Link Wheel (changes/0349, part 2), from SWGR Wheel Bridge 0.1 (MIT):
//  - reads any DirectInput wheel, pedals and buttons and drives the game through a virtual Xbox pad (ViGEmBus)
//  - takes the game's telemetry from Game Link (the VNSG frames of changes/0340, over UDP)
//  - synthesizes force feedback from it and sends it to the wheelbase picked in Setup
//
//   GameLinkWheel.exe                 the window (double-click)
//   GameLinkWheel.exe --console       the text dashboard
//   options: --port <telemetry UDP port> --control-port <port> --dashboard (started by the dashboard: its heartbeat is
//            required, a lost one holds the force at zero) --parent <pid> (end when that process ends)
//   tools:   --replay <recording.tsv[.gz]> [profile.json]   --list-devices   --ffb-selftest   --idle-probe   --version

string configPath = BridgeHost.ConfigPath;

int? port = null, controlPort = null, parent = null;
bool dashboard = false;
var rest = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    bool Int(out int v) { v = 0; return i + 1 < args.Length && int.TryParse(args[++i], out v); }
    if (a == "--port" && Int(out int p1)) port = p1;
    else if (a == "--control-port" && Int(out int p2)) controlPort = p2;
    else if (a == "--parent" && Int(out int p3)) parent = p3;
    else if (a == "--dashboard") dashboard = true;
    else rest.Add(a);
}
args = [.. rest];
var options = new HostOptions(port, controlPort, dashboard);

// Only one Game Link Wheel may own the virtual pad and the wheelbase at a time.
using var instance = new Mutex(false, @"Local\VNMotion_GameLinkWheel_Instance");
bool AlreadyRunning()
{
    try { return !instance.WaitOne(0); }
    catch (AbandonedMutexException) { return false; }   // previous instance crashed; we own it now
}

if (args is ["--version"]) { Console.WriteLine($"Game Link Wheel {typeof(BridgeHost).Assembly.GetName().Version?.ToString(3)}"); return; }

if (args is ["--list-devices"])
{
    using var w = new WheelInput(new BridgeConfig());
    Thread.Sleep(600);
    Console.WriteLine("Controllers:");
    foreach (var d in w.Devices) Console.WriteLine($"  {d.Name}  {d.Instance}  axes {string.Join(' ', d.Axes)}");
    Console.WriteLine("Force-feedback devices:");
    foreach (var (g, n) in FfbDevice.List()) Console.WriteLine($"  {n}  {g}");
    return;
}

// End with the process that started us (the dashboard): a crashed dashboard leaves no force behind.
if (parent is { } ppid)
{
    var watch = new Thread(() =>
    {
        try { Process.GetProcessById(ppid).WaitForExit(); } catch { }
        // changes/0349 (Main's review): first the window or the console loop is asked to end itself, which lets go of the
        // wheelbase properly. If the program is still here 3 s later, Environment.Exit: both paths register a ProcessExit
        // backstop (BridgeHost.ReleaseOnExit) that stops the force loop, which zeroes the force and lets go of the base.
        AppQuit.Request("the dashboard that started it has ended");
        Thread.Sleep(3000);
        Environment.Exit(0);
    }) { IsBackground = true, Name = "parent watch" };
    watch.Start();
}

// Double-clicking the exe opens the window; --console gives the text dashboard (also used when output is redirected).
if (args is [] && !Console.IsOutputRedirected) args = ["--gui"];

if (args is ["--gui"])
{
    // The window owns the screen; drop the console this exe was started with so no empty terminal hangs around.
    FreeConsole();
    if (AlreadyRunning())
    {
        System.Windows.Forms.MessageBox.Show("Game Link Wheel is already running. Close it first.",
            "Game Link Wheel", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
        return;
    }
    WheelBridge.Gui.MainForm.Run(options);
    return;
}

if (args is ["--replay", var replayPath, ..var replayRest])
{
    // Optional profile: replay with that profile's feel settings (the live config file is not changed).
    var cfg = BridgeConfig.Load(configPath);
    if (replayRest is [var profile]) cfg.ApplyProfile(BridgeConfig.ReadFile(profile));
    Replay.Run(replayPath, cfg);
    return;
}

if (args is ["--ffb-selftest"])
{
    // Acquire the chosen wheelbase and hold a zero-force effect for 3 s: proves the FFB path without moving the wheel.
    var cfg = BridgeConfig.Load(configPath);
    using var dev = new FfbDevice();
    bool ok = dev.Start(cfg.Ffb.DeviceInstance, cfg.Ffb.DeviceName);
    Console.WriteLine($"start: {ok}: {dev.Status}");
    for (int i = 0; i < 300 && ok; i++) { dev.Set(0); Thread.Sleep(10); }
    Console.WriteLine($"after 3 s at zero force: {dev.Status}");
    return;
}

if (args is ["--idle-probe"])
{
    // Read the wheel at rest for 5 s: shows how noisy the angle is (drives the Damping layer).
    var cfg = BridgeConfig.Load(configPath);
    using var w = new WheelInput(cfg);
    var ang = new List<float>(); var times = new List<double>();
    var sw = Stopwatch.StartNew();
    WheelState? prev = null; int changes = 0;
    while (sw.Elapsed.TotalSeconds < 6)
    {
        var s = w.State;
        if (s is not null && sw.Elapsed.TotalSeconds > 1)
        {
            if (prev is not null && s.AngleDegrees != prev.AngleDegrees) { changes++; times.Add(sw.Elapsed.TotalMilliseconds); }
            ang.Add(s.AngleDegrees); prev = s;
        }
        Thread.Sleep(2);
    }
    Console.WriteLine($"status {w.Status}, samples {ang.Count}, angle changes {changes}");
    if (ang.Count > 0)
    {
        Console.WriteLine($"angle min {ang.Min():F3} max {ang.Max():F3}");
        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
        if (gaps.Count > 0) Console.WriteLine($"angle update gap ms: median {gaps.Order().ElementAt(gaps.Count / 2):F1}");
    }
    return;
}

if (AlreadyRunning())
{
    Console.WriteLine("Game Link Wheel is already running. Close it first.");
    return;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var host = new BridgeHost(options);
AppDomain.CurrentDomain.ProcessExit += (_, _) => host.ReleaseOnExit();   // --parent's last resort: zero the force first
AppQuit.Raised += _ => { try { cts.Cancel(); } catch { } };              // changes/0349: "quit", or the dashboard gone
if (AppQuit.Requested) cts.Cancel();
var config = host.Config;
var ffb = host.Ffb;
string? note = config.LoadWarning ?? $"Config: {configPath}";

// Without an interactive console (e.g. output piped to a log, or started by the dashboard), one status line per second.
if (Console.IsOutputRedirected)
{
    Console.WriteLine(note);
    try
    {
        while (!cts.IsCancellationRequested)
        {
            await Task.Delay(1000, cts.Token);
            if (host.PollAutoProfile() is { } switched) Console.WriteLine($"{DateTime.Now:HH:mm:ss} {switched}");
            Console.WriteLine(host.StatusJson());
        }
    }
    catch (OperationCanceledException) { }
    return;
}

string? lastError = null;
Console.CursorVisible = false;
Console.Clear();
try
{
    while (!cts.IsCancellationRequested)
    {
        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true).Key;
            if (key == ConsoleKey.Q) cts.Cancel();
            if (key == ConsoleKey.R) note = host.ToggleRecording();
            if (key is ConsoleKey.Oem4 or ConsoleKey.Oem6)   // [ and ]
            {
                float step = key == ConsoleKey.Oem6 ? 0.01f : -0.01f;
                config.SteeringAntiDeadzone = MathF.Round(Math.Clamp(config.SteeringAntiDeadzone + step, 0f, 0.6f), 2);
                host.SaveConfig();
                note = $"Anti-deadzone {config.SteeringAntiDeadzone:F2} (saved)";
            }
            if (key is ConsoleKey.OemMinus or ConsoleKey.OemPlus)   // - and =
            {
                float step = key == ConsoleKey.OemPlus ? 30f : -30f;
                config.SteeringLockDegrees = Math.Clamp(config.SteeringLockDegrees + step, 90f, 1080f);
                host.SaveConfig();
                note = $"Steering lock {config.SteeringLockDegrees:F0}° (saved)";
            }
            if (key == ConsoleKey.K)
            {
                ffb.Killed = !ffb.Killed;
                note = ffb.Killed ? "FFB KILLED. Press K to resume" : "FFB resumed";
            }
            if (key == ConsoleKey.L)
            {
                if (!ffb.CenterLock && ffb.Racing) note = "Force center is only available outside a race";
                else if (!ffb.CenterLock && ffb.NeedsTravel) note = "Force center needs the wheel's rotation: enter it in Setup (open the window) first";
                else if (!ffb.CenterLock && ffb.NeedsMotorTest) note = "Force center needs the motor direction: take your hands off the wheel and press [T] first";
                else { ffb.CenterLock = !ffb.CenterLock; note = ffb.CenterLock ? "Force center on: wheel pinned at 0°, stick held at 0 ([L] to release)" : "Force center off"; }
            }
            if (key == ConsoleKey.T)
            {
                note = "Motor test running. Hands off the wheel…";
                var (ok, msg) = await ffb.RunMotorTestAsync();
                if (ok) host.SaveConfig();
                note = ok ? msg + " (saved)" : msg;
            }
            if (key is ConsoleKey.OemComma or ConsoleKey.OemPeriod)   // , and .
            {
                float step = key == ConsoleKey.OemPeriod ? 0.05f : -0.05f;
                config.Ffb.MasterGain = MathF.Round(Math.Clamp(config.Ffb.MasterGain + step, 0f, 1f), 2);
                host.SaveConfig();
                note = $"FFB master {config.Ffb.MasterGain:P0} (saved)";
            }
            if (key >= ConsoleKey.F1 && key < ConsoleKey.F1 + FfbModel.LayerOrder.Length)
            {
                string name = FfbModel.LayerOrder[key - ConsoleKey.F1];
                if (config.Ffb.Layers.TryGetValue(name, out var layer))
                {
                    layer.Enabled = !layer.Enabled;
                    host.SaveConfig();
                    note = $"FFB layer {name} {(layer.Enabled ? "on" : "off")} (saved)";
                }
            }
            if (key == ConsoleKey.C) note = host.BeginCalibration();
        }
        if (host.PollCalibration() is { } result) note = result;
        if (host.PollAutoProfile() is { } switched) note = switched;
        if ((host.FrameError ?? ffb.LastError) is { } err && err != lastError) { lastError = err; note = err; }
        if (ffb.CenterLockFault is { } fault) { ffb.CenterLockFault = null; note = fault; }
        if (host.Pad.SlotWarning is { } slot) note = slot;
        host.Dashboard.Draw(host.Receiver, host.Recorder, note,
            WheelPanel.Render(host.Wheel, host.Pad, config, host.Calibrator, host.Dashboard.GameSteer) + Environment.NewLine + FfbPanel.Render(ffb, config.Ffb));
        await Task.Delay(50);
    }
}
finally
{
    Console.CursorVisible = true;
}

[System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool FreeConsole();
