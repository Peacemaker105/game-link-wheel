using Vortice.DirectInput;

namespace WheelBridge;

/// <summary>
/// Drives the wheelbase with a DirectInput constant-force effect in background + exclusive mode, so force keeps
/// flowing while the game has focus. (From SWGR Wheel Bridge: its author found this the one path that works with the
/// game in front; it is also the most universal, as any base that works in ordinary sims takes a constant force.)
/// changes/0349: the base is the one the user picked in Setup, by its DirectInput instance, and nothing else: never "any
/// force-feedback device", which could put forces tuned for one motor on another.
/// </summary>
public sealed class FfbDevice : IDisposable
{
    private IDirectInput8? _di;
    private IDirectInputDevice8? _device;
    private IDirectInputEffect? _effect;
    private HiddenWindow? _hidden;
    private readonly ConstantForce _force = new() { Magnitude = 0 };
    private EffectParameters? _params;
    private int _lastMagnitude = int.MinValue;
    private DateTime _nextRecovery;

    private DateTime _failingSince = DateTime.MaxValue;
    /// <summary>How long re-acquiring may keep failing before the device is given up and recreated (e.g. after a replug).</summary>
    private static readonly TimeSpan LostAfter = TimeSpan.FromSeconds(3);

    public string Status { get; private set; } = "not started";
    public bool Ready => _effect is not null;
    /// <summary>Recovery has failed for a while: the owner should dispose this device and start a new one.</summary>
    public bool Lost { get; private set; }
    /// <summary>The base being driven, as DirectInput names it (null until started).</summary>
    public string? ProductName { get; private set; }

    /// <summary>The attached force-feedback controllers, for the Setup's picker.</summary>
    public static List<(Guid Instance, string Name)> List()
    {
        using var di = DInput.DirectInput8Create();
        return di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.ForceFeedback | DeviceEnumerationFlags.AttachedOnly)
            .Select(d => (d.InstanceGuid, (d.InstanceName ?? d.ProductName ?? "wheelbase").Trim())).ToList();
    }

    /// <summary>
    /// Which attached base to open: the one picked in Setup, by its instance or, when that is not attached (another USB
    /// port can give it a new instance), the only attached force-feedback device with its name. Never any other: forces
    /// tuned for one motor must not go to another. Empty when there is none.
    /// </summary>
    public static Guid Pick(IEnumerable<(Guid Instance, string Name)> attached, Guid instance, string? name)
    {
        if (instance == Guid.Empty) return Guid.Empty;
        var list = attached.ToList();
        if (list.Any(d => d.Instance == instance)) return instance;
        if (string.IsNullOrEmpty(name)) return Guid.Empty;
        var same = list.Where(d => d.Name == name).ToList();
        return same.Count == 1 ? same[0].Instance : Guid.Empty;
    }

    /// <param name="instance">The base the user picked in Setup (its DirectInput instance GUID).</param>
    /// <param name="name">Its name then, for <see cref="Pick"/>'s fallback.</param>
    public bool Start(Guid instance, string? name = null)
    {
        if (instance == Guid.Empty) { Status = "no wheelbase chosen: pick yours in Setup"; return false; }
        try
        {
            _hidden = new HiddenWindow("GameLinkWheelFfb");
            if (_hidden.Handle == IntPtr.Zero) { Status = "could not create the FFB window"; return false; }

            _di = DInput.DirectInput8Create();
            var attached = _di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.ForceFeedback | DeviceEnumerationFlags.AttachedOnly);
            var chosen = Pick(attached.Select(d => (d.InstanceGuid, (d.InstanceName ?? d.ProductName ?? "wheelbase").Trim())), instance, name);
            var pick = attached.FirstOrDefault(d => chosen != Guid.Empty && d.InstanceGuid == chosen);
            if (pick is null) { Status = "the chosen wheelbase is not connected (or is off)"; return false; }
            ProductName = (pick.InstanceName ?? pick.ProductName ?? "wheelbase").Trim();

            _device = _di.CreateDevice(pick.InstanceGuid);
            _device.SetDataFormat<RawJoystickState>().CheckError();
            _device.SetCooperativeLevel(_hidden.Handle, CooperativeLevel.Background | CooperativeLevel.Exclusive).CheckError();
            try { _device.Properties.AutoCenter = false; } catch { /* not all drivers expose it */ }
            _device.Acquire().CheckError();

            _params = new EffectParameters
            {
                Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                Duration = -1,                // INFINITE
                SamplePeriod = 0,
                Gain = 10000,
                TriggerButton = -1,           // DIEB_NOTRIGGER
                TriggerRepeatInterval = 0,
                StartDelay = 0,
                Axes = [(int)JoystickOffset.X],
                Directions = [1],             // single axis: the magnitude's sign gives the direction
                Parameters = _force,
            };
            _effect = _device.CreateEffect(EffectGuid.ConstantForce, _params);
            _effect.Start(1, EffectPlayFlags.None).CheckError();
            Status = $"DirectInput constant force on \"{ProductName}\"";
            return true;
        }
        catch (Exception ex)
        {
            Status = "FFB start failed: " + ex.Message;
            Release();
            return false;
        }
    }

    /// <summary>Torque -1..1 (already signed for the motor).</summary>
    public void Set(float torque)
    {
        if (_effect is null || _params is null) return;
        int magnitude = (int)Math.Round(Math.Clamp(torque, -1, 1) * 10000);
        if (magnitude == _lastMagnitude) return;
        try
        {
            _force.Magnitude = magnitude;
            _effect.SetParameters(_params, EffectParameterFlags.TypeSpecificParameters);
            _lastMagnitude = magnitude;
            _failingSince = DateTime.MaxValue;
        }
        catch (Exception ex)
        {
            if (_failingSince == DateTime.MaxValue) _failingSince = DateTime.UtcNow;
            Recover(ex);
            // An unplugged base comes back as a new device instance: re-acquiring the old one never works.
            if (DateTime.UtcNow - _failingSince > LostAfter) { Lost = true; Status = "device lost, reconnecting: " + ex.Message; }
        }
    }

    /// <summary>Re-acquire and restart after the device was lost (unplugged, another app grabbed it, …).</summary>
    private void Recover(Exception ex)
    {
        if (DateTime.UtcNow < _nextRecovery) return;
        _nextRecovery = DateTime.UtcNow.AddSeconds(1);
        try
        {
            _device?.Acquire();
            _effect?.SetParameters(_params!, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);
            Status = "recovered after: " + ex.Message;
            _failingSince = DateTime.MaxValue;
        }
        catch (Exception again) { Status = "device lost: " + again.Message; }
        _lastMagnitude = int.MinValue;
    }

    private void Release()
    {
        try { _effect?.Stop(); } catch { }
        try { _device?.SendForceFeedbackCommand(ForceFeedbackCommand.Reset); } catch { }
        try { _device?.Unacquire(); } catch { }
        _effect?.Dispose(); _effect = null;
        _device?.Dispose(); _device = null;
        _di?.Dispose(); _di = null;
    }

    public void Dispose()
    {
        if (_effect is not null) { try { _force.Magnitude = 0; _effect.SetParameters(_params!, EffectParameterFlags.TypeSpecificParameters); } catch { } }
        Release();
        Status = "stopped";
        _hidden?.Dispose();
    }
}
