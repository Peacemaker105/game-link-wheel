namespace WheelBridge;

/// <summary>
/// Wheel state → virtual Xbox pad values, from the bindings in the config. Pure (no I/O), so the pad thread, the
/// Controls tab preview and the tests all use the same code.
/// </summary>
public static class PadMapping
{
    /// <summary>Bindable Xbox buttons, in display order (Guide is left out: it opens the Xbox overlay).</summary>
    public static readonly string[] Buttons = ["A", "B", "X", "Y", "LB", "RB", "LS", "RS", "Back", "Start", "Up", "Down", "Left", "Right"];
    public static readonly string[] Triggers = ["RT", "LT"];
    public static readonly string[] Pedals = ["Throttle", "Brake", "Clutch"];

    /// <summary>What the game does with each control (game defaults), for the Controls tab.</summary>
    public static readonly Dictionary<string, string> GameFunction = new()
    {
        ["RT"] = "Accelerate", ["LT"] = "Brake / reverse",
        ["A"] = "Boost", ["B"] = "Malfunction recovery", ["X"] = "Drift / maneuver", ["Y"] = "",
        ["LB"] = "Ability", ["RB"] = "Camera", ["LS"] = "", ["RS"] = "Ramjet mode",
        ["Back"] = "", ["Start"] = "Pause", ["Up"] = "Ability select", ["Down"] = "Ability select",
        ["Left"] = "Ability select", ["Right"] = "Ability select",
    };

    public static float PedalValue(string? which, WheelState s, BridgeConfig cfg) => which switch
    {
        "Throttle" => cfg.Throttle?.Normalize(s.RawThrottle, cfg.PedalDeadzone) ?? 0,
        "Brake" => cfg.Brake?.Normalize(s.RawBrake, cfg.PedalDeadzone) ?? 0,
        "Clutch" => cfg.Clutch?.Normalize(s.RawClutch, cfg.PedalDeadzone) ?? 0,
        _ => 0,
    };

    private static bool AnyPressed(Dictionary<string, int[]> map, string control, WheelState s)
    {
        if (!map.TryGetValue(control, out var indices) || indices is null) return false;
        foreach (int i in indices) if (s.Pressed.Contains(i)) return true;
        return false;
    }

    /// <summary>
    /// Fills <paramref name="down"/> (indexed like <see cref="Buttons"/>) and returns stick X (-1..1) and the triggers (0..1).
    /// A trigger takes the larger of its pedal and its buttons (a held button = fully pressed), so a wheel without
    /// pedals can accelerate and brake on paddles.
    /// With <paramref name="axesHeld"/> (a menu, changes/0360) the wheel and pedals count as centered and released,
    /// pedal buttons included; buttons, hats and paddle triggers still go through.
    /// </summary>
    public static (float Stick, float Rt, float Lt) Map(WheelState s, BridgeConfig cfg, bool[] down, bool axesHeld = false)
    {
        // Read each collection once: the GUI replaces them (never edits them in place) while this runs on the pad thread.
        var buttons = cfg.Buttons;
        var triggers = cfg.TriggerButtons;
        var pedalButtons = cfg.PedalButtons;

        float stick = axesHeld ? 0 : VirtualPad.SteeringToStick(s.AngleDegrees, cfg);
        float rt = Math.Max(axesHeld ? 0 : PedalValue(cfg.RightTriggerPedal, s, cfg), AnyPressed(triggers, "RT", s) ? 1 : 0);
        float lt = Math.Max(axesHeld ? 0 : PedalValue(cfg.LeftTriggerPedal, s, cfg), AnyPressed(triggers, "LT", s) ? 1 : 0);
        for (int b = 0; b < Buttons.Length; b++)
        {
            bool d = AnyPressed(buttons, Buttons[b], s);
            if (!d && !axesHeld)
                foreach (var pb in pedalButtons)
                    if (string.Equals(pb.Button, Buttons[b], StringComparison.OrdinalIgnoreCase) && PedalValue(pb.Pedal, s, cfg) >= pb.Threshold) { d = true; break; }
            down[b] = d;
        }
        return (stick, rt, lt);
    }

