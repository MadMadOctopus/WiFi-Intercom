using System.Drawing;
using System.ComponentModel;

namespace IntercomCompanion;

/// <summary>A compact, flat slider used where the design requires no WinForms track-bar chrome.</summary>
internal sealed class FlatSlider : Control
{
    private int value;
    private bool dragging;

    public FlatSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Height = 20;
        Minimum = 64;
        Maximum = 1024;
        AccentColor = UiStyles.Green;
        Cursor = Cursors.Hand;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Minimum { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Maximum { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentColor { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => value;
        set
        {
            var next = Math.Clamp(value, Minimum, Maximum);
            if (this.value == next) return;
            this.value = next;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ValueCommitted;
    public event EventHandler? ValueChanged;

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var left = 4;
        var right = Math.Max(left + 1, Width - 5);
        var y = Height / 2;
        var fraction = Maximum == Minimum ? 0f : (float)(Value - Minimum) / (Maximum - Minimum);
        var x = left + (int)Math.Round((right - left) * fraction);
        using var track = new Pen(Color.FromArgb(226, 229, 232), 3);
        using var fill = new Pen(AccentColor, 3);
        eventArgs.Graphics.DrawLine(track, left, y, right, y);
        eventArgs.Graphics.DrawLine(fill, left, y, x, y);
        using var brush = new SolidBrush(Color.White);
        using var border = new Pen(UiStyles.InputBorder);
        eventArgs.Graphics.FillRectangle(brush, x - 4, y - 6, 9, 13);
        eventArgs.Graphics.DrawRectangle(border, x - 4, y - 6, 8, 12);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button != MouseButtons.Left) return;
        dragging = true;
        Capture = true;
        SetValueFromX(eventArgs.X);
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        if (dragging) SetValueFromX(eventArgs.X);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (!dragging || eventArgs.Button != MouseButtons.Left) return;
        dragging = false;
        Capture = false;
        SetValueFromX(eventArgs.X);
        ValueCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void SetValueFromX(int x)
    {
        var left = 4;
        var right = Math.Max(left + 1, Width - 5);
        var fraction = Math.Clamp((x - left) / (float)(right - left), 0f, 1f);
        Value = Minimum + (int)Math.Round(fraction * (Maximum - Minimum));
    }
}
