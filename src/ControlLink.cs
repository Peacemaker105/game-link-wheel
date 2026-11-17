using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WheelBridge;

/// <summary>
/// The dashboard's line to Game Link Wheel (changes/0349): plain-text UDP datagrams on 127.0.0.1 only.
///   "estop"  - the dashboard's e-stop or park tripped: force fades to zero within 100 ms and stays there;
///   "resume" - it is clear again: force comes back over 1 s;
///   "hb"     - a heartbeat. When the dashboard started this program (<paramref name="requireHeartbeat"/>), no heartbeat for
///              <see cref="HeartbeatTimeout"/> counts as an e-stop, and only "resume" lifts it, once heartbeats are back;
///   "status" - answered with one JSON line to the sender.
/// Anything else is ignored. Nothing here can raise the force: every command can only hold it at zero or let it be.
/// </summary>
/// <param name="clock">UTC now; the tests pass their own.</param>
public sealed class ControlLink(int port, Action<bool> setEStop, Func<string> status, bool requireHeartbeat, Func<DateTime>? clock = null) : IDisposable
{
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(1.5);
    private UdpClient? _udp;
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private DateTime _lastBeat = clock?.Invoke() ?? DateTime.UtcNow;
    private bool _commandedStop, _linkLost;
    public string? LastError { get; private set; }
    public bool LinkLost => _linkLost;
    public int Port => port;

    public async Task RunAsync(CancellationToken ct)
    {
        try { _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)); }
        catch (SocketException ex)
        {
            LastError = $"can't listen on UDP {port}: {ex.Message}";
            if (requireHeartbeat) { _linkLost = true; Apply(); }   // started by the dashboard but deaf to it: hold the force at zero
            return;
        }
        try { _udp.Client.IOControl(unchecked((int)0x9800000C), [0], null); } catch { }
        using var watch = new Timer(_ => Watchdog(), null, 250, 250);
        using (ct.Register(() => _udp.Dispose()))
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await _udp.ReceiveAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { continue; }
                string reply = Handle(Encoding.ASCII.GetString(r.Buffer).Trim());
                if (reply.Length > 0) { try { await _udp.SendAsync(Encoding.UTF8.GetBytes(reply), r.RemoteEndPoint, ct); } catch { } }
            }
        }
    }

    /// <summary>One command; returns the reply (empty for none). The tests call this and <see cref="Watchdog"/> directly.</summary>
    public string Handle(string cmd)
    {
        lock (this)
        {
            switch (cmd)
            {
                case "estop": _commandedStop = true; break;
                case "resume":
                    _commandedStop = false;
                    if (_linkLost && _now() - _lastBeat < HeartbeatTimeout) _linkLost = false;
                    break;
                case "hb": _lastBeat = _now(); break;
                case "status": return status();
                default: return "";
            }
            Apply();
            return "";
        }
    }

    public void Watchdog()
    {
        lock (this)
        {
            if (requireHeartbeat && !_linkLost && _now() - _lastBeat > HeartbeatTimeout)
            {
                _linkLost = true;
                Apply();
            }
        }
    }

    private void Apply() => setEStop(_commandedStop || _linkLost);

    public void Dispose() => _udp?.Dispose();
}
