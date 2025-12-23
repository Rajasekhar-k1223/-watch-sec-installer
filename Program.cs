using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace watch_sec_installer;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
