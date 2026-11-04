using System.Buffers.Binary;
using RecursiveAI.OneMail.Pst.Ltp;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>Row-by-row changes to a table context, checked against a dictionary and read back through the table reader.</summary>
public sealed class TableContextEditorTests
{
    private const ushort IntColumn = 0x0E08;
    private const ushort StringColumn = 0x3001;
    private static readonly (ushort Id, ushort Type)[] Columns = [(IntColumn, LtpPropertyTypes.PtypInteger32), (StringColumn, LtpPropertyTypes.PtypString)];

    private static byte[]? Text(int seed)
    {
        seed &= 0xFFFFF;
        // Absent, empty, short, and sometimes large enough that the heap fills many blocks.
        return (seed % 11) switch
        {
            0 => null,
            1 => [],
            _ => System.Text.Encoding.Unicode.GetBytes(new string((char)('a' + (seed % 26)), 1 + (seed * 7 % 180))),
        };
    }

    private static byte[] Int(int v) => BitConverter.GetBytes(v);

    private static Nid Host(NdbEditor ed, IReadOnlyDictionary<uint, (int Number, byte[]? Text)> rows)
    {
        var b = new TableContextBuilder(ed.Blocks, Columns, streamHeap: true, rowIdsAscending: true);
        foreach (KeyValuePair<uint, (int Number, byte[]? Text)> r in rows.OrderBy(static r => r.Key))
        {
            b.AddRow(r.Key, [Int(r.Value.Number), r.Value.Text, null, null]);   // the builder sorts columns by tag and adds PidTagLtpRowId and PidTagLtpRowVer
        }

        LtpNodeResult result = b.Build();
        Nid nid = ed.AllocateNid(NidType.NormalMessage);   // any NID the header counts will do for a node the test fills itself
        ed.UpsertNode(new NodeEntry(nid, result.Data, result.WriteSubnodes(ed.Blocks), default));
        return nid;
    }

    private static void Verify(NdbEditor ed, Nid nid, Dictionary<uint, (int Number, byte[]? Text)> model)
    {
        Assert.True(TableContext.TryOpen(ed.Committed, nid, out TableContext? t, out _));
        using TableContext table = t!;
        Assert.Equal(model.Count, table.RowCount);
        int intColumn = table.FindColumn(IntColumn), textColumn = table.FindColumn(StringColumn);
        byte[] buffer = new byte[table.RowSize];
        long seen = 0;
        long previous = -1;
        var indexes = new HashSet<uint>();
        foreach (TcRowIndexEntry e in table.EnumerateRowIndex())
        {
            Assert.True(e.RowId > previous, "row index keys are not strictly ascending");
            previous = e.RowId;
            Assert.True(e.RowIndex < model.Count && indexes.Add(e.RowIndex), "row indexes are not a permutation of 0..n-1");
            Assert.True(table.TryReadRow(e.RowIndex, buffer));
            TableRow row = table.GetRow(buffer);
            Assert.Equal(e.RowId, row.RowId);
            (int number, byte[]? text) = model[e.RowId];
            Assert.True(row.TryGetCell(intColumn, out LtpValueRef iv));
            Assert.Equal(number, BinaryPrimitives.ReadInt32LittleEndian(table.ReadValue(iv)));
            if (text is null)
            {
                Assert.False(row.HasCell(textColumn));
            }
            else
            {
                Assert.True(row.TryGetCell(textColumn, out LtpValueRef sv));
                Assert.Equal(text, table.ReadValue(sv) ?? []);
            }

            seen++;
        }

        Assert.Equal(model.Count, seen);
        // Every key is found through the index tree, not only by walking the leaves.
        foreach (uint id in model.Keys.Take(2000))
        {
            Assert.True(table.TryFindRow(id, out _));
        }
    }

    [Theory]
    [InlineData(1_500, 1, 40, 400)]
    [InlineData(230_000, 2, 2, 3_000)]
    public void RandomChangesKeepTheTableReadableAndTheFileConsistent(int initialRows, int seed, int cycles, int opsPerCycle)
    {
        string path = NdbEditorTests.Build(1);
        try
        {
            var rnd = new Random(seed);
            int baselineSoft = NdbChecker.Check(File.ReadAllBytes(path)).Soft.Count;   // the builder leaves a counter one behind
            var model = new Dictionary<uint, (int, byte[]?)>();
            for (int i = 0; i < initialRows; i++)
            {
                model[(uint)((i + 1) * 2)] = (i, Text(i));
            }

            using (NdbEditor ed = NdbEditor.Open(path))
            {
            Nid nid = Host(ed, model);
            ed.Commit();
            Verify(ed, nid, model);
            uint nextId = (uint)(initialRows * 2) + 100;
            for (int c = 0; c < cycles; c++)
            {
                using TableContextEditor? t = TableContextEditor.TryOpen(ed, nid);
                Assert.NotNull(t);
                Assert.Equal(model.Count, t.RowCount);
                var keys = model.Keys.ToList();
                for (int i = 0; i < opsPerCycle; i++)
                {
                    int what = rnd.Next(10);
                    if (what < 4 && model.Count > 5)
                    {
                        uint id = keys[rnd.Next(keys.Count)];
                        t.Remove(id);
                        model.Remove(id);
                    }
                    else if (what < 6 && model.Count > 5)
                    {
                        uint id = keys[rnd.Next(keys.Count)];
                        if (model.ContainsKey(id))
                        {
                            int v = rnd.Next();
                            t.Upsert(id, [Int(v), Text(v)]);
                            model[id] = (v, Text(v));
                        }
                    }
                    else
                    {
                        // New rows land between existing keys (odd IDs) and past the end.
                        uint id = what < 9 ? (uint)(rnd.Next(initialRows * 2) | 1) : nextId++;
                        int v = rnd.Next();
                        t.Upsert(id, [Int(v), Text(v)]);
                        model[id] = (v, Text(v));
                    }
                }

                Assert.True(t.Apply());
                ed.Commit();
                Verify(ed, nid, model);
            }
            }

            NdbChecker.Report r = NdbChecker.Check(File.ReadAllBytes(path));
            Assert.True(r.Errors.Count == 0, string.Join("; ", r.Errors.Take(5)));
            Assert.True(r.Soft.Count == baselineSoft, string.Join("; ", r.Soft.Take(5)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
