using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;

namespace RecursiveAI.OneMail.PstOracle;

/// <summary>
/// Copies one message of a store into small stores of its own, each without one of its properties, so a property that makes the repair tool
/// reject the copy can be found by scanning them all.
///   PstOracle msgvariants &lt;file&gt; &lt;subject&gt; &lt;out dir&gt;
/// </summary>
internal static class MessageVariants
{
    public static int Run(string path, string subject, string dir)
    {
        if (!PstStore.TryOpen(path, out PstStore? store, out _) || store is null)
        {
            Console.WriteLine("open failed");
            return 4;
        }

        Directory.CreateDirectory(dir);
        using (store)
        {
            PstMessage? source = store.EnumerateFolders().SelectMany(f => f.EnumerateMessages()).FirstOrDefault(m => m.Subject is { } s && s.EndsWith(subject, StringComparison.Ordinal));
            if (source is null)
            {
                Console.WriteLine("no such message");
                return 1;
            }

            foreach (MapiProperty p in source.Properties.Where(p => p.Id >= 0x8000))
            {
                Console.WriteLine($"named {p.Tag} {p.Name} {Convert.ToHexString(p.GetBytes().Span[..Math.Min(16, p.GetBytes().Length)])}");
            }

            var ids = source.Properties.Select(p => p.Id).Distinct().ToList();
            Write(Path.Combine(dir, "v-full.pst"), source, null);
            foreach (ushort id in ids)
            {
                Write(Path.Combine(dir, $"v-no{id:x4}.pst"), source, id);
            }

            Write(Path.Combine(dir, "v-nonamed.pst"), source, 0xFFFF);
            Console.WriteLine($"{ids.Count + 2} variants");
        }

        return 0;
    }

    /// <summary>Copies the first 1..n messages of a folder into stores of their own: <c>PstOracle foldervariants &lt;file&gt; &lt;folder name&gt; &lt;out dir&gt;</c>.</summary>
    public static int Folder(string path, string folderName, string dir)
    {
        if (!PstStore.TryOpen(path, out PstStore? store, out _) || store is null)
        {
            Console.WriteLine("open failed");
            return 4;
        }

        Directory.CreateDirectory(dir);
        using (store)
        {
            PstFolder folder = store.EnumerateFolders().First(f => f.Name == folderName);
            PstMessage[] all = folder.EnumerateMessages().ToArray();
            for (int n = 1; n <= all.Length; n++)
            {
                string file = Path.Combine(dir, $"f-first{n}.pst");
                File.Delete(file);
                using PstBuilder b = PstBuilder.Create(file, new PstBuilderOptions { Timestamp = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero) });
                PstFolderWriter w = b.IpmSubtree.CreateFolder("Folder");
                for (int i = 0; i < n; i++)
                {
                    w.AddMessage(all[i]);
                }

                b.Complete();
            }

            Console.WriteLine($"{all.Length} variants");
        }

        return 0;
    }

    private static void Write(string path, PstMessage source, ushort? drop)
    {
        File.Delete(path);
        using PstBuilder b = PstBuilder.Create(path, new PstBuilderOptions { Timestamp = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero) });
        var m = new MapiMessage();
        foreach (MapiProperty p in source.Properties)
        {
            if (drop == p.Id || (drop == 0xFFFF && p.Id >= 0x8000))
            {
                continue;
            }

            m.Properties.Add(p);
        }

        foreach (MapiRecipient r in source.Recipients)
        {
            m.AddRecipient(r);
        }

        b.IpmSubtree.CreateFolder("Folder").AddMessage(m);
        b.Complete();
    }
}
