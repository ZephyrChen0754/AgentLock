using System.Diagnostics;
using System.Text.Json;
using AgentLock.Native;
using AgentLock.Security;

namespace AgentLock.Ui;

internal sealed class MainForm : Form
{
    private readonly PasswordStore passwords;
    private InputGate gate = new();
    private readonly PrivacyCurtain curtain = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 300 };
    private readonly NotifyIcon tray;
    private readonly Label status = new();
    private readonly Label details = new();
    private readonly Label health = new();
    private readonly Button protect = new RoundedButton();
    private readonly CheckBox hideScreen = new() { Text = "离开时隐藏所有屏幕", Checked = true, AutoSize = true };
    private readonly CheckBox stayAwake = new() { Text = "保护期间阻止自动睡眠", Checked = true, AutoSize = true };
    private bool protectedMode, authenticating, shutdown, gateReady, sessionLocked, recovering, sessionNotifications, closeAfterNativeLock;
    private DateTime lastHeartbeatWrite;
    private DateTime? began;
    private Process? watchdog;
    private UnlockForm? activeUnlock;
    private DateTime recoveryBegan, lastLockRequest;
    private DateTime? authFocusLostSince;
    private readonly NativeRecoveryAuthorizationPolicy nativeRecovery = new();
    private readonly int windowsSessionId;
    private bool passwordResetSetupPending, passwordResetDialogOpen;

    public MainForm() : this(new PasswordStore()) { }

    internal MainForm(PasswordStore passwords)
    {
        this.passwords = passwords ?? throw new ArgumentNullException(nameof(passwords));
        using (var currentProcess = Process.GetCurrentProcess()) windowsSessionId = currentProcess.SessionId;
        Text = "AgentLock · 同桌面守护";
        VisualTheme.ApplyForm(this);
        ClientSize = new Size(704, 706);
        MinimumSize = new Size(640, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = BrandMark.LoadIcon();
        BuildLayout();
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 AgentLock", null, (_, _) => ShowMain());
        menu.Items.Add("离开并继续工作", null, (_, _) => BeginProtection());
        menu.Items.Add("解锁本地操作", null, (_, _) => RequestUnlock());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => { if (protectedMode) RequestUnlock(); if (!protectedMode) { shutdown = true; Close(); } });
        tray = new NotifyIcon { Icon = BrandMark.LoadIcon(), Text = "AgentLock · 未开启保护", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => { if (protectedMode) RequestUnlock(); else ShowMain(); };
        ConnectGate();
        timer.Tick += (_, _) => TickProtection();
        Shown += (_, _) => Initialize();
        FormClosing += OnClosing;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Top, Height = 706, Padding = new Padding(28), ColumnCount = 1, RowCount = 9 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var logo = new PictureBox { Image = BrandMark.CreateBitmap(104), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(48, 48), Margin = new Padding(0, 1, 14, 0), AccessibleName = "AgentLock 标志", TabStop = false };
        logo.Disposed += (_, _) => logo.Image?.Dispose();
        header.Controls.Add(logo, 0, 0);
        header.SetRowSpan(logo, 2);
        header.Controls.Add(new Label { Text = "AgentLock", Dock = DockStyle.Fill, Font = VisualTheme.UiFont(19, FontStyle.Bold), Margin = Padding.Empty }, 1, 0);
        header.Controls.Add(new Label { Text = "Windows · 同桌面守护", Dock = DockStyle.Fill, ForeColor = VisualTheme.Muted, Font = VisualTheme.UiFont(9), Margin = Padding.Empty }, 1, 1);
        root.Controls.Add(header, 0, 0);

        var introduction = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(0, 10, 0, 0) };
        introduction.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        introduction.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        introduction.Controls.Add(new Label { Text = "人离开，Agent 接着工作", Dock = DockStyle.Fill, Font = VisualTheme.UiFont(22, FontStyle.Bold), Margin = Padding.Empty });
        introduction.Controls.Add(new Label { Text = "保护本地操作，让任务留在熟悉的桌面。", Dock = DockStyle.Fill, ForeColor = VisualTheme.Muted, Margin = new Padding(0, 2, 0, 0) });
        root.Controls.Add(introduction, 0, 1);

        var card = new RoundedCard { Dock = DockStyle.Fill, Padding = new Padding(20, 14, 20, 12), Margin = new Padding(0, 0, 0, 12) };
        status.Dock = DockStyle.Top; status.Height = 32; status.Font = VisualTheme.UiFont(13, FontStyle.Bold);
        details.Dock = DockStyle.Fill; details.ForeColor = VisualTheme.Muted; details.Font = VisualTheme.UiFont(9.5F);
        card.Controls.Add(details); card.Controls.Add(status); root.Controls.Add(card, 0, 2);

        protect.Text = "离开并继续工作"; protect.Dock = DockStyle.Fill; protect.Margin = new Padding(0, 0, 0, 6);
        VisualTheme.StyleButton(protect, primary: true);
        protect.Font = VisualTheme.UiFont(12, FontStyle.Bold); protect.Enabled = false;
        protect.Click += (_, _) => { if (protectedMode) RequestUnlock(); else BeginProtection(); }; root.Controls.Add(protect, 0, 3);

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 13, 0, 0), Margin = Padding.Empty, WrapContents = true };
        hideScreen.ForeColor = stayAwake.ForeColor = VisualTheme.Ink;
        hideScreen.Margin = new Padding(0, 0, 20, 0); stayAwake.Margin = Padding.Empty;
        options.Controls.Add(hideScreen); options.Controls.Add(stayAwake); root.Controls.Add(options, 0, 4);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        var set = new RoundedButton { Text = "设置保护密码", Size = new Size(180, 38), Margin = new Padding(0, 0, 12, 0) };
        VisualTheme.StyleButton(set);
        set.Click += (_, _) => ConfigurePassword();
        var test = new RoundedButton { Text = "运行兼容性检查", Size = new Size(180, 38), Margin = Padding.Empty };
        VisualTheme.StyleButton(test);
        test.Click += (_, _) => StartDiagnostics();
        actions.Controls.Add(set); actions.Controls.Add(test); root.Controls.Add(actions, 0, 5);

        var healthCard = new RoundedCard { Dock = DockStyle.Fill, SurfaceColor = VisualTheme.AccentSoft, BorderColor = VisualTheme.AccentSoft, Padding = new Padding(14, 8, 14, 6), Margin = new Padding(0, 0, 0, 4), CornerRadius = 12 };
        health.Dock = DockStyle.Fill; health.BackColor = VisualTheme.AccentSoft; health.ForeColor = VisualTheme.Muted; health.Font = VisualTheme.UiFont(9);
        healthCard.Controls.Add(health); root.Controls.Add(healthCard, 0, 6);

        var help = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 14, 0, 0) };
        help.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        help.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        help.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        help.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var keycap = new RoundedCard { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 5), CornerRadius = 8, BorderColor = VisualTheme.Border };
        keycap.Controls.Add(new Label { Text = "F12", Dock = DockStyle.Fill, Font = new Font("Consolas", 11, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter });
        help.Controls.Add(keycap, 0, 0);
        help.Controls.Add(new Label { Text = "回来后，输入保护密码恢复操作。", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = VisualTheme.Muted, Font = VisualTheme.UiFont(9.5F), Margin = Padding.Empty }, 1, 0);
        var boundary = new Label { Text = "系统安全界面与管理员操作可能中断保护。\nAgent 兼容性取决于截图与输入方式。", Dock = DockStyle.Fill, ForeColor = VisualTheme.Muted, Font = VisualTheme.UiFont(8.5F), Margin = new Padding(0, 6, 0, 0) };
        help.Controls.Add(boundary, 0, 1); help.SetColumnSpan(boundary, 2);
        root.Controls.Add(help, 0, 7);

        root.Controls.Add(new Label { Text = Application.ProductVersion.Split('+')[0] + "  ·  Windows 同桌面版本  ·  本机保存配置与日志", Dock = DockStyle.Fill, ForeColor = VisualTheme.Muted, Font = VisualTheme.UiFont(8.5F), TextAlign = ContentAlignment.BottomLeft, Margin = Padding.Empty }, 0, 8);
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        viewport.SizeChanged += (_, _) => root.Height = Math.Max(650, viewport.ClientSize.Height);
        viewport.Controls.Add(root);
        Controls.Add(viewport);
    }

    private void Initialize()
    {
        try
        {
            gate.Start();
            if (!RuntimeState.WTSRegisterSessionNotification(Handle, 0)) throw new InvalidOperationException("无法监听 Windows 锁屏与本人恢复事件。");
            sessionNotifications = true;
            RuntimeState.WriteHeartbeat();
            File.Delete(RuntimeState.SessionPath + ".ready");
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位应用。");
            watchdog = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, ArgumentList = { "--watchdog", Environment.ProcessId.ToString(), RuntimeState.SessionPath } });
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!File.Exists(RuntimeState.SessionPath + ".ready") && watchdog is not null && !watchdog.HasExited && DateTime.UtcNow < deadline) Thread.Sleep(25);
            if (watchdog is null || watchdog.HasExited || !File.Exists(RuntimeState.SessionPath + ".ready")) throw new InvalidOperationException("独立恢复监控未就绪。");
            gateReady = gate.IsHealthy;
            if (!gateReady) throw new InvalidOperationException("输入保护没有通过功能检查。");
            timer.Start();
            RuntimeState.Log("app-started version=" + Application.ProductVersion.Split('+')[0] + " pid=" + Environment.ProcessId);
        }
        catch (Exception e) { gateReady = false; health.Text = "启动检查未通过：" + e.Message; RuntimeState.Log("startup-failed type=" + e.GetType().Name); }
        UpdateUi();
    }

    private void ConfigurePassword(string? recoveryMessage = null)
    {
        if (sessionLocked || nativeRecovery.RecoveryRequested || passwordResetDialogOpen || passwordResetSetupPending) return;
        if (protectedMode) { RequestUnlock(); return; }
        if (passwords.IsConfigured && !passwords.CanReplacePassword)
        {
            using var verify = new UnlockForm(passwords, recoveryMessage);
            var result = verify.ShowDialog(this);
            if (verify.NativeRecoveryRequested)
            {
                if (!BeginPasswordRecovery()) BeginInvoke(() => ConfigurePassword("无法确认 Windows 登录恢复通道，请稍后重试。"));
                return;
            }
            if (result != DialogResult.OK || !verify.Verified) return;
        }
        passwordResetDialogOpen = true;
        try
        {
            using var setup = new SetupPasswordForm(passwords);
            setup.ShowDialog(this);
        }
        finally { passwordResetDialogOpen = false; UpdateUi(); }
    }

    private void BeginProtection()
    {
        if (protectedMode || authenticating || recovering || sessionLocked || nativeRecovery.RecoveryRequested || passwordResetDialogOpen || passwordResetSetupPending) return;
        if (!passwords.IsConfigured) { ConfigurePassword(); if (!passwords.IsConfigured) return; }
        if (!gateReady || !gate.IsHealthy || !sessionNotifications || watchdog is null || watchdog.HasExited) { MessageBox.Show(this, "保护检查未通过，暂时不能开启。请重新启动程序。", "AgentLock"); return; }
        if (RuntimeState.GetSystemMetrics(95) > 0 || RuntimeState.GetSystemMetrics(94) != 0)
        {
            MessageBox.Show(this, "检测到触屏或笔输入。当前版本尚未验证这类输入的拦截，不能开启保护。", "设备需要验证", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
        }
        try
        {
            passwords.RevokePasswordResetAuthorization();
            RuntimeState.RecoveryRequested = false;
            RuntimeState.Armed = true; RuntimeState.WriteHeartbeat();
            hideScreen.Enabled = stayAwake.Enabled = false;
            Hide();
            gate.SetMode(GateMode.Guarded);
            if (hideScreen.Checked) { curtain.Show(); if (!curtain.CaptureExcluded) throw new InvalidOperationException("屏幕截图兼容设置未生效。"); }
            if (stayAwake.Checked && RuntimeState.SetThreadExecutionState(0x80000003) == 0) throw new InvalidOperationException("无法保持电脑与显示输出唤醒。");
            protectedMode = RuntimeState.Armed = true; recovering = false; began = DateTime.UtcNow;
            RuntimeState.WriteHeartbeat(); RuntimeState.Log("protection-started screens=" + curtain.Count);
            UpdateUi();
        }
        catch (Exception e)
        {
            gate.SetMode(GateMode.Open); curtain.Hide(); RuntimeState.Armed = protectedMode = false;
            RuntimeState.RecoveryRequested = false;
            RuntimeState.SetThreadExecutionState(0x80000000);
            RuntimeState.WriteHeartbeat();
            hideScreen.Enabled = stayAwake.Enabled = true; ShowMain();
            MessageBox.Show(this, "保护没有开启：" + e.Message, "AgentLock", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RequestUnlock(string? recoveryMessage = null)
    {
        if (!protectedMode || authenticating || sessionLocked || recovering)
        {
            RuntimeState.Log($"unlock-request-ignored protected={protectedMode} authenticating={authenticating} locked={sessionLocked} recovering={recovering}");
            return;
        }
        RuntimeState.Log("unlock-dialog-opening");
        authenticating = true;
        authFocusLostSince = null;
        using var unlock = new UnlockForm(passwords, recoveryMessage) { TopMost = true, StartPosition = FormStartPosition.CenterScreen };
        bool retryRecovery = false;
        activeUnlock = unlock;
        unlock.Shown += (_, _) =>
        {
            try
            {
                bool foreground = AuthenticationDialogPresenter.PrepareShown(unlock, gate);
                RuntimeState.Log($"unlock-dialog-shown visible={unlock.Visible} foreground={foreground} captureExcluded=true");
                if (!foreground)
                {
                    authFocusLostSince = DateTime.UtcNow;
                    unlock.ShowFocusHint();
                }
            }
            catch (Exception e) { FailProtection("无法显示认证窗口：" + e.Message); unlock.Close(); }
        };
        try
        {
            var result = unlock.ShowDialog();
            if (unlock.NativeRecoveryRequested) retryRecovery = !BeginPasswordRecovery();
            else if (result == DialogResult.OK && unlock.Verified) EndProtection();
            else RuntimeState.Log("unlock-dialog-closed verified=false");
        }
        finally
        {
            authenticating = false;
            activeUnlock = null;
            authFocusLostSince = null;
            if (protectedMode && !sessionLocked && !recovering) { gate.SetMode(GateMode.Guarded); curtain.Refresh(); }
            UpdateUi();
        }
        if (retryRecovery) BeginInvoke(() => RequestUnlock("无法确认 Windows 登录恢复通道。保护仍保持，请稍后重试。"));
    }

    private void EndProtection()
    {
        RuntimeState.Armed = protectedMode = recovering = RuntimeState.RecoveryRequested = false; RuntimeState.WriteHeartbeat();
        curtain.Hide(); gate.SetMode(GateMode.Open);
        RuntimeState.SetThreadExecutionState(0x80000000);
        hideScreen.Enabled = stayAwake.Enabled = true;
        RuntimeState.Log("protection-ended authenticated=true");
        RuntimeState.Log("unlock-input-summary " + JsonSerializer.Serialize(gate.HotkeySnapshot));
        ShowMain();
    }

    private void TickProtection()
    {
        ObserveNativeSession();
        TryShowRecoveredPasswordSetup();
        if (protectedMode && RuntimeState.RecoveryRequested && !recovering && !sessionLocked) FailProtection("程序异常，需要系统锁屏恢复");
        if (((protectedMode && recovering) || nativeRecovery.RecoveryRequested) && !sessionLocked && DateTime.UtcNow - lastLockRequest > TimeSpan.FromSeconds(2)) RequestNativeLock();
        if (protectedMode && !sessionLocked && !recovering)
        {
            if (RuntimeState.GetSystemMetrics(95) > 0 || RuntimeState.GetSystemMetrics(94) != 0) { FailProtection("检测到新增触屏或笔输入设备"); return; }
            if (!gate.IsHealthy) { FailProtection("输入保护失去响应"); return; }
            if (watchdog is null || watchdog.HasExited) { FailProtection("恢复监控已停止"); return; }
            if (authenticating && activeUnlock is { IsDisposed: false, IsHandleCreated: true })
            {
                if (IsAuthenticationForeground(activeUnlock))
                {
                    if (authFocusLostSince is not null) RuntimeState.Log("unlock-dialog-foreground-restored");
                    authFocusLostSince = null;
                }
                else
                {
                    authFocusLostSince ??= DateTime.UtcNow;
                    activeUnlock.Activate();
                    if (IsAuthenticationForeground(activeUnlock)) authFocusLostSince = null;
                    else if (DateTime.UtcNow - authFocusLostSince.Value > TimeSpan.FromSeconds(8))
                    {
                        FailProtection("认证窗口无法获得键盘焦点，请通过 Windows 登录恢复"); return;
                    }
                }
            }
        }
        // Keep covering new displays until Windows confirms the actual lock,
        // including while a recovery request is waiting for confirmation.
        if (protectedMode && !sessionLocked)
        {
            if (hideScreen.Checked)
            {
                try
                {
                    curtain.Refresh();
                    // New monitors must remain covered while the owner verifies their password.
                    // Restore only our dialog above the refreshed curtain without changing focus.
                    if (authenticating && activeUnlock is { IsDisposed: false, IsHandleCreated: true }
                        && !Win32.SetWindowPos(activeUnlock.Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                            Win32.SWP_NOACTIVATE | Win32.SWP_NOSIZE | Win32.SWP_NOMOVE))
                        throw new InvalidOperationException("无法保持认证窗口可见。");
                }
                catch (Exception e) { FailProtection("屏幕保护失效：" + e.Message); return; }
            }
        }
        if (DateTime.UtcNow - lastHeartbeatWrite > TimeSpan.FromSeconds(1))
        {
            RuntimeState.WriteHeartbeat(); lastHeartbeatWrite = DateTime.UtcNow;
        }
        UpdateUi();
    }

    private void FailProtection(string reason)
    {
        gateReady = false;
        RuntimeState.Log("protection-fault reason=" + reason);
        health.Text = "需要处理：" + reason;
        if (protectedMode && !sessionLocked && !recovering)
        {
            recovering = RuntimeState.RecoveryRequested = true;
            recoveryBegan = DateTime.UtcNow;
            RuntimeState.WriteHeartbeat();
            activeUnlock?.Close();
            RequestNativeLock();
        }
        UpdateUi();
    }

    private void RequestNativeLock()
    {
        lastLockRequest = DateTime.UtcNow;
        bool requested = RuntimeState.LockWorkStation();
        RuntimeState.Log("native-lock-request accepted=" + requested + " confirmed=false");
    }

    private bool BeginPasswordRecovery()
    {
        if (!nativeRecovery.TryBeginRecovery(SessionState.IsCurrentSessionLocked()))
        {
            RuntimeState.Log("password-recovery-not-started nativeSessionUnavailableOrBusy=true");
            return false;
        }
        passwords.RevokePasswordResetAuthorization();
        passwordResetSetupPending = false;
        recovering = true;
        recoveryBegan = DateTime.UtcNow;
        if (protectedMode)
        {
            if (gate.IsHealthy) gate.SetMode(GateMode.Guarded);
            RuntimeState.RecoveryRequested = true;
            RuntimeState.WriteHeartbeat();
        }
        RuntimeState.Log("password-recovery-requested nativeAuthenticationRequired=true");
        RequestNativeLock();
        UpdateUi();
        return true;
    }

    private void ObserveNativeSession()
    {
        // Notifications only wake this synchronous query. They cannot themselves
        // authorize a reset or release protection.
        var locked = SessionState.IsCurrentSessionLocked();
        if (locked == true && !sessionLocked && nativeRecovery.ConfirmLock(locked))
        {
            sessionLocked = true;
            activeUnlock?.Close();
            RuntimeState.Log("native-session-locked confirmed=true source=session-query pid=" + Environment.ProcessId);
            RuntimeState.Log("unlock-input-summary " + JsonSerializer.Serialize(gate.HotkeySnapshot));
            if (protectedMode)
            {
                RuntimeState.Armed = RuntimeState.RecoveryRequested = false;
                RuntimeState.WriteHeartbeat();
                curtain.Hide(); gate.SetMode(GateMode.Open);
            }
            UpdateUi();
            if (closeAfterNativeLock) BeginInvoke(Close);
        }
        else if (locked == false && sessionLocked)
        {
            bool showReset = nativeRecovery.RecoveryRequested;
            if (!nativeRecovery.TryConfirmUnlock(locked)) return;
            sessionLocked = false;
            activeUnlock?.Close();
            RuntimeState.Log("native-session-unlocked confirmed=true source=session-query pid=" + Environment.ProcessId);
            if (protectedMode) EndProtection();
            else recovering = false;
            passwords.AuthorizePasswordResetAfterNativeUnlock();
            passwordResetSetupPending |= showReset;
            gate.Dispose(); gate = new InputGate(); ConnectGate();
            try { gate.Start(); gateReady = gate.IsHealthy; }
            catch (Exception e) { gateReady = false; health.Text = "输入保护需要重新启动：" + e.Message; }
            UpdateUi();
        }
    }

    private void TryShowRecoveredPasswordSetup()
    {
        if (!passwordResetSetupPending || sessionLocked || authenticating || passwordResetDialogOpen) return;
        // Recheck immediately before opening setup. Unknown state never grants it.
        if (SessionState.IsCurrentSessionLocked() != false) return;
        passwordResetSetupPending = false;
        if (!passwords.CanReplacePassword) return;
        passwordResetDialogOpen = true;
        ShowMain();
        RuntimeState.Log("password-recovery-setup-opening nativeAuthenticated=true");
        try
        {
            using var setup = new SetupPasswordForm(passwords, isRecovery: true);
            setup.ShowDialog(this);
            RuntimeState.Log("password-recovery-setup-closed passwordChanged=" + !passwords.CanReplacePassword);
        }
        finally
        {
            // The original record is retained on cancellation. Recovery grants
            // cannot be reused after the one setup dialog has finished.
            passwords.RevokePasswordResetAuthorization();
            passwordResetDialogOpen = false;
            UpdateUi();
        }
    }

    private static bool IsAuthenticationForeground(Form dialog)
    {
        var foreground = Win32.GetForegroundWindow();
        return foreground == dialog.Handle || Win32.IsChild(dialog.Handle, foreground);
    }

    private void UpdateUi()
    {
        status.Text = sessionLocked ? "Windows 已锁定" : nativeRecovery.RecoveryRequested ? "正在请求 Windows 登录验证" : recovering ? DateTime.UtcNow - recoveryBegan > TimeSpan.FromSeconds(4) ? "保护异常 · 系统锁屏尚未确认" : "保护异常 · 正在请求系统锁屏" : protectedMode ? "保护已开启" : "当前可正常操作";
        status.ForeColor = recovering ? Color.FromArgb(160, 53, 43) : protectedMode ? Color.FromArgb(0, 116, 111) : Color.FromArgb(22, 42, 58);
        details.Text = nativeRecovery.RecoveryRequested ? "完成 Windows 登录后，重新设置保护密码。" : recovering && !sessionLocked ? "正在重试系统锁屏。请确认电脑已锁定，再离开。" : protectedMode ? "原桌面继续运行 · F12 验证后解锁" : passwords.IsConfigured ? "保护密码已设置，准备好后即可离开。" : "首次使用请设置你自己的保护密码。";
        protect.Text = protectedMode ? "验证并恢复本地操作" : "离开并继续工作";
        protect.Enabled = !recovering && !sessionLocked && (protectedMode || (gateReady && gate.IsHealthy));
        tray.Text = nativeRecovery.RecoveryRequested ? "AgentLock · 等待 Windows 登录验证" : recovering ? "AgentLock · 保护异常，等待 Windows 锁屏" : protectedMode ? "AgentLock · 保护中 / F12 解锁" : "AgentLock · 未开启保护";
        var stats = gate.Snapshot;
        if (gateReady) health.Text = "输入保护：" + (gate.IsHealthy ? "响应正常" : "失去响应") + "   保护密码：" + (passwords.IsConfigured ? "已设置" : "未设置") + "\n已拦截键盘 " + stats.BlockedKeyboard + " / 鼠标 " + stats.BlockedMouse + "，放行自动输入 " + stats.AllowedInjected + (began is not null && protectedMode ? "\n本次保护 " + (DateTime.UtcNow - began.Value).ToString(@"hh\:mm\:ss") : "");
    }

    private void StartDiagnostics()
    {
        if (protectedMode) return;
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Normal, ArgumentList = { "--diagnostics" } });
    }
    private void ShowMain() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    internal void SaveSmokeReport(string output)
    {
        Directory.CreateDirectory(output);
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(output, "main-window.png"));
        var report = new
        {
            checkedAt = DateTimeOffset.Now,
            ready = gateReady && gate.IsHealthy && sessionNotifications && watchdog is not null && !watchdog.HasExited,
            hookHealthy = gate.IsHealthy,
            sessionNotifications,
            watchdogAlive = watchdog is not null && !watchdog.HasExited,
            probeDetails = gate.ProbeDetails,
            noProtectionActivated = !RuntimeState.Armed && !protectedMode,
            startupMessage = health.Text
        };
        File.WriteAllText(Path.Combine(output, "startup-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    internal void CloseForSmoke() { if (protectedMode) throw new InvalidOperationException("Smoke test cannot close active protection."); shutdown = true; Close(); }
    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !shutdown) { e.Cancel = true; if (protectedMode) RequestUnlock(); else Hide(); return; }
        if (protectedMode && !sessionLocked)
        {
            e.Cancel = true; closeAfterNativeLock = shutdown = true;
            FailProtection("保护期间请求退出，需要先确认 Windows 锁屏");
            return;
        }
        timer.Stop(); RuntimeState.Armed = false;
        RuntimeState.RecoveryRequested = false;
        try { RuntimeState.WriteHeartbeat(); } catch { }
        curtain.Dispose(); gate.Dispose(); tray.Dispose();
        if (sessionNotifications) RuntimeState.WTSUnRegisterSessionNotification(Handle);
        RuntimeState.SetThreadExecutionState(0x80000000);
    }

    private void ConnectGate()
    {
        var connectedGate = gate;
        connectedGate.UnlockRequested += () =>
        {
            RuntimeState.Log("unlock-request-received " + JsonSerializer.Serialize(connectedGate.HotkeySnapshot));
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(() =>
            {
                if (IsDisposed || !ReferenceEquals(connectedGate, gate)) return;
                RuntimeState.Log("unlock-request-dispatched");
                RequestUnlock();
            });
        };
        connectedGate.Faulted += reason =>
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(() =>
            {
                if (IsDisposed || !ReferenceEquals(connectedGate, gate)) return;
                if (sessionLocked)
                {
                    gateReady = false;
                    RuntimeState.Log("input-filter-unavailable nativeLocked=true");
                    return;
                }
                FailProtection(reason);
            });
        };
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x02B1 && message.LParam.ToInt64() == windowsSessionId)
        {
            int notification = message.WParam.ToInt32();
            if (notification is 7 or 8) ObserveNativeSession();
        }
        base.WndProc(ref message);
    }
}
