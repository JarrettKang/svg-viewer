namespace SvgViewer;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool test = args.Length >= 2 && args[0] == "--self-test";
        if (test)
        {
            Directory.CreateDirectory(args[1]);
            File.WriteAllText(Path.Combine(args[1], "startup.log"), "Main entered\n");
        }
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) =>
        {
            if (test) { File.WriteAllText(Path.Combine(args[1], "unhandled.log"), e.Exception.ToString()); Application.Exit(); }
            else MessageBox.Show(e.Exception.Message, "SVG 查看器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        Application.Run(new ViewerForm(test ? null : args.FirstOrDefault(), test ? args[1] : null));
    }
}
