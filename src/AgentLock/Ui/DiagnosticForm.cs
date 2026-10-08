using System.Runtime.InteropServices;
using System.Text.Json;
using AgentLock.Native;
using AgentLock.Security;

namespace AgentLock.Ui;

internal sealed class DiagnosticForm : Form
{
    private readonly string[] arguments;
    private readonly Label result = new() { Dock = DockStyle.Fill, AutoSize = false };
    private readonly Button run = new RoundedButton() { Text = "开始兼容性检查", Dock = DockStyle.Bottom, Height = 48 };
    private readonly PatternPanel target = new() { Dock = DockStyle.Fill };
    private readonly TextBox typingTarget = new()
    {
        Dock = DockStyle.Bottom,
        MaxLength = 128,
        ShortcutsEnabled = false,
        PlaceholderText = "检查时会在这里填写测试文字",
        AccessibleName = "仅供兼容性检查的测试输入框"
    };
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 220 };
    private bool working;
    private TaskCompletionSource<bool>? ordinaryInputCompletion;
    private readonly nuint ordinaryInputMarker = (nuint)Random.Shared.Next(1, int.MaxValue);
    private bool ordinaryKeyDownReceived, ordinaryKeyUpReceived;
    private TaskCompletionSource<bool>? ordinaryMouseCompletion;
    private readonly nuint ordinaryMouseMarker = (nuint)Random.Shared.Next(1, int.MaxValue);
    private bool ordinaryMouseMoveReceived, ordinaryMouseDownReceived, ordinaryMouseUpReceived;
    private const uint MouseLeftDown = 0x0002, MouseLeftUp = 0x0004;
    private const uint MouseVirtualDesk = 0x4000, MouseAbsolute = 0x8000, KeyboardUnicode = 0x0004;

    public DiagnosticForm(string[] args)
    {
        arguments = args;
        Text = "AgentLock · 桌面兼容性检查";
        VisualTheme.ApplyForm(this);
        Icon = BrandMark.LoadIcon();
        ClientSize = new Size(740, 580);
        MinimumSize = new Size(640, 500);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(new Label { Text = "同桌面兼容性检查\n屏幕会短暂变黑；密码窗口展示 2 秒后自动取消。不会设置密码。", Dock = DockStyle.Fill, Font = VisualTheme.UiFont(11), ForeColor = VisualTheme.Ink });
        layout.Controls.Add(target); layout.Controls.Add(result); layout.Controls.Add(run); Controls.Add(layout);
        VisualTheme.StyleButton(run, primary: true);
        result.ForeColor = VisualTheme.Muted;
        target.Padding = new Padding(12);
        target.Controls.Add(typingTarget);
        animation.Tick += (_, _) => { target.Sequence++; target.Invalidate(); };
        animation.Start();
        KeyDown += (_, e) => ObserveOrdinaryInput(e, keyDown: true);
        KeyUp += (_, e) => ObserveOrdinaryInput(e, keyDown: false);
        target.MouseMove += (_, e) => ObserveOrdinaryMouse(e, MouseObservedEvent.Move);
        target.MouseDown += (_, e) => ObserveOrdinaryMouse(e, MouseObservedEvent.Down);
        target.MouseUp += (_, e) => ObserveOrdinaryMouse(e, MouseObservedEvent.Up);
        run.Click += async (_, _) => await CheckAsync();
        Shown += async (_, _) => { Activate(); if (arguments.Contains("--auto")) { await Task.Delay(600); await CheckAsync(); if (arguments.Contains("--close")) Close(); } };
    }

    private async Task CheckAsync()
    {
        if (working) return;
        working = true; run.Enabled = false;
        var report = new Dictionary<string, object?>
        {
            ["checkedAt"] = DateTimeOffset.Now, ["version"] = Application.ProductVersion.Split('+')[0], ["os"] = Environment.OSVersion.VersionString,
            ["scope"] = "own-test-window; two-frame GDI capture; foreground-checked F23, own-panel mouse and own-field Unicode SendInput; no real-Agent claim",
            ["realAgentCapture"] = "pending", ["physicalKeyboardMouse"] = "pending",
            ["touch"] = RuntimeState.GetSystemMetrics(95), ["monitorCount"] = Screen.AllScreens.Length
        };
        using var filter = new InputGate();
        using var privacy = new PrivacyCurtain();
        try
        {
            result.Text = "检查将在 1 秒后开始，屏幕自动恢复。";
            await Task.Delay(1000);
            filter.Start(); report["hookFunctionalProbes"] = filter.IsHealthy;
            using var before = CaptureTarget();
            if (ColorDistance(SampleColor(before, target.DynamicRegion), target.DynamicColor) >= 24)
                throw new InvalidOperationException("测试窗口被其他窗口遮挡，已停止截图。请点击检查窗口后重新开始。");
            var baseline = before.GetPixel(before.Width / 2, before.Height / 2);
            IntPtr foreground = RuntimeState.GetForegroundWindow();
            int sequenceBefore = target.Sequence;
            filter.SetMode(GateMode.Guarded);
            privacy.Show();
            report["curtainVisible"] = privacy.IsVisible;
            report["captureExclusionConfirmed"] = privacy.CaptureExcluded;
            await Task.Delay(750);
            using var after = CaptureTarget();
            var through = after.GetPixel(after.Width / 2, after.Height / 2);
            int firstCapturedSequence = target.Sequence;
            var dynamicRegion = target.DynamicRegion;
            var firstDynamicColor = SampleColor(after, dynamicRegion);
            var firstExpectedColor = target.DynamicColor;
            var ordinaryAllowedBefore = filter.Snapshot.AllowedInjected;
            report["ordinaryInjectedKeyboardPasses"] = await CheckOrdinaryInjectedInputAsync(report);
            report["ordinaryInjectedHookCountDelta"] = filter.Snapshot.AllowedInjected - ordinaryAllowedBefore;
            ordinaryAllowedBefore = filter.Snapshot.AllowedInjected;
            report["ordinaryInjectedMousePasses"] = await CheckOrdinaryInjectedMouseAsync(report);
            report["ordinaryInjectedMouseHookCountDelta"] = filter.Snapshot.AllowedInjected - ordinaryAllowedBefore;
            ordinaryAllowedBefore = filter.Snapshot.AllowedInjected;
            report["ordinaryInjectedTextPasses"] = await CheckOrdinaryInjectedTextAsync(report);
            report["ordinaryInjectedTextHookCountDelta"] = filter.Snapshot.AllowedInjected - ordinaryAllowedBefore;
            await Task.Delay(750);
            using var second = CaptureTarget();
            var secondThrough = second.GetPixel(second.Width / 2, second.Height / 2);
            var secondDynamicColor = SampleColor(second, dynamicRegion);
            var secondExpectedColor = target.DynamicColor;
            int capturedColorDistance = ColorDistance(firstDynamicColor, secondDynamicColor);
            int expectedColorDistance = ColorDistance(firstExpectedColor, secondExpectedColor);
            bool firstMatchesExpected = ColorDistance(firstDynamicColor, firstExpectedColor) < 24;
            bool secondMatchesExpected = ColorDistance(secondDynamicColor, secondExpectedColor) < 24;
            report["gdiSeesUnderlyingTestWindow"] = ColorDistance(baseline, through) < 24
                && ColorDistance(baseline, secondThrough) < 24 && firstMatchesExpected && secondMatchesExpected;
            report["foregroundPreserved"] = foreground == RuntimeState.GetForegroundWindow();
            report["underlyingWindowStillUpdates"] = firstCapturedSequence > sequenceBefore
                && target.Sequence > firstCapturedSequence && expectedColorDistance > 24
                && capturedColorDistance > 24 && firstMatchesExpected && secondMatchesExpected;
            report["dynamicRegion"] = new { dynamicRegion.X, dynamicRegion.Y, dynamicRegion.Width, dynamicRegion.Height };
            report["firstCapturedSequence"] = firstCapturedSequence;
            report["secondCapturedSequence"] = target.Sequence;
            report["dynamicRegionFirstColor"] = ColorHex(firstDynamicColor);
            report["dynamicRegionSecondColor"] = ColorHex(secondDynamicColor);
            report["dynamicRegionFirstExpectedColor"] = ColorHex(firstExpectedColor);
            report["dynamicRegionSecondExpectedColor"] = ColorHex(secondExpectedColor);
            report["dynamicRegionColorDistance"] = capturedColorDistance;
            report["dynamicRegionExpectedColorDistance"] = expectedColorDistance;
            report["dynamicRegionFirstMatchesExpected"] = firstMatchesExpected;
            report["dynamicRegionSecondMatchesExpected"] = secondMatchesExpected;
            report["guardedHookFunctionalProbes"] = filter.IsHealthy;
            // Optional authentication UI metadata must not alter the already-measured foundation verdict.
            report["result"] = report.Where(v => v.Value is bool).All(v => (bool)v.Value!) ? "foundation-passed; real-Agent and physical-input pending" : "foundation-failed";
            string output = GetOutputPath(); Directory.CreateDirectory(output);
            after.Save(Path.Combine(output, "diagnostic-capture.png"));
            second.Save(Path.Combine(output, "diagnostic-capture-second.png"));
            before.Save(Path.Combine(output, "diagnostic-baseline.png"));
            report["authenticationDialog"] = CheckAuthenticationDialog(filter, privacy, output);
            report["physicalUnlockHotkey"] = "pending: this UI check does not generate or prove a physical hotkey";
            report["passwordUnlock"] = "pending: no password was set, entered or submitted in this UI check";
            filter.SetMode(GateMode.Open); privacy.Hide();
            result.Text = "基础检查：" + (report["result"]!.ToString()!.StartsWith("foundation-passed") ? "通过" : "需要处理")
                + "\n截图兼容：" + report["gdiSeesUnderlyingTestWindow"] + "  动态画面：" + report["underlyingWindowStillUpdates"]
                + "\n自动输入：键盘 " + report["ordinaryInjectedKeyboardPasses"] + " / 点击 " + report["ordinaryInjectedMousePasses"] + " / 文字 " + report["ordinaryInjectedTextPasses"]
                + "\n还需实际 Agent 截图和实体键鼠验证。\n记录位置：" + output;
            File.WriteAllText(Path.Combine(output, "diagnostic-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            report["result"] = "failed"; report["error"] = e.Message;
            result.Text = "检查未通过：" + e.Message + "\n屏幕与键鼠已恢复。";
            var output = GetOutputPath(); Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "diagnostic-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            try { filter.SetMode(GateMode.Open); } catch { }
            privacy.Hide(); working = false; run.Enabled = true;
        }
    }

    private static Dictionary<string, object?> CheckAuthenticationDialog(InputGate gate, PrivacyCurtain privacy, string output)
    {
        var report = new Dictionary<string, object?>
        {
            ["scope"] = "own authentication UI only; shared presenter; no physical hotkey or password verification",
            ["foregroundPolicy"] = "metadata only: Windows may refuse automatic activation; physical focus acquisition remains pending",
            ["temporaryStoreConfigured"] = false,
            ["previewKind"] = "DrawToBitmap of own empty dialog for layout; not a desktop screenshot or physical visibility proof"
        };
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "AgentLock.AuthenticationUi", Guid.NewGuid().ToString("N"));
        var store = new PasswordStore(temporaryRoot); // No directory, password verifier or production credential is created.
        using var dialog = new UnlockForm(store);
        using var refreshTimer = new System.Windows.Forms.Timer { Interval = 300 };
        using var closeTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        bool cursorSaved = GetCursorPos(out var originalCursor);
        bool automaticCancellation = false;
        bool sampledAfterRefresh = false;
        bool shown = false;

        static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }

        void Cancel()
        {
            if (dialog.IsDisposed) return;
            dialog.DialogResult = DialogResult.Cancel;
            dialog.Close();
        }

        void RecordFailure(Exception error)
        {
            report["status"] = "failed: own authentication UI could not be checked";
            report["error"] = error.Message;
            Cancel();
        }

        try
        {
            if (store.IsConfigured)
                throw new InvalidOperationException("临时认证检查路径不是空白路径，已取消检查。");
            var controls = Descendants(dialog).ToArray();
            var password = controls.OfType<TextBox>().Single(field => field.UseSystemPasswordChar);
            var cancel = controls.OfType<Button>().Single(button => button.DialogResult == DialogResult.Cancel);
            var submit = controls.OfType<Button>().Single(button => button.Text == "解锁");
            // Keep the real field's layout/hit path but prevent anyone entering a secret during the timed preview.
            password.ReadOnly = true;
            password.ShortcutsEnabled = false;
            submit.Enabled = false;
            dialog.AcceptButton = null;
            report["passwordInputDisabledForPreview"] = password.ReadOnly && !submit.Enabled && dialog.AcceptButton is null;

            void Sample(string phase)
            {
                bool affinityConfirmed = Win32.GetWindowDisplayAffinity(dialog.Handle, out uint affinity)
                    && affinity == Win32.WDA_EXCLUDEFROMCAPTURE;
                bool visible = dialog.Visible && Win32.IsWindowVisible(dialog.Handle);
                bool nativeTopMost = (GetExtendedWindowStyle(dialog.Handle) & Win32.WS_EX_TOPMOST) != 0;
                bool PasswordOrButtonHitOwned(Control control)
                {
                    if (!control.Visible || !control.IsHandleCreated) return false;
                    var point = control.PointToScreen(new Point(control.ClientSize.Width / 2, control.ClientSize.Height / 2));
                    var hit = Win32.WindowFromPoint(new Win32.POINT { X = point.X, Y = point.Y });
                    Win32.GetWindowThreadProcessId(hit, out uint processId);
                    return hit != IntPtr.Zero && processId == Environment.ProcessId
                        && (hit == dialog.Handle || Win32.IsChild(dialog.Handle, hit));
                }
                bool passwordHit = PasswordOrButtonHitOwned(password);
                bool cancelHit = PasswordOrButtonHitOwned(cancel);
                bool mouseFilterAcceptsPasswordField = gate.CanAcceptAuthenticationMouseAt(
                    password.PointToScreen(new Point(password.ClientSize.Width / 2, password.ClientSize.Height / 2)));
                bool foreground = AuthenticationDialogPresenter.IsForeground(dialog);
                report[phase] = new
                {
                    visible,
                    nativeTopMost,
                    affinityConfirmed,
                    affinity = $"0x{affinity:X}",
                    passwordFieldHitOwned = passwordHit,
                    cancelButtonHitOwned = cancelHit,
                    mouseFilterAcceptsPasswordField,
                    foreground,
                    curtainVisible = privacy.IsVisible,
                    curtainCaptureExcluded = privacy.CaptureExcluded,
                    dialogBounds = new { dialog.Bounds.X, dialog.Bounds.Y, dialog.Bounds.Width, dialog.Bounds.Height }
                };
                if (phase == "afterCurtainRefresh")
                {
                    // Foreground is deliberately excluded: no OS focus restrictions are bypassed for a PASS.
                    report["displayPathPassed"] = visible && nativeTopMost && affinityConfirmed && (passwordHit || cancelHit)
                        && mouseFilterAcceptsPasswordField && privacy.IsVisible && privacy.CaptureExcluded;
                    report["foregroundAfterRefresh"] = foreground;
                    sampledAfterRefresh = true;
                }
            }

            closeTimer.Tick += (_, _) =>
            {
                closeTimer.Stop();
                automaticCancellation = true;
                Cancel();
            };
            refreshTimer.Tick += (_, _) =>
            {
                refreshTimer.Stop();
                try
                {
                    privacy.Refresh();
                    if (!Win32.SetWindowPos(dialog.Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW))
                        throw new InvalidOperationException("刷新隐私幕后无法重新显示认证窗口。");
                    Sample("afterCurtainRefresh");
                    if (password.Text.Length != 0)
                        throw new InvalidOperationException("空白认证窗口检查已停止，未保存窗口图像。");
                    using var preview = new Bitmap(dialog.Width, dialog.Height);
                    dialog.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size));
                    string previewPath = Path.Combine(output, "auth-dialog.png");
                    preview.Save(previewPath);
                    report["emptyDialogPreviewSaved"] = true;
                    report["emptyDialogPreviewPath"] = previewPath;
                }
                catch (Exception error) { RecordFailure(error); }
            };
            dialog.Shown += (_, _) =>
            {
                shown = true;
                closeTimer.Start();
                try
                {
                    report["foregroundAtShown"] = AuthenticationDialogPresenter.PrepareShown(dialog, gate);
                    Sample("shown");
                    refreshTimer.Start();
                }
                catch (Exception error) { RecordFailure(error); }
            };
            // Match the product's ownerless modal path; only our own window is activated by the presenter.
            dialog.ShowDialog();
            report["shownEventReceived"] = shown;
            report["curtainRefreshedAndDialogRaised"] = sampledAfterRefresh;
            report["automaticallyCancelled"] = automaticCancellation;
            report["passwordVerified"] = dialog.Verified;
            report["temporaryStoreStillUnconfigured"] = !store.IsConfigured;
            if (!report.ContainsKey("status"))
                report["status"] = sampledAfterRefresh && automaticCancellation
                    ? "completed: own UI display check; physical hotkey, owner click and password unlock remain pending"
                    : "incomplete: dialog closed before the timed display check finished";
        }
        catch (Exception error) { RecordFailure(error); }
        finally
        {
            refreshTimer.Stop();
            closeTimer.Stop();
            if (!dialog.IsDisposed && dialog.Visible) Cancel();
            try { gate.SetMode(gate.IsHealthy ? GateMode.Guarded : GateMode.Open); } catch { }
            if (cursorSaved)
            {
                bool requested = SetCursorPos(originalCursor.X, originalCursor.Y);
                report["cursorRestored"] = requested && GetCursorPos(out var restored)
                    && Math.Abs(restored.X - originalCursor.X) <= 2 && Math.Abs(restored.Y - originalCursor.Y) <= 2;
            }
        }
        return report;
    }

    private Bitmap CaptureTarget()
    {
        target.Refresh(); // Paint the current sequence before sampling actual captured pixels.
        var origin = target.PointToScreen(Point.Empty);
        var bitmap = new Bitmap(target.ClientSize.Width, target.ClientSize.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(origin, Point.Empty, bitmap.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private async Task<bool> CheckOrdinaryInjectedInputAsync(Dictionary<string, object?> report)
    {
        ordinaryKeyDownReceived = ordinaryKeyUpReceived = false;
        ordinaryInputCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Never activate another application or send text/clicks. A focus mismatch skips this test.
            if (RuntimeState.GetForegroundWindow() != Handle)
            {
                report["ordinaryInjectedKeyboardStatus"] = "skipped: foreground is not the diagnostic window";
                return false;
            }
            var inputs = new[]
            {
                Win32.CreateKeyboardInput((ushort)Keys.F23, ordinaryInputMarker),
                Win32.CreateKeyboardInput((ushort)Keys.F23, ordinaryInputMarker, Win32.KEYEVENTF_KEYUP)
            };
            // Check again immediately before the single down/up batch is sent.
            if (RuntimeState.GetForegroundWindow() != Handle)
            {
                report["ordinaryInjectedKeyboardStatus"] = "skipped: foreground changed before SendInput";
                return false;
            }
            uint sent = Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
            report["ordinaryInjectedKeyboardSendCount"] = sent;
            if (sent != inputs.Length)
            {
                report["ordinaryInjectedKeyboardStatus"] = "failed: SendInput did not send both F23 events";
                return false;
            }
            var completed = await Task.WhenAny(ordinaryInputCompletion.Task, Task.Delay(500));
            bool received = completed == ordinaryInputCompletion.Task && ordinaryInputCompletion.Task.Result;
            report["ordinaryInjectedKeyboardStatus"] = received
                ? "passed: marked F23 down/up received by the diagnostic window in guarded mode"
                : "failed: marked F23 down/up was not received by the diagnostic window";
            return received;
        }
        finally
        {
            report["ordinaryInjectedKeyDownReceived"] = ordinaryKeyDownReceived;
            report["ordinaryInjectedKeyUpReceived"] = ordinaryKeyUpReceived;
            ordinaryInputCompletion = null;
        }
    }

    private void ObserveOrdinaryInput(KeyEventArgs e, bool keyDown)
    {
        if (e.KeyCode != Keys.F23 || ordinaryInputCompletion is null)
            return;
        e.Handled = true;
        if (RuntimeState.GetForegroundWindow() != Handle
            || unchecked((nuint)GetMessageExtraInfo().ToInt64()) != ordinaryInputMarker)
            return;
        if (keyDown) ordinaryKeyDownReceived = true;
        else ordinaryKeyUpReceived = true;
        if (ordinaryKeyDownReceived && ordinaryKeyUpReceived)
            ordinaryInputCompletion.TrySetResult(true);
    }

    private async Task<bool> CheckOrdinaryInjectedMouseAsync(Dictionary<string, object?> report)
    {
        ordinaryMouseMoveReceived = ordinaryMouseDownReceived = ordinaryMouseUpReceived = false;
        ordinaryMouseCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cursorChanged = false;
        var originalCursor = new Win32.POINT();
        try
        {
            if (!GetCursorPos(out originalCursor))
            {
                report["ordinaryInjectedMouseStatus"] = "skipped: original cursor position could not be saved";
                return false;
            }
            var localPoint = new Point(target.ClientSize.Width / 2, target.ClientSize.Height / 2);
            var screenPoint = target.PointToScreen(localPoint);
            // A real movement is needed even when the cursor happens to start at the usual test point.
            if (Math.Abs(screenPoint.X - originalCursor.X) < 8 && Math.Abs(screenPoint.Y - originalCursor.Y) < 8)
            {
                localPoint.X += Math.Min(48, target.ClientSize.Width / 4);
                screenPoint = target.PointToScreen(localPoint);
            }
            var virtualScreen = SystemInformation.VirtualScreen;
            if (!OwnsPanelPoint(screenPoint) || !virtualScreen.Contains(screenPoint)
                || virtualScreen.Width < 2 || virtualScreen.Height < 2 || (GetAsyncKeyState(0x01) & 0x8000) != 0)
            {
                report["ordinaryInjectedMouseStatus"] = "skipped: foreground, panel ownership, screen bounds or released left button was not confirmed";
                return false;
            }
            report["ordinaryInjectedMouseTarget"] = new { screenPoint.X, screenPoint.Y };
            report["ordinaryInjectedMouseVirtualScreen"] = new { virtualScreen.X, virtualScreen.Y, virtualScreen.Width, virtualScreen.Height };
            // Do not force mouse capture: this test must exercise the curtain's real hit-test path.
            if (GetCapture() != IntPtr.Zero)
            {
                report["ordinaryInjectedMouseStatus"] = "skipped: a control already owns mouse capture";
                return false;
            }
            int x = NormalizeAbsoluteCoordinate(screenPoint.X, virtualScreen.Left, virtualScreen.Width);
            int y = NormalizeAbsoluteCoordinate(screenPoint.Y, virtualScreen.Top, virtualScreen.Height);
            var inputs = new[]
            {
                Win32.CreateMouseInput(x, y, Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_MOVE_NOCOALESCE
                    | MouseAbsolute | MouseVirtualDesk, ordinaryMouseMarker),
                Win32.CreateMouseInput(flags: MouseLeftDown, extraInfo: ordinaryMouseMarker),
                Win32.CreateMouseInput(flags: MouseLeftUp, extraInfo: ordinaryMouseMarker)
            };
            // Re-check both foreground and WindowFromPoint immediately before this one serialized batch.
            if (!OwnsPanelPoint(screenPoint) || RuntimeState.GetForegroundWindow() != Handle || GetCapture() != IntPtr.Zero)
            {
                report["ordinaryInjectedMouseStatus"] = "skipped: test-window ownership changed before SendInput";
                return false;
            }
            uint sent = Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
            cursorChanged = sent > 0;
            report["ordinaryInjectedMouseSendCount"] = sent;
            if (sent != inputs.Length)
            {
                // If a partial batch delivered its down, release it only at the still-owned test point.
                if (sent == 2 && GetCursorPos(out var current)
                    && OwnsPanelPoint(new Point(current.X, current.Y)) && RuntimeState.GetForegroundWindow() == Handle)
                    Win32.SendInput(1, [Win32.CreateMouseInput(flags: MouseLeftUp, extraInfo: ordinaryMouseMarker)], Marshal.SizeOf<Win32.INPUT>());
                report["ordinaryInjectedMouseStatus"] = "failed: SendInput did not send the full mouse move/down/up batch";
                return false;
            }
            var completed = await Task.WhenAny(ordinaryMouseCompletion.Task, Task.Delay(500));
            bool received = completed == ordinaryMouseCompletion.Task && ordinaryMouseCompletion.Task.Result;
            report["ordinaryInjectedMouseStatus"] = received
                ? "passed: marked mouse move and left down/up reached the own test panel through the privacy curtain"
                : "failed: own test panel did not receive all marked mouse events";
            return received;
        }
        finally
        {
            report["ordinaryInjectedMouseMoveReceived"] = ordinaryMouseMoveReceived;
            report["ordinaryInjectedMouseDownReceived"] = ordinaryMouseDownReceived;
            report["ordinaryInjectedMouseUpReceived"] = ordinaryMouseUpReceived;
            ordinaryMouseCompletion = null;
            if (!target.IsDisposed && GetCapture() == target.Handle)
                target.Capture = false;
            // Restore only cursor position. Never send a click or keyboard event at the saved position.
            if (cursorChanged)
            {
                bool requested = SetCursorPos(originalCursor.X, originalCursor.Y);
                // Functional hook probes can move the pointer by one unit; allow only that small tolerance.
                report["ordinaryInjectedMouseCursorRestored"] = requested && GetCursorPos(out var restored)
                    && Math.Abs(restored.X - originalCursor.X) <= 2 && Math.Abs(restored.Y - originalCursor.Y) <= 2;
            }
        }
    }

    private bool OwnsPanelPoint(Point screenPoint)
    {
        if (IsDisposed || target.IsDisposed || !target.Visible || !target.IsHandleCreated
            || RuntimeState.GetForegroundWindow() != Handle || !target.ClientRectangle.Contains(target.PointToClient(screenPoint)))
            return false;
        var hit = WindowFromPoint(new Win32.POINT { X = screenPoint.X, Y = screenPoint.Y });
        Win32.GetWindowThreadProcessId(hit, out uint owner);
        return hit == target.Handle && owner == Environment.ProcessId;
    }

    private void ObserveOrdinaryMouse(MouseEventArgs e, MouseObservedEvent observed)
    {
        if (ordinaryMouseCompletion is null || RuntimeState.GetForegroundWindow() != Handle
            || !target.ClientRectangle.Contains(e.Location)
            || unchecked((nuint)GetMessageExtraInfo().ToInt64()) != ordinaryMouseMarker)
            return;
        if (observed == MouseObservedEvent.Move) ordinaryMouseMoveReceived = true;
        else if (e.Button == MouseButtons.Left && observed == MouseObservedEvent.Down) ordinaryMouseDownReceived = true;
        else if (e.Button == MouseButtons.Left && observed == MouseObservedEvent.Up) ordinaryMouseUpReceived = true;
        if (ordinaryMouseMoveReceived && ordinaryMouseDownReceived && ordinaryMouseUpReceived)
            ordinaryMouseCompletion.TrySetResult(true);
    }

    private async Task<bool> CheckOrdinaryInjectedTextAsync(Dictionary<string, object?> report)
    {
        const string expected = "AgentLock 测试 123";
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs e)
        {
            if (typingTarget.Text == expected && RuntimeState.GetForegroundWindow() == Handle && GetFocus() == typingTarget.Handle)
                completion.TrySetResult(true);
        }
        try
        {
            if (RuntimeState.GetForegroundWindow() != Handle || !typingTarget.Visible || !typingTarget.Enabled
                || typingTarget.ReadOnly || typingTarget.UseSystemPasswordChar || typingTarget.PasswordChar != '\0')
            {
                report["ordinaryInjectedTextStatus"] = "skipped: own visible non-password test field was not confirmed";
                return false;
            }
            typingTarget.Clear();
            if (!typingTarget.Focus() || GetFocus() != typingTarget.Handle)
            {
                report["ordinaryInjectedTextStatus"] = "skipped: own test field did not receive focus";
                return false;
            }
            typingTarget.TextChanged += Changed;
            var inputs = new List<Win32.INPUT>();
            foreach (char character in expected)
            {
                foreach (uint flags in new[] { KeyboardUnicode, KeyboardUnicode | Win32.KEYEVENTF_KEYUP })
                    inputs.Add(new Win32.INPUT
                    {
                        Type = Win32.INPUT_KEYBOARD,
                        Data = new Win32.INPUTUNION
                        {
                            Keyboard = new Win32.KEYBDINPUT { Vk = 0, Scan = character, Flags = flags, ExtraInfo = ordinaryInputMarker }
                        }
                    });
            }
            // Unicode goes only to our explicitly focused field; no IME, clipboard or authentication input is involved.
            if (RuntimeState.GetForegroundWindow() != Handle || GetFocus() != typingTarget.Handle)
            {
                report["ordinaryInjectedTextStatus"] = "skipped: own field lost focus before SendInput";
                return false;
            }
            uint sent = Win32.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Win32.INPUT>());
            report["ordinaryInjectedTextSendCount"] = sent;
            if (sent != inputs.Count)
            {
                report["ordinaryInjectedTextStatus"] = "failed: SendInput did not send the full Unicode batch";
                return false;
            }
            var completed = await Task.WhenAny(completion.Task, Task.Delay(500));
            bool received = completed == completion.Task && completion.Task.Result && typingTarget.Text == expected;
            report["ordinaryInjectedTextStatus"] = received
                ? "passed: Unicode test string received exactly by the own non-password field"
                : "failed: own test field did not receive the exact Unicode string";
            return received;
        }
        finally
        {
            typingTarget.TextChanged -= Changed;
            report["ordinaryInjectedTextExpected"] = expected;
            report["ordinaryInjectedTextObserved"] = typingTarget.Text;
        }
    }

    private static int NormalizeAbsoluteCoordinate(int coordinate, int origin, int extent)
        => (int)Math.Clamp(Math.Round((coordinate - origin) * 65535d / (extent - 1)), 0d, 65535d);

    private enum MouseObservedEvent { Move, Down, Up }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Win32.POINT point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Win32.POINT point);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr window, int index);
    private static long GetExtendedWindowStyle(IntPtr window)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(window, -20).ToInt64() : GetWindowLong32(window, -20);

    [DllImport("user32.dll")]
    private static extern IntPtr GetMessageExtraInfo();

    private static Color SampleColor(Bitmap bitmap, Rectangle region)
    {
        region = Rectangle.Intersect(region, new Rectangle(Point.Empty, bitmap.Size));
        if (region.Width < 1 || region.Height < 1)
            throw new InvalidOperationException("动态测试区域不在截图内。");
        long red = 0, green = 0, blue = 0;
        for (int y = region.Top; y < region.Bottom; y++)
            for (int x = region.Left; x < region.Right; x++)
            {
                var color = bitmap.GetPixel(x, y);
                red += color.R; green += color.G; blue += color.B;
            }
        int pixels = region.Width * region.Height;
        return Color.FromArgb((int)(red / pixels), (int)(green / pixels), (int)(blue / pixels));
    }

    private static string ColorHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private string GetOutputPath()
    {
        int index = Array.IndexOf(arguments, "--output");
        return index >= 0 && index + 1 < arguments.Length ? Path.GetFullPath(arguments[index + 1]) : Path.Combine(RuntimeState.Root, "diagnostics", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    }
    private static int ColorDistance(Color left, Color right) => Math.Abs(left.R - right.R) + Math.Abs(left.G - right.G) + Math.Abs(left.B - right.B);
    protected override void OnFormClosing(FormClosingEventArgs e) { if (working) e.Cancel = true; else animation.Stop(); base.OnFormClosing(e); }

    private sealed class PatternPanel : Panel
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Sequence { get; set; }
        public Rectangle DynamicRegion => new(Width - 66, 28, 32, 32);
        public Color DynamicColor => Color.FromArgb(48 + (Sequence % 160 * 37 % 160),
            48 + (Sequence % 160 * 67 % 160), 48 + (Sequence % 160 * 97 % 160));
        public PatternPanel() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(20, 150, 130));
            using var font = new Font("Microsoft YaHei UI", 17, FontStyle.Bold);
            e.Graphics.DrawString("Agent 应看到这张测试画面", font, Brushes.White, 24, 24);
            e.Graphics.DrawString("动态帧 " + Sequence, Font, Brushes.White, 24, 80);
            using var brush = new SolidBrush(DynamicColor);
            e.Graphics.FillRectangle(brush, Width - 70, 24, 40, 40);
            base.OnPaint(e);
        }
    }
}
