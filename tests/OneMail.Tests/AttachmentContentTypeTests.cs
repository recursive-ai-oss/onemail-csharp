using System.Text;

namespace RecursiveAI.OneMail.Tests;

public class AttachmentContentTypeTests
{
    private static IMailAttachment Attachment(string type, string name)
    {
        string eml = "From: a@example.com\r\nTo: b@example.com\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=b\r\n\r\n" +
            "--b\r\nContent-Type: text/plain\r\n\r\nhi\r\n" +
            "--b\r\nContent-Type: " + type + "; name=\"" + name + "\"\r\nContent-Disposition: attachment; filename=\"" + name + "\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n\r\nAAEC\r\n--b--\r\n";
        return Assert.Single(Mail.Parse(Encoding.ASCII.GetBytes(eml)).Attachments);
    }

    [Theory]
    [InlineData("report.PDF", "application/pdf")]
    [InlineData("a.b.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData(@"C:\x\photo.JPeG", "image/jpeg")]
    [InlineData("invite.ics", "text/calendar")]
    public void GenericTypeWithKnownExtensionIsInferred(string name, string expected)
    {
        IMailAttachment a = Attachment("application/octet-stream", name);
        Assert.Equal(expected, a.ContentType);
        Assert.True(a.ContentTypeInferred);
    }

    [Theory]
    [InlineData("data.unknownext")]
    [InlineData("noextension")]
    [InlineData("trailingdot.")]
    public void UnknownExtensionStaysOctetStream(string name)
    {
        IMailAttachment a = Attachment("application/octet-stream", name);
        Assert.Equal("application/octet-stream", a.ContentType);
        Assert.False(a.ContentTypeInferred);
    }

    [Fact]
    public void StoredSpecificTypeWins()
    {
        IMailAttachment a = Attachment("image/png", "report.pdf");
        Assert.Equal("image/png", a.ContentType);
        Assert.False(a.ContentTypeInferred);
    }
}
