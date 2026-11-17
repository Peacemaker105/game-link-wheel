using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using WheelBridge;

// Offline regression suite for Game Link Wheel (changes/0349; from SWGR Wheel Bridge 0.1's suite, MIT). No wheel, ViGEm,
// game or dashboard needed: sockets only on 127.0.0.1 ports the system hands out, never Game Link's 4126-4128.
// Usage: dotnet run -c Release [-- filter]

var tests = new List<(string Name, Action Body)>
{
    ("frame: round trip of every field", FrameTests.RoundTrip),
    ("frame: NaN/∞ floats are read as 0", FrameTests.NonFinite),
    ("frame: bad magic / version / short buffer rejected", FrameTests.Rejects),
    ("frame: fuzzed frames never throw, always finite", FrameTests.Fuzz),
    ("vnsg: every field lands where the model reads it", VnsgTests.Fields),
    ("vnsg: yaw rate from the heading: + right, across ±180°, 0 across gaps, vehicles and invalid frames", VnsgTests.YawRate),
    ("vnsg: not Valid is no vehicle; paused, cinematic and not controlled mute the model", VnsgTests.Muting),
    ("vnsg: receiver drops reordered frames, counts bad ones, survives a throwing handler", VnsgTests.Receiver),
    ("vnsg: frames over UDP on loopback", VnsgTests.Udp),
    ("config: corrupt file → defaults, .bad copy, warning", ConfigTests.Corrupt),
    ("config: nulls and out-of-range values are repaired", ConfigTests.Sanitize),
    ("config: save is atomic and round-trips", ConfigTests.AtomicSave),
    ("profile: never changes master, sign, devices, torque, rotation, pedals or buttons", ConfigTests.ProfileKeepsHardware),
    ("profile: shipped defaults load and cover every layer", ConfigTests.DefaultProfiles),
    ("profile: auto switch per vehicle, never over unsaved tuning", ConfigTests.AutoSwitch),
    ("profile: seeding respects existing and deleted profiles", ConfigTests.Seeding),
    ("profile: the shipped profiles are inside the exe, byte for byte, and seed by the same rules", ConfigTests.Embedded),
    ("profile: Load reference carries the bridge's tuning over to the heading-derived yaw", ProfileLoadTests.LoadRetune),
    ("model: deterministic over recorded laps", ModelTests.Deterministic),
    ("model: NaN wheel input never reaches the motor and doesn't stick", ModelTests.NaNInput),
    ("model: muted to exactly 0 after the finish", ModelTests.MutedAfterFinish),
    ("model: pause detection on frozen telemetry", ModelTests.Pause),
    ("model: impact queue stays bounded while muted", ModelTests.ImpactsBounded),
    ("model: no vehicle clears wrecked / airborne", ModelTests.NoVehicleClears),
    ("steering: mapping symmetric, monotonic, ±1 at lock", InputTests.Steering),
    ("pedals: normalize safe for any deadzone and either axis direction", InputTests.Pedals),
    ("perf: model step, frame parse and allocations", PerfTests.Budget),
    ("lean: pushes back toward upright, finite, silent at gain 0", ModelTests.Lean),
    ("lean: light, and never pushes toward the lean while unwinding", ModelTests.LeanUnwind),
    ("force center: holds center, lets go when the motor turns the wrong way", ForceTests.CenterLock),
    ("config: no force until the motor test; profiles never set the direction", ConfigTests.ForceSignTested),
    ("bindings: capture, bind / replace / clear", InputTests.Capture),
    ("bindings: buttons, pedal buttons, paddle triggers, no pedals", InputTests.Bindings),
    ("bindings: descriptions and duplicate detection", InputTests.BindingText),
    ("devices: steering from any axis, right is +, the rotation sets the degrees", DeviceTests.Steering),
    ("devices: an unplugged wheel gives no state, unplugged pedals read released", DeviceTests.Unplugged),
    ("devices: a replugged controller or base is found by name, never one of two", DeviceTests.Replugged),
    ("devices: buttons and hats from every bound controller, ids by slot", DeviceTests.Buttons),
    ("devices: binding a button on a new controller adds the controller", DeviceTests.Capture),
    ("devices: axis capture and pedal capture", DeviceTests.AxisCaptures),
    ("devices: wheel speed from the angle's own updates", DeviceTests.WheelSpeed),
    ("safety: strength from the rated torque never above 3.9 Nm x 0.85; an unknown base is weakest", SafetyTests.Strength),
    ("safety: the motor test pushes about 0.47 Nm on any base", SafetyTests.MotorTestPush),
    ("safety: e-stop fades every force within 100 ms, back over 1 s", SafetyTests.EStop),
    ("safety: a loop that starts during an e-stop makes no force from its first tick", SafetyTests.EStopAtLoopStart),
    ("safety: no force while killed, untested or without the rotation", SafetyTests.Gates),
    ("safety: another wheelbase starts untested and at the weakest strength", SafetyTests.Device),
    ("control link: e-stop, resume, a lost heartbeat latches until resume", SafetyTests.Control),
    ("control link: deaf to the dashboard holds the force at zero", SafetyTests.ControlDeaf),
    ("control link: commands and status over UDP on loopback", SafetyTests.ControlUdp),
    ("control link: quit holds the force at zero, then asks the program to close, once", SafetyTests.ControlQuit),
};

// Keep the tests' data out of any real data folder.
Environment.SetEnvironmentVariable("VNM_GAMELINK_WHEEL_HOME", Path.Combine(Path.GetTempPath(), "glwtest_home_" + Environment.ProcessId));

string? filter = args.FirstOrDefault();
int pass = 0, fail = 0;
foreach (var (name, body) in tests)
{
    if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
    var sw = Stopwatch.StartNew();
    try { body(); pass++; Console.WriteLine($"PASS  {name}  ({sw.ElapsedMilliseconds} ms)"); }
    catch (Exception ex) { fail++; Console.WriteLine($"FAIL  {name}\n      {ex.GetType().Name}: {ex.Message}"); }
}
Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;

static class Check
{
    public static void True(bool cond, string what) { if (!cond) throw new Exception(what); }
    public static void Near(double a, double b, double tol, string what) { if (Math.Abs(a - b) > tol) throw new Exception($"{what}: {a} vs {b}"); }
}

/// <summary>Builds wire frames the way the Lua exporter does.</summary>
static class Frames
{
    public static byte[] Build(Action<float[]>? floats = null, byte[]? bytes = null, FrameFlags flags = FrameFlags.HasVehicle,
                               VehicleClass cls = VehicleClass.Car, uint seq = 1, double simTime = 0, float dt = 1 / 60f,
                               (float x, float y, float z, float lx, float ly, float lz, float pvd, int id, bool veh)[]? colls = null,
                               ushort version = TelemetryFrame.Version)
    {
        var f = new float[TelemetryFrame.FloatCount];
        f[(int)F.QuatW] = 1;
        floats?.Invoke(f);
        var b = new byte[TelemetryFrame.Size];
        var s = b.AsSpan();
        "SWGR"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], version);
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], (ushort)TelemetryFrame.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], seq);
        BinaryPrimitives.WriteDoubleLittleEndian(s[12..], simTime);
        BinaryPrimitives.WriteSingleLittleEndian(s[20..], dt);
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)flags);
        s[28] = (byte)cls;
        colls ??= [];
        s[29] = (byte)colls.Length;
        int o = TelemetryFrame.HeaderSize;
        foreach (var v in f) { BinaryPrimitives.WriteSingleLittleEndian(s[o..], v); o += 4; }
        for (int i = 0; i < TelemetryFrame.ByteCount; i++) s[o++] = bytes is not null && i < bytes.Length ? bytes[i] : (byte)0;
        for (int i = 0; i < Math.Min(colls.Length, TelemetryFrame.MaxCollisions); i++)
        {
            var c = colls[i];
            int p = o + i * TelemetryFrame.CollisionSize;
            float[] cv = [c.x, c.y, c.z, c.lx, c.ly, c.lz, c.pvd];
            for (int k = 0; k < 7; k++) BinaryPrimitives.WriteSingleLittleEndian(s[(p + k * 4)..], cv[k]);
            BinaryPrimitives.WriteInt32LittleEndian(s[(p + 28)..], c.id);
            s[p + 32] = (byte)(c.veh ? 1 : 0);
        }
        return b;
    }

    public static TelemetryFrame Parse(byte[] b) => TelemetryFrame.Parse(b, out var e) ?? throw new Exception("parse failed: " + e);

    /// <summary>A racing frame at speed (InputMode Racing, on the ground).</summary>
    public static byte[] Racing(uint seq, double t, float speed = 250, float slip = 3, float yaw = 0.3f, FrameFlags extra = 0,
                                (float, float, float, float, float, float, float, int, bool)[]? colls = null) =>
        Build(f =>
        {
            f[(int)F.SpeedKmph] = speed; f[(int)F.TopSpeedKmph] = 346; f[(int)F.RelVelX] = speed * 27.78f;
            f[(int)F.SlipDeg] = slip; f[(int)F.AngVelZ] = yaw; f[(int)F.OnGroundScale] = 1; f[(int)F.Throttle] = 1;
            f[(int)F.RpmScale] = 0.7f;
            // a moving vehicle never repeats its orientation bit for bit
            float a = (float)(t * 0.37);
            f[(int)F.QuatZ] = MathF.Sin(a / 2); f[(int)F.QuatW] = MathF.Cos(a / 2);
        }, bytes: [0, 0, 0, 0, 0, 3, 0, 0], flags: FrameFlags.HasVehicle | FrameFlags.CollisionsOk | extra, seq: seq, simTime: t, colls: colls);

    /// <summary>Laps recorded with SWGR Wheel Bridge 0.1 (fb3e573a1c, MIT): tests/recordings, gzipped. One per vehicle class.</summary>
    public static IEnumerable<string> Recordings()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "recordings");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.tsv.gz").Order() : [];
    }

    /// <summary>
    /// A recording → frames, through the bridge's own (private) Replay.Pack. changes/0349: the recorded AngVelZ is the
    /// game's own field, which Game Link Wheel never gets; it is replaced by the yaw rate from the heading, as
    /// Vnsg.ToBridgeFrame makes it, so the model is replayed on what it gets in use (<paramref name="gameYaw"/> keeps it).
    /// </summary>
    public static List<(TelemetryFrame frame, float dt)> Load(string path, bool gameYaw = false)
    {
        var pack = typeof(Replay).GetMethod("Pack", BindingFlags.NonPublic | BindingFlags.Static)!;
        string text;
        using (var gz = new System.IO.Compression.GZipStream(File.OpenRead(path), System.IO.Compression.CompressionMode.Decompress))
        using (var rd = new StreamReader(gz)) text = rd.ReadToEnd();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        var header = lines[0].TrimStart('\ufeff').Split('\t');
        int Col(string n) => Array.IndexOf(header, n) is >= 0 and var i ? i : throw new Exception("no column " + n);
        int dtCol = Col("dt");
        var yaw = new Vnsg.YawRate();
        var list = new List<(TelemetryFrame, float)>();
        foreach (var line in lines.Skip(1))
        {
            var c = line.Split('\t');
            var bytes = (byte[])pack.Invoke(null, [c, header, CultureInfo.InvariantCulture])!;
            if (!gameYaw)
            {
                float Q(string n) => c[Col(n)] is { Length: > 0 } v ? float.Parse(v, CultureInfo.InvariantCulture) : 0;
                float rate = yaw.Next(Vnsg.Heading(Q("QuatX"), Q("QuatY"), Q("QuatZ"), Q("QuatW")),
                    double.Parse(c[Col("simTime")], CultureInfo.InvariantCulture), byte.Parse(c[Col("class")]),
                    (uint.Parse(c[Col("flags")]) & (uint)FrameFlags.HasVehicle) != 0);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(TelemetryFrame.HeaderSize + (int)F.AngVelZ * 4), rate);
            }
            list.Add((Parse(bytes), float.Parse(c[dtCol], CultureInfo.InvariantCulture)));
        }
        return list;
    }
}

