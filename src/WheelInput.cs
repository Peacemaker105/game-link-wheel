using System.Runtime.InteropServices;
using Vortice.DirectInput;

namespace WheelBridge;

/// <summary>
/// Snapshot of the wheel, pedals and buttons, read from whichever DirectInput controllers they are bound to (changes/0349:
/// any wheel, pedal set or button box; the bridge this comes from read a MOZA base through MOZA's SDK).
/// Angle: degrees, + right. Raw pedals: the bound axis, 0..65535 (PedalRange maps them). Pressed: button ids, see <see cref="ButtonIds"/>.
/// </summary>
public sealed record WheelState(
    float AngleDegrees, float VelocityDps,
    int RawThrottle, int RawBrake, int RawClutch,
    IReadOnlySet<int> Pressed, DateTime Time)
{
    /// <summary>Every attached controller this poll, bound or not (the setup's "move the control" capture reads these).</summary>
    public IReadOnlyList<DeviceSnapshot> Devices { get; init; } = [];
}

/// <summary>One controller's raw state: 8 axes 0..65535 (<see cref="AxisNames"/>), 128 buttons, 4 hats (-1 = centred).</summary>
public sealed record DeviceSnapshot(Guid Instance, string Name, int[] Axes, bool[] Buttons, int[] Povs)
{
    public static readonly string[] AxisNames = ["X", "Y", "Z", "RX", "RY", "RZ", "Slider 1", "Slider 2"];
    public const int AxisCount = 8;
}

/// <summary>
/// Button ids: a bound device's place in <see cref="BridgeConfig.Devices"/> (its slot) times 1000, plus the button number
/// (0..127), or 128 + 4 × hat + direction (0 up, 1 right, 2 down, 3 left) for a hat.
/// </summary>
public static class ButtonIds
{
    public const int PerDevice = 1000, HatBase = 128;
    public static int Button(int slot, int n) => slot * PerDevice + n;
    public static int Hat(int slot, int hat, int dir) => slot * PerDevice + HatBase + hat * 4 + dir;
    public static int SlotOf(int id) => id / PerDevice;

    /// <summary>"button 5" / "hat 1 up", with the device's slot shown when there is more than one device.</summary>
    public static string Describe(int id, BridgeConfig cfg)
    {
        int slot = SlotOf(id), n = id % PerDevice;
        string what = n < HatBase ? $"button {n + 1}" : $"hat {(n - HatBase) / 4 + 1} {new[] { "up", "right", "down", "left" }[(n - HatBase) % 4]}";
        if (cfg.Devices.Count <= 1) return what;
        string dev = slot < cfg.Devices.Count ? cfg.Devices[slot].Name : $"device {slot + 1}";
        return $"{dev}: {what}";
    }

    /// <summary>A hat's angle (hundredths of a degree, -1/65535 centred) as the directions it presses: diagonals press two.</summary>
    public static IEnumerable<int> HatDirections(int pov)
    {
        if (pov < 0 || pov >= 36000) yield break;
        double a = pov / 100.0;
        for (int dir = 0; dir < 4; dir++)
        {
            double d = Math.Abs(((a - dir * 90 + 540) % 360) - 180);
            if (d < 67.5) yield return dir;
        }
    }
}

/// <summary>
/// Polls every attached game controller through DirectInput at ~500 Hz, each in background, non-exclusive mode (the
/// force-feedback base is also held exclusively by <see cref="FfbDevice"/>; DirectInput always allows a non-exclusive
/// reader beside that). A bound device that disappears (unplugged) drops out of the snapshot at once, so what it drove
/// goes neutral rather than holding its last value; without the steering device there is no state at all (no force,
/// a neutral pad). Controllers are looked for again every 2 s while a bound one is missing.
/// </summary>
public sealed class WheelInput : IDisposable
{
    private const int PollMs = 2;
    private readonly BridgeConfig _cfg;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private volatile WheelState? _state;

    public WheelState? State => _state;
    private volatile IReadOnlyList<DeviceSnapshot> _devices = [];
    /// <summary>Every attached controller at the last poll, bound or not: what Setup's "move the control" capture reads, before anything is bound.</summary>
    public IReadOnlyList<DeviceSnapshot> Devices => _devices;
    public string Status { get; private set; } = "starting";
    public double PollHz { get; private set; }

    /// <summary>Test hook: a source of snapshots instead of DirectInput (no hardware in the tests).</summary>
    private readonly Func<IReadOnlyList<DeviceSnapshot>>? _fake;

