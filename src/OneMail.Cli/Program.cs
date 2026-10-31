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

        Console.Error.WriteLine("onemail " + Version());
        Console.Error.WriteLine("usage: onemail --version");
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