static class FrameTests
{
    public static void RoundTrip()
    {
        var b = Frames.Build(f => { for (int i = 0; i < f.Length; i++) f[i] = i * 1.5f - 7; },
            bytes: [1, 2, 3, 4, 5, 6, 7, 8], flags: FrameFlags.HasVehicle | FrameFlags.RaceDone | FrameFlags.Drifting,
            cls: VehicleClass.Pod, seq: 0xDEADBEEF, simTime: 1234.5678, dt: 0.0125f,
            colls: [(1, 2, 3, 4, 5, 6, 777, 42, true), (0, 0, 0, 0, -50, 0, 10, -3, false)]);
        var fr = Frames.Parse(b);
        Check.True(fr.Seq == 0xDEADBEEF && fr.SimTime == 1234.5678 && fr.Dt == 0.0125f, "header");
        Check.True(fr.VehicleClass == VehicleClass.Pod && fr.Has(FrameFlags.RaceDone) && fr.Has(FrameFlags.Drifting) && !fr.Has(FrameFlags.OnFoot), "flags/class");
        for (int i = 0; i < TelemetryFrame.FloatCount; i++) Check.True(fr.Floats[i] == i * 1.5f - 7, $"float {i}");
        for (int i = 0; i < TelemetryFrame.ByteCount; i++) Check.True(fr.Bytes[i] == i + 1, $"byte {i}");
        Check.True(fr.CollisionCount == 2 && fr.Collisions.Length == 2, "collision count");
        Check.True(fr.Collisions[0].PointVelocityDelta == 777 && fr.Collisions[0].Id == 42 && fr.Collisions[0].OtherIsVehicle, "collision 0");
        Check.True(fr.Collisions[1].LocalLocation.Y == -50 && fr.Collisions[1].Id == -3, "collision 1");
        Check.True(TelemetryFrame.Size == 318, $"frame size {TelemetryFrame.Size} (exporter sends 318)");
    }

    public static void NonFinite()
    {
        var b = Frames.Build(f => { f[(int)F.SlipDeg] = float.NaN; f[(int)F.SpeedKmph] = float.PositiveInfinity; f[(int)F.Roll] = float.NegativeInfinity; },
            colls: [(float.NaN, 0, 0, 0, float.PositiveInfinity, 0, float.NaN, 1, false)]);
        var fr = Frames.Parse(b);
        Check.True(fr.Floats.All(float.IsFinite), "floats finite");
        var c = fr.Collisions[0];
        Check.True(float.IsFinite(c.WorldDirection.X) && float.IsFinite(c.LocalLocation.Y) && float.IsFinite(c.PointVelocityDelta), "collision finite");
    }

    public static void Rejects()
    {
        var good = Frames.Build();
        var bad = (byte[])good.Clone(); bad[0] = (byte)'X';
        Check.True(TelemetryFrame.Parse(bad, out _) is null, "bad magic accepted");
        Check.True(TelemetryFrame.Parse(Frames.Build(version: 2), out var e) is null && e!.Contains("v2"), "wrong version accepted");
        Check.True(TelemetryFrame.Parse(good.AsSpan(0, 100), out _) is null, "short buffer accepted");
        var many = (byte[])good.Clone(); many[29] = 255;   // more contacts than the frame carries
        Check.True(Frames.Parse(many).Collisions.Length == TelemetryFrame.MaxCollisions, "collision count not capped");
    }

    public static void Fuzz()
    {
        var rng = new Random(1234);
        var buf = new byte[TelemetryFrame.Size];
        var model = new FfbModel(new FfbConfig());
        double now = 0;
        for (int i = 0; i < 50_000; i++)
        {
            rng.NextBytes(buf);
            if (i % 2 == 0) { Frames.Build().AsSpan(0, 8).CopyTo(buf); }   // valid header, random body
            var fr = TelemetryFrame.Parse(buf, out _);
            if (fr is null) continue;
            Check.True(fr.Floats.All(float.IsFinite), "non-finite float survived");
            model.OnFrame(fr, now);
            for (int k = 0; k < 4; k++)
            {
                now += 0.002;
                float t = model.Step(now, 0.002f, (float)(rng.NextDouble() * 900 - 450), (float)(rng.NextDouble() * 4000 - 2000), 180, (1, 1, 0));
                Check.True(float.IsFinite(t) && Math.Abs(t) <= 1, $"torque {t}");
            }
        }
    }
}

