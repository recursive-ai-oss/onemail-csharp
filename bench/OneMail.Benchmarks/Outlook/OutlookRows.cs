using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace RecursiveAI.OneMail.Benchmarks;

/// <summary>Three launches, one warm-up and three measured iterations: for walks that take seconds each.</summary>
internal sealed class LargeRowConfig : ManualConfig
{
    public LargeRowConfig()
    {
        AddJob(Environment.GetEnvironmentVariable("ONEMAIL_BENCH_DRY") == "1"
            ? Job.Dry
            : Job.Default.WithLaunchCount(3).WithWarmupCount(1).WithIterationCount(3).WithInvocationCount(1).WithUnrollFactor(1).WithId("large"));
        AddExporter(BenchmarkDotNet.Exporters.Json.JsonExporter.FullCompressed);
        WithOptions(ConfigOptions.DisableOptimizationsValidator);
    }
}

/// <summary>The MSG files both libraries open, and the TNEF files, chosen once so every row and helper sees the same set.</summary>
internal static class OutlookSets
{
    private static string[]? s_msgs;

    /// <summary>MSG files that OneMail opens and MsgReader reads without throwing (a few fuzz-minimised corpus files are refused by one or both).</summary>
    public static string[] Msgs => s_msgs ??= [.. OutlookInputs.MsgFiles().Where(Both)];

    private static bool Both(string path)
    {
        try
        {
            return OutlookWorkloads.MsgOurs(path).Messages == 1 && OutlookWorkloads.MsgTheirs(path).Messages == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The path for an MSG row name: the smallest, median-sized and largest openable file, or all of them.</summary>
    public static string[] MsgSet(string name)
    {
        string[] bySize = [.. Msgs.OrderBy(f => new FileInfo(f).Length).ThenBy(f => f, StringComparer.Ordinal)];
        return name switch
        {
            "msg-smallest" => [bySize[0]],
            "msg-median" => [bySize[bySize.Length / 2]],
            "msg-largest" => [bySize[^1]],
            "msg-corpus" => Msgs,
            _ => throw new ArgumentException("unknown MSG input " + name, nameof(name)),
        };
    }

    public static readonly string[] MsgNames = ["msg-smallest", "msg-median", "msg-largest", "msg-corpus"];
}

/// <summary>PST/OST: open, walk every folder, read every message and every attachment's bytes. The real files and the 1k stores.</summary>
[Config(typeof(RowConfig))]
[MemoryDiagnoser(displayGenColumns: false)]
public class PstRow
{
    [ParamsSource(nameof(Inputs))]
    public string Input { get; set; } = "";

    public static IEnumerable<string> Inputs => [.. OutlookInputs.PstReal, .. OutlookInputs.PstSmallGenerated];

    [Benchmark] public long Ours() => OutlookWorkloads.PstOurs(OutlookInputs.PstPath(Input)).Checksum;

    [Benchmark] public long Theirs() => OutlookWorkloads.PstTheirs(OutlookInputs.PstPath(Input)).Checksum;
}

/// <summary>The same walk over the 10k and 50k stores.</summary>
[Config(typeof(LargeRowConfig))]
[MemoryDiagnoser(displayGenColumns: false)]
public class PstLargeRow
{
    [ParamsSource(nameof(Inputs))]
    public string Input { get; set; } = "";

    public static IEnumerable<string> Inputs => OutlookInputs.PstLargeGenerated;

    [Benchmark] public long Ours() => OutlookWorkloads.PstOurs(OutlookInputs.PstPath(Input)).Checksum;

    [Benchmark] public long Theirs() => OutlookWorkloads.PstTheirs(OutlookInputs.PstPath(Input)).Checksum;
}

/// <summary>The 4 KiB-layout stores, OneMail alone: the comparator cannot read them, so there is no ratio, only a time and allocations.</summary>
[Config(typeof(RowConfig))]
[MemoryDiagnoser(displayGenColumns: false)]
public class PstFourKiBRow
{
    [ParamsSource(nameof(Inputs))]
    public string Input { get; set; } = "";

    public static IEnumerable<string> Inputs => OutlookInputs.PstFourKiB;

    [Benchmark] public long Ours() => OutlookWorkloads.PstOurs(OutlookInputs.PstPath(Input)).Checksum;
}

/// <summary>MSG: open, read the fields and all three bodies, read every attachment.</summary>
[Config(typeof(RowConfig))]
[MemoryDiagnoser(displayGenColumns: false)]
public class MsgRow
{
    private string[] _files = [];

    [ParamsSource(nameof(Inputs))]
    public string Input { get; set; } = "";

    public static IEnumerable<string> Inputs => OutlookSets.MsgNames;

    [GlobalSetup]
    public void Setup() => _files = OutlookSets.MsgSet(Input);

    [Benchmark]
    public long Ours()
    {
        long n = 0;
        foreach (string f in _files)
        {
            n += OutlookWorkloads.MsgOurs(f).Checksum;
        }

        return n;
    }

    [Benchmark]
    public long Theirs()
    {
        long n = 0;
        foreach (string f in _files)
        {
            n += OutlookWorkloads.MsgTheirs(f).Checksum;
        }

        return n;
    }
}

/// <summary>TNEF: parse the stream in memory, read the fields, the best body and every attachment.</summary>
[Config(typeof(RowConfig))]
[MemoryDiagnoser(displayGenColumns: false)]
public class TnefRow
{
    private byte[] _data = [];

    [ParamsSource(nameof(Inputs))]
    public string Input { get; set; } = "";

    public static IEnumerable<string> Inputs => OutlookInputs.TnefNames;

    [GlobalSetup]
    public void Setup() => _data = File.ReadAllBytes(OutlookInputs.TnefPath(Input));

    [Benchmark] public long Ours() => OutlookWorkloads.TnefOurs(_data).Checksum;

    [Benchmark] public long Theirs() => OutlookWorkloads.TnefTheirs(_data).Checksum;
}
