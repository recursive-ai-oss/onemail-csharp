using RecursiveAI.OneMail;

namespace RecursiveAI.OneMail.WasmSmoke;

/// <summary>Runs the format core in the browser's .NET runtime and prints what it is built on.</summary>
internal static class Program
{
    private static void Main() => Console.WriteLine(OneMailLibrary.Describe());
}
