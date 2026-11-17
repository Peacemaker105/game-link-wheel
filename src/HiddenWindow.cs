using System.Runtime.InteropServices;

namespace WheelBridge;

/// <summary>
/// A never-shown top-level window with its own message-loop thread: DirectInput's cooperative levels need a window, and
/// background mode keeps the devices working while the game has focus. (From SWGR Wheel Bridge's FfbDevice, moved here so
/// the input reader and the force-feedback device each have one.)
/// </summary>
internal sealed class HiddenWindow : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly string _className;
    private uint _threadId;
    // The window class is registered once per process and outlives any one HiddenWindow (the FFB device is recreated
    // after an unplug), so its WndProc must be a delegate that is never collected.
    private static readonly WndProc Proc = DefWindowProc;
    private static readonly IntPtr ProcPtr = Marshal.GetFunctionPointerForDelegate(Proc);
    public IntPtr Handle { get; private set; }

    public HiddenWindow(string className)
    {
        _className = className;
        _thread = new Thread(Run) { IsBackground = true, Name = className };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(2000);
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var wc = new WNDCLASS { lpfnWndProc = ProcPtr, lpszClassName = _className, hInstance = GetModuleHandle(null) };
        RegisterClass(ref wc);   // fails harmlessly (class exists) for every window after the first
        Handle = CreateWindowEx(0, wc.lpszClassName, _className, 0x00CF0000 /* WS_OVERLAPPEDWINDOW */, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _ready.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(500);
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName; public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WNDCLASS wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint id, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
