using System.Drawing;
using System.ComponentModel;

namespace IntercomCompanion;

internal static class UiStyles
{
    public static readonly Color Ink = Color.FromArgb(23, 25, 28);
    public static readonly Color Body = Color.FromArgb(61, 65, 71);
    public static readonly Color Secondary = Color.FromArgb(99, 103, 109);
    public static readonly Color Muted = Color.FromArgb(131, 135, 141);
    public static readonly Color Disabled = Color.FromArgb(154, 160, 166);
    public static readonly Color Surface = Color.FromArgb(245, 246, 247);
    public static readonly Color White = Color.White;
    public static readonly Color Chrome = Color.FromArgb(238, 240, 242);
    public static readonly Color Border = Color.FromArgb(220, 223, 227);
    public static readonly Color ControlBorder = Color.FromArgb(173, 178, 184);
    public static readonly Color InputBorder = Color.FromArgb(122, 127, 133);
    public static readonly Color RowRule = Color.FromArgb(237, 239, 241);
    public static readonly Color Divider = Color.FromArgb(230, 232, 234);
    public static readonly Color HairRule = Color.FromArgb(242, 244, 245);
    public static readonly Color NavHover = Color.FromArgb(228, 231, 234);
    public static readonly Color Green = Color.FromArgb(46, 125, 50);
    public static readonly Color Blue = Color.FromArgb(21, 101, 192);
    public static readonly Color DeepBlue = Color.FromArgb(13, 71, 161);
    public static readonly Color Purple = Color.FromArgb(106, 27, 154);
    public static readonly Color Amber = Color.FromArgb(249, 168, 37);
    public static readonly Color Red = Color.FromArgb(178, 34, 34);
    public static readonly Color DarkRed = Color.FromArgb(142, 26, 26);
    public static readonly Color BlueTint = Color.FromArgb(238, 244, 251);
    public static readonly Color PurpleTint = Color.FromArgb(243, 236, 247);
    public static readonly Color AmberTint = Color.FromArgb(253, 246, 227);
    public static readonly Color RedTint = Color.FromArgb(253, 243, 243);
    public static readonly Color AmberInk = Color.FromArgb(107, 83, 0);
    public static readonly Color AmberBorder = Color.FromArgb(232, 217, 168);
    public static readonly Color BlueBorder = Color.FromArgb(207, 224, 243);
    public static readonly Color RedInk = Color.FromArgb(122, 32, 32);
    public static readonly Color RedRule = Color.FromArgb(242, 214, 214);
    public static readonly Color DisabledRed = Color.FromArgb(208, 138, 138);
    public static readonly Color OfflineDot = Color.FromArgb(201, 204, 208);
    public static readonly Color SliderTrack = Color.FromArgb(226, 229, 232);
    public static readonly Color ProgressTrack = Color.FromArgb(233, 235, 238);
    public static readonly Color DialogFooter = Color.FromArgb(247, 248, 249);
    public static readonly Color TalkingHalo = Color.FromArgb(70, Blue);
    public static readonly Color LogBackground = Color.FromArgb(28, 31, 35);
    public static readonly Color LogBorder = Color.FromArgb(16, 18, 21);
    public static readonly Color LogError = Color.FromArgb(239, 154, 154);
    public static readonly Color LogInfo = Color.FromArgb(100, 181, 246);
    public static readonly Color LogWarning = Color.FromArgb(255, 213, 79);

    public static readonly Font NowTitle = new("Segoe UI", 19.5f, FontStyle.Bold);
    public static readonly Font CounterValue = new("Segoe UI", 16.5f, FontStyle.Bold);
    public static readonly Font PageHeading = new("Segoe UI", 15f, FontStyle.Bold);
    public static readonly Font Broadcast = new("Segoe UI", 12.75f, FontStyle.Bold);
    public static readonly Font CardOverflow = new("Segoe UI", 12f);
    public static readonly Font PanelHeading = new("Segoe UI", 11.25f, FontStyle.Bold);
    public static readonly Font CardAlias = new("Segoe UI", 10.5f, FontStyle.Bold);
    public static readonly Font BodyFont = new("Segoe UI", 9.75f);
    public static readonly Font BodyBold = new(BodyFont, FontStyle.Bold);
    public static readonly Font SecondaryFont = new("Segoe UI", 9f);
    public static readonly Font SecondaryBold = new(SecondaryFont, FontStyle.Bold);
    public static readonly Font Hint = new("Segoe UI", 8.25f);
    public static readonly Font Meta = new("Segoe UI", 7.5f);
    public static readonly Font Badge = new("Segoe UI", 6.75f, FontStyle.Bold);
    public static readonly Font Log = new("Consolas", 9f);

    public static void StyleButton(Button button, Padding padding)
    {
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = White;
        button.ForeColor = Ink;
        button.Font = SecondaryFont;
        button.Padding = padding;
        button.Margin = Padding.Empty;
        button.FlatAppearance.BorderColor = ControlBorder;
        button.FlatAppearance.MouseOverBackColor = HairRule;
        button.FlatAppearance.MouseDownBackColor = RowRule;
    }

    public static void StylePrimary(Button button, Color? accent = null)
    {
        var color = accent ?? Blue;
        StyleButton(button, new Padding(14, 7, 14, 7));
        button.BackColor = color;
        button.ForeColor = White;
        button.Font = SecondaryBold;
        button.FlatAppearance.BorderColor = color;
        button.FlatAppearance.MouseOverBackColor = color;
        button.FlatAppearance.MouseDownBackColor = color;
    }

    public static void StyleDanger(Button button)
    {
        StylePrimary(button, Red);
    }

    public static void StyleInput(Control control, int width)
    {
        control.Width = width;
        control.Font = BodyFont;
        control.Margin = Padding.Empty;
        if (control is TextBox textBox)
        {
            textBox.BorderStyle = BorderStyle.FixedSingle;
            textBox.Height = 26;
        }
        else if (control is ComboBox comboBox)
        {
            comboBox.FlatStyle = FlatStyle.Flat;
            comboBox.Height = 26;
        }
    }

    public static Panel BorderedPanel(Padding padding) => new()
    {
        BackColor = White,
        Padding = padding,
        Margin = Padding.Empty,
    };

    public static void DrawBorder(PaintEventArgs eventArgs, Control control, Color? color = null)
    {
        using var pen = new Pen(color ?? Border);
        eventArgs.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, control.Width - 1), Math.Max(0, control.Height - 1));
    }

    public static Panel Rule(Color? color = null, int height = 1) => new()
    {
        Height = height,
        Dock = DockStyle.Top,
        BackColor = color ?? RowRule,
        Margin = Padding.Empty,
    };
}

/// <summary>Retains the design's tinted disabled primary state instead of
/// allowing the Windows button renderer to replace it with system grey.</summary>
internal sealed class DisabledTintButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DisabledTint { get; set; } = UiStyles.DisabledRed;

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (Enabled) return;

        using var brush = new SolidBrush(DisabledTint);
        eventArgs.Graphics.FillRectangle(brush, ClientRectangle);
        using var pen = new Pen(DisabledTint);
        eventArgs.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        TextRenderer.DrawText(eventArgs.Graphics, Text, Font, ClientRectangle, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}
