namespace SonosCast;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // A second copy would fight the first over the stream port; bring the running one forward instead.
        using var single = new Mutex(true, "SonosCast.SingleInstance", out bool first);
        if (!first)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(MainForm.ShowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { /* the other copy is still starting up */ }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(startInTray: args.Contains(Settings.TrayArg)));
    }
}