static class ConfigTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "wbtest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static void Corrupt()
    {
        string d = TempDir(), p = Path.Combine(d, "wheelbridge.json");
        File.WriteAllText(p, "{ \"SteeringLockDegrees\": 180, oops");
        var c = BridgeConfig.Load(p);
        Check.True(c.LoadWarning is not null, "no warning");
        Check.True(File.ReadAllText(p + ".bad").Contains("oops"), "bad copy missing");
        Check.True(c.SteeringLockDegrees == 360, "not defaults");
        Check.True(BridgeConfig.Load(p).LoadWarning is null, "rewritten file still unreadable");
    }

    public static void Sanitize()
    {
        string d = TempDir(), p = Path.Combine(d, "wheelbridge.json");
        File.WriteAllText(p, """
            { "Buttons": null, "PedalButtons": [null, {"Button":"X","Pedal":null,"Threshold":"NaN"}],
              "SteeringLockDegrees": 0, "PedalDeadzone": 0.5, "VehicleProfiles": null,
              "Ffb": { "Layers": { "Centering": null, "Road": { "Gain": 99 } }, "AligningPeakSlipDeg": 0, "LoadReference": -1,
                       "MasterGain": 7, "ForceSign": 0, "UpdateHz": 0, "StaleMs": -5, "CenterLockFullAtDeg": 0 } }
            """);
        BridgeConfig c;
        try { c = BridgeConfig.Load(p); } catch (Exception ex) { throw new Exception("load threw: " + ex.Message); }
        // "NaN" as a string isn't valid JSON for float without AllowNamedFloatingPointLiterals: then it's the corrupt path.
        if (c.LoadWarning is not null)
        {
            File.WriteAllText(p, File.ReadAllText(p + ".bad").Replace("\"NaN\"", "0"));
            c = BridgeConfig.Load(p);
            Check.True(c.LoadWarning is null, "still unreadable: " + c.LoadWarning);
        }
        Check.True(c.Buttons is not null && c.PedalButtons.Count == 1 && c.PedalButtons[0].Pedal == "Clutch", "buttons/pedal buttons");
        Check.True(c.SteeringLockDegrees >= 90 && c.PedalDeadzone <= 0.4f, "steering/pedal ranges");
        Check.True(c.VehicleProfiles.Count == 4, "vehicle profiles restored");
        var f = c.Ffb;
        Check.True(f.Layers.Count == FfbModel.LayerOrder.Length && f.Layers.Values.All(l => l is not null), "layers restored");
        Check.True(f.Layers["Road"].Gain <= 2, "layer gain clamp");
        Check.True(f.AligningPeakSlipDeg >= 0.5f && f.LoadReference >= 0.5f && f.MasterGain <= 1 && f.ForceSign == 1
                   && f.UpdateHz >= 50 && f.StaleMs >= 30 && f.CenterLockFullAtDeg >= 1, "ffb ranges");
        // And the repaired config must drive the model without NaN.
        var m = new FfbModel(f);
        m.OnFrame(Frames.Parse(Frames.Racing(1, 0)), 0);
        for (int i = 1; i < 200; i++) Check.True(float.IsFinite(m.Step(i * 0.002, 0.002f, 30, 100, c.SteeringLockDegrees, (0, 0, 9))), "NaN torque");
    }

    public static void AtomicSave()
    {
        string d = TempDir(), p = Path.Combine(d, "wheelbridge.json");
        var c = BridgeConfig.Load(p);
        c.Ffb.Layers["Road"].Gain = 0.77f;
        c.Save(p);
        Check.True(!File.Exists(p + ".tmp"), "temp file left behind");
        Check.True(BridgeConfig.Load(p).Ffb.Layers["Road"].Gain == 0.77f, "value not saved");
        // Saving while another handle reads the file (the GUI autosave racing an editor) must not throw.
        using (var reader = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) c.Save(p);
    }

    public static void ProfileKeepsHardware()
    {
        Guid mine = Guid.NewGuid(), other = Guid.NewGuid();
        var c = new BridgeConfig { Throttle = new PedalRange { Released = -100, Pressed = 9000 }, SteeringTravelDegrees = 540, TravelConfirmed = true,
                                   Steering = new AxisRef { Device = mine, DeviceName = "MyBase", Axis = 0 }, Devices = [new DeviceRef { Instance = mine, Name = "MyBase" }] };
        c.Buttons["A"] = [14];
        c.Ffb.MasterGain = 0.3f; c.Ffb.ForceSign = -1; c.Ffb.DeviceInstance = mine; c.Ffb.DeviceName = "MyBase"; c.Ffb.RatedNm = 8; c.Ffb.BaseScale = 0.5f;
        c.Ffb.UpdateHz = 333; c.Ffb.CenterLockForce = 0.4f;
        var p = new BridgeConfig { SteeringLockDegrees = 200, SteeringTravelDegrees = 1080, Steering = new AxisRef { Device = other, Axis = 3 } };
        p.Ffb.MasterGain = 1; p.Ffb.ForceSign = 1; p.Ffb.DeviceInstance = other; p.Ffb.DeviceName = "Other"; p.Ffb.RatedNm = 2; p.Ffb.BaseScale = 1;
        p.Ffb.UpdateHz = 1000; p.Ffb.CenterLockForce = 1;
        p.Ffb.Layers["Engine"].Gain = 0.99f; p.Ffb.LoadReference = 3;
        c.ApplyProfile(p);
        Check.True(c.SteeringLockDegrees == 200 && c.Ffb.Layers["Engine"].Gain == 0.99f && c.Ffb.LoadReference == 3, "feel not applied");
        Check.True(c.Ffb.MasterGain == 0.3f && c.Ffb.ForceSign == -1 && c.Ffb.DeviceInstance == mine && c.Ffb.DeviceName == "MyBase" && c.Ffb.UpdateHz == 333
                   && c.Ffb.CenterLockForce == 0.4f, "hardware/user settings changed by a profile");
        Check.True(c.Ffb.RatedNm == 8 && c.Ffb.BaseScale == 0.5f, "the base's torque or strength changed by a profile");
        Check.True(c.SteeringTravelDegrees == 540 && c.TravelConfirmed && c.Steering!.Device == mine && c.Devices.Single().Instance == mine, "the wheel's rotation or bindings changed by a profile");
        Check.True(c.Throttle!.Pressed == 9000 && c.Buttons["A"][0] == 14, "pedals/buttons changed");
    }

    public static void ForceSignTested()
    {
        string d = TempDir(), p = Path.Combine(d, "wheelbridge.json");
        var c = BridgeConfig.Load(p);
        Check.True(!c.Ffb.ForceSignTested && !c.SetupDone, "a new config claims a tested motor direction or a finished setup");
        c.Ffb.ForceSign = -1; c.Ffb.ForceSignTested = true; c.SetupDone = true;
        c.Save(p);
        var back = BridgeConfig.Load(p);
        Check.True(back.Ffb.ForceSign == -1 && back.Ffb.ForceSignTested && back.SetupDone, "not saved");
        // A profile (which may have been saved on another wheel) never changes it.
        var fresh = new BridgeConfig();
        fresh.ApplyProfile(new BridgeConfig { Ffb = { ForceSignTested = true, ForceSign = -1 } });
        Check.True(!fresh.Ffb.ForceSignTested && fresh.Ffb.ForceSign == 1, "profile set the motor direction");
        back.ApplyProfile(new BridgeConfig());
        Check.True(back.Ffb.ForceSignTested && back.Ffb.ForceSign == -1, "profile cleared the motor direction");
    }

    public static string DefaultsDir()
    {
        string a = Path.Combine(AppContext.BaseDirectory, "defaults", "profiles");
        return Directory.Exists(a) ? a : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "defaults", "profiles"));
    }

    public static void DefaultProfiles()
    {
        var files = Directory.GetFiles(DefaultsDir(), "*.json");
        var names = files.Select(Path.GetFileNameWithoutExtension).ToHashSet();
        foreach (var v in BridgeConfig.DefaultVehicleProfiles().Values) Check.True(names.Contains(v), $"default profile {v} missing");
        foreach (var file in files)
        {
            var p = BridgeConfig.ReadFile(file);
            Check.True(p.Ffb.Layers.Count == FfbModel.LayerOrder.Length, $"{file}: layers");
            string raw = File.ReadAllText(file);
            foreach (var hw in new[] { "MasterGain", "ForceSign", "Buttons", "Throttle", "Device", "DeviceInstance", "DeviceName", "RatedNm", "BaseScale",
                                       "Devices", "Steering", "ThrottleAxis", "SteeringTravelDegrees", "TravelConfirmed" })
                Check.True(!raw.Contains($"\"{hw}\""), $"{Path.GetFileName(file)} ships hardware setting {hw}");
            // Each one must produce sane forces on a recorded lap.
            var rec = Frames.Recordings().FirstOrDefault();
            if (rec is null) continue;
            var m = new FfbModel(p.Ffb);
            double now = 0; int clipped = 0, ticks = 0;
            foreach (var (fr, dt) in Frames.Load(rec).Take(3000))
            {
                m.OnFrame(fr, now);
                for (int i = 0; i < Math.Max(1, (int)Math.Round(dt * 500)); i++)
                {
                    now += 0.002;
                    float t = m.Step(now, 0.002f, fr[F.SteerInput] * p.SteeringLockDegrees / 2, 0, p.SteeringLockDegrees, (0, 0, 9));
                    Check.True(float.IsFinite(t), "NaN");
                    if (Math.Abs(t) >= 0.999f) clipped++;
                    ticks++;
                }
            }
            Check.True(clipped < ticks * 0.3, $"{Path.GetFileName(file)} clips {clipped * 100 / Math.Max(1, ticks)}% of ticks");
        }
    }

    public static void AutoSwitch()
    {
        string d = TempDir(), prof = Path.Combine(d, "profiles");
        Directory.CreateDirectory(prof);
        var a = new BridgeConfig { SteeringLockDegrees = 300 }; a.Save(Path.Combine(prof, "Land Speeder.json"));
        var b = new BridgeConfig { SteeringLockDegrees = 150 }; b.Save(Path.Combine(prof, "Speeder Bike.json"));
        var cfg = BridgeConfig.Load(Path.Combine(d, "wheelbridge.json"));
        int saves = 0;
        var pm = new ProfileManager(cfg, prof, () => saves++);

        Check.True(pm.OnVehicle(null) is null && pm.OnVehicle(VehicleClass.Unknown) is null, "switched on no vehicle");
        Check.True(pm.OnVehicle(VehicleClass.Car)?.Contains("Land Speeder") == true && cfg.SteeringLockDegrees == 300, "car profile");
        Check.True(pm.OnVehicle(VehicleClass.Car) is null, "re-switched to the same vehicle");
        cfg.SteeringLockDegrees = 310;   // unsaved tuning
        Check.True(pm.Modified, "modified not detected");
        var msg = pm.OnVehicle(VehicleClass.Bike);
        Check.True(msg?.Contains("unsaved") == true && cfg.SteeringLockDegrees == 310 && cfg.ActiveProfile == "Land Speeder", "unsaved tuning discarded: " + msg);
        Check.True(pm.OnVehicle(VehicleClass.Bike) is null, "refusal message repeated every poll");
        pm.Save("Land Speeder");
        Check.True(!pm.Modified && BridgeConfig.ReadFile(pm.PathOf("Land Speeder")).SteeringLockDegrees == 310, "save");
        Check.True(pm.OnVehicle(VehicleClass.Bike)?.Contains("Speeder Bike") == true && cfg.SteeringLockDegrees == 150, "bike profile");
        Check.True(pm.OnVehicle(VehicleClass.Pod)?.Contains("no profile") == true && cfg.SteeringLockDegrees == 150, "missing profile");
        cfg.AutoSwitchProfiles = false;
        Check.True(pm.OnVehicle(VehicleClass.Car) is null && cfg.SteeringLockDegrees == 150, "switched while auto is off");
        Check.True(pm.Save("bad:name").Contains("can't") && pm.Save("  ").Contains("can't") && pm.Save("..").Contains("can't"), "bad names accepted");
        Check.True(saves > 0, "config not saved after switching");
    }

    public static void Seeding()
    {
        string d = TempDir(), src = Path.Combine(d, "src"), dst = Path.Combine(d, "profiles");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "Podracer.json"), "{\"SteeringLockDegrees\": 200}");
        File.WriteAllText(Path.Combine(src, "Speeder Bike.json"), "{\"SteeringLockDegrees\": 180}");
        ProfileManager.SeedDefaults(src, dst);
        Check.True(File.Exists(Path.Combine(dst, "Podracer.json")), "not seeded");
        File.WriteAllText(Path.Combine(dst, "Speeder Bike.json"), "{\"SteeringLockDegrees\": 123}");   // user's own tuning
        var cfg = new BridgeConfig();
        var pm = new ProfileManager(cfg, dst, () => { });
        pm.Delete("Podracer");
        ProfileManager.SeedDefaults(src, dst);
        Check.True(!File.Exists(Path.Combine(dst, "Podracer.json")), "deleted default came back");
        Check.True(File.ReadAllText(Path.Combine(dst, "Speeder Bike.json")).Contains("123"), "user profile overwritten");
        Check.True(!pm.Names().Contains("Podracer.json") && pm.Names().SequenceEqual(["Speeder Bike"]), "names: " + string.Join(",", pm.Names()));
    }

    // changes/0349: the dashboard downloads the exe alone, so the shipped profiles must be inside it. Without them every
    // vehicle would run on the built-in default (the Land Speeder's Load reference on a Podracer).
    public static void Embedded()
    {
        var shipped = ProfileManager.EmbeddedDefaults(typeof(ProfileManager).Assembly);
        string defaults = DefaultsDir();
        var files = Directory.GetFiles(defaults, "*.json").Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Check.True(files.Length == 4, "defaults/profiles: " + string.Join(", ", files));
        Check.True(shipped.Select(s => s.Name).SequenceEqual(files), "inside the exe: " + string.Join(", ", shipped.Select(s => s.Name)));
        foreach (var (name, bytes) in shipped)
            Check.True(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(defaults, name))), name + " inside the exe differs from defaults/profiles");

        string dst = Path.Combine(TempDir(), "profiles");
        ProfileManager.SeedDefaults(shipped, dst);
        Check.True(Directory.GetFiles(dst, "*.json").Length == 4, "an empty folder was not seeded from the exe");
        var cfg = new BridgeConfig();
        var pm = new ProfileManager(cfg, dst, () => { });
        Check.True(pm.Names().Count() == 4, "names: " + string.Join(",", pm.Names()));
        File.WriteAllText(Path.Combine(dst, "Podracer.json"), "{\"SteeringLockDegrees\": 123}");   // the user's own tuning
        pm.Delete("Speeder Bike");
        ProfileManager.SeedDefaults(shipped, dst);
        Check.True(File.ReadAllText(Path.Combine(dst, "Podracer.json")).Contains("123"), "the user's profile was overwritten");
        Check.True(!File.Exists(Path.Combine(dst, "Speeder Bike.json")), "a deleted default came back");
        // a name with a folder in it is never written
        ProfileManager.SeedDefaults([("..\\evil.json", new byte[] { 1 }), ("sub/x.json", new byte[] { 1 })], dst);
        Check.True(!File.Exists(Path.Combine(Path.GetDirectoryName(dst)!, "evil.json")) && !Directory.Exists(Path.Combine(dst, "sub")), "a path escaped the profiles folder");
    }
}

static class ModelTests
{
    private static uint Hash(IEnumerable<float> xs) { uint h = 2166136261; foreach (var x in xs) h = (h ^ BitConverter.SingleToUInt32Bits(x)) * 16777619; return h; }

    private static List<float> Run(List<(TelemetryFrame f, float dt)> frames, FfbConfig cfg)
    {
        var m = new FfbModel(cfg);
        var outp = new List<float>();
        double now = 0; float last = 0;
        foreach (var (fr, dt) in frames)
        {
            m.OnFrame(fr, now);
            int steps = Math.Max(1, (int)Math.Round(Math.Max(0.001f, dt) * 500));
            float target = fr[F.SteerInput] * 90;
            for (int i = 0; i < steps; i++)
            {
                now += 0.002;
                float angle = last + (target - last) * (i + 1) / steps;
                outp.Add(m.Step(now, 0.002f, angle, (angle - last) * 500, 180, (0, 0, 9)));
            }
            last = target;
        }
        return outp;
    }

    public static void Deterministic()
    {
        var recs = Frames.Recordings().ToList();
        Check.True(recs.Count > 0, "no recordings found under Dumps\\recordings");
        foreach (var rec in recs)
        {
            var frames = Frames.Load(rec);
            var a = Run(frames, new FfbConfig());
            var b = Run(frames, new FfbConfig());
            Check.True(a.Count > 100, $"{Path.GetFileName(rec)}: only {a.Count} ticks");
            Check.True(Hash(a) == Hash(b), $"{Path.GetFileName(rec)} not deterministic");
            Check.True(a.All(float.IsFinite) && a.Any(t => Math.Abs(t) > 0.05f), $"{Path.GetFileName(rec)}: no force or NaN");
        }
    }

    public static void NaNInput()
    {
        var m = new FfbModel(new FfbConfig());
        double now = 0;
        for (int i = 0; i < 300; i++)
        {
            m.OnFrame(Frames.Parse(Frames.Racing((uint)i, now)), now);
            for (int k = 0; k < 8; k++)
            {
                now += 0.002;
                float angle = i == 150 && k == 0 ? float.NaN : 20, vel = i == 160 ? float.PositiveInfinity : 10;
                float t = m.Step(now, 0.002f, angle, vel, 180, (0, 0, 9));
                Check.True(float.IsFinite(t), $"NaN torque at frame {i}");
            }
        }
        float after = m.Step(now + 0.002, 0.002f, 20, 0, 180, (0, 0, 9));
        Check.True(float.IsFinite(after) && Math.Abs(after) > 0.01f, $"force didn't come back after a NaN input ({after})");
    }

