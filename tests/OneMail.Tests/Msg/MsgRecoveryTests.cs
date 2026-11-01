using System.Buffers.Binary;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;

namespace RecursiveAI.OneMail.Tests.Msg;

/// <summary>Opening a damaged MSG with <see cref="MsgReaderOptions.Recovery"/>: strict stays the default, nothing throws, every loss is a defect.</summary>
public class MsgRecoveryTests
{
    private static readonly MsgReaderOptions Recover = new() { Recovery = true };

    private static byte[] Fixture(string name) => MsgFixtures.All()[name];

    // MS-CFB 2.3: the FAT's first sector is the first DIFAT entry in the header (offset 0x4C); each FAT slot is 4 bytes.
    private static byte[] BreakFatSlot(byte[] src, int sector, uint value)
    {
        int ss = 1 << BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(0x1E));
        uint fat0 = BinaryPrimitives.ReadUInt32LittleEndian(src.AsSpan(0x4C));
        byte[] copy = (byte[])src.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(512 + (int)(fat0 * (long)ss) + (sector * 4)), value);
        return copy;
    }

    private static Dictionary<string, int> Properties(MsgFile f)
    {
        var d = new Dictionary<string, int>();
        void Visit(MapiObject o)
        {
            foreach (MapiProperty p in o.Properties)
            {
                d[((IMsgItem)o).Layout.Path + "|" + p.Tag.ToHex()] = p.ValueCount;
                for (int i = 0; i < p.ValueCount; i++)
                {
                    _ = p.GetBytes(i);
                }
            }

            if (o is MapiMessage m)
            {
                foreach (MapiRecipient r in m.Recipients)
                {
                    Visit(r);
                }

                foreach (MapiAttachment a in m.Attachments)
                {
                    Visit(a);
                    if (a.EmbeddedMessage is { } inner)
                    {
                        Visit(inner);
                    }
                }
            }
        }

        f.ReadEverything();
        Visit(f.Message);
        return d;
    }

    [Fact]
    public void AnIntactFileReadsTheSameWithAndWithoutRecovery()
    {
        byte[] data = Fixture("recipients-attachments.msg");
        Assert.True(MsgFile.TryOpen(data, out MsgFile? strict, out _));
        Assert.True(MsgFile.TryOpen(data, out MsgFile? rec, out _, Recover));
        using (strict)
        using (rec)
        {
            Assert.False(rec.IsRecovered);
            Assert.Equal(Properties(strict), Properties(rec));
            Assert.Equal(strict.Defects.Count, rec.Defects.Count);
        }
    }

    [Fact]
    public void StrictModeStillRefusesABrokenChain()
    {
        byte[] bad = BreakFatSlot(Fixture("recipients-attachments.msg"), 3, 0xFFFFFFFE);
        Assert.False(MsgFile.TryOpen(bad, out _, out IReadOnlyList<MapiDefect> defects));
        Assert.Equal(MapiDefectKind.CompoundFileInvalid, Assert.Single(defects).Kind);
    }

    [Fact]
    public void ACutStreamKeepsTheBytesBeforeTheBreakAndSaysSo()
    {
        byte[] data = Fixture("recipients-attachments.msg");
        int found = 0;
        int sectors = Math.Min(128, (data.Length - 512) / 512);
        for (int k = 0; k < sectors; k++)
        {
            byte[] bad = BreakFatSlot(data, k, 0xFFFFFFFE);
            if (!MsgFile.TryOpen(bad, out MsgFile? f, out _, Recover))
            {
                continue;
            }

            using (f)
            {
                Properties(f);
                if (f.IsRecovered)
                {
                    Assert.Contains(f.Defects, d => d.Kind == MapiDefectKind.CompoundFileRecovered);
                    Assert.Contains(f.Defects, d => d.Kind == MapiDefectKind.CompoundFileDamage);
                }

                if (f.Defects.Any(d => d.Kind == MapiDefectKind.StreamDamaged))
                {
                    found++;
                    MapiDefect s = f.Defects.First(d => d.Kind == MapiDefectKind.StreamDamaged);
                    Assert.StartsWith("/", s.Location);
                    Assert.Contains("declares", s.Detail);
                }
            }
        }

        Assert.True(found > 0, "no cut stream was reported");
    }

    [Theory]
    [InlineData("recipients-attachments.msg")]
    [InlineData("all-types.msg")]
    [InlineData("recipients-embedded.msg")]
    public void DamageAnywhereNeverThrowsAndAnyLossIsFlagged(string name)
    {
        byte[] data = Fixture(name);
        Assert.True(MsgFile.TryOpen(data, out MsgFile? original, out _));
        Dictionary<string, int> reference;
        using (original)
        {
            reference = Properties(original);
        }

        int sectors = Math.Min(128, (data.Length - 512) / 512);
        for (int k = 0; k < sectors; k++)
        {
            foreach (uint v in new uint[] { 0xFFFFFFFF, 0xFFFFFFFE, (uint)k })
            {
                byte[] bad = BreakFatSlot(data, k, v);
                if (!MsgFile.TryOpen(bad, out MsgFile? f, out IReadOnlyList<MapiDefect> why, Recover))
                {
                    Assert.NotEmpty(why);
                    continue;
                }

                using (f)
                {
                    Dictionary<string, int> got = Properties(f);
                    bool lost = reference.Keys.Any(key => !got.ContainsKey(key));
                    if (lost)
                    {
                        Assert.True(f.IsRecovered, $"{name}: slot {k} = {v:X}: properties lost without a recovery defect");
                    }
                }
            }
        }
    }

    [Fact]
    public void ATruncatedFileWithItsTablesIntactOpensAndTheMissingTailIsReported()
    {
        // The last byte of the file is cut: the final sector is only partly there. Recovery uses the bytes that exist.
        byte[] data = Fixture("recipients-attachments.msg");
        byte[] cut = data.AsSpan(0, data.Length - 1).ToArray();
        Assert.False(MsgFile.TryOpen(cut, out _, out _));
        Assert.True(MsgFile.TryOpen(cut, out MsgFile? f, out _, Recover));
        using (f)
        {
            Assert.True(f.IsRecovered);
            Assert.Contains(f.Defects, d => d.Kind == MapiDefectKind.CompoundFileDamage);
        }
    }

    [Fact]
    public void GarbageIsStillRefusedWithRecoveryOn()
    {
        Assert.False(MsgFile.TryOpen(new byte[2000], out _, out IReadOnlyList<MapiDefect> defects, Recover));
        Assert.Equal(MapiDefectKind.CompoundFileInvalid, defects[0].Kind);
    }
}
