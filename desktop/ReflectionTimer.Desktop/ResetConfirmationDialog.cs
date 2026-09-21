using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

namespace ReflectionTimer.Accessible;

// A separate task-switchable dialog, even when launched by a hidden App view
// or a floating tool window. Native controls expose text and focus through UIA/MSAA.
internal sealed class ResetConfirmationDialog : Form
{
    private readonly Button confirm;

    internal ResetConfirmationDialog(ResetWarning warning, AppColorTheme theme)
    {
        Text = warning.Title;
        AccessibleName = warning.Title;
        AccessibleDescription = warning.Message;
        AccessibleRole = AccessibleRole.Dialog;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = true;
        MinimizeBox = false;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        TopMost = true; // Do not appear behind always-on-top timer/prompt windows.
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Question;
        var palette = PreviewTheme.Palette(theme, SystemInformation.HighContrast);
        BackColor = palette.Background;
        ForeColor = palette.Text;

        // Read-only, but focusable: screen readers can revisit and navigate all
        // warning text without depending on automatic dialog announcements.
        var message = new TextBox {
            Name = "reset-message", Text = warning.Message.Replace("\n", Environment.NewLine),
            AccessibleName = "Reset confirmation details", Multiline = true, ReadOnly = true,
            BorderStyle = BorderStyle.None, WordWrap = true, TabStop = true, TabIndex = 2,
            BackColor = palette.Background, ForeColor = palette.Text,
            Width = 420, Margin = new Padding(0, 0, 0, 18)
        };
        message.Height = TextRenderer.MeasureText(message.Text, message.Font, new Size(message.Width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + message.Font.Height;
        confirm = new Button {
            Name = "reset-ok", Text = "OK", AccessibleName = "OK",
            AccessibleDescription = warning.Message, DialogResult = DialogResult.OK,
            AutoSize = true, MinimumSize = new(88, 32), TabIndex = 0,
            BackColor = palette.Raised, ForeColor = palette.Text, UseVisualStyleBackColor = false
        };
        var cancel = new Button {
            Name = "reset-cancel", Text = "Cancel", AccessibleName = "Cancel",
            AccessibleDescription = "Keep the session and saved reflection text without resetting.",
            DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new(88, 32), TabIndex = 1,
            BackColor = palette.Raised, ForeColor = palette.Text, UseVisualStyleBackColor = false
        };
        var buttons = new FlowLayoutPanel {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            Anchor = AnchorStyles.Right, TabIndex = 0, Margin = Padding.Empty
        };
        buttons.Controls.Add(confirm);
        buttons.Controls.Add(cancel);
        var layout = new TableLayoutPanel {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
            Padding = new Padding(20), Dock = DockStyle.Fill, TabStop = false
        };
        layout.Controls.Add(message, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        Controls.Add(layout);
        AcceptButton = confirm;
        CancelButton = cancel;
        ActiveControl = confirm;
    }

    protected override CreateParams CreateParams
    {
        get {
            var value = base.CreateParams;
            // WS_EX_APPWINDOW opts into Alt+Tab/taskbar independently of an
            // owner's tool-window style; never inherit TOOLWINDOW/NOACTIVATE.
            value.ExStyle = (value.ExStyle | 0x40000) & ~0x08000080;
            return value;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        // Request foreground and keyboard focus once the modal window is shown,
        // even when its initiating App/tool window was hidden or inactive.
        BeginInvoke(() => {
            if(IsDisposed || !Visible)return;
            WindowActivation.Focus(this);
            ActiveControl = confirm;
            confirm.Focus();
        });
        base.OnShown(e);
    }
}
