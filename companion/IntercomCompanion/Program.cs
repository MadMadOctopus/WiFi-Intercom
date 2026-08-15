namespace IntercomCompanion;

internal static class Program
{
    private const string InstanceMutexName = @"Local\WiFiIntercomCompanion";
    private const string ActivateEventName = @"Local\WiFiIntercomCompanion.Activate";

    [STAThread]
    private static void Main()
    {
        using var instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            SignalExistingInstance();
            return;
        }

        ApplicationConfiguration.Initialize();
        using var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        using var form = new MainForm();
        var activationThread = new Thread(() => WaitForActivation(form, activationEvent)) { IsBackground = true };
        activationThread.Start();
        Application.Run(form);
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(ActivateEventName);
            activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The primary instance is still starting. A subsequent launch will activate it.
        }
    }

    private static void WaitForActivation(MainForm form, EventWaitHandle activationEvent)
    {
        while (!form.IsDisposed)
        {
            activationEvent.WaitOne();
            if (form.IsDisposed) return;
            try
            {
                form.BeginInvoke(() =>
                {
                    form.Show();
                    if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
                    form.Activate();
                    form.BringToFront();
                });
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
    }
}
