namespace VoHiveControl;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instanceLock = new Mutex(true, "Local\\VoHiveControlSingleton", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("VoHive 控制台已经在运行。", "VoHive 控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
    }
}
