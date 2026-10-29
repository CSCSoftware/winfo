namespace WinfoMain.Test;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "Unhandled Exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            File.WriteAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinfoMain", "crash.txt"),
                e.ExceptionObject?.ToString() ?? "unknown");

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
