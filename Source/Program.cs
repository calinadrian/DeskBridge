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
        using var single=new Mutex(true,@"Local\DeskBridge-"+Environment.UserName,out bool firstInstance);
        if(!firstInstance) return;
        Application.Run(new MainForm(args.Contains("--startup")));
    }
}
