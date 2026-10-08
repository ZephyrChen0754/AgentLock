using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AgentLock.Native;

public enum GateMode { Open, Guarded, Authenticating }

public readonly record struct GateStats(long BlockedKeyboard, long BlockedMouse, long AllowedInjected, DateTime LastHeartbeatUtc);
public readonly record struct HotkeyStats(GateMode Mode, long GuardedPhysicalEvents, long GuardedInjectedEvents,
    long F12Candidates, long RequestsDetected, long CallbacksInvoked, long HandlerFailures,
    uint LastGuardedKeyboardFlags, bool F12Held,
    bool LastF12WasRepeat, bool LastF12RequestKeyHeld);

public sealed class InputBusyException : InvalidOperationException
{
    public InputBusyException() : base("有按键或鼠标按钮仍处于按下状态，保护尚未开启。请松开全部按键和鼠标按钮，并等待 Agent 完成当前按键动作后重试。") { }
}

/// <summary>Session-local input filter, not an OS security boundary. No keystroke contents are stored.</summary>
public sealed class InputGate : IDisposable
{
    private const long HealthyWindowTicks = TimeSpan.TicksPerSecond * 4;
    private readonly ManualResetEventSlim _started = new(false), _probed = new(false);
    private readonly Win32.HookProc _keyboardCallback, _mouseCallback;
    private readonly nuint _probeMarker;
    private readonly object _stateLock = new();
    private readonly bool[] _physicalKeys = new bool[256];
    private readonly bool[] _allowedPhysicalKeys = new bool[256];
    private readonly bool[] _physicalMouseButtons = new bool[5];
    private readonly bool[] _allowedPhysicalMouseButtons = new bool[5];
    private Thread? _thread;
    private System.Threading.Timer? _timer;
    private IntPtr _keyboardHook, _mouseHook, _allowedWindow;
    private uint _threadId;
    private int _mode, _disposed, _faulted, _startCalled, _probeQueued;
    private long _blockedKeyboard, _blockedMouse, _allowedInjected;
    private long _keyboardProbeTicks, _mouseProbeTicks, _lastHeartbeatTicks, _probeSentTicks;
    private long _keyboardCallbacks, _mouseCallbacks;
    private uint _lastSendCount;
    private int _lastSendError, _probeAttempts;
    private uint _lastMouseFlags;
    private long _lastMouseExtraBits;
    private long _guardedPhysicalEvents, _guardedInjectedEvents, _f12Candidates;
    private long _unlockRequestsDetected, _unlockCallbacksInvoked, _unlockHandlerFailures;
    private uint _lastGuardedKeyboardFlags;
    private bool _lastF12WasRepeat, _lastF12RequestKeyHeld;
    private Exception? _startException;
    private bool _unlockF12Down;

    public event Action? UnlockRequested;
    public event Action<string>? Faulted;

    public InputGate()
    {
        _keyboardCallback = KeyboardHook;
        _mouseCallback = MouseHook;
        // Mouse extra-information can pass through a 32-bit field inside the input
        // stack even for a 64-bit caller. Use one exact, nonzero 31-bit cookie for
        // both routes; never mask an incoming event into a match. A clear sign bit
        // also makes zero/sign extension agree. This is a probe tag, not identity.
        var random = RandomNumberGenerator.GetBytes(sizeof(uint));
        _probeMarker = (nuint)((BitConverter.ToUInt32(random) & 0x3FFFFFFF) | 0x40000000);
    }

