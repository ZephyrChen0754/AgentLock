using System.ComponentModel;
using System.Runtime.InteropServices;
using AgentLock.Ui;

namespace AgentLock.Native;

/// <summary>Monitor privacy overlay excluded from supported capture APIs. All methods belong on the UI thread.</summary>
public sealed class PrivacyCurtain : IDisposable
{
    private readonly List<CurtainWindow> _windows = [];
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private bool _visible, _disposed;
    private string _topology = "";

    public event Action<string>? Faulted;
    public bool IsVisible => _visible && _windows.Count > 0 && _windows.All(w => !w.IsDisposed && w.Visible);
    public int Count => _windows.Count;
    public bool CaptureExcluded => _windows.Count > 0 && _windows.All(w => w.IsHandleCreated
        && Win32.GetWindowDisplayAffinity(w.Handle, out var value) && value == Win32.WDA_EXCLUDEFROMCAPTURE);

    public void Show()
    {
        CheckThread();
        _visible = true;
        Refresh();
    }

    public void Hide()
    {
        CheckThread();
        _visible = false;
        foreach (var window in _windows) window.Hide();
    }

    public void Refresh()
    {
        CheckThread();
        if (!_visible) return;
        try
        {
            var screens = Screen.AllScreens;
            if (screens.Length == 0) throw new InvalidOperationException("没有检测到可覆盖的显示器。");
            var topology = string.Join(";", screens.Select(s => $"{s.DeviceName}:{s.Bounds}"));
            if (_topology != topology || _windows.Count != screens.Length || _windows.Any(w => w.IsDisposed))
                Rebuild(screens, topology);
            foreach (var window in _windows) window.EnsureVisibleAndTopmost();
            if (!CaptureExcluded) throw new InvalidOperationException("隐私幕的截图排除标记已失效。");
        }
        catch (Exception ex)
        {
            Faulted?.Invoke("隐私幕异常：" + ex.Message);
            throw;
        }
    }

    private void Rebuild(Screen[] screens, string topology)
    {
        var replacements = new List<CurtainWindow>();
        try
        {
            foreach (var screen in screens)
            {
                var window = new CurtainWindow(screen.Bounds);
                replacements.Add(window);
                window.Prepare();
            }
            // Show replacements before disposing old overlays to reduce display-change gaps.
            foreach (var window in replacements) window.EnsureVisibleAndTopmost();
        }
        catch
        {
            foreach (var window in replacements) window.Dispose();
            throw;
        }
        var old = _windows.ToArray();
        _windows.Clear();
        _windows.AddRange(replacements);
        _topology = topology;
        foreach (var window in old) window.Dispose();
    }

    private void CheckThread()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _ownerThread) throw new InvalidOperationException("隐私幕必须在创建它的 UI 线程操作。");
    }

    public void Dispose()
    {
        if (_disposed) return;
        CheckThread();
        _disposed = true;
        _visible = false;
        foreach (var window in _windows) window.Dispose();
        _windows.Clear();
    }

    private sealed class CurtainWindow : Form
    {
        private readonly Rectangle _monitorBounds;
        internal CurtainWindow(Rectangle bounds)
        {
            _monitorBounds = bounds;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = Color.Black;
            ShowInTaskbar = false;
            Text = "AgentLock 隐私幕";
            TabStop = false;
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= Win32.WS_EX_TOPMOST | Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT
                    | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
                return parameters;
            }
        }

        internal void Prepare()
        {
            var handle = Handle;
            if (!Win32.SetLayeredWindowAttributes(handle, 0, 255, Win32.LWA_ALPHA))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置隐私幕的不透明图层。");
            if (!Win32.SetWindowDisplayAffinity(handle, Win32.WDA_EXCLUDEFROMCAPTURE))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 拒绝为隐私幕启用截图排除。");
            if (!Win32.GetWindowDisplayAffinity(handle, out var affinity) || affinity != Win32.WDA_EXCLUDEFROMCAPTURE)
                throw new InvalidOperationException("Windows 未确认隐私幕的截图排除标记。");
        }

        internal void EnsureVisibleAndTopmost()
        {
            if (!Visible) Show();
            if (!Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, _monitorBounds.X, _monitorBounds.Y,
                _monitorBounds.Width, _monitorBounds.Height, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法维持显示器隐私幕。");
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            CurtainArtwork.Draw(e.Graphics, ClientSize);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) { message.Result = (IntPtr)3; return; } // WM_MOUSEACTIVATE / MA_NOACTIVATE
            if (message.Msg == 0x0084) { message.Result = (IntPtr)(-1); return; } // WM_NCHITTEST / HTTRANSPARENT
            base.WndProc(ref message);
        }
    }
}
