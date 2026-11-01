using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Pst.Messaging;
using RecursiveAI.OneMail.Pst.Ndb;

namespace RecursiveAI.OneMail.Durability;

/// <summary>The files the campaigns start from.</summary>
internal static class Bases
{
    public static readonly string[] Names = ["ours", "tika1", "tika2"];

    /// <summary>The bytes of a base file: one the builder writes, or one of the Outlook-written Tika test files.</summary>
    public static byte[] Load(string name, string corpus)
    {
        switch (name)
        {
            case "ours":
                string path = Path.Combine(Path.GetTempPath(), "durability-base-" + Guid.NewGuid().ToString("N") + ".pst");
                try
                {
                    using (PstBuilder b = PstBuilder.Create(path, new PstBuilderOptions { Timestamp = Scenarios.Stamp, StoreId = Guid.Parse("11111111-2222-3333-4444-555555555555") }))
                    {
                        for (int f = 0; f < 3; f++)
                        {
                            PstFolderWriter folder = b.IpmSubtree.CreateFolder("Folder " + f);
                            for (int i = 0; i < 40; i++)
                            {
                                folder.AddMessage(Scenarios.Message($"base {f}/{i}", 2, i % 4 == 0 ? 1 : 0, 3000 + (i * 100), i % 3 == 0, new string('b', 200 + (i * 13))));
                            }
                        }

                        b.Complete();
                    }

                    return File.ReadAllBytes(path);
                }
                finally
                {
                    File.Delete(path);
                }

            case "tika1":
                return File.ReadAllBytes(Find(corpus, "testPST.pst"));
            case "tika2":
                return File.ReadAllBytes(Find(corpus, "testPST_variousBodyTypes.pst"));
            default:
                throw new ArgumentException("unknown base " + name);
        }
    }

    private static string Find(string corpus, string name)
    {
        foreach (string c in new[] { Path.Combine(corpus, "pst", "tika", name), Path.Combine(corpus, "pst", name) })
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        throw new FileNotFoundException("corpus file " + name + " is not under " + corpus);
    }
}

/// <summary>Write scenarios. Each is deterministic: the same file and seed give the same operations and the same NIDs.</summary>
internal static class Scenarios
{
    public static readonly DateTimeOffset Stamp = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

    public static readonly string[] Names = ["add", "delete", "folders", "grow", "split", "named", "mixed"];

    private static readonly Guid NamedSet = Guid.Parse("00020329-0000-0000-c000-000000000046");

    public static PstWriteOptions Options => new() { Timestamp = Stamp, AutoCommitRows = 0 };

    public static MapiMessage Message(string subject, int recipients, int attachments, int attachmentBytes, bool read, string body, string? named = null)
    {
        var m = new MapiMessage();
        m.Properties.Add(Str(0x001A, "IPM.Note"));
        m.Properties.Add(Str(0x0037, subject));
        m.Properties.Add(MapiProperty.Create(new MapiTag(0x0039, MapiType.Time), BitConverter.GetBytes(133_500_000_000_000_000L)));
        m.Properties.Add(Str(0x0C1A, "Sender"));
        m.Properties.Add(Str(0x1000, body));
        m.Properties.Add(MapiProperty.Create(new MapiTag(0x0E07, MapiType.Integer32), BitConverter.GetBytes(read ? 1 : 0)));
        if (named is not null)
        {
            m.Properties.Add(MapiProperty.Create(new MapiTag(0x8000, MapiType.UnicodeString), Encoding.Unicode.GetBytes("value " + named), new MapiNamedProperty(NamedSet, named)));
        }

        for (int i = 0; i < recipients; i++)
        {
            var r = new MapiRecipient(m);
            r.Properties.Add(MapiProperty.Create(new MapiTag(0x0C15, MapiType.Integer32), BitConverter.GetBytes(1 + (i % 3))));
            r.Properties.Add(Str(0x3001, "Person " + i));
            r.Properties.Add(Str(0x3002, "SMTP"));
            r.Properties.Add(Str(0x3003, $"p{i}@example.com"));
            m.AddRecipient(r);
        }

        for (int i = 0; i < attachments; i++)
        {
            var a = new MapiAttachment(m);
            a.Properties.Add(MapiProperty.Create(new MapiTag(0x3705, MapiType.Integer32), BitConverter.GetBytes(1)));
            a.Properties.Add(Str(0x3707, $"file{i}.bin"));
            byte[] data = new byte[attachmentBytes + i];
            for (int j = 0; j < data.Length; j++)
            {
                data[j] = (byte)((j * 7) ^ (j >> 8));
            }

            a.Properties.Add(MapiProperty.Create(new MapiTag(0x3701, MapiType.Binary), data));
            m.AddAttachment(a);
        }

        return m;
    }