    public static void MutedAfterFinish()
    {
        var m = new FfbModel(new FfbConfig());
        double now = 0;
        float last = 1;
        for (int i = 0; i < 120; i++)
        {
            m.OnFrame(Frames.Parse(Frames.Racing((uint)i, now, extra: FrameFlags.RaceDone,
                colls: [(0, -1, 0, 100, 60, 0, 2500, 1000 + i, true)])), now);
            for (int k = 0; k < 8; k++) { now += 0.002; last = m.Step(now, 0.002f, 40, 300, 180, (1, 1, 0)); }
        }
        Check.True(m.State.Contains("finished") && !m.Racing, "state: " + m.State);
        Check.True(last == 0, $"force after finish: {last}");
    }

    public static void Pause()
    {
        var m = new FfbModel(new FfbConfig());
        var frozen = Frames.Parse(Frames.Racing(1, 5));
        for (int i = 0; i < 5; i++) m.OnFrame(frozen, i * 0.016);
        Check.True(m.Paused, "frozen frames at speed not detected as paused");
        m.OnFrame(Frames.Parse(Frames.Racing(2, 5.1)), 0.1);
        Check.True(!m.Paused, "still paused after motion");
    }

    public static void ImpactsBounded()
    {
        var m = new FfbModel(new FfbConfig());
        var impacts = (System.Collections.ICollection)typeof(FfbModel).GetField("_impacts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;
        double now = 0;
        for (int i = 0; i < 20_000; i++)
        {
            // 4 new contacts every frame during a long muted run-out; no Step calls with live force.
            var colls = Enumerable.Range(0, 4).Select(k => (0f, -1f, 0f, 100f, 60f, 0f, 3000f, i * 4 + k, false)).ToArray();
            m.OnFrame(Frames.Parse(Frames.Racing((uint)i, now, extra: FrameFlags.RaceDone, colls: colls)), now);
            now += 1 / 60.0;
        }
        Check.True(impacts.Count < 200, $"{impacts.Count} queued impacts");
    }

    public static void Lean()
    {
        float Run(float roll, float gain)
        {
            var cfg = new FfbConfig();
            foreach (var l in cfg.Layers.Values) l.Enabled = false;
            cfg.Layers[FfbLayers.Lean].Enabled = true;
            cfg.Layers[FfbLayers.Lean].Gain = gain;
            var m = new FfbModel(cfg);
            double now = 0; float t = 0;
            for (int i = 0; i < 120; i++)
            {
                var raw = Frames.Racing((uint)i, now);
                BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(TelemetryFrame.HeaderSize + (int)F.Roll * 4), roll);
                m.OnFrame(Frames.Parse(raw), now);
                for (int k = 0; k < 8; k++) { now += 0.002; t = m.Step(now, 0.002f, 0, 0, 180, (0, 0, 9)); }
            }
            return t;
        }
        // Roll < 0 = leaning right (follows right steer) → push left (negative), and the reverse.
        float right = Run(-1.2f, 0.45f), left = Run(1.2f, 0.45f);
        Check.True(right < -0.1f && left > 0.1f, $"lean push right-lean {right}, left-lean {left}");
        Check.Near(right, -left, 1e-4, "lean not symmetric");
        Check.True(Run(-1.2f, 0) == 0 && Run(0, 0.45f) == 0, "lean force with gain 0 or upright");
        Check.True(float.IsFinite(Run(float.NaN, 0.45f)), "NaN roll");
    }

    public static void LeanUnwind()
    {
        var cfg = new FfbConfig();
        foreach (var l in cfg.Layers.Values) l.Enabled = false;
        cfg.Layers[FfbLayers.Lean].Enabled = true;
        cfg.Layers[FfbLayers.Lean].Gain = 1;
        var m = new FfbModel(cfg);
        double now = 0;
        // Lean right (roll -1.4), then straighten up fast (0.25 s): the push must stay toward upright (left, <= 0) the whole
        // time. The old lean-in kick pushed right (toward the lean) while unwinding, so the wheel ran away.
        for (int i = 0; i < 180; i++)
        {
            float roll = i < 90 ? -1.4f : Math.Min(0, -1.4f + (i - 90) * 1.4f / 15);
            var raw = Frames.Racing((uint)i, now);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(TelemetryFrame.HeaderSize + (int)F.Roll * 4), roll);
            m.OnFrame(Frames.Parse(raw), now);
            for (int k = 0; k < 8; k++)
            {
                now += 0.002;
                float t = m.Step(now, 0.002f, 0, 0, 180, (0, 0, 9));
                Check.True(t <= 1e-6f, $"lean pushed toward the lean ({t:F3}) at frame {i}, roll {roll:F2}");
            }
        }
        // The shipped bike profile keeps it light: at most ~0.15 of full torque from a full lean.
        string bike = Path.Combine(AppContext.BaseDirectory, "defaults", "profiles", "Speeder Bike.json");
        if (!File.Exists(bike)) bike = Path.Combine(ConfigTests.DefaultsDir(), "Speeder Bike.json");
        float gain = BridgeConfig.ReadFile(bike).Ffb.Layers[FfbLayers.Lean].Gain;
        Check.True(gain is > 0 and <= 0.15f, $"Speeder Bike lean gain {gain}");
    }

    public static void NoVehicleClears()
    {
        var m = new FfbModel(new FfbConfig());
        var wreck = Frames.Parse(Frames.Build(f => { f[(int)F.CrashTimeRemaining] = 2; f[(int)F.SpeedKmph] = 10; f[(int)F.QuatW] = 1; },
            bytes: [0, 0, 0, 0, 0, 3, 0, 0]));
        m.OnFrame(wreck, 0);
        m.Step(0.002, 0.002f, 0, 0, 180, (0, 0, 9));
        Check.True(m.Wrecked && m.Airborne, "setup: wrecked and airborne");
        m.OnFrame(Frames.Parse(Frames.Build(flags: FrameFlags.OnFoot)), 0.01);
        Check.True(!m.Wrecked && !m.Airborne, "flags kept after leaving the vehicle");
    }
}

static class ForceTests
{
    /// <summary>
    /// Runs the force-center hold against a simulated wheel: 0.03 kg·m² rim, 3.9 Nm at full output, 10 ms from torque
    /// to motor, end stops at ±90°. motorSign -1 is a motor that turns the other way than ForceSign says.
    /// </summary>
    private static (bool Released, float Angle, string? Fault, float MaxAngle) Hold(int motorSign, float startDeg, double seconds = 3)
    {
        var eng = (FfbEngine)RuntimeHelpers.GetUninitializedObject(typeof(FfbEngine));
        typeof(FfbEngine).GetField("_cfg", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(eng, new BridgeConfig());
        var torque = typeof(FfbEngine).GetMethod("CenterLockTorque", BindingFlags.NonPublic | BindingFlags.Instance)!;
        eng.CenterLock = true;
        const float dt = 0.002f, inertia = 0.03f, peakNm = 3.9f;
        var delay = new Queue<float>(Enumerable.Repeat(0f, 5));
        float angle = startDeg, vel = 0, max = 0;
        for (int i = 0; i < seconds / dt; i++)
        {
            float t = (float)torque.Invoke(eng, [angle, vel, dt])!;
            if (float.IsNaN(t)) return (true, angle, eng.CenterLockFault, max);
            delay.Enqueue(t);
            float nm = motorSign * delay.Dequeue() * peakNm - 0.02f * vel / 57.3f;   // plus a little bearing friction
            vel += nm / inertia * 57.3f * dt;
            angle += vel * dt;
            if (Math.Abs(angle) > 90) { angle = Math.Clamp(angle, -90, 90); vel = -0.3f * vel; }
            max = Math.Max(max, Math.Abs(angle));
        }
        return (false, angle, eng.CenterLockFault, max);
    }

    public static void CenterLock()
    {
        foreach (float start in new[] { 60f, -45f, 5f })
        {
            var ok = Hold(+1, start);
            Check.True(!ok.Released && ok.Fault is null, $"correct motor from {start}°: released ({ok.Fault})");
            Check.True(Math.Abs(ok.Angle) < 1.5f, $"correct motor from {start}°: ended at {ok.Angle:F1}°");
            Check.True(ok.MaxAngle <= Math.Abs(start) + 2, $"correct motor from {start}°: swung out to {ok.MaxAngle:F1}°");
        }
        foreach (float start in new[] { 30f, -10f, 0.5f })
        {
            var bad = Hold(-1, start, seconds: 1.5);
            Check.True(bad.Released && bad.Fault?.Contains("Motor test") == true, $"wrong motor direction from {start}°: never let go (max {bad.MaxAngle:F0}°)");
        }
    }
}

static class InputTests
{
    private static WheelState State(int thr = -32768, int brk = -32768, int clu = -32768, params int[] pressed) =>
        new(0, 0, thr, brk, clu, pressed.ToHashSet(), DateTime.UtcNow);

    private static BridgeConfig Pedalled()
    {
        var c = new BridgeConfig
        {
            Throttle = new PedalRange { Released = -32768, Pressed = 32767 },
            Brake = new PedalRange { Released = -32768, Pressed = 32767 },
            Clutch = new PedalRange { Released = -32768, Pressed = 32767 },
        };
        c.Sanitize();
        return c;
    }

    public static void Bindings()
    {
        var c = Pedalled();
        c.Buttons = new() { ["A"] = [14], ["RB"] = [22, 23] };
        c.PedalButtons = [new PedalButton { Button = "X", Pedal = "Clutch", Threshold = 0.5f }];
        var down = new bool[PadMapping.Buttons.Length];
        int Ix(string b) => Array.IndexOf(PadMapping.Buttons, b);

        var (_, rt, lt) = PadMapping.Map(State(thr: 32767, brk: -32768, clu: 32767, pressed: [14, 23]), c, down);
        Check.True(rt == 1 && lt == 0, $"pedal triggers {rt}/{lt}");
        Check.True(down[Ix("A")] && down[Ix("RB")] && down[Ix("X")] && !down[Ix("B")], "buttons: " + string.Join(",", PadMapping.Buttons.Where((_, i) => down[i])));

        PadMapping.Map(State(clu: -20000), c, down);
        Check.True(!down[Ix("X")], "clutch below threshold pressed X");

        // No pedals at all: triggers on paddles.
        c.RightTriggerPedal = "None"; c.LeftTriggerPedal = "None";
        c.Throttle = c.Brake = c.Clutch = null;
        c.TriggerButtons = new() { ["RT"] = [40], ["LT"] = [41] };
        (_, rt, lt) = PadMapping.Map(State(pressed: [40]), c, down);
        Check.True(rt == 1 && lt == 0, $"paddle RT {rt}/{lt}");
        (_, rt, lt) = PadMapping.Map(State(), c, down);
        Check.True(rt == 0 && lt == 0, "paddles released");

        // A pedal and a button on the same trigger: the larger wins.
        c = Pedalled();
        c.TriggerButtons = new() { ["RT"] = [40], ["LT"] = [] };
        (_, rt, _) = PadMapping.Map(State(thr: 0, pressed: [40]), c, down);
        Check.True(rt == 1, "button didn't override half throttle");
        (_, rt, _) = PadMapping.Map(State(thr: 0), c, down);
        Check.True(rt is > 0.4f and < 0.6f, $"half throttle {rt}");

        // Sanitize fills a missing TriggerButtons section.
        var raw = new BridgeConfig { TriggerButtons = null! };
        raw.Sanitize();
        Check.True(raw.TriggerButtons["RT"] is not null && raw.TriggerButtons["LT"] is not null, "TriggerButtons not repaired");
    }

