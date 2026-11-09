using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DownloadDock.Core.Screenshots;
using DownloadDock.Interop;
using DownloadDock.Services;

namespace DownloadDock.Screenshots;

/// <summary>
/// Global keyboard shortcuts via a low-level keyboard hook running on its own thread and message loop
/// (so a busy UI thread can never delay the user's typing).
///
/// Why a hook rather than RegisterHotKey: Explorer already owns Win+Shift+&lt;number&gt; (it launches a new
/// instance of the n-th taskbar app), so RegisterHotKey can't claim the macOS-style defaults. The hook sees
/// the keys first, consumes only an exact match of a configured shortcut, and sends a harmless unassigned
/// key so releasing Win doesn't open the Start menu. Everything else – including Win+Shift+S and Print
/// Screen – passes through untouched; Print Screen is merely observed for screenshot detection.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private const int WM_RUN_QUEUE = NativeMethods.WM_APP + 7;

    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly HashSet<int> _swallowedKeys = new();
    private ScreenshotNative.LowLevelKeyboardProc? _proc; // kept alive while hooked
    private IntPtr _hook;
    private int _threadId;
    private volatile Dictionary<string, Hotkey> _hotkeys = new();
    private volatile Action<Hotkey>? _recorder;
    private volatile Action? _recordCancelled;
    private volatile bool _disposed;

    /// <summary>A configured shortcut was pressed (raised on the UI dispatcher). Argument: shortcut id.</summary>
    public event Action<string>? HotkeyPressed;

    /// <summary>Print Screen was released (raised on the UI dispatcher). The key is not consumed.</summary>
    public event Action? PrintScreenObserved;

    public bool IsHooked => _hook != IntPtr.Zero;

    /// <summary>When set, Esc is consumed and this runs instead (used while a region selection is open).</summary>
    public volatile Action? EscapeOverride;

    /// <summary>While true, configured shortcuts are ignored (e.g. while a capture is already running).</summary>
    public bool Suspended { get; set; }

    public HotkeyService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _thread = new Thread(Run) { IsBackground = true, Name = "DownloadDock keyboard hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Replaces the active shortcuts (id → shortcut). Empty shortcuts are ignored.</summary>
    public void SetHotkeys(IReadOnlyDictionary<string, Hotkey> hotkeys)
        => _hotkeys = hotkeys.Where(kv => !kv.Value.IsEmpty).ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>Captures the next key combination instead of acting on it (used by the Settings shortcut boxes).</summary>
    public void BeginRecording(Action<Hotkey> recorded, Action cancelled)
    {
        _recordCancelled = cancelled;
        _recorder = recorded;
    }

    public void EndRecording()
    {
        _recorder = null;
        _recordCancelled = null;
    }

    private void Run()
    {
        _threadId = ScreenshotNative.GetCurrentThreadId();
        _proc = HookProc;
        _hook = ScreenshotNative.SetWindowsHookExW(ScreenshotNative.WH_KEYBOARD_LL, _proc, ScreenshotNative.GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
            Log.Error("Keyboard hook could not be installed (error " + Marshal.GetLastWin32Error() + ")");
        _ready.Set();

        while (ScreenshotNative.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_RUN_QUEUE)
            {
                while (_queue.TryDequeue(out var action))
                {
                    try { action(); } catch (Exception ex) { Log.Error("Hook thread action failed", ex); }
                }
            }
        }

        if (_hook != IntPtr.Zero) ScreenshotNative.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private void RunOnHookThread(Action action)
    {
        _queue.Enqueue(action);
        ScreenshotNative.PostThreadMessageW(_threadId, WM_RUN_QUEUE, IntPtr.Zero, IntPtr.Zero);
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || _disposed) return ScreenshotNative.CallNextHookEx(_hook, nCode, wParam, lParam);
        var k = Marshal.PtrToStructure<ScreenshotNative.KBDLLHOOKSTRUCT>(lParam);
        if (k.vkCode == ScreenshotNative.VK_DUMMY && k.dwExtraInfo == ScreenshotNative.DummyExtraInfo)
            return ScreenshotNative.CallNextHookEx(_hook, nCode, wParam, lParam);

        var msg = (int)wParam;
        var down = msg is ScreenshotNative.WM_KEYDOWN or ScreenshotNative.WM_SYSKEYDOWN;
        var up = msg is ScreenshotNative.WM_KEYUP or ScreenshotNative.WM_SYSKEYUP;

        // Release of a key we consumed on the way down: consume it too.
        if (up && _swallowedKeys.Remove(k.vkCode))
            return (IntPtr)1;
        if (down && _swallowedKeys.Contains(k.vkCode))
            return (IntPtr)1; // auto-repeat of a consumed key

        if (up && k.vkCode == Hotkey.VK_SNAPSHOT)
            _dispatcher.BeginInvoke(() => PrintScreenObserved?.Invoke());

        if (!down || Hotkey.IsModifierKey(k.vkCode))
            return ScreenshotNative.CallNextHookEx(_hook, nCode, wParam, lParam);

        var mods = CurrentModifiers();

        if (k.vkCode == ScreenshotNative.VK_ESCAPE && EscapeOverride is { } escape)
        {
            _dispatcher.BeginInvoke(escape);
            _swallowedKeys.Add(k.vkCode);
            return (IntPtr)1;
        }

        if (_recorder is { } recorder)
        {
            if (k.vkCode == ScreenshotNative.VK_ESCAPE && mods == HotkeyModifiers.None)
            {
                var cancel = _recordCancelled;
                _dispatcher.BeginInvoke(() => cancel?.Invoke());
            }
            else
            {
                var hk = new Hotkey(mods, k.vkCode);
                _dispatcher.BeginInvoke(() => recorder(hk));
            }
            Consume(k.vkCode, mods);
            return (IntPtr)1;
        }

        if (!Suspended)
        {
            foreach (var (id, hk) in _hotkeys)
            {
                if (hk.VirtualKey != k.vkCode || hk.Modifiers != mods) continue;
                _dispatcher.BeginInvoke(() => HotkeyPressed?.Invoke(id));
                Consume(k.vkCode, mods);
                return (IntPtr)1;
            }
        }

        return ScreenshotNative.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Consume(int vk, HotkeyModifiers mods)
    {
        _swallowedKeys.Add(vk);
        if (mods.HasFlag(HotkeyModifiers.Win) || mods.HasFlag(HotkeyModifiers.Alt))
            RunOnHookThread(SendDummyKey); // stops Win (Start menu) / Alt (menu bar) from activating on release
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        if (ScreenshotNative.IsDown(ScreenshotNative.VK_LWIN) || ScreenshotNative.IsDown(ScreenshotNative.VK_RWIN)) m |= HotkeyModifiers.Win;
        if (ScreenshotNative.IsDown(ScreenshotNative.VK_SHIFT)) m |= HotkeyModifiers.Shift;
        if (ScreenshotNative.IsDown(ScreenshotNative.VK_CONTROL)) m |= HotkeyModifiers.Control;
        if (ScreenshotNative.IsDown(ScreenshotNative.VK_MENU)) m |= HotkeyModifiers.Alt;
        return m;
    }

    private static void SendDummyKey()
    {
        var inputs = new[]
        {
            new ScreenshotNative.INPUT { type = 1, ki = new ScreenshotNative.KEYBDINPUT { wVk = ScreenshotNative.VK_DUMMY, dwExtraInfo = ScreenshotNative.DummyExtraInfo } },
            new ScreenshotNative.INPUT { type = 1, ki = new ScreenshotNative.KEYBDINPUT { wVk = ScreenshotNative.VK_DUMMY, dwFlags = 0x0002, dwExtraInfo = ScreenshotNative.DummyExtraInfo } },
        };
        ScreenshotNative.SendInput(inputs.Length, inputs, Marshal.SizeOf<ScreenshotNative.INPUT>());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) ScreenshotNative.PostThreadMessageW(_threadId, ScreenshotNative.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
    }
}
