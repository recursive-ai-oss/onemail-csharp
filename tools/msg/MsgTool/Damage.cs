using System.Buffers.Binary;
using System.Security.Cryptography;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Msg;

namespace MsgTool;

// MsgTool damage <file|dir>...
// For each message, damages the compound file in memory in several ways (truncation, a broken FAT chain, a broken directory link, a lost
// mini-stream, a lost directory start) and opens every variant in strict mode and with recovery. Per variant it prints how the strict open
// went, and for the recovered open: the properties found against the intact original (same, changed, missing), exceptions (must be 0) and
// the recovery defects. A variant that lost anything without being flagged as recovered counts as unexplained (must be 0).
// MS-CFB 2.2 (header), 2.3 (FAT), 2.6.1 (directory entry) give the offsets used.
internal static class Damage
{
    private const uint FreeSect = 0xFFFFFFFF;
    private const uint EndOfChain = 0xFFFFFFFE;

    public static int Run(string[] files)
    {
        Console.WriteLine("file\tvariant\tstrict\trecovered\tflagged\toriginal_props\tsame\tchanged\tmissing\tobjects_lost\texceptions\tdamage_defects\tstream_defects\tunexplained");
        int exceptions = 0, unexplained = 0, variants = 0, strictOpen = 0, recoveredOpen = 0, failedBoth = 0;
        long totalProps = 0, totalSame = 0;
        foreach (string f in files)
        {
            byte[] original = File.ReadAllBytes(f);
            Dictionary<string, string>? reference = Flatten(original, strict: true, out int refObjects, out _, out _);
            if (reference is null)
            {
                Console.WriteLine($"{Path.GetFileName(f)}\t(original)\tREFUSED");
                continue;
            }

            foreach ((string name, byte[]? data) in Variants(original))
            {
                if (data is null)
                {
                    continue;
                }

                variants++;
                Dictionary<string, string>? strict = Flatten(data, strict: true, out _, out _, out int strictEx);
                Dictionary<string, string>? rec = Flatten(data, strict: false, out int objects, out MsgFile? info, out int recEx);
                exceptions += strictEx + recEx;
                int same = 0, changed = 0, missing = 0;
                int damage = 0, streams = 0;
                bool flagged = false;
                if (rec is not null)
                {
                    foreach ((string k, string h) in reference)
                    {
                        if (!rec.TryGetValue(k, out string? h2))
                        {
                            missing++;
                        }
                        else if (h2 == h)
                        {
                            same++;
                        }
                        else
                        {
                            changed++;
                        }
                    }
                }

                if (info is not null)
                {
                    flagged = info.IsRecovered;
                    damage = info.Defects.Count(d => d.Kind == MapiDefectKind.CompoundFileDamage);
                    streams = info.Defects.Count(d => d.Kind == MapiDefectKind.StreamDamaged);
                    info.Dispose();
                }

                int lost = Math.Max(0, refObjects - objects);
                bool loss = rec is null ? false : missing + changed + lost > 0;
                int bad = loss && !flagged ? 1 : 0;
                // Strict success means the file was not damaged in a way the checks see; recovery must then read it the same way.
                if (strict is not null && rec is not null && (strict.Count != rec.Count || flagged))
                {
                    bad = 1;
                }

                unexplained += bad;
                if (strict is not null)
                {
                    strictOpen++;
                }

                if (rec is not null)
                {
                    recoveredOpen++;
                    totalProps += reference.Count;
                    totalSame += same;
                }
                else if (strict is null)
                {
                    failedBoth++;
                }

                Console.WriteLine($"{Path.GetFileName(f)}\t{name}\t{(strict is null ? "refused" : "opens")}\t{(rec is null ? "refused" : "opens")}\t{flagged}\t{reference.Count}\t{same}\t{changed}\t{missing}\t{lost}\t{strictEx + recEx}\t{damage}\t{streams}\t{bad}");
            }
        }

        Console.WriteLine($"# variants {variants}; strict opens {strictOpen}; recovery opens {recoveredOpen}; refused by both {failedBoth}; properties same {totalSame} of {totalProps} (in files that opened); exceptions {exceptions}; unexplained {unexplained}");
        return exceptions + unexplained == 0 ? 0 : 1;
    }

    // Every property of every object, keyed by object path and tag with the index of equal tags, valued by a hash of its bytes.
    private static Dictionary<string, string>? Flatten(byte[] data, bool strict, out int objects, out MsgFile? file, out int exceptions)
    {
        objects = 0;
        file = null;
        exceptions = 0;
        MsgFile? f = null;
        try
        {
            var options = new MsgReaderOptions { Recovery = !strict };
            if (!MsgFile.TryOpen(data, out f, out _, options))
            {
                return null;
            }

            f.ReadEverything();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            int count = 0;
            Walk(f.Message, result, ref count);
            objects = count;
            file = f;
            return result;
        }
        catch (Exception)
        {
            exceptions++;
            f?.Dispose();
            return null;
        }
    }

