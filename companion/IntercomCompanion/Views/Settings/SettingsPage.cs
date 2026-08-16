namespace IntercomCompanion.Views.Settings;

/// <summary>Base for a settings page: a scrolling host with a left-aligned inner
/// stack capped at the page's max width. Pages add cards to <see cref="Stack"/>;
/// the stack owns the heading and detail line.</summary>
internal abstract class SettingsPage : UserControl
{
    protected FlowLayoutPanel Stack { get; }
    protected int MaxWidth { get; }

    protected SettingsPage(string title, string detail, int maxWidth)
    {
        MaxWidth = maxWidth;
        Dock = DockStyle.Fill;
        BackColor = UiStyles.Surface;
        Visible = false;

        var scroll = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiStyles.Surface,
            Padding = new Padding(28, 24, 28 + SystemInformation.VerticalScrollBarWidth, 24),
        };
        Stack = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Width = maxWidth, BackColor = UiStyles.Surface, Margin = Padding.Empty,
        };
        Stack.Controls.Add(new Label { Text = title, AutoSize = true, Font = UiStyles.PageHeading, ForeColor = UiStyles.Ink, Margin = Padding.Empty });
        if (!string.IsNullOrEmpty(detail))
            Stack.Controls.Add(new Label { Text = detail, AutoSize = true, MaximumSize = new Size(maxWidth, 0), Font = UiStyles.BodyFont, ForeColor = UiStyles.Secondary, Margin = new Padding(0, 6, 0, 0) });

        scroll.Controls.Add(Stack);
        Controls.Add(scroll);
    }
}
