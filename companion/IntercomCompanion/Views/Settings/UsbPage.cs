namespace IntercomCompanion.Views.Settings;

/// <summary>"Set up a device over USB": the step strip, the port picker with an
/// identify status line, Wi-Fi credentials, and the send button.</summary>
internal sealed class UsbPage : SettingsPage
{
    public sealed record PortOption(string Port, string Display)
    {
        public override string ToString() => Display;
    }

    private readonly ComboBox portCombo = new();
    private readonly ComboBox ssidCombo = new();
    private readonly TextBox passwordBox = new() { UseSystemPasswordChar = true, MaxLength = 64 };
    private readonly TextBox aliasBox = new() { MaxLength = 24 };
    private readonly Label identity = new() { AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) };

    public UsbPage() : base(
        "Set up a device over USB",
        "Connect the device by USB, give it your Wi-Fi credentials, and it reboots onto the network. The password stays on this PC and is never saved by the companion.",
        720)
    {
        UiKit.StyleCombo(portCombo, 280);
        UiKit.StyleCombo(ssidCombo, 240);
        // Editable so setup still works if no scan result is available; the list
        // still offers scanned SSIDs (review R2-25).
        ssidCombo.DropDownStyle = ComboBoxStyle.DropDown;
        UiStyles.StyleInput(passwordBox, 240);
        UiStyles.StyleInput(aliasBox, 240);
        identity.Text = "Connect a device to identify it over USB";

        Stack.Controls.Add(BuildSteps());

        var card = UiKit.Card(720, new Padding(20));
        var form = UiKit.FormGrid(150);

        var rescan = UiKit.PlainButton("Rescan");
        rescan.Click += (_, _) => RescanClicked?.Invoke(this, EventArgs.Empty);
        var portRow = Row(portCombo, UiKit.Spacer(8), rescan, UiKit.Spacer(8), identity);
        UiKit.AddField(form, "USB port", portRow);

        var wifiRow = Row(ssidCombo, UiKit.Spacer(8), UiKit.Hint("2.4 GHz only"));
        UiKit.AddField(form, "Wi-Fi network", wifiRow);

        var show = UiKit.PlainButton("Show");
        show.Click += (_, _) => { passwordBox.UseSystemPasswordChar = !passwordBox.UseSystemPasswordChar; show.Text = passwordBox.UseSystemPasswordChar ? "Show" : "Hide"; };
        UiKit.AddField(form, "Password", Row(passwordBox, UiKit.Spacer(8), show));

        UiKit.AddField(form, "Device alias", aliasBox);

        var send = UiKit.PrimaryButton("Send to device and reboot");
        send.Click += (_, _) => SendClicked?.Invoke(this, EventArgs.Empty);
        var submit = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 20, 0, 0), Padding = new Padding(0, 16, 0, 0) };
        submit.Paint += (_, e) => { using var pen = new Pen(UiStyles.RowRule); e.Graphics.DrawLine(pen, 0, 0, submit.Width, 0); };
        send.Margin = Padding.Empty;
        submit.Controls.Add(send);
        submit.Controls.Add(new Label { Text = "The device restarts and should appear in Devices within a few seconds.", AutoSize = true, Font = UiStyles.SecondaryFont, ForeColor = UiStyles.Secondary, Margin = new Padding(12, 7, 0, 0) });
        form.Controls.Add(submit, 1, form.RowCount);
        form.RowCount++;

        card.Controls.Add(form);
        Stack.Controls.Add(card);
    }

    public event EventHandler? RescanClicked;
    public event EventHandler? SendClicked;

    public string? SelectedPort => (portCombo.SelectedItem as PortOption)?.Port;
    public string Ssid => (ssidCombo.SelectedItem as string ?? ssidCombo.Text).Trim();
    public string Password => passwordBox.Text;
    public string DeviceAlias => aliasBox.Text.Trim();

    public void SetPorts(IReadOnlyList<PortOption> ports, string? selectPort)
    {
        portCombo.BeginUpdate();
        portCombo.Items.Clear();
        foreach (var port in ports) portCombo.Items.Add(port);
        portCombo.SelectedItem = ports.FirstOrDefault(port => port.Port == selectPort) ?? ports.FirstOrDefault();
        portCombo.EndUpdate();
    }

    public void SetSsids(IReadOnlyList<string> ssids, string? current)
    {
        ssidCombo.BeginUpdate();
        ssidCombo.Items.Clear();
        foreach (var ssid in ssids) ssidCombo.Items.Add(ssid);
        if (current is not null && ssids.Contains(current)) ssidCombo.SelectedItem = current;
        else if (ssids.Count > 0) ssidCombo.SelectedIndex = 0;
        ssidCombo.EndUpdate();
    }

    public void SetAlias(string alias) => aliasBox.Text = alias;
    public void ClearPassword() => passwordBox.Clear();

    public void SetIdentity(string text, Color color)
    {
        identity.Text = text;
        identity.ForeColor = color;
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        foreach (var control in controls) { control.Margin = new Padding(0, 0, 0, 0); row.Controls.Add(control); }
        return row;
    }

    private Panel BuildSteps()
    {
        var steps = new TableLayoutPanel { Width = 720, Height = 68, ColumnCount = 3, RowCount = 1, BackColor = UiStyles.White, Margin = new Padding(0, 22, 0, 0) };
        steps.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        var data = new[]
        {
            ("STEP 1", "Connect by USB", UiStyles.White, UiStyles.Green, UiStyles.Ink),
            ("STEP 2", "Enter Wi-Fi credentials", UiStyles.BlueTint, UiStyles.Blue, UiStyles.DeepBlue),
            ("STEP 3", "Device reboots and appears", UiStyles.Surface, UiStyles.Disabled, UiStyles.Muted),
        };
        for (var i = 0; i < data.Length; i++)
        {
            var step = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = data[i].Item3, Padding = new Padding(14, 10, 8, 8), Margin = Padding.Empty };
            step.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            step.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            step.Controls.Add(new Label { Text = data[i].Item1, AutoSize = true, Font = UiStyles.Hint, ForeColor = data[i].Item4, Margin = Padding.Empty }, 0, 0);
            step.Controls.Add(new Label { Text = data[i].Item2, AutoSize = true, Font = UiStyles.BodyFont, ForeColor = data[i].Item5, Margin = new Padding(0, 3, 0, 0) }, 0, 1);
            steps.Controls.Add(step, i, 0);
        }
        steps.Paint += (_, e) => UiStyles.DrawBorder(e, steps);
        return steps;
    }
}
