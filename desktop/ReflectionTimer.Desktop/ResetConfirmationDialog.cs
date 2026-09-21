using ReflectionTimer.Core;
using ReflectionTimer.Desktop;
using System.Runtime.InteropServices;

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
        var focusColor = FocusColor(palette, theme);
        ApplyFocusOutline(confirm, palette, focusColor);
        ApplyFocusOutline(cancel, palette, focusColor);
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

    private static void ApplyFocusOutline(Button button, PreviewPalette palette, Color color)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = palette.Border;
        button.FlatAppearance.MouseOverBackColor = palette.Raised;
        button.GotFocus += (_, _) => button.Invalidate();
        button.LostFocus += (_, _) => button.Invalidate();
        button.Paint += (_, e) => {
            if(!button.Focused)return;
            // Paint inside the existing bounds: focus never changes button size.
            using var pen = new Pen(color, Math.Max(2, button.LogicalToDeviceUnits(3))) {
                Alignment = System.Drawing.Drawing2D.PenAlignment.Inset
            };
            e.Graphics.DrawRectangle(pen, 0, 0, button.ClientSize.Width - 1, button.ClientSize.Height - 1);
        };
    }

    private static Color FocusColor(PreviewPalette palette, AppColorTheme theme)
    {
        // Match the Windows title-bar accent when it is clearly visible against
        // the button. Respect contrast themes and fall back to the app's accent.
        if(SystemInformation.HighContrast || theme == AppColorTheme.HighContrast)return palette.Accent;
        if(DwmGetColorizationColor(out var argb, out _) != 0)return palette.Accent;
        var accent = Color.FromArgb(255, (int)(argb >> 16 & 255), (int)(argb >> 8 & 255), (int)(argb & 255));
        static double Light(Color c) {
            static double Channel(byte b) { var v = b / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            return .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        }
        var a = Light(accent); var b = Light(palette.Raised);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05) >= 3 ? accent : palette.Accent;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(out uint color, [MarshalAs(UnmanagedType.Bool)] out bool opaque);

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
