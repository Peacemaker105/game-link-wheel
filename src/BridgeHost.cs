using System.Globalization;
using System.Text.Json;

namespace WheelBridge;

/// <summary>How the host was started (Program's arguments).</summary>
public sealed record HostOptions(int? TelemetryPort = null, int? ControlPort = null, bool RequireHeartbeat = false);

/// <summary>
/// Everything Game Link Wheel runs (telemetry from Game Link, the controllers, the virtual pad, FFB, the dashboard's
/// control link), wired together. Used by both the console dashboard and the GUI. (From SWGR Wheel Bridge's BridgeHost:
/// changes/0349 replaced its named pipe and shared memory with Game Link's UDP frames, and its MOZA SDK with DirectInput.)
/// </summary>
public sealed class BridgeHost : IDisposable
{
    /// <summary>
    /// Where gamelinkwheel.json, profiles and recordings live: VNM_GAMELINK_WHEEL_HOME if set (the tests do), otherwise
    /// beside the dashboard's own data (%APPDATA%\vn-motion-dashboard\game-link-wheel), never in the install folder,
    /// which an update replaces.
    /// </summary>
    public static readonly string DataDir = ResolveDataDir();
    public static readonly string ConfigPath = Path.Combine(DataDir, "gamelinkwheel.json");
    public static readonly string ProfileDir = Path.Combine(DataDir, "profiles");
    public static readonly string RecordDir = Path.Combine(DataDir, "recordings");

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _rxTask, _controlTask;
    private readonly object _recLock = new();
    private Recorder? _recorder;

    public BridgeConfig Config { get; }
    public Dashboard Dashboard { get; } = new();
    public WheelInput Wheel { get; }
    public VirtualPad Pad { get; }
    public FfbEngine Ffb { get; }
    public PedalCalibrator Calibrator { get; } = new();
    public VnsgReceiver Receiver { get; }
    public ControlLink Control { get; }
    public ProfileManager Profiles { get; }
    public Recorder? Recorder { get { lock (_recLock) return _recorder; } }
    /// <summary>Last error from handling a frame (recording); the stream itself keeps going.</summary>
    public string? FrameError { get; private set; }

    public BridgeHost(HostOptions? options = null)
    {
        options ??= new();
        // The shipped profiles are inside the exe (the dashboard downloads it alone, changes/0349); a defaults folder
        // beside it, as a build leaves one, can only add to them.
        ProfileManager.SeedDefaults(ProfileManager.EmbeddedDefaults(), ProfileDir);
        ProfileManager.SeedDefaults(Path.Combine(AppContext.BaseDirectory, "defaults", "profiles"), ProfileDir);
        Config = BridgeConfig.Load(ConfigPath);
        if (options.TelemetryPort is { } tp) Config.TelemetryPort = tp;
        if (options.ControlPort is { } cp) Config.ControlPort = cp;
        Profiles = new ProfileManager(Config, ProfileDir, SaveConfig);
        Wheel = new WheelInput(Config);
        Pad = new VirtualPad(Wheel, Config);
        Ffb = new FfbEngine(Config, Wheel, Pad);
        Pad.HoldStickCentered = () => Ffb.CenterLock;
        Receiver = new VnsgReceiver(Config.TelemetryPort, (frame, _) =>
        {
            Dashboard.OnFrame(frame);
            Ffb.OnFrame(frame);
            Pad.Telemetry = (frame.Has(FrameFlags.OnFoot), DateTime.UtcNow);
            lock (_recLock)
            {
                try { _recorder?.Write(frame); }
                catch (Exception ex)
                {
                    FrameError = $"recording stopped: {ex.Message}";
                    try { _recorder?.Dispose(); } catch { }
                    _recorder = null;
                }
            }
        });
        Control = new ControlLink(Config.ControlPort, on => Ffb.EStop = on, StatusJson, options.RequireHeartbeat);
        _rxTask = Task.Run(() => Receiver.RunAsync(_cts.Token));
        _controlTask = Task.Run(() => Control.RunAsync(_cts.Token));
    }

    /// <summary>One JSON line for the dashboard's panel ("status" on the control link).</summary>
    public string StatusJson()
    {
        var ci = CultureInfo.InvariantCulture;
        return JsonSerializer.Serialize(new
        {
            t = "status",
            telemetry = Receiver.Connected,
            frames = Receiver.FramesReceived,
            wheel = Wheel.Status,
            device = Ffb.DeviceName,
            ffb = Ffb.DeviceStatus,
            loopHz = Math.Round(Ffb.LoopHz, 1),
            state = Ffb.ModelState,
            estop = Ffb.EStop,
            killed = Ffb.Killed,
            needsMotorTest = Ffb.NeedsMotorTest,
            needsTravel = Ffb.NeedsTravel,
            estopGain = Math.Round(Ffb.EStopGain, 2),
            baseScale = Math.Round(Ffb.BaseScale, 3),
            pad = Pad.Status,
            torque = Math.Round(Ffb.LastTorque, 3).ToString(ci),
        });
    }

    /// <summary>Saves gamelinkwheel.json. Never throws (it runs from timers and key handlers): a failure is reported through <see cref="FrameError"/>.</summary>
    public void SaveConfig()
    {
        try { Config.Save(ConfigPath); }
        catch (Exception ex) { FrameError = $"couldn't save settings: {ex.Message}"; }
    }

    /// <summary>Call regularly (~1–20 Hz) on the UI/console thread: switches profile when the vehicle type changes.</summary>
    public string? PollAutoProfile() => Profiles.OnVehicle(Dashboard.CurrentVehicle);

    private static string ResolveDataDir()
    {
        if (Environment.GetEnvironmentVariable("VNM_GAMELINK_WHEEL_HOME") is { Length: > 0 } env) return Ensure(env);
        return Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vn-motion-dashboard", "game-link-wheel"));
    }

    private static string Ensure(string dir) { Directory.CreateDirectory(dir); return dir; }

    // ───────────────────────────── recording / calibration ─────────────────────────────

    /// <summary>Starts or stops a telemetry recording; returns a status message.</summary>
    public string ToggleRecording()
    {
        lock (_recLock)
        {
            if (_recorder is null)
            {
                try { _recorder = new Recorder(RecordDir); }
                catch (Exception ex) { return "Couldn't start recording: " + ex.Message; }
                FrameError = null;
                return $"Recording to {_recorder.Path}";
            }
            var msg = $"Saved {_recorder.Rows} rows to {_recorder.Path}";
            _recorder.Dispose();
            _recorder = null;
            return msg;
        }
    }

    /// <summary>Starts pedal calibration; returns a status message.</summary>
    public string BeginCalibration()
    {
        if (Calibrator.Active) return "Calibration already running";
        if (Wheel.State is not { } ws) return "Can't calibrate: no wheel data";
        Calibrator.Begin(ws);
        return "Calibrating: press each pedal fully once…";
    }

    /// <summary>Call regularly (~10–20 Hz). Returns a message when a running calibration finishes (config saved).</summary>
    public string? PollCalibration()
    {
        if (Calibrator.Active && Wheel.State is { } s && Calibrator.Update(s, Config) is { } result)
        {
            SaveConfig();
            return result + " (saved)";
        }
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        // FFB first so the force is zeroed while the wheel is still being read, then the pad, then the input.
        Ffb.Dispose();
        Pad.Dispose();
        Wheel.Dispose();
        lock (_recLock) { _recorder?.Dispose(); _recorder = null; }
        try { Task.WaitAll([_rxTask, _controlTask], 1000); } catch { }
        Receiver.Dispose();
        Control.Dispose();
    }
}
