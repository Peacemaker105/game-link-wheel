using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace WheelBridge;

/// <summary>
/// The game's telemetry as VN Motion's UE4SS mod sends it (changes/0340): VNSG version 1, 328 bytes, little-endian, one
/// frame per game frame, forwarded unchanged by Game Link over UDP. <see cref="ToBridgeFrame"/> turns one into the bridge's
/// own frame (<see cref="TelemetryFrame"/>), so the force model, the recorder and the replay work on it unchanged. Where
/// the two differ:
///   - AngVelZ, which the model's cornering load reads, is the yaw rate derived from the heading (rad/s, + right turn),
///     not the game's RelativeAngularVelocity.Z: changes/0340 found that field follows the heading rate only loosely
///     (correlation 0.08 to 0.74 by vehicle) with a scale of 0.05 to 0.73. LoadReference is re-tuned for it (changes/0349);
///   - the frame's own Valid, Paused, Cinematic and Controlled flags mute the model: not Valid reads as no vehicle, the
///     others as not racing (InputMode 0), since the bridge's frame has no such flags;
///   - SteerIntent, LevitationScale, SpeedScale, the bypass heats, AirBrakeScale and PredictedHitTime are not in VNSG and
///     read 0 (only the bridge's display used them; SteerIntent reads the steering input).
/// </summary>
public static class Vnsg
{
    public const int Size = 328, Version = 1, FloatsAt = 32, BytesAt = 188, CollisionsAt = 196, CollisionSize = 33, MaxCollisions = 4;
    public static ReadOnlySpan<byte> Magic => "VNSG"u8;

    /// <summary>The 39 floats in wire order (changes/0340's table).</summary>
    public static readonly string[] FloatNames =
    [
        "PosX", "PosY", "PosZ", "VelX", "VelY", "VelZ", "QuatX", "QuatY", "QuatZ", "QuatW", "AngVelX", "AngVelY", "AngVelZ",
        "RelVelX", "RelVelY", "RelVelZ", "RelAngVelX", "RelAngVelY", "RelAngVelZ", "SpeedKmph", "TopSpeedKmph", "SlipDeg",
        "Steer", "Throttle", "Brake", "Rpm", "RpmScale", "OnGroundScale", "TimeInAir", "Roll", "Pitch", "RamjetHeat",
        "AfterburnerEnergy", "BoostTransition", "CrashTimeRemaining", "DraftingScale", "GroundNormalX", "GroundNormalY", "GroundNormalZ",
    ];
    public static readonly string[] ByteNames =
        ["BoostState", "RamjetState", "AfterburnerState", "LandingState", "PerfectLandingState", "DraftingState", "EnergyBrakeState", "InputMode"];
    /// <summary>Flag bits, bit 0 upwards.</summary>
    public static readonly string[] FlagNames =
        ["HasVehicle", "Valid", "Paused", "Cinematic", "Controlled", "InAir", "Drifting", "UpsideDown", "BoostActive", "CollisionsOk", "RaceDone", "OnFoot"];
    private static int Fi(string n) => Array.IndexOf(FloatNames, n);
    private static int Bi(string n) => Array.IndexOf(ByteNames, n);
    private static uint Bit(string n) => 1u << Array.IndexOf(FlagNames, n);

