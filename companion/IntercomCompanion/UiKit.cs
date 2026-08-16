using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace IntercomCompanion;

/// <summary>Reusable flat controls and layout helpers shared by the redesign
/// views. Every control here paints itself from <see cref="UiStyles"/>; none use
/// stock chrome, absolute positioning of content, or <c>Controls.Clear</c> on a
/// live container.</summary>
internal static class UiKit
{
    /// <summary>Two-column label/field grid: an absolute label column and a
    /// filling field column, matching the prototype's <c>170px 1fr</c> grids.</summary>
    public static TableLayoutPanel FormGrid(int labelWidth)
    {
        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Margin = Padding.Empty,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    /// <summary>Adds one <c>label · field(s)</c> row to a <see cref="FormGrid"/>.</summary>
    public static void AddField(TableLayoutPanel grid, string label, params Control[] fields)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Font = UiStyles.BodyFont,
            ForeColor = UiStyles.Ink,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 7, 16, 7),
            Margin = Padding.Empty,
        }, 0, row);
        var fieldPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
            Anchor = AnchorStyles.Left,
        };
        foreach (var field in fields)
        {
            field.Margin = new Padding(0, 4, 8, 4);
            fieldPanel.Controls.Add(field);
        }
        grid.Controls.Add(fieldPanel, 1, row);
    }

    /// <summary>A muted hint label that sizes to its text and never wraps.</summary>
    public static Label Hint(string text, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        Font = UiStyles.SecondaryFont,
        ForeColor = color ?? UiStyles.Secondary,
        Anchor = AnchorStyles.Left,
        Padding = new Padding(0, 6, 0, 0),
        Margin = Padding.Empty,
    };

    /// <summary>A note flagging that a field's change restarts the device.</summary>
    public static Label RestartNote() => new()
    {
        Text = "restarts the device",
        AutoSize = true,
        Font = UiStyles.Hint,
        ForeColor = UiStyles.Muted,
        Anchor = AnchorStyles.Left,
        Padding = new Padding(0, 7, 0, 0),
        Margin = Padding.Empty,
    };

    public static Control Spacer(int width) => new Panel { Width = width, Height = 1, Margin = Padding.Empty };

    /// <summary>A flat plain button (white, 1 px grey border).</summary>
    public static Button PlainButton(string text)
    {
        var button = new Button { Text = text };
        UiStyles.StyleButton(button, new Padding(12, 6, 12, 6));
        return button;
    }

    public static Button PrimaryButton(string text, Color? accent = null)
    {
        var color = accent ?? UiStyles.Blue;
        var button = new DisabledTintButton { Text = text, DisabledTint = Blend(color, UiStyles.White, 0.55f) };
        UiStyles.StylePrimary(button, color);
        return button;
    }

    public static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * amount),
        (int)(from.G + (to.G - from.G) * amount),
        (int)(from.B + (to.B - from.B) * amount));

    /// <summary>A white, 1 px-bordered card that grows to its content.</summary>
    public static Panel Card(int width, Padding padding, int topMargin = 18)
    {
        var card = new Panel
        {
            Width = width, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = UiStyles.White, Padding = padding, Margin = new Padding(0, topMargin, 0, 0),
        };
        card.Paint += (_, e) => UiStyles.DrawBorder(e, card);
        return card;
    }

    public static void StyleCombo(ComboBox combo, int width)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.Font = UiStyles.BodyFont;
        combo.Width = width;
        combo.Margin = Padding.Empty;
        combo.BackColor = UiStyles.White;
    }
}

/// <summary>A small anti-aliased state dot (identity strip, offline rows).</summary>
internal sealed class CircleDot : Control
{
    private Color dotColor = UiStyles.Green;

