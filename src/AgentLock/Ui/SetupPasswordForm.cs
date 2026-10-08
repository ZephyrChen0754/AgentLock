using AgentLock.Security;

namespace AgentLock.Ui;

public sealed class SetupPasswordForm : Form
{
    private static readonly Color Navy = VisualTheme.Ink;
    private static readonly Color Teal = VisualTheme.Accent;
    private readonly PasswordStore _store;
    private readonly TextBox _password;
    private readonly TextBox _confirmation;
    private readonly Label _status;
    private readonly Button _save;
    private readonly Button _cancel;
    private readonly ToolTip _tip = new();
    private bool _saving;

    public SetupPasswordForm(PasswordStore store, bool isRecovery = false)
    {
        _store = store;
        Text = isRecovery ? "重新设置保护密码 · AgentLock" : "设置保护密码 · AgentLock";
        VisualTheme.ApplyForm(this);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 480);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 9,
            BackColor = VisualTheme.Canvas
        };
        for (var row = 0; row < 9; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        layout.Controls.Add(CreateHeader(isRecovery), 0, 0);
        var introduction = new Label
        {
            Text = isRecovery ? "已通过 Windows 登录验证，现在设置新的保护密码。" : "设置独立保护密码，回来时用它恢复键鼠操作。",
            AutoSize = true,
            ForeColor = VisualTheme.Muted,
            Margin = new Padding(0, 0, 0, 16)
        };
        layout.Controls.Add(introduction, 0, 1);
        layout.Controls.Add(FieldLabel("保护密码（至少 8 个字符）"), 0, 2);
        _password = PasswordBox("保护密码");
        layout.Controls.Add(CreatePasswordSurface(_password), 0, 3);
        layout.Controls.Add(FieldLabel("再次输入保护密码"), 0, 4);
        _confirmation = PasswordBox("确认保护密码");
        layout.Controls.Add(CreatePasswordSurface(_confirmation), 0, 5);

        var hint = new Label
        {
            Text = "这是 AgentLock 保护密码，与 Windows 登录密码分别使用。",
            AutoSize = true,
            ForeColor = VisualTheme.Muted,
            Font = VisualTheme.UiFont(9F),
            Margin = Padding.Empty,
            MaximumSize = new Size(460, 0)
        };
        _tip.SetToolTip(hint, "忘记保护密码时，请先通过 Windows 原生登录完成本人解锁，再恢复保护密码。");
        layout.Controls.Add(hint, 0, 6);
        _status = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 48,
            ForeColor = Color.FromArgb(167, 49, 52),
            Margin = new Padding(0, 8, 0, 4),
            AccessibleName = "密码设置结果"
        };
        layout.Controls.Add(_status, 0, 7);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty
        };
        _save = ActionButton("保存密码", primary: true);
        _save.Click += SavePassword;
        _cancel = ActionButton("取消", primary: false);
        _cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(_save);
        buttons.Controls.Add(_cancel);
        layout.Controls.Add(buttons, 0, 8);
        AcceptButton = _save;
        CancelButton = _cancel;
        Shown += (_, _) => _password.Focus();
        FormClosing += (_, e) => { if (_saving) e.Cancel = true; };
    }

    private async void SavePassword(object? sender, EventArgs e)
    {
        if (_saving)
            return;
        var password = _password.Text;
        if (password.Length < 8 || string.IsNullOrWhiteSpace(password))
        {
            _status.Text = "请输入至少 8 个字符，且不能全部为空格。";
            _password.Focus();
            return;
        }
        if (!string.Equals(password, _confirmation.Text, StringComparison.Ordinal))
        {
            _status.Text = "两次输入的密码不一致。";
            _confirmation.SelectAll();
            _confirmation.Focus();
            return;
        }

        _saving = true;
        SetEnabled(false);
        _status.ForeColor = Teal;
        _status.Text = "正在保存…";
        try
        {
            await Task.Run(() => _store.SetPassword(password));
            _password.Clear();
            _confirmation.Clear();
            _saving = false;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (ArgumentException)
        {
            _status.ForeColor = Color.FromArgb(167, 49, 52);
            _status.Text = "保护密码须为 8–256 个字符，且不能全部为空格。";
        }
        catch (InvalidOperationException)
        {
            _status.ForeColor = Color.FromArgb(167, 49, 52);
            _status.Text = "无法设置密码。已有密码时，请先验证或完成 Windows 原生解锁后的恢复。";
        }
        catch (Exception)
        {
            _status.ForeColor = Color.FromArgb(167, 49, 52);
            _status.Text = "密码未能安全保存。请取消，并检查当前用户的存储权限。";
        }
        finally
        {
            _saving = false;
            if (!IsDisposed)
                SetEnabled(true);
        }
    }

    private void SetEnabled(bool enabled)
    {
        _password.Enabled = enabled;
        _confirmation.Enabled = enabled;
        _save.Enabled = enabled;
        _cancel.Enabled = enabled;
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = VisualTheme.UiFont(9.5F, FontStyle.Bold),
        ForeColor = VisualTheme.Ink,
        Margin = new Padding(0, 0, 0, 8)
    };

    private static TextBox PasswordBox(string accessibleName) => new()
    {
        Dock = DockStyle.Fill,
        UseSystemPasswordChar = true,
        MaxLength = 256,
        Font = VisualTheme.UiFont(11F),
        ForeColor = VisualTheme.Ink,
        BackColor = VisualTheme.Surface,
        BorderStyle = BorderStyle.None,
        Margin = Padding.Empty,
        AccessibleName = accessibleName
    };

    private static Button ActionButton(string text, bool primary)
    {
        var button = new RoundedButton
        {
            Text = text,
            Size = new Size(112, 42),
            Margin = new Padding(8, 0, 0, 0),
            AccessibleName = text
        };
        VisualTheme.StyleButton(button, primary);
        return button;
    }

    private static Control CreateHeader(bool isRecovery)
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
            Text = isRecovery ? "重新设置保护密码" : "设置保护密码",
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
            Margin = new Padding(0, 0, 0, 16)
        };
        surface.Controls.Add(password);
        return surface;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tip.Dispose();
        base.Dispose(disposing);
    }
}