    public static void BindingText()
    {
        var c = Pedalled();
        c.Buttons = new() { ["A"] = [14], ["RB"] = [14] };
        c.TriggerButtons = new() { ["RT"] = [40], ["LT"] = [] };
        c.PedalButtons = [new PedalButton { Button = "X", Pedal = "Clutch", Threshold = 0.5f }];
        Check.True(PadMapping.Describe("RT", c) == "throttle pedal, button 41", "RT: " + PadMapping.Describe("RT", c));
        Check.True(PadMapping.Describe("X", c).Contains("clutch"), "X: " + PadMapping.Describe("X", c));
        Check.True(PadMapping.Describe("Y", c) == "not bound", "Y");
        c.LeftTriggerPedal = "None";
        Check.True(PadMapping.Describe("LT", c) == "not bound", "LT none: " + PadMapping.Describe("LT", c));
        Check.True(PadMapping.ControlsUsing(14, c).Count == 2, "duplicate not found");
        Check.True(PadMapping.Buttons.All(PadMapping.GameFunction.ContainsKey) && PadMapping.Triggers.All(PadMapping.GameFunction.ContainsKey), "game function list incomplete");
    }

    public static void Capture()
    {
        var c = Pedalled();
        var cap = new BindCapture("A", State(pressed: [3]), c, TimeSpan.FromSeconds(6));
        Check.True(cap.Poll(State(pressed: [3]), c) == (null, null), "a button held at the start was captured");
        Check.True(cap.Poll(State(), c) == (null, null), "release captured");
        Check.True(cap.Poll(State(pressed: [3]), c) == (3, null), "re-pressed button not captured");
        cap = new BindCapture("X", State(), c, null);
        Check.True(cap.Poll(State(clu: 32767), c) == (null, "Clutch") && !cap.TimedOut, "pedal not captured");
        cap = new BindCapture("RT", State(), c, null);
        Check.True(cap.Poll(State(thr: 32767), c) == (null, null), "a trigger captured a pedal");
        Check.True(new BindCapture("A", State(), c, TimeSpan.FromMilliseconds(-1)).TimedOut, "timeout");

        c.Buttons = new() { ["A"] = [1], ["B"] = [] };
        PadMapping.BindButton(c, "A", 2);
        Check.True(c.Buttons["A"].SequenceEqual([1, 2]), "add: " + string.Join(",", c.Buttons["A"]));
        c.PedalButtons = [new PedalButton { Button = "A", Pedal = "Clutch" }];
        string msg = PadMapping.BindButton(c, "B", 2, replace: true);
        Check.True(msg.Contains("also bound to A") && c.Buttons["B"].SequenceEqual([2]), "replace / duplicate note: " + msg);
        PadMapping.BindButton(c, "A", 7, replace: true);
        Check.True(c.Buttons["A"].SequenceEqual([7]) && c.PedalButtons.Count == 0, "replace kept the old inputs");
        PadMapping.BindPedal(c, "A", "Brake", replace: true);
        Check.True(c.Buttons["A"].Length == 0 && c.PedalButtons.Single().Pedal == "Brake", "pedal replace");
        PadMapping.BindButton(c, "LT", 40);
        Check.True(c.TriggerButtons["LT"].SequenceEqual([40]), "trigger button");
        PadMapping.ClearBinding(c, "LT");
        PadMapping.ClearBinding(c, "A");
        Check.True(c.TriggerButtons["LT"].Length == 0 && c.Buttons["A"].Length == 0 && c.PedalButtons.Count == 0 && c.LeftTriggerPedal == "Brake", "clear");
    }

    public static void Steering()
    {
        var c = new BridgeConfig { SteeringLockDegrees = 180, SteeringAntiDeadzone = 0.31f, SteeringLinearity = 1.3f };
        float prev = -2;
        for (float a = -120; a <= 120; a += 0.5f)
        {
            float s = VirtualPad.SteeringToStick(a, c);
            Check.True(s >= prev - 1e-6f && s is >= -1 and <= 1, $"not monotonic/limited at {a}°");
            Check.Near(s, -VirtualPad.SteeringToStick(-a, c), 1e-6, $"asymmetric at {a}°");
            prev = s;
        }
        Check.True(VirtualPad.SteeringToStick(0, c) == 0, "center not 0");
        Check.Near(VirtualPad.SteeringToStick(90, c), 1, 1e-6, "lock not 1");
        Check.True(VirtualPad.SteeringToStick(1, c) >= 0.31f, "anti-deadzone missing");
        c.InvertSteering = true;
        Check.True(VirtualPad.SteeringToStick(45, c) < 0, "invert");
    }

    public static void Pedals()
    {
        var r = new PedalRange { Released = -32768, Pressed = 32767 };
        var rev = new PedalRange { Released = 32767, Pressed = -32768 };
        foreach (float dz in new[] { 0f, 0.03f, 0.2f, 0.5f, 0.9f, -1f })
            foreach (int raw in new[] { -32768, -1000, 0, 20000, 32767 })
            {
                float v = r.Normalize(raw, dz), w = rev.Normalize(raw, dz);
                Check.True(float.IsFinite(v) && v is >= 0 and <= 1 && float.IsFinite(w) && w is >= 0 and <= 1, $"dz {dz} raw {raw}: {v}/{w}");
            }
        Check.Near(r.Normalize(32767, 0.03f), 1, 1e-6, "full press");
        Check.Near(rev.Normalize(32767, 0.03f), 0, 1e-6, "reversed released");
        Check.True(new PedalRange { Released = 5, Pressed = 5 }.Normalize(100, 0) == 0, "zero span");
    }
}

static class PerfTests
{
    public static void Budget()
    {
        var cfg = new FfbConfig();
        var m = new FfbModel(cfg);
        var raw = Frames.Racing(1, 0, colls: [(0, -1, 0, 100, 60, 0, 2500, 7, true)]);
        var frames = Enumerable.Range(0, 64).Select(i => Frames.Parse(Frames.Racing((uint)i, i / 60.0, colls: [(0, -1, 0, 100, 60, 0, 2500, i % 3, true)]))).ToArray();

        // Warm up, then measure the 500 Hz model step and the per-frame work.
        double now = 0;
        for (int i = 0; i < 20_000; i++) { if (i % 8 == 0) m.OnFrame(frames[i / 8 % 64], now); now += 0.002; m.Step(now, 0.002f, 10, 50, 180, (0.5f, 0.5f, 0)); }

        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        const int N = 200_000;
        for (int i = 0; i < N; i++) { now += 0.002; m.Step(now, 0.002f, 10, 50, 180, (0.5f, 0.5f, 0)); }
        double stepUs = sw.Elapsed.TotalMilliseconds * 1000 / N;
        long stepAlloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / N;

        a0 = GC.GetAllocatedBytesForCurrentThread();
        sw.Restart();
        const int P = 100_000;
        for (int i = 0; i < P; i++) { var f = TelemetryFrame.Parse(raw, out _)!; m.OnFrame(f, now); }
        double frameUs = sw.Elapsed.TotalMilliseconds * 1000 / P;
        long frameAlloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / P;

        Console.WriteLine($"      model step {stepUs:F2} µs ({stepAlloc} B alloc), budget 2000 µs at 500 Hz");
        Console.WriteLine($"      parse + OnFrame {frameUs:F2} µs ({frameAlloc} B alloc) per frame");
        Check.True(stepUs < 50, "model step too slow");
        Check.True(stepAlloc < 64, $"model step allocates {stepAlloc} B per tick (GC pressure at 500 Hz)");
        Check.True(frameUs < 100, "frame handling too slow");
    }
}

// ───────────────────────────── changes/0349: Game Link Wheel ─────────────────────────────

static class Net
{
    public static int FreePort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}

static class VnsgTests
{
    public static readonly string[] Racing = ["HasVehicle", "Valid", "Controlled"];

    private static int Index(string[] names, string n)
    {
        int i = Array.IndexOf(names, n);
        return i >= 0 ? i : throw new Exception("no VNSG field " + n);
    }