    public bool IsHealthy
    {
        get
        {
            var now = DateTime.UtcNow.Ticks;
            return Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _faulted) == 0
                && Volatile.Read(ref _threadId) != 0
                && IsRecent(Interlocked.Read(ref _keyboardProbeTicks), now)
                && IsRecent(Interlocked.Read(ref _mouseProbeTicks), now);
        }
    }

    public GateStats Snapshot => new(Interlocked.Read(ref _blockedKeyboard), Interlocked.Read(ref _blockedMouse),
        Interlocked.Read(ref _allowedInjected), new DateTime(Interlocked.Read(ref _lastHeartbeatTicks), DateTimeKind.Utc));

    /// <summary>F12 counts and event metadata only; no general key values or authentication input are recorded.</summary>
    public HotkeyStats HotkeySnapshot
    {
        get
        {
            lock (_stateLock)
                return new((GateMode)_mode, _guardedPhysicalEvents, _guardedInjectedEvents, _f12Candidates,
                    _unlockRequestsDetected, Interlocked.Read(ref _unlockCallbacksInvoked),
                    Interlocked.Read(ref _unlockHandlerFailures), _lastGuardedKeyboardFlags,
                    _unlockF12Down,
                    _lastF12WasRepeat, _lastF12RequestKeyHeld);
        }
    }

    public string ProbeDetails => $"keyboardSeen={Interlocked.Read(ref _keyboardProbeTicks) != 0}, "
        + $"mouseSeen={Interlocked.Read(ref _mouseProbeTicks) != 0}, keyboardCallbacks={Interlocked.Read(ref _keyboardCallbacks)}, "
        + $"mouseCallbacks={Interlocked.Read(ref _mouseCallbacks)}, sendCount={Volatile.Read(ref _lastSendCount)}/4, "
        + $"sendError={Volatile.Read(ref _lastSendError)}, attempts={Volatile.Read(ref _probeAttempts)}, "
        + $"INPUT={Marshal.SizeOf<Win32.INPUT>()}, inputUnionOffset={Marshal.OffsetOf<Win32.INPUT>(nameof(Win32.INPUT.Data))}, "
        + $"KBDLL={Marshal.SizeOf<Win32.KBDLLHOOKSTRUCT>()}, MSLL={Marshal.SizeOf<Win32.MSLLHOOKSTRUCT>()}, "
        + $"mouseInputExtraOffset={Marshal.OffsetOf<Win32.MOUSEINPUT>(nameof(Win32.MOUSEINPUT.ExtraInfo))}, "
        + $"mouseHookExtraOffset={Marshal.OffsetOf<Win32.MSLLHOOKSTRUCT>(nameof(Win32.MSLLHOOKSTRUCT.ExtraInfo))}, "
        + $"lastMouseFlags=0x{Volatile.Read(ref _lastMouseFlags):X}, "
        + $"lastMouseExtra=0x{unchecked((ulong)Interlocked.Read(ref _lastMouseExtraBits)):X}, probeMarker=0x{_probeMarker:X}, "
        + $"inputDesktop={Win32.TryGetInputDesktopName() ?? "unavailable"}";

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _startCalled, 1) != 0) throw new InvalidOperationException("InputGate has already started.");
        _thread = new Thread(Run) { IsBackground = true, Name = "AgentLock input hooks" };
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(3)))
        {
            ReportFault("输入线程启动超时。");
            throw new TimeoutException("Input hook thread did not start.");
        }
        if (_startException is not null) throw new InvalidOperationException("输入监测无法启动。", _startException);
        _timer = new System.Threading.Timer(Tick, null, 0, 750);
        if (!_probed.Wait(TimeSpan.FromSeconds(3)) || !IsHealthy)
        {
            ReportFault("键盘或鼠标功能探针未返回；当前桌面可能不是普通交互桌面。");
            throw new InvalidOperationException("Input hook functional probes failed. " + ProbeDetails);
        }
    }

    public void SetMode(GateMode mode, IntPtr allowedWindow = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode != GateMode.Open && !IsHealthy) throw new InvalidOperationException("输入过滤器未通过健康检查。");
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if ((GateMode)_mode == GateMode.Open && mode == GateMode.Guarded)
            {
                // Share this check and publication with hook decisions: an admitted
                // physical down cannot slip between checking and entering Guarded.
                // Async state also catches keys held before our hooks started and
                // agent-held input. Query it here, never in an input callback.
                if (_physicalKeys.Any(down => down) || _physicalMouseButtons.Any(down => down))
                    throw new InputBusyException();
                for (int key = 1; key < 256; key++)
                    if (Win32.GetAsyncKeyState(key) < 0) throw new InputBusyException();
                Array.Clear(_allowedPhysicalKeys);
                Array.Clear(_allowedPhysicalMouseButtons);
            }
            // Auth -> Guarded keeps admitted downs so only their matching physical
            // releases can finish. We never synthesize releases for agent-held keys.
            Interlocked.Exchange(ref _allowedWindow, allowedWindow);
            Volatile.Write(ref _mode, (int)mode);
        }
    }

    private void Run()
    {
        try
        {
            Win32.PeekMessage(out _, IntPtr.Zero, 0, 0, Win32.PM_NOREMOVE);
            Volatile.Write(ref _threadId, Win32.GetCurrentThreadId());
            var module = Win32.GetModuleHandle(null);
            _keyboardHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyboardCallback, module, 0);
            if (_keyboardHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Keyboard hook installation failed.");
            _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseCallback, module, 0);
            if (_mouseHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Mouse hook installation failed.");
            _started.Set();
            int result;
            while ((result = Win32.GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                Win32.TranslateMessage(in message);
                Win32.DispatchMessage(in message);
            }
            if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Input message loop failed.");
        }
        catch (Exception ex)
        {
            _startException = ex;
            ReportFault("输入过滤器异常：" + ex.Message);
        }
        finally
        {
            if (_keyboardHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_mouseHook);
            _keyboardHook = _mouseHook = IntPtr.Zero;
            Volatile.Write(ref _threadId, 0);
            _started.Set();
            if (Volatile.Read(ref _disposed) == 0) ReportFault("输入监测线程已经停止。");
        }
    }

    private void Tick(object? _)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _faulted) != 0) return;
        var now = DateTime.UtcNow.Ticks;
        var firstSent = Interlocked.Read(ref _probeSentTicks);
        if (firstSent != 0 && now - firstSent > HealthyWindowTicks && !IsHealthy)
        {
            ReportFault("输入功能心跳失效，过滤钩子可能已被系统移除或交互桌面已切换。");
            return;
        }
        if (Interlocked.CompareExchange(ref _probeQueued, 1, 0) == 0)
        {
            // Do not call SendInput on the thread that receives the hook callbacks:
            // that thread must keep pumping while Windows delivers injected events.
            try { SendProbes(); }
            catch (Exception ex) { ReportFault("功能探针异常：" + ex.Message); }
            finally { Interlocked.Exchange(ref _probeQueued, 0); }
        }
    }

    private void SendProbes()
    {
        Interlocked.CompareExchange(ref _probeSentTicks, DateTime.UtcNow.Ticks, 0);
        // Unique marked events are swallowed by our own callbacks. Windows may discard
        // zero-distance movement before a low-level callback, so send a tiny reversible
        // relative pair. If a hook is missing there may be a one-unit cursor movement;
        // acceleration and screen-edge clipping mean exact return cannot be guaranteed.
        // The probe never clicks, scrolls, types text, or opens applications.
        var inputs = new[]
        {
            Win32.CreateKeyboardInput(Win32.VK_F24, _probeMarker),
            Win32.CreateKeyboardInput(Win32.VK_F24, _probeMarker, Win32.KEYEVENTF_KEYUP),
            Win32.CreateMouseInput(dx: 1, flags: Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_MOVE_NOCOALESCE, extraInfo: _probeMarker),
            Win32.CreateMouseInput(dx: -1, flags: Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_MOVE_NOCOALESCE, extraInfo: _probeMarker)
        };
        Interlocked.Increment(ref _probeAttempts);
        Marshal.SetLastPInvokeError(0);
        var sent = Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
        Volatile.Write(ref _lastSendCount, sent);
        Volatile.Write(ref _lastSendError, Marshal.GetLastWin32Error());
        if (sent != inputs.Length)
            ReportFault("功能探针无法注入；当前桌面或权限不支持过滤健康检查。" + ProbeDetails);
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return Win32.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        try
        {
            Interlocked.Increment(ref _keyboardCallbacks);
            var key = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            if (key.ExtraInfo == _probeMarker && key.VkCode == Win32.VK_F24 && (key.Flags & Win32.LLKHF_INJECTED) != 0)
            {
                ProbeSeen(true);
                return (IntPtr)1;
            }
            var injected = (key.Flags & Win32.LLKHF_INJECTED) != 0;
            var message = unchecked((uint)wParam.ToInt64());
            var down = message is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
            var up = message is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;
            bool block, requestUnlock = false;
            lock (_stateLock)
            {
                var mode = (GateMode)_mode;
                bool tracked = !injected && key.VkCode < _physicalKeys.Length && (down || up);
                bool admittedDown = tracked && _allowedPhysicalKeys[key.VkCode];
                bool alreadyPhysicallyDown = tracked && _physicalKeys[key.VkCode];
                bool shortcutEvent = false;
                if (mode == GateMode.Guarded)
                {
                    if (injected) _guardedInjectedEvents++; else _guardedPhysicalEvents++;
                    _lastGuardedKeyboardFlags = key.Flags;
                    if (tracked && down && key.VkCode == Win32.VK_F12)
                    {
                        _f12Candidates++;
                        // Fixed F12 metadata in Guarded only, including repeats.
                        // No authentication input or ordinary key values are saved.
                        _lastF12WasRepeat = alreadyPhysicallyDown;
                        _lastF12RequestKeyHeld = _unlockF12Down;
                    }
                }
                if (tracked)
                {
                    _physicalKeys[key.VkCode] = down;
                    if (_unlockF12Down && key.VkCode == Win32.VK_F12)
                    {
                        shortcutEvent = true;
                        if (up) _unlockF12Down = false;
                    }
                    else if (ShouldRequestUnlock(mode, injected, key.VkCode, down, alreadyPhysicallyDown,
                        requestKeyHeld: _unlockF12Down))
                    {
                        // Keep the matching repeats/up swallowed even if opening
                        // the dialog or unlocking changes mode while F12 is held.
                        requestUnlock = true;
                        _unlockF12Down = true;
                        _unlockRequestsDetected++;
                        shortcutEvent = true;
                    }
                }
                block = shortcutEvent || ShouldBlock(mode, injected)
                    || (!injected && mode == GateMode.Authenticating
                        && ShouldBlockAuthenticationKey(key.VkCode, ControlDown, AltDown, WindowsDown));
                if (!shortcutEvent && ShouldAllowPairedRelease(mode, injected, up, admittedDown)) block = false;
                // Releases for a down swallowed during Guarded must not clear an
                // agent's existing key state.
                if (tracked && up && mode == GateMode.Authenticating && !admittedDown) block = true;
                if (tracked && !block)
                {
                    if (down) _allowedPhysicalKeys[key.VkCode] = true;
                    else if (up) _allowedPhysicalKeys[key.VkCode] = false;
                }
            }
            if (requestUnlock)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    try
                    {
                        var handler = UnlockRequested;
                        if (handler is null) throw new InvalidOperationException("没有注册解锁请求处理程序。");
                        Interlocked.Increment(ref _unlockCallbacksInvoked);
                        handler.Invoke();
                    }
                    catch (Exception error)
                    {
                        Interlocked.Increment(ref _unlockHandlerFailures);
                        ReportFault("解锁请求处理失败（" + error.GetType().Name + "），需要通过 Windows 登录恢复。");
                    }
                });
            }
            if (block)
            {
                Interlocked.Increment(ref _blockedKeyboard);
                return (IntPtr)1;
            }
            if (injected) Interlocked.Increment(ref _allowedInjected);
            // Other hooks may synchronously send messages: never hold our state
            // lock while entering another process's hook chain.
            return Win32.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }
        catch
        {
            ReportFault("键盘过滤回调异常。");
            return (GateMode)Volatile.Read(ref _mode) == GateMode.Open ? Win32.CallNextHookEx(_keyboardHook, code, wParam, lParam) : (IntPtr)1;
        }
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return Win32.CallNextHookEx(_mouseHook, code, wParam, lParam);
        try
        {
            Interlocked.Increment(ref _mouseCallbacks);
            var mouse = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            Volatile.Write(ref _lastMouseFlags, mouse.Flags);
            Interlocked.Exchange(ref _lastMouseExtraBits, unchecked((long)(ulong)mouse.ExtraInfo));
            if (wParam == (IntPtr)0x0200 && mouse.ExtraInfo == _probeMarker && (mouse.Flags & Win32.LLMHF_INJECTED) != 0)
            {
                ProbeSeen(false);
                return (IntPtr)1;
            }
            var injected = (mouse.Flags & Win32.LLMHF_INJECTED) != 0;
            bool block;
            lock (_stateLock)
            {
                var mode = (GateMode)_mode;
                int button = MouseButtonEvent(unchecked((uint)wParam.ToInt64()), mouse.MouseData, out bool down, out bool up);
                bool tracked = !injected && button >= 0;
                bool admittedDown = tracked && _allowedPhysicalMouseButtons[button];
                if (tracked) _physicalMouseButtons[button] = down;
                // A physical click on our visible dialog must be able to activate
                // it even if another application still has foreground focus.
                // Hit-window and capture checks keep that first click on our UI.
                block = mode switch
                {
                    GateMode.Open => false,
                    GateMode.Guarded => !injected,
                    GateMode.Authenticating => injected || !IsAuthenticationMouseAllowed(mouse.Point),
                    _ => true
                };
                if (ShouldAllowPairedRelease(mode, injected, up, admittedDown)) block = false;
                if (tracked && up && mode == GateMode.Authenticating && !admittedDown) block = true;
                if (tracked && !block)
                {
                    if (down) _allowedPhysicalMouseButtons[button] = true;
                    else if (up) _allowedPhysicalMouseButtons[button] = false;
                }
            }
            if (block)
            {
                Interlocked.Increment(ref _blockedMouse);
                return (IntPtr)1;
            }
            if (injected) Interlocked.Increment(ref _allowedInjected);
            return Win32.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }
        catch
        {
            ReportFault("鼠标过滤回调异常。");
            return (GateMode)Volatile.Read(ref _mode) == GateMode.Open ? Win32.CallNextHookEx(_mouseHook, code, wParam, lParam) : (IntPtr)1;
        }
    }

    /// <summary>Pure policy function; injected is an event attribute, not a trusted-agent identity.</summary>
    public static bool ShouldBlockInput(GateMode mode, bool injected, bool allowedForeground)
        => mode switch
        {
            GateMode.Open => false,
            GateMode.Guarded => !injected,
            GateMode.Authenticating => injected || !allowedForeground,
            _ => true
        };

    /// <summary>Only a matching physical release may finish an already admitted physical press.</summary>
    public static bool ShouldAllowPairedRelease(GateMode mode, bool injected, bool isRelease, bool hadAllowedPhysicalDown)
        => mode is GateMode.Guarded or GateMode.Authenticating
            && !injected && isRelease && hadAllowedPhysicalDown;

    /// <summary>Only fresh physical F12 requests the password dialog; it does not authenticate the caller.</summary>
    public static bool ShouldRequestUnlock(GateMode mode, bool injected, uint virtualKey, bool isDown,
        bool wasAlreadyDown, bool requestKeyHeld)
        => mode == GateMode.Guarded && !injected && isDown && !wasAlreadyDown && !requestKeyHeld
            && virtualKey == Win32.VK_F12;

    /// <summary>New mouse input is limited to our visible hit window with safe mouse capture, regardless of foreground.</summary>
    public static bool ShouldBlockAuthenticationMouse(bool injected, bool allowedWindowVisible, bool insideAllowedWindow,
        bool hitsAllowedWindow, bool captureAllowed)
        => injected || !allowedWindowVisible || !insideAllowedWindow || !hitsAllowedWindow || !captureAllowed;

    private static int MouseButtonEvent(uint message, uint data, out bool down, out bool up)
    {
        down = message is 0x0201 or 0x0204 or 0x0207 or 0x020B;
        up = message is 0x0202 or 0x0205 or 0x0208 or 0x020C;
        return message switch
        {
            0x0201 or 0x0202 => 0,
            0x0204 or 0x0205 => 1,
            0x0207 or 0x0208 => 2,
            0x020B or 0x020C when (data >> 16) == 1 => 3,
            0x020B or 0x020C when (data >> 16) == 2 => 4,
            _ => -1
        };
    }

    /// <summary>Prevent ordinary-desktop shortcuts from leaving the password window. SAS remains a Windows boundary.</summary>
    public static bool ShouldBlockAuthenticationKey(uint key, bool control, bool alt, bool windows)
        => key is 0x5B or 0x5C || windows
            || (alt && key is 0x09 or 0x1B or 0x73 or 0x20) // Alt+Tab/Esc/F4/Space
            || (control && key == 0x1B) // Ctrl+Esc and Ctrl+Shift+Esc
            || (control && alt && key == 0x2E); // SAS itself cannot be intercepted reliably.

    private bool ControlDown => _physicalKeys[Win32.VK_CONTROL] || _physicalKeys[0xA2] || _physicalKeys[0xA3];
    private bool AltDown => _physicalKeys[Win32.VK_MENU] || _physicalKeys[0xA4] || _physicalKeys[0xA5];
    private bool WindowsDown => _physicalKeys[0x5B] || _physicalKeys[0x5C];

    private bool IsAuthenticationMouseAllowed(Win32.POINT point)
    {
        var allowed = Interlocked.CompareExchange(ref _allowedWindow, IntPtr.Zero, IntPtr.Zero);
        if (allowed == IntPtr.Zero || !Win32.IsWindowVisible(allowed) || !Win32.GetWindowRect(allowed, out var rect)) return false;
        bool inside = point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
        if (!inside) return false;
        var hit = Win32.WindowFromPoint(point);
        bool hitsAllowed = hit == allowed || (hit != IntPtr.Zero && Win32.IsChild(allowed, hit));
        if (!hitsAllowed) return false;
        // GetCapture() would inspect our hook thread, not the active GUI thread.
        // Query the foreground GUI state instead; an unknown or unrelated capture
        // rejects new input. A previously admitted release may still finish above.
        var info = new Win32.GUITHREADINFO { Size = (uint)Marshal.SizeOf<Win32.GUITHREADINFO>() };
        bool captureAllowed = Win32.GetGUIThreadInfo(0, ref info)
            && (info.Capture == IntPtr.Zero || info.Capture == allowed || Win32.IsChild(allowed, info.Capture));
        return !ShouldBlockAuthenticationMouse(false, true, inside, hitsAllowed, captureAllowed);
    }

    /// <summary>Read-only compatibility query for our diagnostic UI; never injects or changes mode.</summary>
    internal bool CanAcceptAuthenticationMouseAt(Point point)
    {
        lock (_stateLock)
            return (GateMode)_mode == GateMode.Authenticating
                && IsAuthenticationMouseAllowed(new Win32.POINT { X = point.X, Y = point.Y });
    }

    private bool ShouldBlock(GateMode mode, bool injected)
    {
        if (mode == GateMode.Open) return false;
        if (mode == GateMode.Guarded) return !injected;
        if (injected) return true;
        var allowed = Interlocked.CompareExchange(ref _allowedWindow, IntPtr.Zero, IntPtr.Zero);
        if (allowed == IntPtr.Zero || !Win32.IsWindow(allowed)) return true;
        var foreground = Win32.GetForegroundWindow();
        return ShouldBlockInput(mode, injected, foreground == allowed || Win32.IsChild(allowed, foreground));
    }

    private void ProbeSeen(bool keyboard)
    {
        var now = DateTime.UtcNow.Ticks;
        if (keyboard) Interlocked.Exchange(ref _keyboardProbeTicks, now);
        else Interlocked.Exchange(ref _mouseProbeTicks, now);
        if (IsRecent(Interlocked.Read(ref _keyboardProbeTicks), now) && IsRecent(Interlocked.Read(ref _mouseProbeTicks), now))
        {
            Interlocked.Exchange(ref _lastHeartbeatTicks, now);
            _probed.Set();
        }
    }

    private static bool IsRecent(long ticks, long now) => ticks > 0 && now >= ticks && now - ticks < HealthyWindowTicks;

    private void ReportFault(string message)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _faulted, 1) == 0)
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                try { Faulted?.Invoke(message); } catch { }
            });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();
        lock (_stateLock) Volatile.Write(ref _mode, (int)GateMode.Open);
        var threadId = Volatile.Read(ref _threadId);
        if (threadId != 0) Win32.PostThreadMessage(threadId, Win32.WM_QUIT, 0, 0);
        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join(TimeSpan.FromSeconds(2));
        // Events deliberately remain undisposed if a stalled callback is still exiting.
        GC.KeepAlive(_keyboardCallback);
        GC.KeepAlive(_mouseCallback);
    }
}
