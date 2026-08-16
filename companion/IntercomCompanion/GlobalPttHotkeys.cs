using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IntercomCompanion;

/// <summary>Low-level, release-aware global shortcuts for hold-to-talk actions.</summary>
internal sealed class GlobalPttHotkeys : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly HookProc hook;
    private nint handle;
    private bool broadcastHeld;
    private bool replyHeld;

    public event Action? BroadcastPressed;
    public event Action? ReplyPressed;
    public event Action? Released;

    public GlobalPttHotkeys()
    {
        hook = Callback;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        handle = SetWindowsHookEx(WhKeyboardLl, hook, GetModuleHandle(module.ModuleName), 0);
        if (handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private nint Callback(int code, nint wParam, nint lParam)
    {
        if (code < 0) return CallNextHookEx(handle, code, wParam, lParam);
        var key = Marshal.ReadInt32(lParam);
        var message = (int)wParam;
        // Control.ModifierKeys is tied to a message loop and can be stale in a
        // low-level hook. Query the physical state so Ctrl+Alt+R works even
        // while another application owns focus.
        var ctrlAlt = IsDown(Keys.ControlKey) && IsDown(Keys.Menu);
        if (message is WmKeyDown or WmSysKeyDown)
        {
            if (ctrlAlt && key == (int)Keys.B && !broadcastHeld) { broadcastHeld = true; BroadcastPressed?.Invoke(); }
            if (ctrlAlt && key == (int)Keys.R && !replyHeld) { replyHeld = true; ReplyPressed?.Invoke(); }
        }
        else if (message is WmKeyUp or WmSysKeyUp)
        {
            if (key == (int)Keys.B && broadcastHeld) { broadcastHeld = false; Released?.Invoke(); }
            if (key == (int)Keys.R && replyHeld) { replyHeld = false; Released?.Invoke(); }
        }
        return CallNextHookEx(handle, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (handle == 0) return;
        UnhookWindowsHookEx(handle);
        handle = 0;
        GC.SuppressFinalize(this);
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int idHook, HookProc callback, nint module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);

    private static bool IsDown(Keys key) => (GetAsyncKeyState((int)key) & unchecked((short)0x8000)) != 0;
}
