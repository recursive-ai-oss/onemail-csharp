using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RecursiveAI.OneMail.Tests.Pst;

/// <summary>Generated PST/OST contents shared by the tests and the corpus oracle tool. Everything is deterministic.</summary>
public static class PstFixtures
{
    public static readonly Guid Set1 = new("11111111-2222-3333-4444-555555555555");

    /// <summary>Deterministic bytes (xorshift), so attachment content can be checked by value or hash.</summary>
    public static byte[] Data(int n, int seed)
    {
        var b = new byte[n];
        uint x = 2463534242u ^ (uint)(seed * 2654435761u);
        for (int i = 0; i < n; i++)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            b[i] = (byte)x;
        }

        return b;
    }

    /// <summary>A small tree that uses every part of the messaging layer once.</summary>
    public static FxFolder Sample()
    {
        var embedded = new FxMessage
        {
            Subject = "Forwarded inner",
            Body = "inner body",
            Recipients = [new FxRecipient { Name = "Inner Rcpt", Smtp = "inner@example.com" }],
            Attachments = [new FxAttachment { Name = "deep.txt", Data = Data(300, 3) }],
        };
        return new FxFolder
        {
            Name = "Top of Test Store",
            Children =
            [
                new FxFolder
                {
                    Name = "Inbox",
                    Messages =
                    [
                        new FxMessage
                        {
                            Subject = "Hello world",
                            Body = "Plain body",
                            Html = "<p>Hello <b>world</b></p>",
                            Recipients =
                            [
                                new FxRecipient { Name = "Ann", Smtp = "ann@example.com", Type = 1 },
                                new FxRecipient { Name = "Bob", Smtp = "bob@example.com", Type = 2 },
                                new FxRecipient { Name = "Cy", Smtp = "cy@example.com", Type = 3 },
                            ],
                            NamedNumbers = [(Set1, 0x8101, 42)],
                            NamedStrings = [(Set1, "x-custom-name", "custom value")],
                            Attachments =
                            [
                                new FxAttachment { Name = "small.bin", Data = Data(100, 1) },
                                new FxAttachment { Name = "large.bin", Data = Data(70_000, 2) },
                                new FxAttachment { Name = "inline.png", Data = Data(50, 4), ContentId = "<img1@example.com>" },
                                new FxAttachment { Name = "inner.msg", Embedded = embedded },
                            ],
                        },
                        new FxMessage { Subject = "Second", Body = new string('x', 5000), Html = "<p>" + new string('y', 6000) + "</p>" },
                    ],
                    Associated = [new FxMessage { Subject = "Rule", MessageClass = "IPM.Rule.Message" }],
                    Children = [new FxFolder { Name = "Deep", Messages = [new FxMessage { Subject = "In deep" }] }],
                },
                new FxFolder { Name = "Empty" },
                new FxFolder { Name = "Calendar", Messages = [new FxMessage { Subject = "Meeting", MessageClass = "IPM.Appointment" }] },
            ],
        };
    }

    /// <summary>
    /// A larger tree: a chain of nested folders, folders with hundreds of messages, messages with many recipients, large attachments
    /// kept in subnodes, embedded messages three deep and named properties.
    /// </summary>
    public static FxFolder Big(int messagesPerFolder = 200, int largeSize = 300_000)
    {
        FxMessage Nested(int depth) => new()
        {
            Subject = "Level " + depth,
            Body = "body of level " + depth,
            Recipients = [new FxRecipient { Name = "R" + depth, Smtp = $"r{depth}@example.com" }],
            Attachments = depth < 3
                ? [new FxAttachment { Name = $"level{depth}.msg", Embedded = Nested(depth + 1) }, new FxAttachment { Name = $"data{depth}.bin", Data = Data(2000 + depth, depth) }]
                : [new FxAttachment { Name = "leaf.bin", Data = Data(9000, 99) }],
        };

        FxFolder chain = new() { Name = "leaf", Messages = [new FxMessage { Subject = "bottom", Body = "at the bottom" }] };
        for (int i = 0; i < 12; i++)
        {
            chain = new FxFolder { Name = "level " + i, Children = [chain], Messages = [new FxMessage { Subject = "in level " + i }] };
        }

        var many = new FxFolder
        {
            Name = "Many",
            Messages =
            [
                .. Enumerable.Range(0, messagesPerFolder).Select(i => new FxMessage
                {
                    Subject = "Message " + i,
                    Body = "Body of message " + i + " " + new string((char)('a' + (i % 26)), i % 40),
                    SubmitFileTime = 133_400_000_000_000_000L + (i * 10_000_000L),
                    Recipients = [.. Enumerable.Range(0, i == 7 ? 300 : 1 + (i % 7)).Select(k => new FxRecipient { Name = $"Person {i}-{k}", Smtp = $"p{i}.{k}@example.com", Type = 1 + (k % 3) })],
                    Attachments = i % 10 == 0 ? [new FxAttachment { Name = $"file{i}.bin", Data = Data(1000 + i, i) }] : [],
                    NamedNumbers = i % 5 == 0 ? [(Set1, (uint)(0x8200 + (i % 3)), i)] : [],
                }),
            ],
        };
        var rich = new FxFolder
        {
            Name = "Rich",
            Messages =
            [
                new FxMessage
                {
                    Subject = "With large parts",
                    Body = new string('z', Math.Min(40_000, largeSize)),
                    Html = "<html><body>" + new string('h', Math.Min(30_000, largeSize)) + "</body></html>",
                    Attachments = [new FxAttachment { Name = "big1.bin", Data = Data(largeSize, 7) }, new FxAttachment { Name = "big2.bin", Data = Data((largeSize / 2) + 1, 8) }, new FxAttachment { Name = "nested.msg", Embedded = Nested(1) }],
                    NamedStrings = [(Set1, "x-header-one", "one"), (new Guid("22222222-2222-2222-2222-222222222222"), "x-header-two", "two")],
                },
            ],
        };
        return new FxFolder { Name = "Big Store", Children = [chain, many, rich, new FxFolder { Name = "Sent Items", Messages = [new FxMessage { Subject = "sent", MessageClass = "IPM.Note" }] }] };
    }
}
