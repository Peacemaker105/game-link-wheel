using System.Drawing;
using System.Windows.Forms;

namespace WheelBridge.Gui;

/// <summary>
/// Guided setup, shown by itself on the first run and from the Controls and Wheel &amp; devices tabs. changes/0349: from
/// SWGR Wheel Bridge's setup, which could take the wheel for granted (MOZA's SDK named it); here the first step finds it:
///  1. the wheel: the force-feedback base (picked from the attached ones), its rated torque, its rotation, and the
///     steering axis (turn the wheel to the right);
///  2. the motor test (no force is sent until it has found which way the motor turns);
///  3. the pedals, one at a time (press it fully and let go: that finds its axis and calibrates it);
///  4. every control bound one at a time, from any attached controller.
/// Settings apply as they are made, except the base and its torque, which apply on Next. Finish only marks the setup as done.
/// </summary>
public sealed class SetupWizard : Form
{
    private enum Step { Wheel, Motor, Pedals, Controls, Done }

    // The order the controls are asked for: what a race needs first, the ones the game doesn't use last.
    private static readonly string[] BindOrder = ["RT", "LT", "A", "X", "B", "LB", "RS", "RB", "Start", "Up", "Down", "Left", "Right", "Y", "LS", "Back"];
    private static readonly TimeSpan SteeringTime = TimeSpan.FromSeconds(10);

