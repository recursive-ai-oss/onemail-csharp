using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using OneEntity = RecursiveAI.OneMail.Mime.MimeEntity;

namespace RecursiveAI.OneMail.Benchmarks;

/// <summary>Short runs, three launches: the per-machine budget is about 45 minutes.</summary>
internal sealed class RowConfig : ManualConfig
{
    public RowConfig()
    {
        // ONEMAIL_BENCH_DRY=1 runs each benchmark once, to check the rows work; its numbers mean nothing.
        AddJob(Environment.GetEnvironmentVariable("ONEMAIL_BENCH_DRY") == "1"
            ? Job.Dry
            : Job.Default.WithLaunchCount(3).WithWarmupCount(3).WithIterationCount(10).WithId("short"));
        AddExporter(BenchmarkDotNet.Exporters.Json.JsonExporter.FullCompressed);
        WithOptions(ConfigOptions.DisableOptimizationsValidator);
    }
}

/// <summary>Base for rows over the four inputs.</summary>
[Config(typeof(RowConfig))]
[MemoryDiagnoser(displayGenColumns: false)]
public abstract class InputRow
{
    [ParamsSource(nameof(InputNames))]
    public string Input { get; set; } = "";

    public static IEnumerable<string> InputNames => Inputs.Names;

    protected byte[] Data { get; set; } = [];

    [GlobalSetup]
    public virtual void Setup() => Data = Inputs.Get(Input);
}

/// <summary>Row 1: whole-message parse to a tree.</summary>
public class ParseRow : InputRow
{
    [Benchmark] public int Ours() => Workloads.ParseOneMail(Data);
    [Benchmark] public int Theirs() => Workloads.ParseMimeKit(Data);
}

/// <summary>Row 2: parse plus decoded Subject, From, To, Date and Message-ID.</summary>
public class HeadersRow : InputRow
{
    [Benchmark] public int Ours() => Workloads.HeadersOneMail(Data);
    [Benchmark] public int Theirs() => Workloads.HeadersMimeKit(Data);
}

/// <summary>Row 3: parse plus every body part decoded to bytes.</summary>
public class BodiesRow : InputRow
{
    [Benchmark] public long Ours() => Workloads.BodiesOneMail(Data);
    [Benchmark] public long Theirs() => Workloads.BodiesMimeKit(Data);
}

/// <summary>Row 4a: write an unmodified parsed message.</summary>
public class WriteRow : InputRow
{
    private OneEntity _one = null!;
    private global::MimeKit.MimeMessage _kit = null!;

    public override void Setup()
    {
        base.Setup();
        _one = Workloads.PrepareOneMail(Data);
        _kit = Workloads.PrepareMimeKit(Data);
    }

    [Benchmark] public long Ours() => Workloads.WriteOneMail(_one);
    [Benchmark] public long Theirs() => Workloads.WriteMimeKit(_kit);
}

/// <summary>Row 4b: build a multipart with a 1 MB attachment and write it.</summary>
[Config(typeof(RowConfig))]
[MemoryDiagnoser(displayGenColumns: false)]
public class BuildRow
{
    private byte[] _att = [];

    [GlobalSetup]
    public void Setup() => _att = NewAttachment();

    private static byte[] NewAttachment()
    {
        byte[] b = new byte[1_000_000];
        new Random(7).NextBytes(b);
        return b;
    }

    [Benchmark] public long Ours() => Workloads.BuildWriteOneMail(_att);
    [Benchmark] public long Theirs() => Workloads.BuildWriteMimeKit(_att);
}

/// <summary>Row 5: the Dovecot test mbox as messages; operations are messages, so time and allocations are per message.</summary>
[Config(typeof(RowConfig))]
[MemoryDiagnoser(displayGenColumns: false)]
public class MboxRow
{
    private byte[] _mbox = [];

    [GlobalSetup]
    public void Setup() => _mbox = Inputs.DovecotMbox();

    [Benchmark(OperationsPerInvoke = 2859)] public int Ours() => Workloads.MboxOneMail(_mbox);
    [Benchmark(OperationsPerInvoke = 2859)] public int Theirs() => Workloads.MboxMimeKit(_mbox);
}
