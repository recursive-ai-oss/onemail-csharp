using BenchmarkDotNet.Running;

namespace RecursiveAI.OneMail.Benchmarks;

/// <summary>Runs the benchmark classes of this assembly. Pass <c>--filter</c> to pick rows; there are none yet.</summary>
internal static class Program
{
    private static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