    /// <summary>Human-readable list of everything bound to a control, e.g. "button 15, Fanatec Pedals: button 3, clutch ≥ 50%".</summary>
    public static string Describe(string control, BridgeConfig cfg)
    {
        var parts = new List<string>();
        if (control == "RT" && cfg.RightTriggerPedal is "Throttle" or "Brake" or "Clutch") parts.Add(cfg.RightTriggerPedal.ToLowerInvariant() + " pedal");
        if (control == "LT" && cfg.LeftTriggerPedal is "Throttle" or "Brake" or "Clutch") parts.Add(cfg.LeftTriggerPedal.ToLowerInvariant() + " pedal");
        var map = control is "RT" or "LT" ? cfg.TriggerButtons : cfg.Buttons;
        if (map.TryGetValue(control, out var idx) && idx is not null) parts.AddRange(idx.Select(i => ButtonIds.Describe(i, cfg)));   // changes/0349: any controller
        if (control is not ("RT" or "LT"))
            parts.AddRange(cfg.PedalButtons.Where(p => string.Equals(p.Button, control, StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{p.Pedal.ToLowerInvariant()} ≥ {p.Threshold:P0}"));
        return parts.Count == 0 ? "not bound" : string.Join(", ", parts);
    }

    /// <summary>Every control a wheel button is bound to (to warn about one button doing two things).</summary>
    public static List<string> ControlsUsing(int button, BridgeConfig cfg) =>
        cfg.Buttons.Concat(cfg.TriggerButtons).Where(kv => kv.Value?.Contains(button) == true).Select(kv => kv.Key).ToList();

    // The pad thread reads these collections while the GUI edits them, so every change builds a new collection and
    // swaps the reference in one assignment (never edits in place).

    /// <summary>Binds a wheel button to a control, added to what is there or (replace) instead of it. Returns a status message.</summary>
    public static string BindButton(BridgeConfig cfg, string control, int button, bool replace = false)
    {
        var others = ControlsUsing(button, cfg).Where(c => c != control).ToList();
        bool trigger = control is "RT" or "LT";
        var map = trigger ? cfg.TriggerButtons : cfg.Buttons;
        var old = map.GetValueOrDefault(control) ?? [];
        int[] list = replace ? [button] : old.Contains(button) ? old : [.. old, button];
        var copy = new Dictionary<string, int[]>(map) { [control] = list };
        if (trigger) cfg.TriggerButtons = copy;
        else
        {
            cfg.Buttons = copy;
            if (replace) cfg.PedalButtons = cfg.PedalButtons.Where(p => !string.Equals(p.Button, control, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return $"{control}: {(replace ? "set to" : "added")} wheel button {button}" + (others.Count > 0 ? $" (note: it is also bound to {string.Join(", ", others)})" : "");
    }

    /// <summary>Binds a pedal (pressed past 50%) to a button control, added or (replace) instead of its other inputs. Returns a status message.</summary>
    public static string BindPedal(BridgeConfig cfg, string control, string pedal, bool replace = false)
    {
        bool has = cfg.PedalButtons.Any(p => p.Button == control && p.Pedal == pedal);
        if (has && !replace) return $"{control}: the {pedal.ToLowerInvariant()} pedal is already bound";
        var keep = replace ? cfg.PedalButtons.Where(p => !string.Equals(p.Button, control, StringComparison.OrdinalIgnoreCase)) : cfg.PedalButtons;
        cfg.PedalButtons = [.. keep, new PedalButton { Button = control, Pedal = pedal, Threshold = 0.5f }];
        if (replace) cfg.Buttons = new Dictionary<string, int[]>(cfg.Buttons) { [control] = [] };
        string usedBy = cfg.RightTriggerPedal == pedal ? "RT" : cfg.LeftTriggerPedal == pedal ? "LT" : "";
        return $"{control}: {(replace ? "set to" : "added")} the {pedal.ToLowerInvariant()} pedal (pressed past 50%)" + (usedBy.Length > 0 ? $" (note: it also drives {usedBy})" : "");
    }

    /// <summary>Removes the wheel buttons (and, for a button, the pedals) bound to a control. A trigger's own pedal stays.</summary>
    public static void ClearBinding(BridgeConfig cfg, string control)
    {
        if (control is "RT" or "LT")
            cfg.TriggerButtons = new Dictionary<string, int[]>(cfg.TriggerButtons) { [control] = [] };
        else
        {
            cfg.Buttons = new Dictionary<string, int[]>(cfg.Buttons) { [control] = [] };
            cfg.PedalButtons = cfg.PedalButtons.Where(p => !string.Equals(p.Button, control, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }
}

/// <summary>
/// Waits for the input to bind to a control: a button that wasn't already held when the capture started, or
/// (for a button, with calibrated pedals) a pedal going from released (&lt; 20%) at the start to past 60%.
/// changes/0349: the button can be on any attached controller (a wheel, a button box, a shifter): when the state carries
/// every controller (<see cref="WheelState.Devices"/>), a new press on one not bound yet adds it to the config's devices
/// and comes back as its id. A state without them (the tests) is read by its ids, as the bridge did.
/// </summary>
public sealed class BindCapture
{
    private readonly HashSet<int> _held;
    private readonly HashSet<(Guid, int)> _heldRaw;
    private readonly float[] _startPedals;

    public string Control { get; }
    /// <summary>When the capture gives up, or null to wait until it is dropped.</summary>
    public DateTime? Until { get; }
    public bool TimedOut => Until is { } u && DateTime.UtcNow > u;

    public BindCapture(string control, WheelState s, BridgeConfig cfg, TimeSpan? timeout)
    {
        Control = control;
        _held = [.. s.Pressed];
        _heldRaw = [.. RawPressed(s.Devices)];
        _startPedals = PedalValues(s, cfg);
        Until = timeout is { } t ? DateTime.UtcNow + t : null;
    }

    private static float[] PedalValues(WheelState s, BridgeConfig cfg) => [.. PadMapping.Pedals.Select(p => PadMapping.PedalValue(p, s, cfg))];

    /// <summary>Every button and hat direction held on every controller, by the controller's instance.</summary>
    public static IEnumerable<(Guid, int)> RawPressed(IReadOnlyList<DeviceSnapshot> devices)
    {
        foreach (var d in devices)
        {
            for (int b = 0; b < d.Buttons.Length && b < ButtonIds.HatBase; b++) if (d.Buttons[b]) yield return (d.Instance, b);
            for (int h = 0; h < d.Povs.Length && h < 4; h++)
                foreach (int dir in ButtonIds.HatDirections(d.Povs[h])) yield return (d.Instance, ButtonIds.HatBase + h * 4 + dir);
        }
    }

    /// <summary>Feeds a sample; returns the new button or pedal once one is pressed.</summary>
    public (int? Button, string? Pedal) Poll(WheelState s, BridgeConfig cfg)
    {
        if (s.Devices.Count > 0)
        {
            foreach (var (g, n) in RawPressed(s.Devices))
                if (!_heldRaw.Contains((g, n)))
                {
                    var dev = s.Devices.First(d => d.Instance == g);
                    return (cfg.SlotOf(g, dev.Name) * ButtonIds.PerDevice + n, null);
                }
        }
        else foreach (int b in s.Pressed) if (!_held.Contains(b)) return (b, null);
        if (Control is not ("RT" or "LT"))
        {
            var now = PedalValues(s, cfg);
            for (int i = 0; i < now.Length; i++)
                if (now[i] >= 0.6f && _startPedals[i] < 0.2f) return (null, PadMapping.Pedals[i]);
        }
        // Buttons let go during the wait can be used again.
        _held.IntersectWith(s.Pressed);
        _heldRaw.IntersectWith(RawPressed(s.Devices));
        return (null, null);
    }
}

/// <summary>
/// Setup's "move the control you want for ..." (changes/0349): the axis, on any attached controller, that has moved
/// furthest from where it was when the capture started, once past a third of its travel; so the user never picks from a
/// list of raw axis names. A controller plugged in during the capture starts from where it is then.
/// </summary>
public sealed class AxisCapture
{
    public const int Threshold = 65535 / 3;
    private readonly Dictionary<Guid, int[]> _start = [];
    private readonly HashSet<(Guid, int)> _exclude = [];

    /// <param name="exclude">Axes already bound to something else (the steering, while a pedal is captured).</param>
    public AxisCapture(IReadOnlyList<DeviceSnapshot> devices, IEnumerable<AxisRef?>? exclude = null)
    {
        foreach (var d in devices) _start[d.Instance] = (int[])d.Axes.Clone();
        foreach (var a in exclude ?? []) if (a is not null) _exclude.Add((a.Device, a.Axis));
    }

    /// <summary>Where an axis was when the capture started (or when its controller first showed up).</summary>
    public int StartOf(Guid device, int axis) => _start.TryGetValue(device, out var st) && axis >= 0 && axis < st.Length ? st[axis] : 0;

    /// <summary>The axis that moved furthest, which way (Increased: its value went up), once one is past the threshold; else null.</summary>
    public (Guid Device, string Name, int Axis, bool Increased)? Poll(IReadOnlyList<DeviceSnapshot> devices)
    {
        (Guid, string, int, bool)? best = null;
        int bestDelta = Threshold;
        foreach (var d in devices)
        {
            if (!_start.TryGetValue(d.Instance, out var st)) { _start[d.Instance] = (int[])d.Axes.Clone(); continue; }
            for (int a = 0; a < DeviceSnapshot.AxisCount && a < d.Axes.Length; a++)
            {
                if (_exclude.Contains((d.Instance, a))) continue;
                int delta = d.Axes[a] - st[a];
                if (Math.Abs(delta) > bestDelta) { bestDelta = Math.Abs(delta); best = (d.Instance, d.Name, a, delta > 0); }
            }
        }
        return best;
    }

    /// <summary>The steering axis from a capture of "turn the wheel to the right": reversed when its value went down.</summary>
    public static AxisRef Steering((Guid Device, string Name, int Axis, bool Increased) c) =>
        new() { Device = c.Device, DeviceName = c.Name, Axis = c.Axis, Invert = !c.Increased };

    /// <summary>A pedal's axis: its direction does not matter, the calibration (PedalRange) learns it.</summary>
    public static AxisRef Pedal((Guid Device, string Name, int Axis, bool Increased) c) =>
        new() { Device = c.Device, DeviceName = c.Name, Axis = c.Axis };
}

/// <summary>
/// Setup's "press the pedal all the way down and let it go" (changes/0349): finds the pedal's axis as
/// <see cref="AxisCapture"/> does, then follows it to its furthest point until it is let go again (back within a tenth
/// of that travel). That is its calibration too: released = where it started, pressed = the furthest point.
/// </summary>
public sealed class PedalCapture
{
    /// <summary>Less travel than this is not a pedal press (the same as <see cref="PedalCalibrator"/>).</summary>
    public const int MinTravel = 2000;
    private readonly AxisCapture _find;
    private (Guid Device, string Name, int Axis, bool Increased)? _axis;
    private int _start, _extreme;

    public PedalCapture(IReadOnlyList<DeviceSnapshot> devices, IEnumerable<AxisRef?>? exclude = null) => _find = new AxisCapture(devices, exclude);

    /// <summary>The axis once found (the pedal is past a third of its travel), else null.</summary>
    public (Guid Device, string Name, int Axis, bool Increased)? Found => _axis;

    /// <summary>Once the pedal has been pressed and let go: its axis and its travel; until then null.</summary>
    public (AxisRef Axis, PedalRange Range)? Poll(IReadOnlyList<DeviceSnapshot> devices)
    {
        if (_axis is null)
        {
            _axis = _find.Poll(devices);
            if (_axis is not { } found) return null;
            _start = _find.StartOf(found.Device, found.Axis);
            _extreme = _start;
        }
        var ax = _axis.Value;
        var d = devices.FirstOrDefault(x => x.Instance == ax.Device);
        if (d is null || ax.Axis >= d.Axes.Length) return null;   // unplugged mid-press: wait for it
        int v = d.Axes[ax.Axis];
        if (Math.Abs(v - _start) > Math.Abs(_extreme - _start)) _extreme = v;
        int travel = Math.Abs(_extreme - _start);
        if (travel < MinTravel || Math.Abs(v - _start) > travel / 10) return null;   // still pressed
        return (AxisCapture.Pedal(ax), new PedalRange { Released = _start, Pressed = _extreme });
    }
}