    /// <summary>Null when the bytes are a VNSG v1 frame; otherwise what is wrong with them.</summary>
    public static string? Problem(ReadOnlySpan<byte> s)
    {
        if (s.Length < 8 || !s[..4].SequenceEqual(Magic)) return "not a VNSG frame";
        int v = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]), n = BinaryPrimitives.ReadUInt16LittleEndian(s[6..]);
        if (v != Version || n != Size || s.Length != Size) return $"a VNSG frame of version {v}, {n} bytes in {s.Length}: this Game Link Wheel reads version {Version}, {Size} bytes";
        return null;
    }

    public static uint Seq(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
    public static double SimTime(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadDoubleLittleEndian(s[12..]);
    public static uint Flags(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt32LittleEndian(s[24..]);
    public static float Float(ReadOnlySpan<byte> s, string name) => BinaryPrimitives.ReadSingleLittleEndian(s[(FloatsAt + 4 * Fi(name))..]);
    public static byte Byte(ReadOnlySpan<byte> s, string name) => s[BytesAt + Bi(name)];

    /// <summary>The heading (radians, rising in a right turn) from the vehicle's quaternion: forward = q·(1,0,0).</summary>
    public static double Heading(float qx, float qy, float qz, float qw)
    {
        double fx = 1 - 2 * (qy * (double)qy + qz * (double)qz), fy = 2 * (qx * (double)qy + qw * (double)qz);
        return Math.Atan2(fy, fx);
    }

    /// <summary>
    /// Heading-derived yaw rate from one frame to the next (rad/s, + right turn), or 0 across anything that is not a plain
    /// step: a first frame, a step of the game's clock outside 0..0.1 s, another vehicle class, or a frame that is not Valid.
    /// </summary>
    public sealed class YawRate
    {
        private double _heading, _time;
        private byte _cls;
        private bool _have;
        public float Next(double heading, double simTime, byte vehicleClass, bool valid)
        {
            float rate = 0;
            if (valid && _have && vehicleClass == _cls)
            {
                double dt = simTime - _time;
                if (dt > 0 && dt <= 0.1)
                {
                    double dh = heading - _heading;
                    dh -= 2 * Math.PI * Math.Round(dh / (2 * Math.PI));   // across ±180°
                    rate = (float)(dh / dt);
                }
            }
            _have = valid;
            _heading = heading; _time = simTime; _cls = vehicleClass;
            return float.IsFinite(rate) ? rate : 0;
        }
    }

    /// <summary>The bridge's frame from a VNSG frame (see the class comment for what differs). Throws on a frame <see cref="Problem"/> refuses.</summary>
    public static TelemetryFrame ToBridgeFrame(ReadOnlySpan<byte> frame, YawRate yaw)
    {
        if (Problem(frame) is { } bad) throw new InvalidDataException(bad);
        byte[] s = frame.ToArray();   // the local functions below read it (a span cannot be captured)
        uint flags = Flags(s);
        bool Has(string f) => (flags & Bit(f)) != 0;
        bool vehicle = Has("HasVehicle") && Has("Valid");
        bool racing = vehicle && !Has("Paused") && !Has("Cinematic") && Has("Controlled");
        byte cls = s[28];
        float q(string n) => Float(s, n);
        float yawRate = yaw.Next(Heading(q("QuatX"), q("QuatY"), q("QuatZ"), q("QuatW")), SimTime(s), cls, vehicle);

        var b = new byte[TelemetryFrame.Size];
        var o = b.AsSpan();
        "SWGR"u8.CopyTo(o);
        BinaryPrimitives.WriteUInt16LittleEndian(o[4..], TelemetryFrame.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(o[6..], (ushort)TelemetryFrame.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(o[8..], Seq(s));
        BinaryPrimitives.WriteDoubleLittleEndian(o[12..], SimTime(s));
        BinaryPrimitives.WriteSingleLittleEndian(o[20..], BinaryPrimitives.ReadSingleLittleEndian(s[20..]));
        FrameFlags ff = FrameFlags.None;
        if (vehicle) ff |= FrameFlags.HasVehicle;
        if (Has("Drifting")) ff |= FrameFlags.Drifting;
        if (Has("UpsideDown")) ff |= FrameFlags.UpsideDown;
        if (Has("BoostActive")) ff |= FrameFlags.BoostToggled;
        if (Has("CollisionsOk")) ff |= FrameFlags.CollisionsOk;
        if (Has("RaceDone")) ff |= FrameFlags.RaceDone;
        if (Has("OnFoot")) ff |= FrameFlags.OnFoot;
        BinaryPrimitives.WriteUInt32LittleEndian(o[24..], (uint)ff);
        o[28] = cls;
        o[29] = s[29];

        float From(F f) => f switch
        {
            F.AngVelX => q("RelAngVelX"), F.AngVelY => q("RelAngVelY"), F.AngVelZ => yawRate,
            F.SteerInput or F.SteerIntent => q("Steer"),
            F.BoostTransitionScale => q("BoostTransition"),
            F.LevitationScale or F.SpeedScale or F.RamjetBypassHeat or F.BypassOverheatPct or F.AirBrakeScale or F.PredictedHitTime => 0,
            _ => q(f.ToString()),
        };
        int at = TelemetryFrame.HeaderSize;
        foreach (var f in Enum.GetValues<F>()) { BinaryPrimitives.WriteSingleLittleEndian(o[at..], vehicle ? From(f) : 0); at += 4; }
        foreach (var bn in Enum.GetValues<B>())
        {
            byte v = vehicle ? s[BytesAt + Bi(bn.ToString())] : (byte)0;
            if (bn == B.InputMode && !racing) v = 0;   // paused, a cinematic, not under your control: the model mutes
            o[at++] = v;
        }
        s.AsSpan(CollisionsAt, MaxCollisions * CollisionSize).CopyTo(o[at..]);   // the same 33-byte layout
        return TelemetryFrame.Parse(b, out var err) ?? throw new InvalidDataException(err);
    }
}

/// <summary>
/// Receives Game Link's VNSG frames over UDP (127.0.0.1 unless <paramref name="anyAddress"/>: the two-PC case, changes/0349),
/// drops older or repeated ones (UDP can reorder) unless the count starts again (a game restart), and hands each on as a
/// bridge frame. A bad datagram is counted and named once; the stream keeps going.
/// </summary>
public sealed class VnsgReceiver(int port, Action<TelemetryFrame, byte[]> onFrame, bool anyAddress = false) : IDisposable
{
    private UdpClient? _udp;
    private readonly Vnsg.YawRate _yaw = new();
    private uint? _lastSeq;
    public bool Connected => (DateTime.UtcNow - LastFrameAt).TotalSeconds < 1;
    public DateTime LastFrameAt { get; private set; } = DateTime.MinValue;
    public long FramesReceived { get; private set; }
    public long BadFrames { get; private set; }
    public long OldFrames { get; private set; }
    public string? LastError { get; private set; }
    public int Port => port;

    public async Task RunAsync(CancellationToken ct)
    {
        try { _udp = new UdpClient(new IPEndPoint(anyAddress ? IPAddress.Any : IPAddress.Loopback, port)); }
        catch (SocketException ex) { LastError = $"can't listen on UDP {port}: {ex.Message}"; return; }
        try { _udp.Client.IOControl(unchecked((int)0x9800000C), [0], null); } catch { }   // SIO_UDP_CONNRESET off
        using (ct.Register(() => _udp.Dispose()))
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await _udp.ReceiveAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException ex) { LastError = ex.Message; continue; }
                Handle(r.Buffer);
            }
        }
    }

    /// <summary>One datagram (the tests call this directly).</summary>
    public void Handle(byte[] data)
    {
        if (Vnsg.Problem(data) is { } bad) { BadFrames++; LastError ??= bad; return; }
        uint seq = Vnsg.Seq(data);
        if (_lastSeq is { } last)
        {
            int d = unchecked((int)(seq - last));
            if (d <= 0 && d > -1000 && (DateTime.UtcNow - LastFrameAt).TotalSeconds < 0.5) { OldFrames++; return; }
        }
        _lastSeq = seq;
        TelemetryFrame frame;
        try { frame = Vnsg.ToBridgeFrame(data, _yaw); }
        catch (Exception ex) { BadFrames++; LastError = ex.Message; return; }
        LastFrameAt = DateTime.UtcNow;
        FramesReceived++;
        try { onFrame(frame, data); }
        catch (Exception ex) { LastError = $"frame handler: {ex.GetType().Name}: {ex.Message}"; }
    }

    public void Dispose() => _udp?.Dispose();
}
