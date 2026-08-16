namespace IntercomCompanion.Views.Settings;

/// <summary>"Identity, audio and shortcuts": companion alias/ID, audio devices,
/// the fixed global hotkeys, and the window behaviour options.</summary>
internal sealed class IdentityPage : SettingsPage
{
    private readonly TextBox aliasBox = new() { MaxLength = 32 };
    private readonly Label companionId = new() { AutoSize = true, Font = UiStyles.BodyFont, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) };
    private readonly CheckBox runInTray = new() { Text = "Keep running in the notification area when closed", AutoSize = true, Font = UiStyles.BodyFont };
    private readonly CheckBox startWithWindows = new() { Text = "Start with Windows", AutoSize = true, Font = UiStyles.BodyFont };

    public IdentityPage() : base("Identity, audio and shortcuts", "", 720)
    {
        MicCombo = new ComboBox();
        SpeakerCombo = new ComboBox();
        UiStyles.StyleInput(aliasBox, 260);
        UiKit.StyleCombo(MicCombo, 320);
        UiKit.StyleCombo(SpeakerCombo, 320);

        var card = UiKit.Card(720, new Padding(20));
        var form = UiKit.FormGrid(170);
        UiKit.AddField(form, "Companion alias", aliasBox);
        UiKit.AddField(form, "Companion ID", companionId);
        UiKit.AddField(form, "Microphone", MicCombo);
        UiKit.AddField(form, "Speaker", SpeakerCombo);

        var broadcastKey = HotkeyBox("Ctrl + Alt + B", 150);
        var replyKey = HotkeyBox("Ctrl + Alt + R", 130);
        var broadcastRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        broadcastRow.Controls.Add(broadcastKey);
        broadcastRow.Controls.Add(UiKit.Hint("works when the window is not focused"));
        UiKit.AddField(form, "Broadcast hotkey", broadcastRow);
        UiKit.AddField(form, "Reply hotkey", replyKey);

        var window = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        runInTray.Margin = new Padding(0, 4, 0, 4);
        startWithWindows.Margin = new Padding(0, 4, 0, 4);
        window.Controls.Add(runInTray);
        window.Controls.Add(startWithWindows);
        UiKit.AddField(form, "Window", window);
        card.Controls.Add(form);
        Stack.Controls.Add(card);

        // The action row is Save alone; navigation back to Talk lives in the
        // settings nav (adjustments/01-back-to-talk.md). Nothing to cancel — the
        // page saves in place.
        var save = UiKit.PrimaryButton("Save");
        save.Padding = new Padding(18, 9, 18, 9);
        save.Click += (_, _) => SaveClicked?.Invoke(this, EventArgs.Empty);
        var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 16, 0, 0) };
        save.Margin = Padding.Empty;
        actions.Controls.Add(save);
        Stack.Controls.Add(actions);
    }

    public ComboBox MicCombo { get; }
    public ComboBox SpeakerCombo { get; }

    public event EventHandler? SaveClicked;

    public string AliasText => aliasBox.Text;
    public bool RunInNotificationArea => runInTray.Checked;
    public bool StartWithWindows => startWithWindows.Checked;

    public void SetCompanionId(uint nodeId) => companionId.Text = $"{nodeId:x8} · fixed for this PC";
    public void SetAlias(string alias) => aliasBox.Text = alias;
    public void SetWindowOptions(bool runInTray, bool startWithWindows)
    {
        this.runInTray.Checked = runInTray;
        this.startWithWindows.Checked = startWithWindows;
    }

    private static TextBox HotkeyBox(string text, int width)
    {
        var box = new TextBox { Text = text, ReadOnly = true, BackColor = UiStyles.White };
        UiStyles.StyleInput(box, width);
        return box;
    }
}