    /// <summary>A VNSG v1 frame as the UE4SS mod packs it (changes/0340): floats and bytes by name.</summary>
    public static byte[] Build(uint seq = 1, double t = 0, string[]? flags = null, byte cls = 1,
                               Dictionary<string, float>? f = null, Dictionary<string, byte>? b = null, byte nColl = 0)
    {
        var a = new byte[Vnsg.Size];
        var s = a.AsSpan();
        "VNSG"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], (ushort)Vnsg.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], (ushort)Vnsg.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], seq);
        BinaryPrimitives.WriteDoubleLittleEndian(s[12..], t);
        BinaryPrimitives.WriteSingleLittleEndian(s[20..], 1 / 60f);
        uint bits = 0;
        foreach (var n in flags ?? Racing) bits |= 1u << Index(Vnsg.FlagNames, n);
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], bits);
        s[28] = cls;
        s[29] = nColl;
        var floats = new Dictionary<string, float> { ["QuatW"] = 1 };
        foreach (var (k, v) in f ?? []) floats[k] = v;
        foreach (var (k, v) in floats) BinaryPrimitives.WriteSingleLittleEndian(s[(Vnsg.FloatsAt + 4 * Index(Vnsg.FloatNames, k))..], v);
        foreach (var (k, v) in b ?? []) s[Vnsg.BytesAt + Index(Vnsg.ByteNames, k)] = v;
        return a;
    }

    /// <summary>The quaternion of a heading (radians) about +Z.</summary>
    public static Dictionary<string, float> Heading(double h) => new() { ["QuatZ"] = (float)Math.Sin(h / 2), ["QuatW"] = (float)Math.Cos(h / 2) };

    public static void Fields()
    {
        // Every VNSG float a distinct value and the header's reserved bytes set, so a field read from the wrong place shows.
        var f = new Dictionary<string, float>();
        for (int i = 0; i < Vnsg.FloatNames.Length; i++) f[Vnsg.FloatNames[i]] = 100 + i;
        f["QuatX"] = 0; f["QuatY"] = 0; f["QuatZ"] = 0; f["QuatW"] = 1;
        var b = new Dictionary<string, byte>();
        for (int i = 0; i < Vnsg.ByteNames.Length; i++) b[Vnsg.ByteNames[i]] = (byte)(10 + i);
        var raw = Build(seq: 77, t: 12.5, flags: [.. Racing, "Drifting", "UpsideDown", "BoostActive", "CollisionsOk", "RaceDone", "OnFoot"],
                        cls: 4, f: f, b: b, nColl: 2);
        raw[30] = 0xEE; raw[31] = 0xEE;
        for (int c = 0; c < 2; c++)
        {
            int p = Vnsg.CollisionsAt + c * Vnsg.CollisionSize;
            for (int k = 0; k < 7; k++) BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(p + k * 4), 1000 * (c + 1) + k);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(p + 28), 4242 + c);
            raw[p + 32] = (byte)c;
        }
        var fr = Vnsg.ToBridgeFrame(raw, new Vnsg.YawRate());
        Check.True(fr.Seq == 77 && fr.SimTime == 12.5 && fr.VehicleClass == VehicleClass.Pod, "header");
        Check.True(fr.Has(FrameFlags.HasVehicle) && fr.Has(FrameFlags.Drifting) && fr.Has(FrameFlags.UpsideDown) && fr.Has(FrameFlags.BoostToggled)
                   && fr.Has(FrameFlags.CollisionsOk) && fr.Has(FrameFlags.RaceDone) && fr.Has(FrameFlags.OnFoot), "flags");
        F[] notCarried = [F.LevitationScale, F.SpeedScale, F.RamjetBypassHeat, F.BypassOverheatPct, F.AirBrakeScale, F.PredictedHitTime];
        foreach (var x in Enum.GetValues<F>())
        {
            float want = x switch
            {
                F.AngVelX => f["RelAngVelX"],
                F.AngVelY => f["RelAngVelY"],
                F.AngVelZ => 0,   // the yaw rate from the heading: none yet on a first frame
                F.SteerInput or F.SteerIntent => f["Steer"],
                F.BoostTransitionScale => f["BoostTransition"],
                _ when notCarried.Contains(x) => 0,
                _ => f[x.ToString()],
            };
            Check.True(fr[x] == want, $"{x}: {fr[x]}, want {want}");
        }
        foreach (var x in Enum.GetValues<B>()) Check.True(fr[x] == b[x.ToString()], $"{x}: {fr[x]}");
        Check.True(fr.CollisionCount == 2, "collision count");
        for (int c = 0; c < 2; c++)
        {
            var col = fr.Collisions[c];
            float at = 1000 * (c + 1);
            Check.True(col.WorldDirection.X == at && col.WorldDirection.Z == at + 2 && col.LocalLocation.X == at + 3 && col.LocalLocation.Z == at + 5
                       && col.PointVelocityDelta == at + 6 && col.Id == 4242 + c && col.OtherIsVehicle == (c == 1), $"collision {c}");
        }
    }

    public static void YawRate()
    {
        // + right: forward turned toward +Y (the game's right) by a positive turn about +Z.
        Check.Near(Vnsg.Heading(0, 0, (float)Math.Sin(Math.PI / 4), (float)Math.Cos(Math.PI / 4)), Math.PI / 2, 1e-6, "heading of +90° about Z");
        var yaw = new Vnsg.YawRate();
        float At(double t, double h, byte cls = 1, string[]? flags = null) =>
            Vnsg.ToBridgeFrame(Build(t: t, cls: cls, flags: flags, f: Heading(h)), yaw)[F.AngVelZ];
        At(0, 0);
        Check.Near(At(0.02, 0.02), 1, 1e-3, "right turn at 1 rad/s");
        Check.Near(At(0.04, 0.0), -1, 1e-3, "left turn");
        Check.True(At(1.00, Math.PI - 0.01) == 0, "across a 0.96 s gap");
        Check.Near(At(1.02, -Math.PI + 0.01), 1, 1e-3, "across ±180°");
        Check.True(At(1.25, -Math.PI + 0.3) == 0, "a 0.23 s step is not a rate");
        Check.True(At(1.27, -Math.PI + 0.32, cls: 2) == 0, "across a vehicle change");
        Check.True(At(1.29, -Math.PI + 0.34, cls: 2, flags: ["HasVehicle", "Controlled"]) == 0, "a frame that is not Valid");
        Check.True(At(1.31, -Math.PI + 0.36, cls: 2) == 0, "the first frame after one that was not Valid");
        Check.Near(At(1.33, -Math.PI + 0.38, cls: 2), 1, 1e-3, "back to normal");
        Check.True(At(1.33, -Math.PI + 0.40, cls: 2) == 0, "a repeated clock");
    }

    public static void Muting()
    {
        var mode = new Dictionary<string, byte> { ["InputMode"] = 3 };
        var moving = new Dictionary<string, float> { ["SpeedKmph"] = 200, ["TopSpeedKmph"] = 346, ["OnGroundScale"] = 1, ["QuatW"] = 1 };
        // A moving vehicle never repeats its orientation bit for bit (frozen frames at speed are the model's pause menu).
        TelemetryFrame Frame(string[] flags, uint seq = 1, double t = 0)
        {
            var fl = new Dictionary<string, float>(moving);
            foreach (var (k, v) in Heading(t * 0.37)) fl[k] = v;
            return Vnsg.ToBridgeFrame(Build(seq: seq, t: t, flags: flags, f: fl, b: mode), new Vnsg.YawRate());
        }
        var ok = Frame(Racing);
        Check.True(ok.Has(FrameFlags.HasVehicle) && ok[B.InputMode] == 3 && ok[F.SpeedKmph] == 200, "racing frame");
        var invalid = Frame(["HasVehicle", "Controlled"]);
        Check.True(!invalid.Has(FrameFlags.HasVehicle) && invalid[F.SpeedKmph] == 0 && invalid[B.InputMode] == 0, "a frame that is not Valid reads as a vehicle");
        foreach (var why in new[] { "Paused", "Cinematic" }) Check.True(Frame([.. Racing, why])[B.InputMode] == 0, $"{why}: not muted");
        Check.True(Frame(["HasVehicle", "Valid"])[B.InputMode] == 0, "not Controlled: not muted");
        // And the model goes quiet on them: force while racing, none (after its fade) in a cinematic.
        var m = new FfbModel(new FfbConfig());
        double now = 0; float t = 0;
        for (int i = 0; i < 60; i++) { m.OnFrame(Frame(Racing, (uint)i, now), now); for (int k = 0; k < 8; k++) { now += 0.002; t = m.Step(now, 0.002f, 40, 0, 360, (0, 0, 9)); } }
        Check.True(m.Racing && Math.Abs(t) > 0.01f, $"no force while racing ({t})");
        for (int i = 60; i < 180; i++) { m.OnFrame(Frame([.. Racing, "Cinematic"], (uint)i, now), now); for (int k = 0; k < 8; k++) { now += 0.002; t = m.Step(now, 0.002f, 40, 0, 360, (0, 0, 9)); } }
        Check.True(!m.Racing && t == 0, $"force in a cinematic ({t})");
    }

    public static void Receiver()
    {
        var seqs = new List<uint>();
        int calls = 0;
        var rx = new VnsgReceiver(0, (f, raw) => { if (++calls == 2) throw new IOException("disk full"); seqs.Add(f.Seq); });
        foreach (uint s in new uint[] { 1, 2, 2, 1, 3 }) rx.Handle(Build(seq: s));
        Check.True(seqs.SequenceEqual([1u, 3u]) && calls == 3, "order / repeats: " + string.Join(",", seqs));
        Check.True(rx.LastError?.Contains("disk full") == true, "handler error not reported: " + rx.LastError);
        rx.Handle("garbage"u8.ToArray());
        var v2 = Build(seq: 4); v2[4] = 2;
        rx.Handle(v2);
        Check.True(rx.BadFrames == 2 && rx.OldFrames == 2, $"bad {rx.BadFrames}, old {rx.OldFrames}");
        rx.Handle(Build(seq: 5000)); rx.Handle(Build(seq: 1));   // the count starts again: a game restart
        Check.True(seqs.SequenceEqual([1u, 3u, 5000u, 1u]), "restart: " + string.Join(",", seqs));
    }

    public static void Udp()
    {
        int port = Net.FreePort();
        var got = new List<uint>();
        using var rx = new VnsgReceiver(port, (f, _) => { lock (got) got.Add(f.Seq); });
        using var cts = new CancellationTokenSource();
        var task = Task.Run(() => rx.RunAsync(cts.Token));
        using var tx = new UdpClient();
        var to = new IPEndPoint(IPAddress.Loopback, port);
        var sw = Stopwatch.StartNew();
        uint seq = 0;
        while (sw.ElapsedMilliseconds < 3000)
        {
            var d = Build(seq: ++seq);
            tx.Send(d, d.Length, to);
            Thread.Sleep(5);
            lock (got) if (got.Count >= 3) break;
        }
        cts.Cancel();
        task.Wait(2000);
        lock (got) Check.True(got.Count >= 3 && got.SequenceEqual(got.Order()), "frames over UDP: " + string.Join(",", got));
        Check.True(rx.LastError is null, "error: " + rx.LastError);
    }
}

static class DeviceTests
{
    public static readonly Guid A = new("aaaaaaaa-0000-0000-0000-000000000001"), Bd = new("bbbbbbbb-0000-0000-0000-000000000002"),
                                C = new("cccccccc-0000-0000-0000-000000000003");

    public static DeviceSnapshot Snap(Guid g, string name, int[]? axes = null, int[]? buttons = null, int[]? povs = null)
    {
        var b = new bool[128];
        foreach (int i in buttons ?? []) b[i] = true;
        return new DeviceSnapshot(g, name, axes ?? Axes(), b, povs ?? [-1, -1, -1, -1]);
    }

    public static int[] Axes(int x = 32768, int y = 32768, int z = 32768) => [x, y, z, 32768, 32768, 32768, 32768, 32768];

    private static BridgeConfig Wheel(bool invert = false, float travel = 900)
    {
        var c = new BridgeConfig { Steering = new AxisRef { Device = A, DeviceName = "Base", Axis = 0, Invert = invert }, SteeringTravelDegrees = travel };
        c.Sanitize();
        return c;
    }

    private static float Angle(int raw, BridgeConfig cfg) => WheelInput.Compose([Snap(A, "Base", Axes(x: raw))], cfg, out _)!.AngleDegrees;

    public static void Steering()
    {
        var c = Wheel();
        Check.Near(Angle(65535, c), 450, 0.01, "full right");
        Check.Near(Angle(0, c), -450, 0.01, "full left");
        Check.Near(Angle(32768, c), 0, 0.01, "center");
        Check.Near(Angle(65535, Wheel(invert: true)), -450, 0.01, "reversed axis");
        Check.Near(Angle(65535, Wheel(travel: 540)), 270, 0.01, "a 540° wheel");
        // Setup's "turn the wheel to the right": whether the value goes up or down, right comes out +.
        foreach (int to in new[] { 60000, 5000 })
        {
            var found = new AxisCapture([Snap(A, "Base")]).Poll([Snap(A, "Base", Axes(x: to))]) ?? throw new Exception("not captured");
            var cfg = new BridgeConfig { Steering = AxisCapture.Steering(found) };
            Check.True(Angle(to, cfg) > 0, $"turning right to raw {to} reads {Angle(to, cfg):F0}°");
        }
    }

