namespace DeskBridge;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if(args.Length==2 && args[0]=="--apply-update") {
            Updater.Apply(args[1]);
            return;
        }
        Updater.CleanOldDownloads();
        Application.Run(new MainForm());
    }
}
