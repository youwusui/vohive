namespace VoHiveControl;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instanceLock = new Mutex(true, "Local\\VoHiveControlSingleton", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("VOHIVE for Windows已经在运行。", "VOHIVE for Windows", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
    }
}
