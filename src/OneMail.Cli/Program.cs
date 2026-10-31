using System.Reflection;

namespace RecursiveAI.OneMail.Cli;

/// <summary>The <c>onemail</c> command-line tool.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            Console.Out.WriteLine(Version());
            return 0;
        }

        if (args.Length > 0 && args[0] == "inspect")
        {
            using StreamWriter stdout = new(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false), 1 << 16) { AutoFlush = false };
            return Inspect.Run(args[1..], stdout, Console.Error);
        }

        if (args.Length > 0 && args[0] is "convert" or "export")
        {
            using StreamWriter stdout = new(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false), 1 << 16) { AutoFlush = false };
            int rc = args[0] == "convert" ? ConvertCommand.Convert(args[1..], stdout, Console.Error) : ConvertCommand.Export(args[1..], stdout, Console.Error);
            stdout.Flush();
            return rc;
        }

        Console.Error.WriteLine("onemail " + Version());
        Console.Error.WriteLine("usage: onemail --version");
        Console.Error.WriteLine("       onemail inspect [--json] [--sizes] [--defects] <file-or-directory>...");
        Console.Error.WriteLine("       " + ConvertCommand.ConvertUsage.Replace("usage: ", "", StringComparison.Ordinal));
        Console.Error.WriteLine("       " + ConvertCommand.ExportUsage.Replace("usage: ", "", StringComparison.Ordinal));
        return args.Length == 0 || args[0] == "--help" ? 0 : 2;
    }

    // The package version, without the source revision that the SDK appends after '+'.
    private static string Version()
    {
        string? full = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (full is null)
        {
            return "unknown";
        }

        int plus = full.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? full : full[..plus];
    }
}