    private static MapiProperty Str(ushort id, string v) => MapiProperty.Create(new MapiTag(id, MapiType.UnicodeString), Encoding.Unicode.GetBytes(v));

    /// <summary>The ordinary folders (not search folders, not the root), the fullest first.</summary>
    public static List<PstFolder> Folders(PstStore s) =>
        s.EnumerateFolders().Where(f => !f.IsSearchFolder && f.ParentFolder is not null).OrderByDescending(f => f.ContentCount ?? 0).ThenBy(f => f.Nid.Value).ToList();

    public static void Run(string name, PstWritableStore w, Action commit)
    {
        List<PstFolder> folders = Folders(w.View);
        PstFolder f = folders[0];
        Nid parent = f.ParentFolder!.Nid;
        Nid fid = f.Nid;
        switch (name)
        {
            case "add":
                for (int i = 0; i < 30; i++)
                {
                    w.AddMessage(fid, Message($"added {i}", 2, i % 4 == 0 ? 2 : 0, 4000 + (i * 500), i % 2 == 0, new string('a', 100 + (i * 31))));
                }

                commit();
                break;
            case "delete":
                {
                    var ids = f.EnumerateMessageIds().ToList();
                    for (int i = 0; i < ids.Count; i += 2)
                    {
                        w.DeleteMessage(ids[i]);
                    }

                    commit();
                    break;
                }

            case "folders":
                {
                    Nid a = w.CreateFolder(parent, "Scenario A");
                    Nid b = w.CreateFolder(a, "Scenario B", "IPF.Appointment");
                    for (int i = 0; i < 5; i++)
                    {
                        w.AddMessage(b, Message($"in b {i}", 1, 0, 0, i > 1, "b"));
                    }

                    commit();
                    w.RenameFolder(a, "Scenario A renamed");
                    var ids = w.View.EnumerateFolders().First(x => x.Nid == fid).EnumerateMessageIds().ToList();
                    if (ids.Count > 0)
                    {
                        w.MoveMessage(ids[0], b);
                    }

                    w.MoveFolder(fid, a);
                    commit();
                    w.DeleteFolder(b);
                    commit();
                    break;
                }

            case "grow":
                {
                    long free = (long)w.Database.Committed.Header.Root.AMapFree;
                    long added = 0;
                    for (int i = 0; added < free + 400_000 && i < 400; i++)
                    {
                        w.AddMessage(fid, Message($"big {i}", 1, 1, 200_000, true, "big"));
                        added += 200_100;
                    }

                    commit();
                    break;
                }

            case "split":
                for (int i = 0; i < 400; i++)
                {
                    w.AddMessage(fid, Message($"bulk {i}", 0, 0, 0, i % 3 != 0, "x"));
                }

                commit();
                break;
            case "named":
                w.AddMessage(fid, Message("named 1", 1, 0, 0, true, "n", "Alpha"));
                commit();
                w.AddMessage(fid, Message("named 2", 1, 0, 0, true, "n", "Beta"));
                w.AddMessage(fid, Message("named 3", 1, 0, 0, true, "n", "Alpha"));
                commit();
                break;
            case "mixed":
                {
                    var ids = f.EnumerateMessageIds().ToList();
                    for (int i = 0; i < 10; i++)
                    {
                        w.AddMessage(fid, Message($"mixed {i}", 1, i % 3 == 0 ? 1 : 0, 6000, true, "m"));
                    }

                    if (ids.Count > 2)
                    {
                        w.ReplaceMessage(ids[1], Message("replaced", 2, 1, 9000, false, "r"));
                    }

                    commit();
                    ids = w.View.EnumerateFolders().First(x => x.Nid == fid).EnumerateMessageIds().ToList();
                    for (int i = 0; i < ids.Count; i += 3)
                    {
                        w.DeleteMessage(ids[i]);
                    }

                    commit();
                    break;
                }

            default:
                throw new ArgumentException("unknown scenario " + name);
        }
    }
}