    public CircleDot(int diameter = 9)
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
        Size = new Size(diameter, diameter);
        Margin = Padding.Empty;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DotColor
    {
        get => dotColor;
        set { if (dotColor == value) return; dotColor = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(dotColor);
        e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>A 1 px vertical divider, inset vertically, matching the identity
/// strip's <c>1px × 28px #E6E8EA</c> rules.</summary>
internal sealed class VDivider : Panel
{
    public VDivider(int inset = 14, int gap = 0)
    {
        Width = 1;
        Anchor = AnchorStyles.Top | AnchorStyles.Bottom;
        BackColor = UiStyles.Divider;
        Margin = new Padding(gap, inset, gap, inset);
    }
}

/// <summary>A filter chip whose count is painted text, not a bordered child
/// control (review R2-19, R2-4). Active state inverts to ink/white.</summary>
internal sealed class Chip : Control
{
    private int count;
    private bool active;

    public Chip(string label)
    {
        Label = label;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 26;
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 6, 0);
        Font = UiStyles.SecondaryFont;
        Measure();
    }

    public string Label { get; }

    public event EventHandler? Selected;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Count
    {
        get => count;
        set { if (count == value) return; count = value; Measure(); Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => active;
        set { if (active == value) return; active = value; Invalidate(); }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Selected?.Invoke(this, EventArgs.Empty);
    }

    private void Measure()
    {
        var labelWidth = TextRenderer.MeasureText(Label, UiStyles.SecondaryFont).Width;
        var countWidth = TextRenderer.MeasureText(count.ToString(), UiStyles.SecondaryFont).Width;
        Width = 9 + labelWidth + 5 + countWidth + 9;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var background = active ? UiStyles.Ink : UiStyles.White;
        using (var fill = new SolidBrush(background)) e.Graphics.FillRectangle(fill, ClientRectangle);
        using (var pen = new Pen(active ? UiStyles.Ink : UiStyles.ControlBorder))
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        var labelColor = active ? UiStyles.White : UiStyles.Body;
        var countColor = active ? UiStyles.Disabled : UiStyles.Muted;
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        var labelSize = TextRenderer.MeasureText(Label, UiStyles.SecondaryFont);
        var bounds = new Rectangle(9, 0, Width, Height);
        TextRenderer.DrawText(e.Graphics, Label, UiStyles.SecondaryFont, bounds, labelColor, flags);
        var countBounds = new Rectangle(9 + labelSize.Width + 5, 0, Width, Height);
        TextRenderer.DrawText(e.Graphics, count.ToString(), UiStyles.SecondaryFont, countBounds, countColor, flags);
    }
}

/// <summary>A joined segmented toggle (Configure dialog: Playback, Buttons, Ring
/// centre). Read-only mode shows state without accepting clicks.</summary>
internal sealed class SegmentToggle : Control
{
    private readonly string[] options;
    private int selectedIndex;

    public SegmentToggle(string[] options, int selected = 0, bool readOnly = false)
    {
        this.options = options;
        selectedIndex = selected;
        ReadOnly = readOnly;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = UiStyles.BodyFont;
        Height = 34;
        Cursor = readOnly ? Cursors.Default : Cursors.Hand;
        Margin = Padding.Empty;
        Measure();
    }

    public bool ReadOnly { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => selectedIndex;
        set { if (selectedIndex == value) return; selectedIndex = value; Invalidate(); }
    }

    public event EventHandler? SelectionChanged;

    private int[] segmentWidths = [];

    private void Measure()
    {
        segmentWidths = new int[options.Length];
        var total = 0;
        for (var i = 0; i < options.Length; i++)
        {
            var w = TextRenderer.MeasureText(options[i], UiStyles.BodyFont).Width + 28;
            segmentWidths[i] = w;
            total += w;
        }
        Width = total + 1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (ReadOnly) return;
        var x = 0;
        for (var i = 0; i < options.Length; i++)
        {
            if (e.X >= x && e.X < x + segmentWidths[i])
            {
                if (i != selectedIndex) { selectedIndex = i; Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty); }
                return;
            }
            x += segmentWidths[i];
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var x = 0;
        for (var i = 0; i < options.Length; i++)
        {
            var selected = i == selectedIndex;
            var rect = new Rectangle(x, 0, segmentWidths[i], Height - 1);
            using (var fill = new SolidBrush(selected ? UiStyles.Blue : UiStyles.White))
                e.Graphics.FillRectangle(fill, rect);
            using (var pen = new Pen(selected ? UiStyles.Blue : UiStyles.ControlBorder))
                e.Graphics.DrawRectangle(pen, rect);
            TextRenderer.DrawText(e.Graphics, options[i], UiStyles.BodyFont, rect,
                selected ? UiStyles.White : UiStyles.Body,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            x += segmentWidths[i];
        }
    }
}
