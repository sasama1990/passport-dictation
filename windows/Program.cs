namespace PassportDictation;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PassportDictation");
        var directoryArgument = Array.IndexOf(args, "--data-dir");
        if (directoryArgument >= 0 && directoryArgument + 1 < args.Length)
            dataDirectory = Path.GetFullPath(args[directoryArgument + 1]);

        var testArgument = Array.IndexOf(args, "--self-test");
        if (testArgument >= 0)
        {
            var report = args.Length > testArgument + 1
                ? Path.GetFullPath(args[testArgument + 1])
                : Path.Combine(dataDirectory, "self-test.json");
            Application.Run(new SmokeTestForm(dataDirectory, report, args.Contains("--protocol-only")));
            return;
        }

        using var instance = new Mutex(true, "Local\\PassportDictation.Receiver", out var first);
        if (!first)
        {
            MessageBox.Show("Passport 语音输入已在运行，请从任务栏托盘打开。", "Passport 语音输入");
            return;
        }

        Application.Run(new MainForm(dataDirectory));
    }
}
