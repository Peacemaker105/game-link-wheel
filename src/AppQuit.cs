namespace WheelBridge;

/// <summary>
/// One request to end the program, wherever it comes from (changes/0349, Main's review): the dashboard's "quit" on the
/// control link, or the parent watch once the dashboard has gone. The window, or the console loop, then ends itself, so
/// the host is disposed and the wheelbase let go properly (force zeroed, effect stopped, device reset and unacquired).
/// Ending the process from outside skips all of that.
/// </summary>
public static class AppQuit
{
    private static int _requested;

    /// <summary>True once an end has been asked for.</summary>
    public static bool Requested => Volatile.Read(ref _requested) != 0;

    /// <summary>Why the end was first asked for, or null.</summary>
    public static string? Reason { get; private set; }

    /// <summary>Raised once, on the asking thread, the first time an end is asked for.</summary>
    public static event Action<string>? Raised;

    public static void Request(string reason)
    {
        if (Interlocked.Exchange(ref _requested, 1) != 0) return;
        Reason = reason;
        try { Raised?.Invoke(reason); } catch { }
    }

    /// <summary>The tests' own: forget a request and every handler.</summary>
    public static void ResetForTests()
    {
        Volatile.Write(ref _requested, 0);
        Reason = null;
        Raised = null;
    }
}
