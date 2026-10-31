using BenchmarkDotNet.Attributes;

namespace RecursiveAI.OneMail.Benchmarks;

/// <summary>Base of the calendar rows: <c>Ours</c> and <c>Theirs</c> run the prepared operations of <see cref="CalendarWorkloads"/>.</summary>
[Config(typeof(RowConfig))]
[MemoryDiagnoser(displayGenColumns: false)]
public abstract class CalendarRow
{
    private Func<long> _ours = null!;
    private Func<long> _theirs = null!;

    public abstract string Input { get; set; }

    internal abstract Func<long> Make(bool ours, string input);

    [GlobalSetup]
    public void Setup()
    {
        _ours = Make(true, Input);
        _theirs = Make(false, Input);
    }

    [Benchmark] public long Ours() => _ours();

    [Benchmark] public long Theirs() => _theirs();
}

/// <summary>ICS parse to the tree plus a typed pass over the events: OneMail.Calendar against Ical.Net.</summary>
public class IcsParseRow : CalendarRow
{
    [ParamsSource(nameof(Names))] public override string Input { get; set; } = "";

    public static IEnumerable<string> Names => CalendarInputs.IcsNames;

    internal override Func<long> Make(bool ours, string input) => CalendarWorkloads.IcsParse(ours, input);
}

/// <summary>ICS build from fields and write.</summary>
public class IcsWriteRow : CalendarRow
{
    [ParamsSource(nameof(Names))] public override string Input { get; set; } = "";

    public static IEnumerable<string> Names => CalendarInputs.IcsBuildNames;

    internal override Func<long> Make(bool ours, string input) => CalendarWorkloads.IcsBuild(ours, input);
}

/// <summary>ICS: change every event's SUMMARY in a parsed calendar and write it.</summary>
public class IcsEditRow : CalendarRow
{
    [ParamsSource(nameof(Names))] public override string Input { get; set; } = "";

    public static IEnumerable<string> Names => CalendarInputs.IcsNames;

    internal override Func<long> Make(bool ours, string input) => CalendarWorkloads.IcsEdit(ours, input);
}

/// <summary>vCard parse plus a typed pass: OneMail.Calendar against FolkerKinzel.VCards.</summary>
public class VCardParseRow : CalendarRow
{
    [ParamsSource(nameof(Names))] public override string Input { get; set; } = "";

    public static IEnumerable<string> Names => CalendarInputs.VcfNames;

    internal override Func<long> Make(bool ours, string input) => CalendarWorkloads.VcfParse(ours, input);
}

/// <summary>vCard build from fields and write.</summary>
public class VCardWriteRow : CalendarRow
{
    [ParamsSource(nameof(Names))] public override string Input { get; set; } = "";

    public static IEnumerable<string> Names => CalendarInputs.VcfBuildNames;

    internal override Func<long> Make(bool ours, string input) => CalendarWorkloads.VcfBuild(ours, input);
}

/// <summary>vCard: change every card's FN in a parsed address book and write it.</summary>
public class VCardEditRow : CalendarRow
{
    [ParamsSource(nameof(Names))] public override string Input { get; set; } = "";

    public static IEnumerable<string> Names => CalendarInputs.VcfNames;

    internal override Func<long> Make(bool ours, string input) => CalendarWorkloads.VcfEdit(ours, input);
}

/// <summary>Occurrences of one rule in a bounded window, from a parsed calendar.</summary>
public class RecurrenceRow : CalendarRow
{
    [ParamsSource(nameof(Names))] public override string Input { get; set; } = "";

    public static IEnumerable<string> Names => CalendarInputs.RecurrenceNames;

    internal override Func<long> Make(bool ours, string input) => CalendarWorkloads.Recurrence(ours, input);
}

/// <summary>jCal / jCard: write and read, OneMail only (no maintained .NET comparator).</summary>
[Config(typeof(RowConfig))]
[MemoryDiagnoser(displayGenColumns: false)]
public class FormsRow
{
    private Func<long> _write = null!;
    private Func<long> _read = null!;

    [Params("ics-invite", "ics-holidays", "vcf-1", "vcf-10k")]
    public string Input { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _write = CalendarWorkloads.FormsWrite(Input);
        _read = CalendarWorkloads.FormsRead(Input);
    }

    [Benchmark] public long Write() => _write();

    [Benchmark] public long Read() => _read();
}

/// <summary>The 50k-event input of the IcsParse row, with the large-row run settings.</summary>
[Config(typeof(LargeRowConfig))]
public class IcsParseLargeRow : CalendarRow
{
    [ParamsSource(nameof(Names))] public override string Input { get; set; } = "";

    public static IEnumerable<string> Names => CalendarInputs.IcsLargeNames;

    internal override Func<long> Make(bool ours, string input) => CalendarWorkloads.IcsParse(ours, input);
}

/// <summary>The 50k-event input of the IcsWrite row, with the large-row run settings.</summary>
[Config(typeof(LargeRowConfig))]
public class IcsWriteLargeRow : CalendarRow
{
    [ParamsSource(nameof(Names))] public override string Input { get; set; } = "";

    public static IEnumerable<string> Names => CalendarInputs.IcsBuildLargeNames;

    internal override Func<long> Make(bool ours, string input) => CalendarWorkloads.IcsBuild(ours, input);
}

/// <summary>The 50k-event input of the IcsEdit row, with the large-row run settings.</summary>
[Config(typeof(LargeRowConfig))]
public class IcsEditLargeRow : CalendarRow
{
    [ParamsSource(nameof(Names))] public override string Input { get; set; } = "";

    public static IEnumerable<string> Names => CalendarInputs.IcsLargeNames;

    internal override Func<long> Make(bool ours, string input) => CalendarWorkloads.IcsEdit(ours, input);
}

/// <summary>jCal write and read of the 50k-event calendar, with the large-row run settings.</summary>
[Config(typeof(LargeRowConfig))]
[MemoryDiagnoser(displayGenColumns: false)]
public class FormsLargeRow
{
    private Func<long> _write = null!;
    private Func<long> _read = null!;

    [Params("ics-50k")]
    public string Input { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _write = CalendarWorkloads.FormsWrite(Input);
        _read = CalendarWorkloads.FormsRead(Input);
    }

    [Benchmark] public long Write() => _write();

    [Benchmark] public long Read() => _read();
}