    public static void Unplugged()
    {
        var c = Wheel();
        Check.True(WheelInput.Compose([], c, out var st) is null && st.Contains("no controller"), "no controllers: " + st);
        Check.True(WheelInput.Compose([Snap(Bd, "Pedals")], c, out st) is null && st.Contains("not connected"), "wheel unplugged: " + st);
        Check.True(WheelInput.Compose([Snap(A, "Base")], new BridgeConfig(), out st) is null && st.Contains("not set up"), "nothing bound: " + st);
        // Reversed pedals (released at 65535) on their own controller: unplugged, they read released, never full travel.
        c.ThrottleAxis = new AxisRef { Device = Bd, DeviceName = "Pedals", Axis = 1 };
        c.Throttle = new PedalRange { Released = 65535, Pressed = 0 };
        c.BrakeAxis = new AxisRef { Device = Bd, DeviceName = "Pedals", Axis = 2 };
        c.Brake = new PedalRange { Released = 65535, Pressed = 0 };
        var down = new bool[PadMapping.Buttons.Length];
        var (_, rt, lt) = PadMapping.Map(WheelInput.Compose([Snap(A, "Base")], c, out _)!, c, down);
        Check.True(rt == 0 && lt == 0, $"unplugged reversed pedals: RT {rt}, LT {lt}");
        (_, rt, lt) = PadMapping.Map(WheelInput.Compose([Snap(A, "Base"), Snap(Bd, "Pedals", Axes(y: 0, z: 65535))], c, out _)!, c, down);
        Check.True(rt == 1 && lt == 0, $"plugged in: RT {rt}, LT {lt}");
    }

    public static void Replugged()
    {
        var c = Wheel();
        var moved = new Guid("aaaaaaaa-9999-0000-0000-000000000001");
        Check.True(WheelInput.FindDevice([Snap(moved, "Base")], A, "Base", c)?.Instance == moved, "a replugged wheel (new instance, same name) not found");
        Check.True(WheelInput.Compose([Snap(moved, "Base", Axes(x: 65535))], c, out _)?.AngleDegrees > 400, "the replugged wheel doesn't steer");
        Check.True(WheelInput.FindDevice([Snap(moved, "Base"), Snap(C, "Base")], A, "Base", c) is null, "one of two same-named controllers taken");
        c.Devices = [new DeviceRef { Instance = A, Name = "Base" }, new DeviceRef { Instance = C, Name = "Base" }];
        Check.True(WheelInput.FindDevice([Snap(C, "Base")], A, "Base", c) is null, "a twin bound by its own instance stood in");
        Check.True(FfbDevice.Pick([(moved, "Base")], A, "Base") == moved, "FFB: a replugged base not picked");
        Check.True(FfbDevice.Pick([(moved, "Base"), (C, "Base")], A, "Base") == Guid.Empty, "FFB: one of two same-named bases picked");
        Check.True(FfbDevice.Pick([(C, "Other")], A, "Base") == Guid.Empty, "FFB: another base picked");
        Check.True(FfbDevice.Pick([(A, "Base")], Guid.Empty, "Base") == Guid.Empty, "FFB: a base picked when none was chosen");
        Check.True(FfbDevice.Pick([(A, "Renamed"), (moved, "Base")], A, "Base") == A, "FFB: the name won over the instance");
    }

    public static void Buttons()
    {
        var c = Wheel();
        c.Devices = [new DeviceRef { Instance = A, Name = "Base" }, new DeviceRef { Instance = Bd, Name = "Box" }];
        var s = WheelInput.Compose([Snap(A, "Base", buttons: [4]), Snap(Bd, "Box", buttons: [0], povs: [4500, -1, -1, -1]), Snap(C, "Shifter", buttons: [1])], c, out _)!;
        Check.True(s.Pressed.SetEquals([4, 1000, 1128, 1129]), "pressed: " + string.Join(",", s.Pressed.Order()));
        Check.True(ButtonIds.Describe(4, c) == "Base: button 5" && ButtonIds.Describe(1000, c) == "Box: button 1" && ButtonIds.Describe(1131, c) == "Box: hat 1 left",
            $"{ButtonIds.Describe(4, c)} / {ButtonIds.Describe(1000, c)} / {ButtonIds.Describe(1131, c)}");
        Check.True(ButtonIds.Describe(41, new BridgeConfig()) == "button 42", "one controller: " + ButtonIds.Describe(41, new BridgeConfig()));
        Check.True(ButtonIds.HatDirections(0).SequenceEqual([0]) && ButtonIds.HatDirections(13500).SequenceEqual([1, 2])
                   && !ButtonIds.HatDirections(-1).Any() && !ButtonIds.HatDirections(65535).Any(), "hat directions");
        c.Buttons["A"] = [1000];
        var down = new bool[PadMapping.Buttons.Length];
        PadMapping.Map(s, c, down);
        Check.True(down[Array.IndexOf(PadMapping.Buttons, "A")], "a button box's button didn't press A");
    }

    public static void Capture()
    {
        var c = Wheel();
        c.Devices = [new DeviceRef { Instance = A, Name = "Base" }];
        WheelState S(params DeviceSnapshot[] d) => WheelInput.Compose(d, c, out _)!;
        var cap = new BindCapture("A", S(Snap(A, "Base", buttons: [2])), c, TimeSpan.FromSeconds(6));
        Check.True(cap.Poll(S(Snap(A, "Base", buttons: [2])), c) == (null, null), "a button held at the start was captured");
        var (btn, _) = cap.Poll(S(Snap(A, "Base", buttons: [2]), Snap(Bd, "Box", buttons: [7])), c);
        Check.True(btn == 1007 && c.Devices.Count == 2 && c.Devices[1].Instance == Bd && c.Devices[1].Name == "Box",
            $"a new controller's button: {btn}, {c.Devices.Count} devices");
        Check.True(S(Snap(A, "Base"), Snap(Bd, "Box", buttons: [7])).Pressed.Contains(1007), "the bound button doesn't read as pressed");
    }

    public static void AxisCaptures()
    {
        DeviceSnapshot[] start = [Snap(A, "Base"), Snap(Bd, "Pedals", Axes(y: 65535, z: 65535))];
        var cap = new AxisCapture(start);
        Check.True(cap.Poll([Snap(A, "Base", Axes(x: 40000)), Snap(Bd, "Pedals", Axes(y: 60000, z: 65535))]) is null, "small moves captured");
        var got = cap.Poll([Snap(A, "Base", Axes(x: 40000)), Snap(Bd, "Pedals", Axes(y: 10000, z: 65535))]);
        Check.True(got is { } g && g.Device == Bd && g.Axis == 1 && !g.Increased, "the pedal's axis: " + got);
        var steer = new AxisRef { Device = A, Axis = 0 };
        Check.True(new AxisCapture(start, [steer]).Poll([Snap(A, "Base", Axes(x: 65535)), Snap(Bd, "Pedals", Axes(y: 65535, z: 65535))]) is null, "an excluded axis captured");
        // A pedal: pressed fully and let go gives its axis and its travel.
        var pc = new PedalCapture(start, [steer]);
        Check.True(pc.Poll([Snap(A, "Base"), Snap(Bd, "Pedals", Axes(y: 65535, z: 30000))]) is null && pc.Found?.Axis == 2, "pressing: not found");
        Check.True(pc.Poll([Snap(A, "Base"), Snap(Bd, "Pedals", Axes(y: 65535, z: 1000))]) is null, "done while still pressed");
        var done = pc.Poll([Snap(A, "Base"), Snap(Bd, "Pedals", Axes(y: 65535, z: 64000))]) ?? throw new Exception("not done after letting go");
        Check.True(done.Axis.Device == Bd && done.Axis.Axis == 2 && done.Range.Released == 65535 && done.Range.Pressed == 1000,
            $"pedal {done.Axis.Describe()} {done.Range.Released}->{done.Range.Pressed}");
        Check.True(done.Range.Normalize(65535, 0) == 0 && done.Range.Normalize(1000, 0) == 1, "its range");
        var twitch = new PedalCapture(start);
        Check.True(twitch.Poll([Snap(A, "Base"), Snap(Bd, "Pedals", Axes(y: 65535, z: 65535 - 1500))]) is null && twitch.Found is null, "a twitch counted");
    }

    public static void WheelSpeed()
    {
        // A 125 Hz controller (its angle steps every 8 ms) read every 2 ms with jitter, the wheel turning at 100°/s.
        var rng = new Random(7);
        var v = new VelocityTracker();
        var outs = new List<float>();
        var naive = new List<float>();
        double t = 0;
        float lastAngle = 0;
        for (int i = 0; i < 1500; i++)
        {
            t += 0.002 + (rng.NextDouble() - 0.5) * 0.0006;
            float angle = (float)(Math.Floor(t * 125) / 125 * 100);
            float speed = v.Next(angle, t);
            if (t > 0.5) { outs.Add(speed); naive.Add((angle - lastAngle) / 0.002f); }
            lastAngle = angle;
        }
        Check.Near(outs.Average(), 100, 5, "mean speed");
        Check.True(outs.Min() > 60 && outs.Max() < 140, $"speed between {outs.Min():F0} and {outs.Max():F0}°/s");
        Check.True(naive.Min() < 1 && naive.Max() > 300, "test check: a difference per read would not have been steady");
        float last = 1;
        for (int i = 0; i < 40; i++) { t += 0.002; last = v.Next(lastAngle, t); }
        Check.True(last == 0, $"a still wheel reads {last}°/s");
        v.Reset();
        Check.True(v.Next(500, t + 1) == 0 && v.Next(float.NaN, t + 1.002) == 0, "after a reset / NaN");
    }
}

static class SafetyTests
{
    public static void Strength()
    {
        Check.True(FfbEngine.AutoScale(0) == FfbEngine.AutoScale(FfbEngine.AssumedNm), "a base whose torque is not known is not taken as 25 Nm");
        for (float nm = 0.5f; nm <= 60; nm += 0.5f)
        {
            float s = FfbEngine.AutoScale(nm);
            Check.True(s is >= 0.05f and <= 1, $"{nm} Nm: scale {s}");
            Check.True(s * nm <= FfbEngine.ReferenceNm * FfbEngine.UntestedMargin + 1e-3f, $"{nm} Nm base: {s * nm:F2} Nm at Master 1");
            if (nm <= FfbEngine.AssumedNm) Check.True(FfbEngine.AutoScale(0) <= s + 1e-6f, $"an unknown base is stronger than a {nm} Nm one");
        }
        Check.Near(FfbEngine.AutoScale(9), 3.9 / 9 * 0.85, 1e-4, "9 Nm");
    }

    public static void MotorTestPush()
    {
        // The push at the motor (Nm) on a base of true torque T whose rating was entered (R = T) or not (R = 0).
        double Nm(float rated, float trueNm) => FfbEngine.MotorTestPush(FfbEngine.AutoScale(rated)) * trueNm;
        Check.Near(Nm(3.9f, 3.9f), 0.12 * 3.9, 1e-4, "the bridge's R3 push changed");
        foreach (float t in new[] { 2f, 3.9f, 5f, 8f, 12f, 20f, 25f })
        {
            Check.True(Nm(t, t) <= 0.12 * 3.9 + 1e-3, $"{t} Nm base, rating entered: {Nm(t, t):F2} Nm");
            Check.True(Nm(0, t) <= 0.12 * 3.9 + 1e-3, $"{t} Nm base, no rating: {Nm(0, t):F2} Nm");
        }
        Check.True(Nm(25, 25) > 0.4, "a 25 Nm base with its rating gets too gentle a push to move the wheel");
        Check.True(FfbEngine.MotorTestPush(1) == FfbEngine.MotorTestTorque, "a strength set to full by the user: not the bridge's push");
    }

