using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;
using RecursiveAI.OneMail.Tests.Pst;

namespace RecursiveAI.OneMail.Durability;

internal enum OpKind : byte
{
    Write,
    Barrier,
    SetLength,
}

internal sealed record Op(OpKind Kind, long Offset, byte[] Data);

/// <summary>An in-memory file that records every write, barrier and length change the editor makes, in order.</summary>
internal sealed class RecordingIo(byte[] image) : EditorIo
{
    private readonly MemoryStream _file = new(image.Length * 2);

    public List<Op> Ops { get; } = [];

    public void Init()
    {
        _file.Write(image);
    }

    public override long Length => _file.Length;

    public override int Read(long offset, Span<byte> buffer)
    {
        if (offset >= _file.Length)
        {
            return 0;
        }

        _file.Position = offset;
        return _file.Read(buffer);
    }

    public override void Write(long offset, ReadOnlySpan<byte> data)
    {
        Ops.Add(new Op(OpKind.Write, offset, data.ToArray()));
        Apply(_file, offset, data);
    }

    public override void Barrier() => Ops.Add(new Op(OpKind.Barrier, 0, []));

    public override void SetLength(long length)
    {
        Ops.Add(new Op(OpKind.SetLength, length, []));
        _file.SetLength(length);
    }

    public override void Dispose()
    {
    }

    public static void Apply(MemoryStream file, long offset, ReadOnlySpan<byte> data)
    {
        if (file.Length < offset + data.Length)
        {
            file.SetLength(offset + data.Length);
        }

        file.Position = offset;
        file.Write(data);
    }
}

/// <summary>A file on disk that stops the process (SIGKILL) when the editor reaches the Nth write or barrier, or after a random time.</summary>
internal sealed class KillingIo(Stream stream, int killAtOp, int killAfterMs) : EditorIo
{
    private int _ops;
    private readonly StreamEditorIoProxy _inner = new(stream);

    public int Operations => _ops;

    public override long Length => _inner.Length;

    public void StartTimer()
    {
        if (killAfterMs > 0)
        {
            new Thread(() =>
            {
                Thread.Sleep(killAfterMs);
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }) { IsBackground = true }.Start();
        }
    }

    private void Tick()
    {
        if (++_ops == killAtOp)
        {
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }
    }

    public override int Read(long offset, Span<byte> buffer) => _inner.Read(offset, buffer);

    public override void Write(long offset, ReadOnlySpan<byte> data)
    {
        Tick();
        _inner.Write(offset, data);
    }

    public override void Barrier()
    {
        Tick();
        _inner.Barrier();
    }

    public override void SetLength(long length)
    {
        Tick();
        _inner.SetLength(length);
    }

    public override void Dispose() => _inner.Dispose();

    private sealed class StreamEditorIoProxy(Stream s) : EditorIo
    {
        public override long Length => s.Length;

        public override int Read(long offset, Span<byte> buffer)
        {
            if (offset >= s.Length)
            {
                return 0;
            }

            s.Position = offset;
            return s.Read(buffer);
        }

        public override void Write(long offset, ReadOnlySpan<byte> data)
        {
            s.Position = offset;
            s.Write(data);
        }

        public override void Barrier()
        {
            if (s is FileStream fs)
            {
                fs.Flush(flushToDisk: true);
            }
        }

        public override void SetLength(long length) => s.SetLength(length);

        public override void Dispose() => s.Dispose();
    }
}

/// <summary>What the recording run produced: the base image, the journal, and the store states a leftover may legitimately be in.</summary>
internal sealed class Recording
{
    public required byte[] Base { get; init; }

    public required List<Op> Ops { get; init; }

    /// <summary>Snapshot hashes: the starting state, then the state after each commit.</summary>
    public required List<string> States { get; init; }

    /// <summary>What the independent node database checker already says about the base file (messages), so only new findings count.</summary>
    public HashSet<string> Baseline { get; init; } = [];
}