    private static void Walk(MapiObject obj, Dictionary<string, string> into, ref int objects)
    {
        objects++;
        string path = ((IMsgItem)obj).Layout.Path;
        var seen = new Dictionary<string, int>();
        foreach (MapiProperty p in obj.Properties)
        {
            string baseKey = path + "|" + p.Tag.ToHex();
            seen.TryGetValue(baseKey, out int n);
            seen[baseKey] = n + 1;
            var h = SHA256.Create();
            using (h)
            {
                for (int i = 0; i < p.ValueCount; i++)
                {
                    byte[] b = p.GetBytes(i).ToArray();
                    h.TransformBlock(BitConverter.GetBytes(b.Length), 0, 4, null, 0);
                    h.TransformBlock(b, 0, b.Length, null, 0);
                }

                h.TransformFinalBlock([], 0, 0);
                into[baseKey + "#" + n] = Convert.ToHexString(h.Hash!)[..16];
            }
        }

        if (obj is MapiMessage m)
        {
            foreach (MapiRecipient r in m.Recipients)
            {
                Walk(r, into, ref objects);
            }

            foreach (MapiAttachment a in m.Attachments)
            {
                Walk(a, into, ref objects);
                if (a.EmbeddedMessage is { } inner)
                {
                    Walk(inner, into, ref objects);
                }
            }
        }
    }

    private static IEnumerable<(string Name, byte[]? Data)> Variants(byte[] src)
    {
        int ss = 1 << BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(0x1E));
        int sectors = (src.Length - 512) / ss;
        uint dirStart = BinaryPrimitives.ReadUInt32LittleEndian(src.AsSpan(0x30));
        uint fat0 = BinaryPrimitives.ReadUInt32LittleEndian(src.AsSpan(0x4C));
        long fatOffset = 512L + (fat0 * (long)ss);
        long dirOffset = 512L + (dirStart * (long)ss);

        foreach (int pct in new[] { 10, 25, 50, 75, 90 })
        {
            yield return ($"truncate-{pct}pct", src.AsSpan(0, (int)(src.LongLength * pct / 100)).ToArray());
        }

        yield return ("truncate-last-byte", src.AsSpan(0, src.Length - 1).ToArray());
        yield return ("truncate-last-sector", src.AsSpan(0, src.Length - ss).ToArray());

        foreach ((string label, int num) in new[] { ("third", 1), ("half", 2), ("two-thirds", 3) })
        {
            int k = num == 1 ? sectors / 3 : num == 2 ? sectors / 2 : sectors * 2 / 3;
            long at = fatOffset + (k * 4L);
            if (k >= ss / 4 || at + 4 > src.Length)
            {
                yield return ($"fat-free-{label}", null);
                continue;
            }

            yield return ($"fat-free-{label}", Patch(src, at, FreeSect));
            yield return ($"fat-end-{label}", Patch(src, at, EndOfChain));
            yield return ($"fat-loop-{label}", Patch(src, at, (uint)k));
        }

        // Directory entry 1: left sibling out of range; entry 0 (root): child out of range.
        yield return ("dir-link-sibling", Patch(src, dirOffset + 128 + 0x44, 0x00FFFFF0));
        yield return ("dir-link-child", Patch(src, dirOffset + 0x4C, 0x00FFFFF0));
        yield return ("dir-link-cycle", Patch(src, dirOffset + 128 + 0x48, 1));
        yield return ("dir-start-lost", Patch(src, 0x30, 0x00FFFF00));
        yield return ("dir-entry-type", PatchByte(src, dirOffset + 128 + 0x42, 0x07));
        yield return ("minifat-lost", Patch(src, 0x3C, EndOfChain));
        yield return ("ministream-lost", Patch(src, dirOffset + 0x74, 0x00FFFF00));
        yield return ("difat-lost", Patch(src, 0x4C, FreeSect));
    }

    private static byte[]? Patch(byte[] src, long offset, uint value)
    {
        if (offset + 4 > src.Length)
        {
            return null;
        }

        byte[] copy = (byte[])src.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan((int)offset), value);
        return copy;
    }

    private static byte[]? PatchByte(byte[] src, long offset, byte value)
    {
        if (offset >= src.Length)
        {
            return null;
        }

        byte[] copy = (byte[])src.Clone();
        copy[offset] = value;
        return copy;
    }
}
