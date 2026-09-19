namespace T2med_Api_Config;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            return ConfigurationCommands.Run(args);
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new ConfigForm());
        return 0;
    }
}