    private readonly BridgeHost _host;
    private readonly Action _changed;
    private readonly Action<string> _say;
    private BridgeConfig Cfg => _host.Config;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };

    private readonly Label _title, _body, _result, _live;
    private readonly Button _action, _back, _skip, _next, _later;
    private Step _step;
    private bool _busy;

    // Wheel: the base picker, its torque and rotation, and the steering capture.
    private readonly TableLayoutPanel _wheelGrid;
    private readonly ComboBox _base;
    private readonly NumericUpDown _rated, _travel;
    private readonly Label _ratedNote;
    private AxisCapture? _steerCapture;
    private DateTime _steerUntil;

    // Pedals: which one is asked for (0 throttle, 1 brake, 2 clutch, 3 all done) and the capture waiting for it.
    private int _pedalStage;
    private PedalCapture? _pedalCapture;

    // Controls: the list being asked, the current one, and the capture waiting for its input.
    private List<string> _controls = [];
    private int _index;
    private BindCapture? _capture;
    private DateTime _advanceAt = DateTime.MaxValue;

    /// <summary>An entry in the base picker. Empty instance = no force feedback.</summary>
    private sealed record BaseItem(Guid Instance, string Name)
    {
        public override string ToString() => Instance == Guid.Empty ? "None: no force feedback" : Name;
    }

    public SetupWizard(BridgeHost host, Action changed, Action<string> say)
    {
        _host = host;
        _changed = changed;
        _say = say;
        Text = "Game Link Wheel setup";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 640);

        _title = new Label { AutoSize = true, Font = new Font("Segoe UI", 13f, FontStyle.Bold), Margin = new Padding(3, 3, 3, 10) };
        _body = new Label { AutoSize = true, MaximumSize = new Size(720, 0), Margin = new Padding(3, 3, 3, 12) };
        _action = new Button { AutoSize = true, Height = 34, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 8) };
        _result = new Label { AutoSize = true, MaximumSize = new Size(720, 0), ForeColor = Color.SeaGreen, Font = new Font(Font, FontStyle.Bold) };
        _live = new Label { AutoSize = true, MaximumSize = new Size(720, 0), Font = new Font("Consolas", 9.5f), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 12, 3, 3) };

        _wheelGrid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Margin = new Padding(3, 0, 3, 10) };
        _base = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
        var refresh = new Button { Text = "Look again", AutoSize = true };
        refresh.Click += (_, _) => { _host.Wheel.ScanRequested = true; FillBases(); };
        _rated = new NumericUpDown { Minimum = 0, Maximum = 60, Increment = 0.5m, DecimalPlaces = 1, Width = 80, TextAlign = HorizontalAlignment.Right };
        _ratedNote = new Label { AutoSize = true, Anchor = AnchorStyles.Left, MaximumSize = new Size(420, 0) };
        _rated.ValueChanged += (_, _) => ShowRatedNote();
        _travel = new NumericUpDown { Minimum = 90, Maximum = 2520, Increment = 10, Width = 80, TextAlign = HorizontalAlignment.Right };
        _travel.ValueChanged += (_, _) => { if (_step == Step.Wheel) { Cfg.SteeringTravelDegrees = (float)_travel.Value; _changed(); } };
        void Row(string label, Control a, Control? b)
        {
            int r = _wheelGrid.RowCount++;
            _wheelGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _wheelGrid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3) }, 0, r);
            _wheelGrid.Controls.Add(a, 1, r);
            if (b is not null) _wheelGrid.Controls.Add(b, 2, r);
        }
        Row("Force feedback wheelbase", _base, refresh);
        Row("Rated torque (Nm)", _rated, _ratedNote);
        Row("Rotation, lock to lock (°)", _travel, new Label { Text = "as set in your wheelbase's own software", AutoSize = true, Anchor = AnchorStyles.Left });

        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16), AutoScroll = true };
        content.Controls.AddRange([_title, _body, _wheelGrid, _action, _result, _live]);

        _next = new Button { AutoSize = true, Height = 32, MinimumSize = new Size(100, 0) };
        _skip = new Button { AutoSize = true, Height = 32, MinimumSize = new Size(100, 0) };
        _back = new Button { Text = "Back", AutoSize = true, Height = 32, MinimumSize = new Size(90, 0) };
        _later = new Button { Text = "Set up later", AutoSize = true, Height = 32 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        buttons.Controls.AddRange([_next, _skip, _back, _later]);
        _later.Margin = new Padding(3, 3, 60, 3);

        Controls.Add(content);
        Controls.Add(buttons);

        _action.Click += async (_, _) => await OnAction();
        _next.Click += (_, _) => OnNext();
        _skip.Click += (_, _) => OnSkip();
        _back.Click += (_, _) => OnBack();
        _later.Click += (_, _) => Close();
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };   // never close in the middle of the motor test
        FormClosed += (_, _) => _timer.Stop();
        _timer.Tick += (_, _) => Tick();

        _rated.Value = (decimal)Math.Clamp(Cfg.Ffb.RatedNm, 0, 60);
        _travel.Value = (decimal)Math.Clamp(Cfg.SteeringTravelDegrees, 90, 2520);
        // The torque box belongs to the base picked: another base starts it at 0 (not known), so one base's rating never
        // carries over to another unseen.
        _base.SelectedIndexChanged += (_, _) =>
            _rated.Value = ChosenBase is { } b && b.Instance == Cfg.Ffb.DeviceInstance ? (decimal)Math.Clamp(Cfg.Ffb.RatedNm, 0, 60) : 0;
        FillBases();
        GoTo(Step.Wheel);
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    // ───────────────────────────── the wheel ─────────────────────────────

    /// <summary>The attached force-feedback devices, with the chosen one (or the only one) selected.</summary>
    private void FillBases()
    {
        List<(Guid Instance, string Name)> found;
        try { found = FfbDevice.List(); }
        catch (Exception ex) { found = []; _result.ForeColor = Color.Firebrick; _result.Text = "Could not list the wheelbases: " + ex.Message; }
        _base.Items.Clear();
        foreach (var (g, n) in found) _base.Items.Add(new BaseItem(g, n));
        var f = Cfg.Ffb;
        if (f.DeviceInstance != Guid.Empty && found.All(d => d.Instance != f.DeviceInstance))
        {
            // Chosen before but not attached now: keep it in the list so Next doesn't silently drop it.
            var again = FfbDevice.Pick(found, f.DeviceInstance, f.DeviceName);
            if (again == Guid.Empty) _base.Items.Add(new BaseItem(f.DeviceInstance, f.DeviceName + " (not connected)"));
        }
        _base.Items.Add(new BaseItem(Guid.Empty, ""));
        var want = FfbDevice.Pick(found, f.DeviceInstance, f.DeviceName);
        if (want == Guid.Empty && f.DeviceInstance != Guid.Empty) want = f.DeviceInstance;
        if (want == Guid.Empty && found.Count == 1) want = found[0].Instance;   // one base attached: that one (applied on Next)
        _base.SelectedItem = _base.Items.Cast<BaseItem>().FirstOrDefault(b => b.Instance == want) ?? _base.Items[^1];
    }

    private BaseItem? ChosenBase => _base.SelectedItem as BaseItem;

    /// <summary>What the entered torque means for the force, under the box.</summary>
    private void ShowRatedNote()
    {
        float nm = (float)_rated.Value, scale = FfbEngine.AutoScale(nm);
        _ratedNote.Text = nm > 0
            ? $"Master 1.00 = ×{scale:0.00} of the base's full torque, about {nm * scale:0.0} Nm"
            : $"Not known: taken as {FfbEngine.AssumedNm:0} Nm, so the force starts weak (×{scale:0.00})";
    }

    /// <summary>Applies the wheel step: the base (another base starts untested), its torque (a deliberate yes when that makes the force stronger), the rotation.</summary>
    private bool ApplyWheel()
    {
        if (Cfg.Steering is null) { _result.ForeColor = Color.Firebrick; _result.Text = "Click Detect steering and turn the wheel first."; return false; }
        var chosen = ChosenBase ?? new BaseItem(Guid.Empty, "");
        string name = chosen.Name.EndsWith(" (not connected)") ? chosen.Name[..^" (not connected)".Length] : chosen.Name;
        if (chosen.Instance != Cfg.Ffb.DeviceInstance || name != Cfg.Ffb.DeviceName)
            _host.Ffb.ChooseDevice(chosen.Instance, name);   // another base: untested, its torque not known yet
        float nm = (float)_rated.Value;
        if (Math.Abs(nm - Cfg.Ffb.RatedNm) > 0.001f && chosen.Instance != Guid.Empty)
        {
            float before = FfbEngine.AutoScale(Cfg.Ffb.RatedNm), after = FfbEngine.AutoScale(nm);
            if (after > before + 0.001f && Cfg.Ffb.BaseScale <= 0
                && MessageBox.Show(this, $"Rated torque {nm:0.#} Nm: Master 1.00 will use ×{after:0.00} of your wheelbase's full torque, about {nm * after:0.0} Nm.\n\n" +
                    "A number lower than your base's real rating makes every force stronger than it was tuned for. " +
                    $"Is {nm:0.#} Nm the rating its maker gives?", "Rated torque", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return false;
            Cfg.Ffb.RatedNm = nm;
        }
        Cfg.SteeringTravelDegrees = (float)_travel.Value;
        Cfg.TravelConfirmed = true;
        _host.SaveConfig();
        _changed();
        return true;
    }

    // ───────────────────────────── steps ─────────────────────────────

    private void GoTo(Step step)
    {
        _step = step;
        _capture = null;
        _steerCapture = null;
        _pedalCapture = null;
        _advanceAt = DateTime.MaxValue;
        _result.Text = "";
        _result.ForeColor = Color.SeaGreen;
        // Each step's Tick decides what is enabled; start every step with everything usable.
        foreach (var b in new[] { _action, _skip, _next, _later }) b.Enabled = true;
        _back.Enabled = step != Step.Wheel;
        _live.Text = "";
        _skip.Visible = true;
        _next.Visible = true;
        _action.Visible = true;
        _wheelGrid.Visible = step == Step.Wheel;
        switch (step)
        {
            case Step.Wheel:
                _title.Text = "1 of 4: Your wheel";
                _body.Text = "Pick the wheelbase that should get force feedback, and enter its rated torque from its maker " +
                             "(for example 5 for a 5 Nm base). The forces were tuned on a 3.9 Nm base, and the torque sizes them for yours. " +
                             "Not sure? Leave it at 0: the force then starts weak.\n\n" +
                             "Enter the rotation (lock to lock) that is set in your wheelbase's own software, then click Detect steering " +
                             "and turn the wheel about a quarter turn to the right.";
                _action.Text = "Detect steering";
                _skip.Visible = false;
                _next.Text = "Next";
                ShowRatedNote();
                if (Cfg.Steering is { } st) _result.Text = $"Steering: {st.Describe()}. Detect again if you changed wheel.";
                break;

            case Step.Motor:
                _title.Text = "2 of 4: Motor test";
                _body.Text = Cfg.Ffb.DeviceInstance == Guid.Empty
                    ? "No force feedback wheelbase was picked, so there is no motor to test. Click Next."
                    : "Game Link Wheel needs to know which way your wheelbase's motor turns. Until this test has run it sends no force feedback: " +
                      "with the wrong direction, every force would push the wheel away from center instead of toward it.\n\n" +
                      "Take your hands off the wheel and click Run motor test. The wheel turns a little to one side for a moment.";
                _action.Text = "Run motor test";
                _action.Visible = Cfg.Ffb.DeviceInstance != Guid.Empty;
                _skip.Text = "Skip";
                _next.Text = "Next";
                if (!_host.Ffb.NeedsMotorTest)
                    _result.Text = $"Already done for this wheelbase (ForceSign {Cfg.Ffb.ForceSign:+0;-0}). Run it again if every force feels backwards.";
                break;

            case Step.Pedals:
                _title.Text = "3 of 4: Pedals";
                ShowPedal();
                break;

            case Step.Controls:
                // A trigger driven by a calibrated pedal needs no button; one without a pedal (no pedals) does.
                _controls = BindOrder.Where(c => c switch
                {
                    "RT" => !HasPedal(Cfg.RightTriggerPedal),
                    "LT" => !HasPedal(Cfg.LeftTriggerPedal),
                    _ => true,
                }).ToList();
                _index = Math.Clamp(_index, 0, _controls.Count - 1);
                _action.Visible = false;
                _next.Visible = false;
                _skip.Text = "Skip";
                ShowControl();
                break;

            case Step.Done:
                _title.Text = "All set";
                _action.Visible = false;
                _skip.Visible = false;
                _next.Text = "Finish";
                string wheel = Cfg.Ffb.DeviceInstance == Guid.Empty ? "no force feedback"
                    : $"{Cfg.Ffb.DeviceName}, {(Cfg.Ffb.RatedNm > 0 ? $"{Cfg.Ffb.RatedNm:0.#} Nm" : "torque not entered")}, strength ×{_host.Ffb.BaseScale:0.00}";
                string motor = Cfg.Ffb.DeviceInstance == Guid.Empty ? "-" : _host.Ffb.NeedsMotorTest
                    ? "not tested yet: no force feedback until you run the Motor test (toolbar)"
                    : $"tested (ForceSign {Cfg.Ffb.ForceSign:+0;-0})";
                string pedals = HasPedal(Cfg.RightTriggerPedal) || HasPedal(Cfg.LeftTriggerPedal) ? "calibrated" : "none (triggers on buttons)";
                var lines = PadMapping.Triggers.Concat(PadMapping.Buttons)
                    .Select(c => $"  {c,-6} {PadMapping.GameFunction.GetValueOrDefault(c, ""),-22} {PadMapping.Describe(c, Cfg)}");
                _body.Text = $"Wheelbase: {wheel}\nSteering: {Cfg.Steering?.Describe() ?? "-"}, {Cfg.SteeringTravelDegrees:F0}° lock to lock\n" +
                             $"Motor direction: {motor}\nPedals: {pedals}\n\nYou can change all of this later: Run setup on the Controls tab or on Wheel & devices, " +
                             "Motor test and Calibrate pedals in the toolbar.";
                _live.Text = string.Join("\n", lines);
                break;
        }
    }

    private bool HasPedal(string pedal) => pedal switch
    {
        "Throttle" => Cfg.Throttle is not null && Cfg.ThrottleAxis is not null,
        "Brake" => Cfg.Brake is not null && Cfg.BrakeAxis is not null,
        "Clutch" => Cfg.Clutch is not null && Cfg.ClutchAxis is not null,
        _ => false,
    };

    private static readonly string[] PedalWords = ["accelerator", "brake", "clutch"];

    private void ShowPedal()
    {
        _result.Text = "";
        _pedalCapture = null;
        _advanceAt = DateTime.MaxValue;
        if (_pedalStage >= 3)
        {
            _body.Text = "Pedals done. The bars below follow your feet: each should reach 100% when pressed fully and 0% when let go. " +
                         "If one doesn't, click Do them again.";
            _action.Text = "Do them again";
            _action.Visible = true;
            _skip.Visible = false;
            _next.Visible = true;
            _next.Text = "Next";
            return;
        }
        string word = PedalWords[_pedalStage];
        bool has = HasPedal(PadMapping.Pedals[_pedalStage]);
        _body.Text = $"Press the {word} pedal all the way down, then let it go." +
                     (_pedalStage == 0 ? "\n\nNo pedals (accelerate and brake on paddles or buttons)? Click No pedals." :
                      _pedalStage == 2 ? "\n\nNo clutch? Click Skip." : "\n\nNo brake pedal? Click Skip.") +
                     (has ? $"\n\nNow: {AxisOf(_pedalStage)?.Describe()}. Pressing it again replaces that; Keep leaves it." : "");
        _action.Visible = false;
        _skip.Visible = true;
        _skip.Text = _pedalStage == 0 ? "No pedals" : "Skip";
        _next.Visible = has;
        _next.Text = "Keep";
        // The steering and the pedals already done this time can't be taken for this one.
        _pedalCapture = new PedalCapture(_host.Wheel.Devices, [Cfg.Steering, .. Enumerable.Range(0, _pedalStage).Select(AxisOf)]);
    }

    private AxisRef? AxisOf(int pedal) => pedal switch { 0 => Cfg.ThrottleAxis, 1 => Cfg.BrakeAxis, _ => Cfg.ClutchAxis };

    private void SetPedal(int pedal, AxisRef? axis, PedalRange? range)
    {
        switch (pedal)
        {
            case 0: Cfg.Throttle = range; Cfg.ThrottleAxis = axis; break;
            case 1: Cfg.Brake = range; Cfg.BrakeAxis = axis; break;
            default: Cfg.Clutch = range; Cfg.ClutchAxis = axis; break;
        }
    }

    private void ShowControl()
    {
        string c = _controls[_index];
        string function = PadMapping.GameFunction.GetValueOrDefault(c, "");
        bool trigger = c is "RT" or "LT";
        bool pedals = HasPedal("Throttle") || HasPedal("Brake") || HasPedal("Clutch");
        _title.Text = $"4 of 4: Controls ({_index + 1} of {_controls.Count})";
        _body.Text = (function.Length > 0
                         ? $"Press the button for {function} (Xbox {c}), on the wheel or any controller" + (!trigger && pedals ? ", or push a pedal past half way." : ".")
                         : $"Xbox {c} is not used by the game by default. Press a button if you want it, or click Skip.")
                     + (trigger ? $"\n\nNo pedal drives {c}, so bind a button (a paddle, for example). It presses {c} fully while held." : "")
                     + $"\n\nNow: {PadMapping.Describe(c, Cfg)}. Pressing a button replaces that; Skip keeps it.";
        _result.Text = "";
        _capture = _host.Wheel.State is { } s ? new BindCapture(c, s, Cfg, timeout: null) : null;
        _advanceAt = DateTime.MaxValue;
    }

    // ───────────────────────────── buttons ─────────────────────────────

    private async Task OnAction()
    {
        if (_busy) return;
        if (_step == Step.Wheel)
        {
            _host.Wheel.ScanRequested = true;
            _steerCapture = new AxisCapture(_host.Wheel.Devices);
            _steerUntil = DateTime.UtcNow + SteeringTime;
            _result.ForeColor = SystemColors.ControlText;
            _result.Text = "Turn the wheel to the right, about a quarter turn…";
        }
        else if (_step == Step.Motor)
        {
            _busy = true;
            foreach (var b in new[] { _action, _back, _skip, _next, _later }) b.Enabled = false;
            _result.ForeColor = SystemColors.ControlText;
            _result.Text = "Running. Hands off the wheel…";
            var (ok, msg) = await _host.Ffb.RunMotorTestAsync();
            if (ok) { _host.SaveConfig(); _changed(); }
            _result.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
            _result.Text = ok ? msg + ". Saved." : msg;
            _say(msg);
            foreach (var b in new[] { _action, _back, _skip, _next, _later }) b.Enabled = true;
            _busy = false;
        }
        else if (_step == Step.Pedals && _pedalStage >= 3)
        {
            _pedalStage = 0;
            ShowPedal();
        }
    }

    private void OnNext()
    {
        switch (_step)
        {
            case Step.Wheel:
                if (ApplyWheel()) GoTo(Step.Motor);
                break;
            case Step.Motor: _pedalStage = 0; GoTo(Step.Pedals); break;
            case Step.Pedals:
                if (_pedalStage >= 3) GoTo(Step.Controls);
                else { _pedalStage++; ShowPedal(); }   // Keep: this pedal stays as it is
                break;
            case Step.Done:
                Cfg.SetupDone = true;
                _changed();
                DialogResult = DialogResult.OK;
                Close();
                break;
        }
    }

    private void OnSkip()
    {
        switch (_step)
        {
            case Step.Motor: _pedalStage = 0; GoTo(Step.Pedals); break;
            case Step.Pedals:
                if (_pedalStage == 0)
                {
                    // No pedals: the triggers go on buttons, asked for in the next step.
                    Cfg.RightTriggerPedal = "None";
                    Cfg.LeftTriggerPedal = "None";
                    _changed();
                    GoTo(Step.Controls);
                    break;
                }
                // No brake / no clutch: nothing on that pedal, and a trigger it drove goes on buttons.
                SetPedal(_pedalStage, null, null);
                string skipped = PadMapping.Pedals[_pedalStage];
                if (Cfg.RightTriggerPedal == skipped) Cfg.RightTriggerPedal = "None";
                if (Cfg.LeftTriggerPedal == skipped) Cfg.LeftTriggerPedal = "None";
                _changed();
                _pedalStage++;
                ShowPedal();
                break;
            case Step.Controls: NextControl(); break;
        }
    }

    private void OnBack()
    {
        switch (_step)
        {
            case Step.Motor: GoTo(Step.Wheel); break;
            case Step.Pedals:
                if (_pedalStage == 0) GoTo(Step.Motor);
                else { _pedalStage = Math.Min(_pedalStage, 3) - 1; ShowPedal(); }
                break;
            case Step.Controls:
                if (_index == 0) { _pedalStage = 3; GoTo(Step.Pedals); }
                else { _index--; ShowControl(); }
                break;
            case Step.Done: _index = _controls.Count - 1; GoTo(Step.Controls); break;
        }
    }

    private void NextControl()
    {
        if (_index + 1 >= _controls.Count) { GoTo(Step.Done); return; }
        _index++;
        ShowControl();
    }

    // ───────────────────────────── live ─────────────────────────────

    private void Tick()
    {
        var s = _host.Wheel.State;
        switch (_step)
        {
            case Step.Wheel:
                TickWheel(s);
                break;

            case Step.Motor:
                bool noBase = Cfg.Ffb.DeviceInstance == Guid.Empty;
                _next.Enabled = !_busy && (noBase || !_host.Ffb.NeedsMotorTest);
                _action.Enabled = !_busy && _host.Ffb.Ready && s is not null;
                _live.Text = s is null ? $"No wheel data: {_host.Wheel.Status}"
                    : $"Wheel {s.AngleDegrees,7:F1}°   FFB: {(_host.Ffb.Ready ? "ready" : _host.Ffb.DeviceStatus)}";
                break;

            case Step.Pedals:
                TickPedals(s);
                break;

            case Step.Controls:
                TickControl(s);
                break;
        }
    }

    private void TickWheel(WheelState? s)
    {
        var devices = _host.Wheel.Devices;
        if (_steerCapture is { } cap)
        {
            if (cap.Poll(devices) is { } found)
            {
                _steerCapture = null;
                Cfg.Steering = AxisCapture.Steering(found);
                _changed();
                _result.ForeColor = Color.SeaGreen;
                _result.Text = $"Steering: {Cfg.Steering.Describe()}. Let the wheel come back to center.";
                _say(_result.Text);
            }
            else if (DateTime.UtcNow > _steerUntil)
            {
                _steerCapture = null;
                _result.ForeColor = Color.Firebrick;
                _result.Text = "Nothing turned far enough. Click Detect steering and turn the wheel about a quarter turn to the right.";
            }
        }
        _action.Enabled = _steerCapture is null && devices.Count > 0;
        _action.Text = _steerCapture is null ? "Detect steering" : $"Turn right… {Math.Max(0, (_steerUntil - DateTime.UtcNow).TotalSeconds):F0}s";
        _next.Enabled = Cfg.Steering is not null && _steerCapture is null;
        string attached = devices.Count == 0 ? "none found: is the wheel plugged in and switched on? (Look again)" : string.Join(", ", devices.Select(d => d.Name));
        string angle = s is null ? (Cfg.Steering is null ? "" : $"\nSteering: {_host.Wheel.Status}")
            : $"\nSteering angle {s.AngleDegrees,7:F1}°  (at full lock it should read ±{Cfg.SteeringTravelDegrees / 2:F0}°; if not, the rotation above is not the base's)";
        _live.Text = $"Controllers: {attached}{angle}";
    }

    private void TickPedals(WheelState? s)
    {
        if (_pedalStage >= 3)
        {
            _action.Enabled = !_busy;
            if (s is null) { _live.Text = $"No wheel data: {_host.Wheel.Status}"; return; }
            _live.Text = string.Join("\n", PadMapping.Pedals.Select((n, i) =>
                $"{n,-9} {(HasPedal(n) ? $"{Dashboard.Meter(PadMapping.PedalValue(n, s, Cfg))} {PadMapping.PedalValue(n, s, Cfg),4:P0}  {AxisOf(i)?.Describe()}" : "not set")}"));
            return;
        }
        if (DateTime.UtcNow >= _advanceAt) { _pedalStage++; ShowPedal(); return; }
        var devices = _host.Wheel.Devices;
        if (_pedalCapture is { } cap && _advanceAt == DateTime.MaxValue)
        {
            if (cap.Poll(devices) is { } got)
            {
                SetPedal(_pedalStage, got.Axis, got.Range);
                string pedal = PadMapping.Pedals[_pedalStage];
                // Pedals after all (No pedals was clicked earlier): put the triggers back on them.
                if (_pedalStage == 0 && Cfg.RightTriggerPedal == "None") Cfg.RightTriggerPedal = "Throttle";
                if (_pedalStage == 1 && Cfg.LeftTriggerPedal == "None") Cfg.LeftTriggerPedal = "Brake";
                _host.SaveConfig();
                _changed();
                _result.ForeColor = Color.SeaGreen;
                _result.Text = $"{pedal}: {got.Axis.Describe()}, {got.Range.Released}→{got.Range.Pressed}. Saved.";
                _say(_result.Text);
                _advanceAt = DateTime.UtcNow.AddMilliseconds(900);
            }
            else if (cap.Found is { } f)
                _result.Text = $"Found {f.Name} {DeviceSnapshot.AxisNames[f.Axis]}: now let it go";
        }
        _live.Text = devices.Count == 0 ? "No controllers found: are the pedals plugged in?"
            : "Controllers: " + string.Join(", ", devices.Select(d => d.Name));
    }

    private void TickControl(WheelState? s)
    {
        if (s is null) { _live.Text = $"No wheel data: {_host.Wheel.Status}"; _capture = null; return; }
        string c = _controls[_index];
        if (DateTime.UtcNow >= _advanceAt) { NextControl(); return; }
        // (Re)arm after the wheel data comes back; not during the pause after a binding.
        if (_capture is null && _advanceAt == DateTime.MaxValue) _capture = new BindCapture(c, s, Cfg, timeout: null);
        if (_capture is { } cap)
        {
            var (button, pedal) = cap.Poll(s, Cfg);
            string? msg = button is { } b ? PadMapping.BindButton(Cfg, c, b, replace: true)
                        : pedal is not null ? PadMapping.BindPedal(Cfg, c, pedal, replace: true) : null;
            if (msg is not null)
            {
                _capture = null;
                _changed();
                _result.ForeColor = msg.Contains("note:") ? Color.DarkOrange : Color.SeaGreen;
                _result.Text = msg;
                _say(msg);
                // A short pause so the result can be read before the next control.
                _advanceAt = DateTime.UtcNow.AddMilliseconds(msg.Contains("note:") ? 1800 : 700);
            }
        }
        var held = s.Devices.SelectMany(d => BindCapture.RawPressed([d]).Select(x => $"{d.Name}: {ButtonIds.Describe(x.Item2, new BridgeConfig())}")).ToList();
        _live.Text = $"Buttons held: {(held.Count == 0 ? "none" : string.Join(", ", held))}";
    }
}