    public static void EStop()
    {
        const float dt = 0.002f;
        float g = 1;
        int ticks = 0;
        while (g > 0 && ticks < 1000) { g = FfbEngine.StepEStopGain(g, true, dt); ticks++; }
        Check.True(ticks * dt <= FfbEngine.EStopFadeSeconds + dt + 1e-4, $"the force is gone after {ticks * dt * 1000:F0} ms");
        for (int i = 0; i < 250; i++) g = FfbEngine.StepEStopGain(g, false, dt);
        Check.Near(g, 0.5, 0.01, "half back after 0.5 s");
        for (int i = 0; i < 260; i++) g = FfbEngine.StepEStopGain(g, false, dt);
        Check.True(g == 1, $"not fully back after 1 s ({g})");
        foreach (var (test, pin, torque) in new[] { (0.12f, float.NaN, 0f), (float.NaN, 0.8f, 0f), (float.NaN, float.NaN, 0.9f) })
            Check.True(FfbEngine.SelectOutput(false, test, false, false, pin, true, torque, 1, 0) == 0, $"the e-stop let through test {test}, pin {pin}, torque {torque}");
    }

    // changes/0349: the dashboard's e-stop can come while no loop runs (no base yet, unplugged). The loop that starts after
    // it must not fade from full strength. (That Loop() calls GainAtLoopStart first is read, not run: it needs a wheel.)
    public static void EStopAtLoopStart()
    {
        float g = FfbEngine.GainAtLoopStart(1, estop: true);
        Check.True(g == 0, $"a loop started during an e-stop begins at {g}");
        g = FfbEngine.StepEStopGain(g, true, 0.002f);
        Check.True(FfbEngine.SelectOutput(false, float.NaN, false, false, float.NaN, true, 0.9f, 1, g) == 0, "force on its first tick");
        Check.True(FfbEngine.SelectOutput(false, 0.12f, false, false, float.NaN, true, 0f, 1, g) == 0, "the motor test's push on its first tick");
        Check.True(FfbEngine.GainAtLoopStart(0.4f, false) == 0.4f && FfbEngine.GainAtLoopStart(1, false) == 1, "a loop started without an e-stop had its fade changed");
    }

    public static void Gates()
    {
        float O(bool killed = false, float test = float.NaN, bool untested = false, bool noTravel = false, float pin = float.NaN,
                bool enabled = true, float torque = 0.5f) => FfbEngine.SelectOutput(killed, test, untested, noTravel, pin, enabled, torque, -1, 1);
        Check.True(O() == -0.5f, "normal: torque x sign");
        Check.True(O(killed: true, test: 0.12f, pin: 0.5f) == 0, "killed");
        Check.True(O(untested: true) == 0 && O(noTravel: true) == 0, "an untested motor / no rotation entered");
        Check.True(O(untested: true, pin: 0.5f) == 0 && O(noTravel: true, pin: 0.5f) == 0, "force center without the motor direction / rotation");
        Check.True(O(untested: true, noTravel: true, test: 0.12f) == 0.12f, "the motor test itself was blocked");
        Check.True(O(pin: 0.3f) == -0.3f, "force center");
        Check.True(O(enabled: false) == 0, "FFB off");
        Check.True(O(torque: float.NaN) == 0 && O(torque: float.PositiveInfinity) == 0, "non-finite torque");
        Check.True(!new BridgeConfig().TravelConfirmed, "a new config claims the rotation was entered");
    }

    public static void Device()
    {
        var f = new FfbConfig { ForceSign = -1, ForceSignTested = true, RatedNm = 5, BaseScale = 0.7f, DeviceInstance = DeviceTests.A, DeviceName = "Base" };
        Check.True(!f.ChooseDevice(DeviceTests.A, "Base renamed") && f.ForceSignTested && f.ForceSign == -1 && f.RatedNm == 5 && f.BaseScale == 0.7f
                   && f.DeviceName == "Base renamed", "the same base lost its settings");
        Check.True(f.ChooseDevice(DeviceTests.C, "Other") && !f.ForceSignTested && f.ForceSign == 1 && f.RatedNm == 0 && f.BaseScale == 0,
            "another base kept the old one's motor direction or strength");
    }

    public static void Control()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        bool? estop = null;
        using var link = new ControlLink(0, on => estop = on, () => "{\"t\":\"status\"}", requireHeartbeat: true, clock: () => now);
        link.Handle("estop"); Check.True(estop == true, "estop");
        link.Handle("resume"); Check.True(estop == false, "resume");
        link.Handle("launch"); link.Handle("gain 1"); Check.True(estop == false, "an unknown command did something");
        Check.True(link.Handle("status").Contains("status"), "status");
        now += TimeSpan.FromSeconds(1); link.Handle("hb");
        now += TimeSpan.FromSeconds(1); link.Watchdog();
        Check.True(estop == false && !link.LinkLost, "tripped with beats 1 s apart");
        now += TimeSpan.FromSeconds(1); link.Watchdog();
        Check.True(estop == true && link.LinkLost, "2 s without a beat didn't hold the force");
        link.Handle("resume"); Check.True(estop == true, "resume lifted a lost link with no beats back");
        link.Handle("hb"); Check.True(estop == true, "a beat alone lifted it (only resume may)");
        link.Handle("resume"); Check.True(estop == false && !link.LinkLost, "resume with the beats back");
        link.Handle("estop"); link.Handle("hb"); Check.True(estop == true, "a heartbeat lifted an e-stop");
        bool? free = null;
        using var alone = new ControlLink(0, on => free = on, () => "", requireHeartbeat: false, clock: () => now);
        now += TimeSpan.FromMinutes(5); alone.Watchdog();
        Check.True(free is null, "the watchdog tripped without a dashboard");
    }

    public static void ControlDeaf()
    {
        using var hog = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)hog.Client.LocalEndPoint!).Port;
        bool? estop = null;
        using var link = new ControlLink(port, on => estop = on, () => "", requireHeartbeat: true);
        link.RunAsync(CancellationToken.None).Wait(2000);
        Check.True(estop == true && link.LastError is not null, "deaf to the dashboard, and the force not held: " + link.LastError);
    }

    public static void ControlUdp()
    {
        int port = Net.FreePort();
        bool? estop = null;
        using var link = new ControlLink(port, on => estop = on, () => "{\"t\":\"status\"}", requireHeartbeat: false);
        using var cts = new CancellationTokenSource();
        var task = Task.Run(() => link.RunAsync(cts.Token));
        using var tx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        tx.Client.ReceiveTimeout = 200;
        var to = new IPEndPoint(IPAddress.Loopback, port);
        string? reply = null;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000 && (reply is null || estop != true))
        {
            byte[] e = "estop"u8.ToArray(), q = "status"u8.ToArray();
            tx.Send(e, e.Length, to);
            tx.Send(q, q.Length, to);
            try { IPEndPoint? from = null; reply = Encoding.UTF8.GetString(tx.Receive(ref from)); } catch (SocketException) { }
        }
        cts.Cancel();
        task.Wait(2000);
        Check.True(estop == true && reply?.Contains("status") == true, $"over UDP: estop {estop}, reply {reply}");
    }

    // changes/0349 (Main's review): the dashboard ends Game Link Wheel with "quit", so the window closes itself and the
    // host lets go of the wheelbase; quit holds the force at zero first, like estop. And the program's one quit request
    // (AppQuit: from quit, or the dashboard gone) is raised once, with its first reason.
    public static void ControlQuit()
    {
        var now = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        bool estop = false;
        int quits = 0;
        using var link = new ControlLink(0, on => estop = on, () => "", requireHeartbeat: false, clock: () => now, quit: () => quits++);
        Check.True(link.Handle("quit") == "" && estop && quits == 1, $"quit: estop {estop}, asked to close {quits} time(s)");
        using var silent = new ControlLink(0, on => estop = on, () => "", requireHeartbeat: false, clock: () => now);
        estop = false;
        Check.True(silent.Handle("quit") == "" && estop, "quit with no close handler did not hold the force");
        AppQuit.ResetForTests();
        try
        {
            string? seen = null;
            int raised = 0;
            AppQuit.Raised += r => { seen = r; raised++; };
            Check.True(!AppQuit.Requested, "a quit was asked for before any request");
            AppQuit.Request("first");
            AppQuit.Request("second");
            Check.True(AppQuit.Requested && raised == 1 && seen == "first" && AppQuit.Reason == "first",
                $"AppQuit: raised {raised} time(s), reason {AppQuit.Reason}");
        }
        finally { AppQuit.ResetForTests(); }
    }
}

static class ProfileLoadTests
{
    /// <summary>
    /// changes/0349: the bridge tuned each profile's LoadReference on the game's own AngVelZ; Game Link Wheel feeds the yaw
    /// rate from the heading. A shipped profile keeps the ratio of its LoadReference to its vehicle's 90th-percentile
    /// |lateral g| (after the model's 25 Hz low-pass, racing above 30 km/h) that the bridge had.
    /// </summary>
    public static void LoadRetune()
    {
        var bridge = new Dictionary<VehicleClass, (string Profile, float Old)>
        {
            [VehicleClass.Car] = ("Land Speeder", 12), [VehicleClass.Bike] = ("Speeder Bike", 6),
            [VehicleClass.Skimmer] = ("Skim Speeder", 11.5f), [VehicleClass.Pod] = ("Podracer", 30),
        };
        var seen = new HashSet<VehicleClass>();
        foreach (var rec in Frames.Recordings())
        {
            var heading = Frames.Load(rec);
            var cls = heading.GroupBy(x => x.frame.VehicleClass).OrderByDescending(g => g.Count()).First().Key;
            if (!bridge.TryGetValue(cls, out var b)) continue;
            seen.Add(cls);
            float now = P90(heading), was = P90(Frames.Load(rec, gameYaw: true));
            float shipped = BridgeConfig.ReadFile(Path.Combine(ConfigTests.DefaultsDir(), b.Profile + ".json")).Ffb.LoadReference;
            double want = b.Old * now / was;
            Console.WriteLine($"      {b.Profile}: p90 {was:F2} on the game's field, {now:F2} from the heading: {b.Old} -> {want:F1} (ships {shipped})");
            Check.True(Math.Abs(shipped / want - 1) < 0.05, $"{b.Profile}: LoadReference {shipped}, the bridge's tuning carried over is {want:F1}");
        }
        Check.True(seen.Count == 4, "a vehicle class without a recording: has " + string.Join(", ", seen));
        Check.True(new FfbConfig().LoadReference == BridgeConfig.ReadFile(Path.Combine(ConfigTests.DefaultsDir(), "Land Speeder.json")).Ffb.LoadReference,
            "the built-in default is not the land speeder's");
    }

    private static float P90(List<(TelemetryFrame frame, float dt)> frames)
    {
        float k = 1 - MathF.Exp(-0.002f * 2 * MathF.PI * 25), lp = 0;
        var xs = new List<float>();
        foreach (var (f, dt) in frames)
        {
            float u = f[F.RelVelX] * f[F.AngVelZ] / 981f;
            for (int i = 0, n = Math.Max(1, (int)Math.Round(Math.Max(0.001f, dt) * 500)); i < n; i++) lp += (u - lp) * k;
            if (f.Has(FrameFlags.HasVehicle) && f[B.InputMode] != 0 && !f.Has(FrameFlags.RaceDone) && f[F.SpeedKmph] >= 30) xs.Add(Math.Abs(lp));
        }
        xs.Sort();
        return xs[(int)(0.9 * xs.Count)];
    }
}