internal static class Harness
{
    public static string Hash(SortedDictionary<string, string> snapshot)
    {
        var sb = new StringBuilder();
        foreach ((string k, string v) in snapshot)
        {
            sb.Append(k).Append('=').Append(v).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static string? HashOf(byte[] file)
    {
        if (!PstStore.TryOpen(file, out PstStore? s, out _))
        {
            return null;
        }

        using (s)
        {
            return Hash(StoreChecks.Snapshot(s!));
        }
    }

    /// <summary>Runs a scenario against an in-memory copy and records everything it does to the file.</summary>
    public static Recording Record(byte[] baseImage, string scenario)
    {
        var io = new RecordingIo(baseImage);
        io.Init();
        var states = new List<string> { HashOf(baseImage)! };
        NdbEditor ed = NdbEditor.Open(io, new NdbEditorOptions());
        using var w = new PstWritableStore(ed, Scenarios.Options);
        io.Ops.Clear();
        Scenarios.Run(scenario, w, () =>
        {
            w.Commit();
            states.Add(SnapshotHash(w));
        });
        return new Recording { Base = baseImage, Ops = io.Ops, States = states, Baseline = [.. NdbChecker.Check(baseImage).Errors] };
    }

    private static string SnapshotHash(PstWritableStore w) => Hash(StoreChecks.Snapshot(w.View));

    /// <summary>The file a crash after operation <paramref name="cut"/> could leave. Writes before the last barrier are on disk; later ones may be in any subset.</summary>
    /// <param name="variant">0: every write before the cut. 1: a random subset of the writes since the last barrier. 2: all, with the last write torn at a sector boundary.</param>
    public static byte[] Materialize(Recording r, int cut, int variant, Random rng)
    {
        var file = new MemoryStream(r.Base.Length * 2);
        file.Write(r.Base);
        int lastBarrier = -1;
        for (int i = 0; i < cut; i++)
        {
            if (r.Ops[i].Kind == OpKind.Barrier)
            {
                lastBarrier = i;
            }
        }

        for (int i = 0; i < cut; i++)
        {
            Op op = r.Ops[i];
            bool open = i > lastBarrier;
            switch (op.Kind)
            {
                case OpKind.Write:
                    if (open && variant == 1 && rng.Next(2) == 0)
                    {
                        break;
                    }

                    ReadOnlySpan<byte> data = op.Data;
                    if (open && variant >= 3)
                    {
                        // Every write since the last barrier reaches the disk in whole 512-byte sectors, in any subset. The 564-byte header write is kept whole
                        // unless variant 4 asks to tear it too (it fits one 4 KiB physical sector, which is the assumption the commit rests on).
                        bool header = op.Offset == 0 && op.Data.Length == 564;
                        if (!header || variant == 4)
                        {
                            for (int at = 0; at < data.Length;)
                            {
                                int piece = Math.Min(data.Length - at, 512 - (int)((op.Offset + at) % 512));    // up to the next absolute sector boundary
                                if (rng.Next(2) == 0)
                                {
                                    RecordingIo.Apply(file, op.Offset + at, data.Slice(at, piece));
                                }

                                at += piece;
                            }

                            break;
                        }
                    }

                    if (open && variant == 2 && i == cut - 1 && data.Length > 512 && !(op.Offset == 0 && data.Length == 564))
                    {
                        data = data[..(512 * rng.Next(1, data.Length / 512))];
                    }

                    RecordingIo.Apply(file, op.Offset, data);
                    break;
                case OpKind.SetLength:
                    if (!(open && (variant == 1 || variant >= 3) && rng.Next(2) == 0))
                    {
                        file.SetLength(op.Offset);
                    }

                    break;
            }
        }

        return file.ToArray();
    }

    // Defects a leftover may carry: the maps are marked invalid (and so may disagree with the header), or the file runs past the recorded end.
    public static (string? Problem, int State, bool InvalidMaps) Classify(byte[] file, Recording r, bool deep)
    {
        if (!PstStore.TryOpen(file, out PstStore? s, out IReadOnlyList<MapiDefect> why))
        {
            return ("does not open: " + string.Join("; ", why), -1, false);
        }

        using (s)
        {
            NdbFile db = s!.Database;
            bool invalid = db.Header.Root.AMapValid is not (1 or 2);
            db.ValidateAllocation();
            _ = db.ReadDensityList();
            foreach (NdbDefect d in db.Defects)
            {
                bool allowed = d.Kind == NdbDefectKind.FileSizeMismatch && file.Length > (long)db.Header.Root.FileEof
                    || invalid && d.Kind is NdbDefectKind.AMapFlagInvalid or NdbDefectKind.AMapFreeSpaceMismatch;
                if (!allowed)
                {
                    return ($"defect {d.Kind} at 0x{d.Offset:x}: {d.Detail}", -1, invalid);
                }
            }

            if (s.Defects.Count > 0)
            {
                return ("messaging defect: " + s.Defects[0], -1, invalid);
            }

            NdbChecker.Report rep = NdbChecker.Check(file, expectValidMaps: !invalid);
            string? fresh = rep.Errors.FirstOrDefault(e => !r.Baseline.Contains(e));
            if (fresh is not null)
            {
                return ("node database checker: " + fresh, -1, invalid);
            }

            string hash = Hash(StoreChecks.Snapshot(s));
            int state = r.States.IndexOf(hash);
            if (state < 0)
            {
                return ("the store is in none of the " + r.States.Count + " expected states", -1, invalid);
            }

            if (deep)
            {
                List<string> problems = StoreChecks.Consistency(s);
                if (problems.Count > 0)
                {
                    return ("inconsistent: " + problems[0], state, invalid);
                }
            }

            return (null, state, invalid);
        }
    }

    /// <summary>A leftover with invalid maps must still be repairable: open it with the rebuild accepted, add an item, and check the result.</summary>
    public static string? RepairAndCheck(byte[] file)
    {
        var ms = new MemoryStream();
        ms.Write(file);
        try
        {
            using (PstWritableStore w = PstStore.OpenForWriting(ms, leaveOpen: true, new PstWriteOptions { Allow = NdbOpenRisks.AllocationMaps, Timestamp = Scenarios.Stamp }))
            {
                PstFolder f = Scenarios.Folders(w.View)[0];
                w.AddMessage(f.Nid, Scenarios.Message("after repair", 1, 1, 5000, true, "r"));
                w.Commit();
            }
        }
        catch (Exception e)
        {
            return "repair failed: " + e.Message;
        }

        byte[] after = ms.ToArray();
        if (!NdbFile.TryOpen(after, out NdbFile? db, out _))
        {
            return "repaired file does not open";
        }

        using (db)
        {
            NdbAllocationSummary sum = db!.ValidateAllocation();
            _ = db.ReadDensityList();
            if (sum.InvalidCount != 0 || sum.FreeBytes != sum.HeaderFreeBytes || db.Defects.Count > 0)
            {
                return "repaired file has defects: " + (db.Defects.Count > 0 ? db.Defects[0].ToString() : $"free {sum.FreeBytes} vs header {sum.HeaderFreeBytes}");
            }
        }

        return null;
    }
}
