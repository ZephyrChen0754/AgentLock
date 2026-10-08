using AgentLock.Security;

namespace AgentLock.Ui;

public sealed class UnlockForm : Form
{
    private static readonly Color Navy = VisualTheme.Ink;
    private static readonly Color Teal = VisualTheme.Accent;
    private readonly PasswordStore _store;
    private readonly TextBox _password;
    private readonly Label _status;
    private readonly Button _unlock;
    private readonly Button _recover;
    private readonly bool _hasInitialStatusMessage;
    private readonly System.Windows.Forms.Timer _cooldownTimer = new() { Interval = 250 };
    private readonly ToolTip _tip = new();
    private DateTimeOffset _retryAt;
    private string _retryMessage = "";
    private bool _verifying;

    public bool Verified { get; private set; }
    public bool NativeRecoveryRequested { get; private set; }

    internal void ShowFocusHint()
    {
        if (_verifying || DateTimeOffset.UtcNow < _retryAt || _hasInitialStatusMessage) return;
        _status.ForeColor = Navy;
        _status.Text = "请点击此窗口，再输入保护密码。";
    }

    public UnlockForm(PasswordStore store, string? statusMessage = null)
    {
        _store = store;
        _hasInitialStatusMessage = !string.IsNullOrWhiteSpace(statusMessage);
        Text = "回来解锁 · AgentLock";
        VisualTheme.ApplyForm(this);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(500, 372);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        TopMost = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 6,
            BackColor = VisualTheme.Canvas
        };
        for (var row = 0; row < 6; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        layout.Controls.Add(CreateHeader(), 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "输入保护密码，恢复本地键鼠操作。",
            AutoSize = true,
            ForeColor = VisualTheme.Muted,
            Margin = new Padding(0, 0, 0, 16)
        }, 0, 1);
        _password = new TextBox
        {
            Dock = DockStyle.Fill,
            UseSystemPasswordChar = true,
            MaxLength = 256,
            Font = VisualTheme.UiFont(11F),
            ForeColor = VisualTheme.Ink,
            BackColor = VisualTheme.Surface,
            BorderStyle = BorderStyle.None,
            Margin = Padding.Empty,
            AccessibleName = "AgentLock 保护密码"
        };
        var field = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 12)
        };
        field.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        field.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        field.Controls.Add(new Label
        {
            Text = "保护密码",
            AutoSize = true,
            Font = VisualTheme.UiFont(9.5F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);
        field.Controls.Add(CreatePasswordSurface(_password), 0, 1);
        layout.Controls.Add(field, 0, 2);
        var hint = new Label
        {
            Text = "请使用 AgentLock 保护密码。",
            AutoSize = true,
            ForeColor = VisualTheme.Muted,
            Font = VisualTheme.UiFont(9F),
            Margin = new Padding(0, 0, 0, 0)
        };
        _tip.SetToolTip(hint, "它与 Windows PIN 分别使用。忘记密码时，请点击“忘记密码”，先完成 Windows 登录，再重设保护密码。");
        layout.Controls.Add(hint, 0, 3);
        _status = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 48,
            Text = statusMessage ?? string.Empty,
            ForeColor = Color.FromArgb(167, 49, 52),
            Margin = new Padding(0, 8, 0, 4),
            AccessibleName = "密码验证结果"
        };
        layout.Controls.Add(_status, 0, 4);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty
        };
        _unlock = new RoundedButton
        {
            Text = "解锁",
            Size = new Size(112, 42),
            Margin = new Padding(8, 0, 0, 0),
            AccessibleName = "验证密码并解锁"
        };
        VisualTheme.StyleButton(_unlock, primary: true);
        _unlock.Click += VerifyPassword;
        var cancel = new RoundedButton
        {
            Text = "取消",
            Size = new Size(104, 42),
            DialogResult = DialogResult.Cancel,
            Margin = new Padding(8, 0, 0, 0),
            AccessibleName = "取消解锁，继续保护"
        };
        VisualTheme.StyleButton(cancel);
        buttons.Controls.Add(_unlock);
        buttons.Controls.Add(cancel);
        _recover = new RoundedButton
        {
            Text = "忘记密码",
            Size = new Size(112, 42),
            Margin = Padding.Empty,
            AccessibleName = "忘记保护密码，通过 Windows 登录恢复"
        };
        VisualTheme.StyleButton(_recover);
        _tip.SetToolTip(_recover, "点击后将进入 Windows 登录。完成本人的 Windows 登录后，才能设置新的保护密码。");
        _recover.Click += (_, _) =>
        {
            if (_verifying) return;
            NativeRecoveryRequested = true;
            DialogResult = DialogResult.Cancel;
            Close();
        };
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.Controls.Add(_recover, 0, 0);
        footer.Controls.Add(buttons, 1, 0);
        layout.Controls.Add(footer, 0, 5);
        AcceptButton = _unlock;
        CancelButton = cancel;
        Shown += (_, _) => _password.Focus();
        FormClosing += (_, _) =>
        {
            if (!Verified)
                DialogResult = DialogResult.Cancel;
        };
        _cooldownTimer.Tick += (_, _) => UpdateCooldown();
    }

    private static Control CreateHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, Height = 64, Margin = new Padding(0, 0, 0, 12) };
        var mark = new Panel { Location = new Point(0, 12), Size = new Size(40, 40), TabStop = false };
        mark.Paint += (_, e) => BrandMark.Draw(e.Graphics, new RectangleF(0, 0, mark.Width, mark.Height));
        header.Controls.Add(mark);
        header.Controls.Add(new Label
        {
            Text = "AGENTLOCK",
            AutoSize = true,
            Location = new Point(54, 2),
            Font = VisualTheme.UiFont(8.5F, FontStyle.Bold),
            ForeColor = VisualTheme.Muted
        });
        header.Controls.Add(new Label
        {
            Text = "欢迎回来",
            AutoSize = true,
            Location = new Point(52, 22),
            Font = VisualTheme.UiFont(18F, FontStyle.Bold),
            ForeColor = VisualTheme.Ink
        });
        return header;
    }

    private static Control CreatePasswordSurface(TextBox password)
    {
        var surface = new RoundedCard
        {
            Dock = DockStyle.Fill,
            Height = 44,
            CornerRadius = 10,
            SurfaceColor = VisualTheme.Surface,
            BorderColor = VisualTheme.Border,
            Padding = new Padding(12, 8, 12, 8),
            Margin = Padding.Empty
        };
        surface.Controls.Add(password);
        return surface;
    }

    private async void VerifyPassword(object? sender, EventArgs e)
    {
        if (_verifying || DateTimeOffset.UtcNow < _retryAt)
            return;
        if (_password.Text.Length == 0)
        {
            _status.Text = "请输入保护密码。";
            _password.Focus();
            return;
        }

        _verifying = true;
        _unlock.Enabled = false;
        _recover.Enabled = false;
        _password.Enabled = false;
        _status.ForeColor = Teal;
        _status.Text = "正在验证…";
        var password = _password.Text;
        _password.Clear();
        VerificationResult result;
        try
        {
            result = await Task.Run(() => _store.Verify(password));
        }
        catch (Exception)
        {
            result = new(false, TimeSpan.Zero, "验证未能完成。请取消并通过 Windows 原生登录恢复。");
        }
        finally
        {
            if (!IsDisposed && !Disposing)
                _recover.Enabled = true;
        }
        if (IsDisposed || Disposing)
            return;
        _verifying = false;
        if (result.Success)
        {
            Verified = true;
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        _password.Enabled = true;
        _status.ForeColor = Color.FromArgb(167, 49, 52);
        _retryMessage = result.Message;
        _retryAt = DateTimeOffset.UtcNow + result.RetryAfter;
        if (result.RetryAfter > TimeSpan.Zero)
            _cooldownTimer.Start();
        UpdateCooldown();
        _password.Focus();
    }

    private void UpdateCooldown()
    {
        var seconds = Math.Max(0, (int)Math.Ceiling((_retryAt - DateTimeOffset.UtcNow).TotalSeconds));
        if (seconds > 0)
        {
            _status.Text = $"{_retryMessage}（{seconds} 秒）";
            _unlock.Enabled = false;
        }
        else
        {
            _cooldownTimer.Stop();
            _status.Text = _retryMessage;
            _unlock.Enabled = !_verifying;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cooldownTimer.Stop();
            _cooldownTimer.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}