    public WheelInput(BridgeConfig cfg, Func<IReadOnlyList<DeviceSnapshot>>? fake = null)
    {
        _cfg = cfg;
        _fake = fake;
        _thread = new Thread(Run) { IsBackground = true, Name = "DirectInput poll", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private sealed class Open(Guid instance, string name, IDirectInputDevice8 device)
    {
        public Guid Instance { get; } = instance;
        public string Name { get; } = name;
        public IDirectInputDevice8 Device { get; } = device;
        public JoystickState State = new();
    }

    private void Run()
    {
        timeBeginPeriod(1);
        try
        {
            if (_fake is not null) { RunFake(); return; }
            RunDirectInput();
        }
        catch (Exception ex) { Status = "DirectInput failure: " + ex.Message; }
        finally { timeEndPeriod(1); }
    }

    private void RunFake()
    {
        while (_running)
        {
            Publish(_fake!());
            Thread.Sleep(PollMs);
        }
    }

    private readonly VelocityTracker _velocity = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>One poll's result: the raw controllers, the bound controls, and the wheel speed from the angle's own updates.</summary>
    private void Publish(IReadOnlyList<DeviceSnapshot> snaps)
    {
        _devices = snaps;
        var state = Compose(snaps, _cfg, out string status);
        if (state is null) _velocity.Reset();
        else state = state with { VelocityDps = _velocity.Next(state.AngleDegrees, _clock.Elapsed.TotalSeconds) };
        _state = state;
        Status = status;
    }

    private void RunDirectInput()
    {
        using var window = new HiddenWindow("GameLinkWheelInput");
        using var di = DInput.DirectInput8Create();
        var open = new List<Open>();
        DateTime nextScan = DateTime.MinValue;
        int polls = 0;
        var hzWindow = DateTime.UtcNow;
        try
        {
            while (_running)
            {
                var now = DateTime.UtcNow;
                bool missing = _cfg.Devices.Any(d => d.Instance != Guid.Empty && open.All(o => o.Instance != d.Instance));
                if (now >= nextScan && (open.Count == 0 || missing || ScanRequested))
                {
                    ScanRequested = false;
                    nextScan = now.AddSeconds(2);
                    Scan(di, window.Handle, open);
                }
                var snaps = new List<DeviceSnapshot>(open.Count);
                for (int i = open.Count - 1; i >= 0; i--)
                {
                    var o = open[i];
                    try
                    {
                        o.Device.Poll();
                        o.Device.GetCurrentJoystickState(ref o.State);
                    }
                    catch
                    {
                        // unplugged (or another program took it exclusively): drop it now; the next scan finds it again
                        try { o.Device.Unacquire(); } catch { }
                        o.Device.Dispose();
                        open.RemoveAt(i);
                        continue;
                    }
                    var s = o.State;
                    snaps.Add(new DeviceSnapshot(o.Instance, o.Name,
                        [s.X, s.Y, s.Z, s.RotationX, s.RotationY, s.RotationZ, s.Sliders[0], s.Sliders[1]],
                        (bool[])s.Buttons.Clone(), (int[])s.PointOfViewControllers.Clone()));
                }
                snaps.Reverse();
                Publish(snaps);
                polls++;
                if ((now - hzWindow).TotalSeconds >= 1) { PollHz = polls / (now - hzWindow).TotalSeconds; polls = 0; hzWindow = now; }
                Thread.Sleep(PollMs);
            }
        }
        finally
        {
            foreach (var o in open) { try { o.Device.Unacquire(); } catch { } o.Device.Dispose(); }
        }
    }

    /// <summary>Ask for a scan for controllers at the next poll (the setup does, so a just-plugged device shows).</summary>
    public volatile bool ScanRequested;

    private static void Scan(IDirectInput8 di, IntPtr hwnd, List<Open> open)
    {
        foreach (var d in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
        {
            if (open.Any(o => o.Instance == d.InstanceGuid)) continue;
            IDirectInputDevice8? dev = null;
            try
            {
                dev = di.CreateDevice(d.InstanceGuid);
                dev.SetDataFormat<RawJoystickState>().CheckError();
                dev.SetCooperativeLevel(hwnd, CooperativeLevel.Background | CooperativeLevel.NonExclusive).CheckError();
                // every axis 0..65535, whatever the driver's own range
                foreach (var obj in dev.GetObjects(DeviceObjectTypeFlags.Axis))
                {
                    try { dev.GetObjectPropertiesById(obj.ObjectId).Range = new InputRange(0, 65535); } catch { }
                }
                dev.Acquire();
                open.Add(new Open(d.InstanceGuid, (d.InstanceName ?? d.ProductName ?? "controller").Trim(), dev));
                dev = null;
            }
            catch { /* a device that will not open is skipped; it is tried again at the next scan */ }
            finally { dev?.Dispose(); }
        }
    }

    /// <summary>
    /// A bound controller among the attached ones: by its DirectInput instance or, when that is not attached (Windows can
    /// give a controller a new instance on another USB port), by its name when exactly one attached controller has that
    /// name and is not bound by its own instance (so one of two identical button boxes never stands in for the other).
    /// </summary>
    public static DeviceSnapshot? FindDevice(IReadOnlyList<DeviceSnapshot> snaps, Guid instance, string? name, BridgeConfig cfg)
    {
        if (instance == Guid.Empty) return null;
        foreach (var s in snaps) if (s.Instance == instance) return s;
        if (string.IsNullOrEmpty(name)) return null;
        DeviceSnapshot? only = null;
        foreach (var s in snaps)
        {
            if (s.Name != name) continue;
            if (only is not null) return null;   // two of them: no guessing
            only = s;
        }
        return only is not null && !IsBound(cfg, only.Instance) ? only : null;
    }

    private static bool IsBound(BridgeConfig cfg, Guid g) =>
        cfg.Devices.Any(d => d.Instance == g) || cfg.Steering?.Device == g || cfg.ThrottleAxis?.Device == g
        || cfg.BrakeAxis?.Device == g || cfg.ClutchAxis?.Device == g;

    /// <summary>
    /// The bound controls from the raw snapshots. Pure, so the tests drive it directly. No steering device bound, or it
    /// is not attached: null (no force, a neutral pad). The speed is not known here (NaN): the poll loop adds it.
    /// </summary>
    public static WheelState? Compose(IReadOnlyList<DeviceSnapshot> snaps, BridgeConfig cfg, out string status)
    {
        DeviceSnapshot? Find(Guid g, string? name) => FindDevice(snaps, g, name, cfg);
        int Raw(AxisRef? a)
        {
            var d = a is null ? null : Find(a.Device, a.DeviceName);
            return d is null || a!.Axis < 0 || a.Axis >= DeviceSnapshot.AxisCount ? 0 : d.Axes[a.Axis];
        }
        // A pedal whose controller is not attached reads as released: raw 0 would be full travel on a reversed pedal
        // (released at 65535), so unplugging the pedals would hold the throttle down.
        int Pedal(AxisRef? a, PedalRange? range)
        {
            var d = a is null ? null : Find(a.Device, a.DeviceName);
            return d is null || a!.Axis < 0 || a.Axis >= DeviceSnapshot.AxisCount ? range?.Released ?? 0 : d.Axes[a.Axis];
        }
        var steer = cfg.Steering;   // read once: Setup can replace it on the GUI thread while this runs on the input thread
        var steerDev = steer is null ? null : Find(steer.Device, steer.DeviceName);
        if (steerDev is null)
        {
            status = steer is null ? "steering not set up: open Setup and turn the wheel"
                   : snaps.Count == 0 ? "no controller found: is the wheel connected?" : "the steering wheel is not connected";
            return null;
        }
        float x = (Raw(steer) - 32767.5f) / 32767.5f;
        if (steer!.Invert) x = -x;
        float angle = Math.Clamp(x, -1f, 1f) * Math.Max(90f, cfg.SteeringTravelDegrees) / 2f;

        var pressed = new HashSet<int>();
        var devs = cfg.Devices;   // swapped whole when a binding adds a device (BridgeConfig.SlotOf)
        for (int slot = 0; slot < devs.Count; slot++)
        {
            var d = Find(devs[slot].Instance, devs[slot].Name);
            if (d is null) continue;
            for (int b = 0; b < d.Buttons.Length && b < ButtonIds.HatBase; b++) if (d.Buttons[b]) pressed.Add(ButtonIds.Button(slot, b));
            for (int h = 0; h < d.Povs.Length && h < 4; h++)
                foreach (int dir in ButtonIds.HatDirections(d.Povs[h])) pressed.Add(ButtonIds.Hat(slot, h, dir));
        }
        status = "ok";
        return new WheelState(angle, float.NaN, Pedal(cfg.ThrottleAxis, cfg.Throttle), Pedal(cfg.BrakeAxis, cfg.Brake), Pedal(cfg.ClutchAxis, cfg.Clutch),
            pressed, DateTime.UtcNow)
        { Devices = snaps };
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(1000);
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
}

/// <summary>
/// Wheel speed (°/s) from the angle's own updates (changes/0349). DirectInput gives a position only, and a controller
/// reports it at its own rate (125 Hz to 1 kHz) while it is read every 2 ms, so a difference per read would be spikes
/// and zeros for the Damping layer. Here the speed is the last change over the time since the change before it, and
/// 0 once the angle has not changed for <see cref="StillAfter"/>. (The bridge had the speed from MOZA's SDK.)
/// </summary>
public sealed class VelocityTracker
{
    public const double StillAfter = 0.05;
    private const double MinStep = 0.001;
    private float _angle = float.NaN, _vel;
    private double _changedAt;

    public float Next(float angle, double t)
    {
        if (!float.IsFinite(angle)) return _vel = 0;
        if (float.IsNaN(_angle)) { _angle = angle; _changedAt = t; return _vel = 0; }
        if (angle != _angle)
        {
            double dt = Math.Clamp(t - _changedAt, MinStep, StillAfter);
            _vel = (float)((angle - _angle) / dt);
            _angle = angle;
            _changedAt = t;
        }
        else if (t - _changedAt > StillAfter) _vel = 0;
        return _vel;
    }

    /// <summary>The wheel went away: the next angle starts again from rest, never a jump across the gap.</summary>
    public void Reset() { _angle = float.NaN; _vel = 0; }
}
