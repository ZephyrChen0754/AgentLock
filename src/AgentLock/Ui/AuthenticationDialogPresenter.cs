using AgentLock.Native;

namespace AgentLock.Ui;

/// <summary>Prepares an already-shown password dialog without bypassing Windows foreground rules.</summary>
internal static class AuthenticationDialogPresenter
{
    internal static bool PrepareShown(UnlockForm dialog, InputGate gate)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(gate);
        if (dialog.IsDisposed || !dialog.IsHandleCreated || !dialog.Visible)
            throw new InvalidOperationException("认证窗口尚未显示，无法开启密码输入。");

        if (!Win32.SetWindowDisplayAffinity(dialog.Handle, Win32.WDA_EXCLUDEFROMCAPTURE)
            || !Win32.GetWindowDisplayAffinity(dialog.Handle, out var affinity)
            || affinity != Win32.WDA_EXCLUDEFROMCAPTURE)
            throw new InvalidOperationException("Windows 未确认认证窗口的截图排除，已停止密码输入。");

        // SetCursorPos through Cursor.Position moves only the pointer; it does not send a click.
        var destination = dialog.PointToScreen(new Point(dialog.ClientSize.Width / 2, dialog.ClientSize.Height / 2));
        Cursor.Position = destination;
        gate.SetMode(GateMode.Authenticating, dialog.Handle);
        if (!dialog.Bounds.Contains(Cursor.Position))
            throw new InvalidOperationException("无法将鼠标定位到认证窗口，已停止密码输入。");

        if (!Win32.SetWindowPos(dialog.Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW))
            throw new InvalidOperationException("无法将认证窗口显示在隐私幕前方。");

        dialog.Activate();
        bool foreground = IsForeground(dialog);
        if (!foreground) dialog.ShowFocusHint();
        return foreground;
    }

    internal static bool IsForeground(UnlockForm dialog)
        => !dialog.IsDisposed && dialog.IsHandleCreated && Win32.GetForegroundWindow() == dialog.Handle;
}
