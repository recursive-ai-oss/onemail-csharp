using System.Text.Json;
using RecursiveAI.OneMail.Cli;
using RecursiveAI.OneMail.Tests.Stores;

namespace RecursiveAI.OneMail.Tests.Cli;

public sealed class RecoverCommandTests
{
    private static string Temp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onemail-recover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void RecoversDeletedDbxMessagesAndWritesTheReport()
    {
        string dir = Temp();
        try
        {
            var msgs = Enumerable.Range(0, 5).Select(i => new DbxTestMessage
            {
                Text = System.Text.Encoding.ASCII.GetBytes("From: a@example.com\r\nSubject: m" + i + "\r\n\r\n" + new string('x', 900)),
                Subject = "m" + i,
            }).ToList();
            string input = Path.Combine(dir, "Inbox.dbx");
            File.WriteAllBytes(input, DbxTestBuilder.MessageDatabase(msgs, new DbxBuild { Deleted = new HashSet<int> { 1 }, Unlinked = new HashSet<int> { 3 } }));
            string before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(input)));
            string output = Path.Combine(dir, "out");
            StringWriter o = new(), e = new();
            Assert.Equal(0, RecoverCommand.Run([input, output], o, e));
            Assert.Equal(2, Directory.GetFiles(output, "*.eml", SearchOption.AllDirectories).Length);
            using JsonDocument report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "recovery-report.json")));
            Assert.Equal("dbx", report.RootElement.GetProperty("format").GetString());
            var statuses = report.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("status").GetString()).Order().ToArray();
            Assert.Equal(["FreeSpace", "Orphaned"], statuses);
            Assert.All(report.RootElement.GetProperty("items").EnumerateArray(), i => Assert.True(i.GetProperty("ranges").GetArrayLength() > 0));
            // The input is untouched, and an output inside the input is refused.
            Assert.Equal(before, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(input))));
            Assert.Equal(2, RecoverCommand.Run([dir, Path.Combine(dir, "inside")], new StringWriter(), new StringWriter()));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReportsUsageAndMissingInput()
    {
        Assert.Equal(2, RecoverCommand.Run([], new StringWriter(), new StringWriter()));
        Assert.Equal(1, RecoverCommand.Run(["/no/such/file.olm", Path.Combine(Path.GetTempPath(), "never")], new StringWriter(), new StringWriter()));
        Assert.Equal(2, RecoverCommand.Run(["--bogus", "a", "b"], new StringWriter(), new StringWriter()));
    }
}
