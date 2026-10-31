using System.Runtime.CompilerServices;
using System.Text;
using RecursiveAI.OneMail.Mime;
using RecursiveAI.OneMail.Mime.Authentication;
using RecursiveAI.OneMail.Mime.Mhtml;
using RecursiveAI.OneMail.Mime.Reading;
using static RecursiveAI.OneMail.Tests.Mime.T;

namespace RecursiveAI.OneMail.Tests.Mime;

public class AuthenticationResultsTests
{
    private static AuthenticationResults P(string v) => AuthenticationResults.Parse(v);

    private static IEnumerable<MimeDefectKind> Kinds(string v) => P(v).Defects.Select(d => d.Kind);

    [Fact]
    public void NoneWithAVersion()
    {
        AuthenticationResults a = P("example.org 1; none");
        Assert.Equal("example.org", a.AuthServId);
        Assert.Equal(1, a.Version);
        Assert.True(a.IsNone);
        Assert.Empty(a.Results);
        Assert.Empty(a.Defects);
    }

    [Fact]
    public void ResultsWithPropertiesAndComments()
    {
        AuthenticationResults a = P("example.com;\r\n          auth=pass (cram-md5) smtp.auth=sender@example.net;\r\n          spf=pass smtp.mailfrom=example.net");
        Assert.Empty(a.Defects);
        Assert.Null(a.Version);
        Assert.Equal(2, a.Results.Count);
        AuthenticationMethodResult auth = a.Results[0];
        Assert.Equal("auth", auth.Method);
        Assert.Equal(AuthenticationResultKind.Pass, auth.Kind);
        Assert.Equal("cram-md5", auth.ResultComment);
        Assert.Equal("sender@example.net", auth.GetProperty("smtp", "auth"));
        Assert.Equal("example.net", a.Results[1].GetProperty("SMTP", "MailFrom"));
    }

    [Fact]
    public void TheCommentHeavyExampleOfRfc8601()
    {
        AuthenticationResults a = P("foo.example.net (foobar) 1 (baz);\r\n    dkim (Because I like it) / 1 (One yay) = (wait for it) fail\r\n      policy (A dot can go here) . (like that) expired\r\n      (this surprised me) = (as I wasn't expecting it) 1362471462");
        Assert.Empty(a.Defects);
        Assert.Equal("foo.example.net", a.AuthServId);
        Assert.Equal(1, a.Version);
        AuthenticationMethodResult r = Assert.Single(a.Results);
        Assert.Equal("dkim", r.Method);
        Assert.Equal(1, r.MethodVersion);
        Assert.Equal(AuthenticationResultKind.Fail, r.Kind);
        AuthenticationProperty p = Assert.Single(r.Properties);
        Assert.Equal(("policy", "expired", "1362471462"), (p.PropertyType, p.Name, p.Value));
    }

    [Fact]
    public void AFieldThatStartsWithAResultHasNoIdentifier()
    {
        AuthenticationResults a = P("spf=pass (sender IP is 192.0.2.1) smtp.mailfrom=example.net; dkim/1=pass header.d=a.example");
        Assert.Equal(string.Empty, a.AuthServId);
        Assert.Equal(["spf", "dkim"], a.Results.Select(r => r.Method));
        Assert.Equal("sender IP is 192.0.2.1", a.Results[0].ResultComment);
        Assert.Equal(1, a.Results[1].MethodVersion);
        MimeDefect d = Assert.Single(a.Defects);
        Assert.Equal(MimeDefectKind.AuthenticationResultsMissingId, d.Kind);
        Assert.Equal(0, d.Offset);
    }

    [Fact]
    public void ReasonAndQuotedValues()
    {
        AuthenticationResults a = P("\"mx host.example\" 1; spf=pass smtp.mailfrom=\"a b@example.net\" reason=\"the \\\"best\\\" reason\"; dkim=fail reason=bad header.d=example.net");
        Assert.Equal("mx host.example", a.AuthServId);
        Assert.Equal("a b@example.net", a.Results[0].GetProperty("smtp", "mailfrom"));
        Assert.Equal("the \"best\" reason", a.Results[0].Reason);
        Assert.Equal("bad", a.Results[1].Reason);
        Assert.Equal("example.net", a.Results[1].GetProperty("header", "d"));
    }

    [Fact]
    public void MethodVersionsAndUnknownResultsAndCase()
    {
        AuthenticationResults a = P("EXAMPLE.com; DKIM/2=Pass; x-vendor=odd");
        Assert.Equal("dkim", a.Results[0].Method);
        Assert.Equal(2, a.Results[0].MethodVersion);
        Assert.Equal("pass", a.Results[0].Result);
        Assert.Equal(AuthenticationResultKind.Other, a.Results[1].Kind);
        Assert.Equal("odd", a.Results[1].Result);
    }

    [Theory]
    [InlineData("pass", AuthenticationResultKind.Pass)]
    [InlineData("fail", AuthenticationResultKind.Fail)]
    [InlineData("softfail", AuthenticationResultKind.SoftFail)]
    [InlineData("neutral", AuthenticationResultKind.Neutral)]
    [InlineData("none", AuthenticationResultKind.None)]
    [InlineData("policy", AuthenticationResultKind.Policy)]
    [InlineData("temperror", AuthenticationResultKind.TempError)]
    [InlineData("permerror", AuthenticationResultKind.PermError)]
    public void ResultKinds(string word, AuthenticationResultKind kind) => Assert.Equal(kind, P("mx; spf=" + word).Results[0].Kind);

    [Fact]
    public void AMissingAuthservIdIsReportedAndTheResultsStillRead()
    {
        AuthenticationResults a = P("; spf=pass smtp.mailfrom=a@example.net");
        Assert.Equal(string.Empty, a.AuthServId);
        Assert.Contains(a.Defects, d => d.Kind == MimeDefectKind.AuthenticationResultsMissingId);
        Assert.Single(a.Results);
        Assert.False(AuthenticationResults.TryParse("; spf=pass"u8, out _));
        Assert.True(AuthenticationResults.TryParse("mx; spf=pass"u8, out _));
        Assert.Contains(MimeDefectKind.AuthenticationResultsMissingId, Kinds(string.Empty));
    }

    [Theory]
    [InlineData("example.com 2; spf=pass")]
    [InlineData("example.com x; spf=pass")]
    [InlineData("example.com 1.0; spf=pass")]
    public void AVersionOtherThanOneIsReported(string v)
    {
        Assert.Contains(MimeDefectKind.AuthenticationResultsInvalidVersion, Kinds(v));
        Assert.Single(P(v).Results); // the results are still read
    }

    [Fact]
    public void ClausesThatAreNotMethodResultsAreSkipped()
    {
        AuthenticationResults a = P("example.com; spf=pass smtp.mailfrom=a@example.net; =pass; dkim; !!! ; arc=pass");
        Assert.Equal(["spf", "arc"], a.Results.Select(r => r.Method));
        Assert.True(a.Defects.Count(d => d.Kind == MimeDefectKind.InvalidAuthenticationResults) >= 3);
    }

    [Fact]
    public void AnEmptyClauseIsReportedAndATrailingSemicolonIsNot()
    {
        Assert.Contains(MimeDefectKind.InvalidAuthenticationResults, Kinds("example.com; ; spf=pass"));
        Assert.Empty(Kinds("example.com; spf=pass smtp.mailfrom=a@example.net;"));
    }

    [Fact]
    public void NoneBesideResultsIsAmbiguousAndBothAreKept()
    {
        AuthenticationResults a = P("example.com; none; spf=pass");
        Assert.True(a.IsNone);
        Assert.Single(a.Results);
        MimeDefect d = Assert.Single(a.Defects, x => x.Kind == MimeDefectKind.AmbiguousAuthenticationResultsNone);
        Assert.True(d.IsAmbiguity);
        Assert.Contains(MimeDefectKind.AmbiguousAuthenticationResultsNone, Kinds("example.com; spf=pass; none"));
    }

    [Fact]
    public void APropertyWithoutATypeIsKeptAndAmbiguous()
    {
        AuthenticationResults a = P("spf-host; dmarc=pass action=none header.from=example.net; compauth=pass reason=100");
        AuthenticationMethodResult dmarc = a.Results[0];
        Assert.Equal(("", "action", "none"), (dmarc.Properties[0].PropertyType, dmarc.Properties[0].Name, dmarc.Properties[0].Value));
        Assert.Equal("example.net", dmarc.GetProperty("header", "from"));
        Assert.Equal("100", a.Results[1].Reason);
        Assert.Contains(a.Defects, d => d.Kind == MimeDefectKind.AmbiguousAuthenticationResultsProperty && d.IsAmbiguity);
    }

    [Fact]
    public void UnterminatedQuotesAndCommentsAreReported()
    {
        Assert.Contains(MimeDefectKind.UnterminatedQuote, Kinds("example.com; spf=pass smtp.mailfrom=\"a@example.net"));
        Assert.Contains(MimeDefectKind.InvalidAuthenticationResults, Kinds("example.com; spf=pass (never closed smtp.mailfrom=a@example.net"));
    }

    [Fact]
    public void DefectOffsetsAreInTheHeaderValue()
    {
        MimeEntity e = MimeEntity.Parse(B("Authentication-Results: example.com; =bad; spf=pass\r\n\r\n"));
        var defects = new List<MimeDefect>();
        IReadOnlyList<AuthenticationResults> list = e.GetAuthenticationResults(defects);
        Assert.Single(list);
        MimeDefect d = Assert.Single(defects);
        Assert.Equal(37, d.Offset); // the value starts at 23, after "Authentication-Results:"; "=bad" is 14 bytes into it
    }

    [Fact]
    public void EveryFieldOfAnEntityIsReturnedInOrder()
    {
        MimeEntity e = MimeEntity.Parse(B("Authentication-Results: a.example; spf=pass\r\nReceived: x\r\nAuthentication-Results: b.example; dkim=fail\r\n\r\n"));
        Assert.Equal(["a.example", "b.example"], e.GetAuthenticationResults().Select(x => x.AuthServId));
    }

    [Fact]
    public void HostileInputNeverThrows()
    {
        foreach (string s in new[] { "(", ")", "\"", ";;;;", "a;(((((", "a; b=\"", "a; /=", "a; b/=c", "a;b=c.d.e=f", ".", "a; ..=.", new string('(', 5000), "a; " + new string(';', 5000), "a;" + string.Concat(Enumerable.Repeat(" x=y", 3000)) })
        {
            _ = P(s);
        }
    }
}

public class ReceivedSpfTests
{
    [Fact]
    public void TheRfc7208Example()
    {
        ReceivedSpf r = ReceivedSpf.Parse("pass (mybox.example.org: domain of myname@example.com designates 192.0.2.1 as permitted sender)\r\n receiver=mybox.example.org; client-ip=192.0.2.1;\r\n envelope-from=\"myname@example.com\"; helo=foo.example.com;");
        Assert.Empty(r.Defects);
        Assert.Equal(SpfResult.Pass, r.Result);
        Assert.Equal("mybox.example.org: domain of myname@example.com designates 192.0.2.1 as permitted sender", r.Comment);
        Assert.Equal("mybox.example.org", r.Receiver);
        Assert.Equal(System.Net.IPAddress.Parse("192.0.2.1"), r.ClientIp);
        Assert.Equal("myname@example.com", r.EnvelopeFrom);
        Assert.Equal("foo.example.com", r.Helo);
        Assert.Equal(["receiver", "client-ip", "envelope-from", "helo"], r.Pairs.Select(p => p.Key));
    }

    [Theory]
    [InlineData("none", SpfResult.None)]
    [InlineData("Neutral", SpfResult.Neutral)]
    [InlineData("PASS", SpfResult.Pass)]
    [InlineData("fail", SpfResult.Fail)]
    [InlineData("softfail", SpfResult.SoftFail)]
    [InlineData("temperror", SpfResult.TempError)]
    [InlineData("permerror", SpfResult.PermError)]
    public void ResultKeywords(string word, SpfResult expected)
    {
        ReceivedSpf r = ReceivedSpf.Parse(word);
        Assert.Equal(expected, r.Result);
        Assert.Empty(r.Defects);
    }

    [Fact]
    public void KeysAreCaseInsensitiveAndAnglesStay()
    {
        ReceivedSpf r = ReceivedSpf.Parse("Pass (mailfrom) Identity=mailfrom; Client-IP=2001:db8::1; envelope-from=<a@example.net>");
        Assert.Equal("mailfrom", r.Identity);
        Assert.Equal(System.Net.IPAddress.Parse("2001:db8::1"), r.ClientIp);
        Assert.Equal("<a@example.net>", r.EnvelopeFrom);
    }

    [Fact]
    public void AnUnknownResultAndStrayWordsAreReported()
    {
        ReceivedSpf r = ReceivedSpf.Parse("maybe (hm) client-ip=192.0.2.1");
        Assert.Equal(SpfResult.Unknown, r.Result);
        Assert.Equal("maybe", r.ResultText);
        Assert.Contains(r.Defects, d => d.Kind == MimeDefectKind.InvalidReceivedSpf);
        Assert.Equal("192.0.2.1", r.Get("client-ip"));

        ReceivedSpf s = ReceivedSpf.Parse("softfail client-ip=192.0.2.1; junk; =x; helo=a");
        Assert.Equal(2, s.Defects.Count(d => d.Kind == MimeDefectKind.InvalidReceivedSpf));
        Assert.Equal("a", s.Helo);
        Assert.Null(ReceivedSpf.Parse("temperror client-ip=not-an-ip").ClientIp);
    }

    [Fact]
    public void QuotedValuesAndEntityAccess()
    {
        MimeEntity e = MimeEntity.Parse(B("Received-SPF: permerror (bad) problem=\"a \\\"b\\\" c\"; client-ip=192.0.2.1\r\nReceived-SPF: pass\r\n\r\n"));
        Assert.Equal(2, e.GetReceivedSpf().Count);
        Assert.Equal("a \"b\" c", e.GetReceivedSpf()[0].Problem);
    }

    [Fact]
    public void HostileInputNeverThrows()
    {
        foreach (string s in new[] { string.Empty, "(", "pass \"", "pass =", "pass ;;;;", "pass a=\"\\", new string('(', 5000), "pass " + string.Concat(Enumerable.Repeat("k=v;", 5000)) })
        {
            _ = ReceivedSpf.Parse(s);
        }
    }
}

public class MhtmlUriTests
{
    [Theory]
    [InlineData("g:h", "g:h")]
    [InlineData("g", "http://a/b/c/g")]
    [InlineData("./g", "http://a/b/c/g")]
    [InlineData("g/", "http://a/b/c/g/")]
    [InlineData("/g", "http://a/g")]
    [InlineData("//g", "http://g")]
    [InlineData("?y", "http://a/b/c/d;p?y")]
    [InlineData("g?y", "http://a/b/c/g?y")]
    [InlineData("#s", "http://a/b/c/d;p?q#s")]
    [InlineData("g#s", "http://a/b/c/g#s")]
    [InlineData("g?y#s", "http://a/b/c/g?y#s")]
    [InlineData(";x", "http://a/b/c/;x")]
    [InlineData("g;x", "http://a/b/c/g;x")]
    [InlineData("g;x?y#s", "http://a/b/c/g;x?y#s")]
    [InlineData("", "http://a/b/c/d;p?q")]
    [InlineData(".", "http://a/b/c/")]
    [InlineData("./", "http://a/b/c/")]
    [InlineData("..", "http://a/b/")]
    [InlineData("../", "http://a/b/")]
    [InlineData("../g", "http://a/b/g")]
    [InlineData("../..", "http://a/")]
    [InlineData("../../", "http://a/")]
    [InlineData("../../g", "http://a/g")]
    [InlineData("../../../g", "http://a/g")]
    [InlineData("../../../../g", "http://a/g")]
    [InlineData("/./g", "http://a/g")]
    [InlineData("/../g", "http://a/g")]
    [InlineData("g.", "http://a/b/c/g.")]
    [InlineData(".g", "http://a/b/c/.g")]
    [InlineData("g..", "http://a/b/c/g..")]
    [InlineData("..g", "http://a/b/c/..g")]
    [InlineData("./../g", "http://a/b/g")]
    [InlineData("./g/.", "http://a/b/c/g/")]
    [InlineData("g/./h", "http://a/b/c/g/h")]
    [InlineData("g/../h", "http://a/b/c/h")]
    [InlineData("g;x=1/./y", "http://a/b/c/g;x=1/y")]
    [InlineData("g;x=1/../y", "http://a/b/c/y")]
    [InlineData("g?y/./x", "http://a/b/c/g?y/./x")]
    [InlineData("g?y/../x", "http://a/b/c/g?y/../x")]
    [InlineData("g#s/./x", "http://a/b/c/g#s/./x")]
    [InlineData("g#s/../x", "http://a/b/c/g#s/../x")]
    [InlineData("http:g", "http:g")]
    public void Rfc3986Section54(string reference, string expected) => Assert.Equal(expected, MhtmlUri.Resolve("http://a/b/c/d;p?q", reference));

    [Fact]
    public void ARelativeOrMissingBaseIsThisMessage()
    {
        Assert.Equal("thismessage:/a/b.png", MhtmlUri.Resolve(null, "a/b.png"));
        Assert.Equal("thismessage:/a/b.png", MhtmlUri.Resolve("relative/base.html", "a/b.png"));
        Assert.Equal("thismessage:/x", MhtmlUri.Resolve("thismessage:/", "x"));
    }

    [Fact]
    public void NormalizeMakesEquivalentUrisEqual()
    {
        Assert.Equal(MhtmlUri.Normalize("http://x.example/dir/my%20pic.png"), MhtmlUri.Normalize("HTTP://X.Example:80/dir/./my pic.png#frag"));
        Assert.Equal(MhtmlUri.Normalize("http://x.example/%7Euser/a"), MhtmlUri.Normalize("http://x.example/~user/a"));
        Assert.Equal(MhtmlUri.Normalize("http://x.example/%c3%a9"), MhtmlUri.Normalize("http://x.example/%C3%A9"));
        Assert.Equal("http://x.example/", MhtmlUri.Normalize("http://x.example"));
        Assert.NotEqual(MhtmlUri.Normalize("http://x.example/a%2Fb"), MhtmlUri.Normalize("http://x.example/a/b"));
    }

    [Fact]
    public void PercentDecodeUsesUtf8AndKeepsBadEscapes()
    {
        Assert.Equal("a@b/c\u00e9", MhtmlUri.PercentDecode("a%40b%2Fc%C3%A9"));
        Assert.Equal("100%zz%", MhtmlUri.PercentDecode("100%zz%"));
    }
}

public class MhtmlDocumentTests
{
    private static string Dir([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "reports"));

    private static MhtmlDocument Doc(string file) => MhtmlDocument.Find(MimeEntity.Parse(File.ReadAllBytes(Path.Combine(Dir(), file))))!;

    private static int Hit(MhtmlDocument d, string reference, string? baseUri = null)
    {
        MhtmlResource? r = d.Resolve(reference, baseUri);
        return r is null ? -1 : d.Resources.ToList().IndexOf(r);
    }

    [Theory]
    [InlineData("cid:logo@example.org", 1)]
    [InlineData("cid:<logo@example.org>", 1)]
    [InlineData("CID:logo@example.org", 1)]
    [InlineData("cid:logo%40example.org", 1)]
    [InlineData("cid:banner@example.org", 2)]
    [InlineData("cid:Banner@Example.org", 2)]
    [InlineData("cid:root@example.org", 0)]
    [InlineData("cid:missing@example.org", -1)]
    [InlineData("  cid:logo@example.org  ", 1)]
    [InlineData("", -1)]
    public void ContentIds(string reference, int expected) => Assert.Equal(expected, Hit(Doc("mht-01-cid.eml"), reference));

    [Theory]
    [InlineData("a.png", 1)]
    [InlineData("./a.png", 1)]
    [InlineData("../img/b.png", 2)]
    [InlineData("/img/b.png", 2)]
    [InlineData("http://www.example.org/dir/a.png", 1)]
    [InlineData("//other.example.net/c.png", 3)]
    [InlineData("HTTP://WWW.EXAMPLE.ORG/dir/a.png", 1)]
    [InlineData("a.png#frag", 1)]
    [InlineData("a.png?x=1", -1)]
    [InlineData("missing.png", -1)]
    [InlineData("http://www.example.org:80/dir/a.png", 1)]
    [InlineData("page.html", 0)]
    public void AbsoluteLocationsAndRelativeReferences(string reference, int expected) => Assert.Equal(expected, Hit(Doc("mht-02-absolute.eml"), reference));

    [Fact]
    public void WithNoRealBaseEverythingIsThisMessage()
    {
        MhtmlDocument d = Doc("mht-03-relative.eml");
        Assert.Equal("thismessage:/", d.BaseUri);
        Assert.Equal("thismessage:/images/a.png", d.Resources[1].AbsoluteLocation);
        Assert.Equal(1, Hit(d, "images/a.png"));
        Assert.Equal(1, Hit(d, "./images/a.png"));
        Assert.Equal(2, Hit(d, "images/../b.png"));
        Assert.Equal(2, Hit(d, "/b.png"));
        Assert.Equal(-1, Hit(d, "c.png"));
    }

    [Fact]
    public void TheStartParameterChoosesTheRoot()
    {
        MhtmlDocument d = Doc("mht-04-start.eml");
        Assert.Equal(1, d.Resources.ToList().FindIndex(r => r.IsRoot));
        Assert.Same(d.Resources[1].Entity, d.Root);
        Assert.Equal("http://x.example/s.html", d.BaseUri);
        Assert.Empty(d.Defects);
    }

    [Fact]
    public void AStartThatNamesNoPartFallsBackToTheFirst()
    {
        MhtmlDocument d = Doc("mht-05-start-missing.eml");
        Assert.True(d.Resources[0].IsRoot);
        Assert.Contains(d.Defects, x => x.Kind == MimeDefectKind.MhtmlStartNotFound);
    }

    [Fact]
    public void DuplicatesAreAmbiguousAndTheFirstWins()
    {
        MhtmlDocument d = Doc("mht-06-duplicates.eml");
        Assert.Contains(d.Defects, x => x.Kind == MimeDefectKind.AmbiguousMhtmlDuplicateLocation && x.IsAmbiguity);
        Assert.Contains(d.Defects, x => x.Kind == MimeDefectKind.AmbiguousMhtmlDuplicateContentId && x.IsAmbiguity);
        Assert.Equal(1, Hit(d, "a.png"));
        Assert.Equal(1, Hit(d, "cid:same@example.org"));
    }

    [Fact]
    public void EscapesMatchExactlyFirstThenTolerantly()
    {
        MhtmlDocument d = Doc("mht-07-escapes.eml");
        Assert.Equal(1, Hit(d, "my%20pic.png"));
        Assert.Equal(1, Hit(d, "my pic.png")); // tolerant: a space against %20
        Assert.Equal(2, Hit(d, "caf%c3%a9.png")); // tolerant: hex case
        Assert.Equal(2, Hit(d, "caf%C3%A9.png"));
        Assert.Equal(3, Hit(d, "up%2Fcase.png"));
        Assert.Equal(3, Hit(d, "up%2fcase.png"));
        Assert.Equal(-1, Hit(d, "up/case.png")); // %2F is not a slash
    }

    [Fact]
    public void ALegacyContentBaseIsHonoured()
    {
        MhtmlDocument d = Doc("mht-08-content-base.eml");
        Assert.Equal("http://base.example/root/", d.BaseUri);
        Assert.Equal(1, Hit(d, "a.png"));
    }

    [Fact]
    public void AFoldInAContentLocationIsRemoved()
    {
        MhtmlDocument d = Doc("mht-09-folded-location.eml");
        Assert.Equal("http://x.example/very/long/path/a.png", d.Resources[1].ContentLocation);
        Assert.Equal(1, Hit(d, "very/long/path/a.png"));
    }

    [Fact]
    public void FileUrls()
    {
        MhtmlDocument d = Doc("mht-10-file-urls.eml");
        Assert.Equal(1, Hit(d, "page_files/a.png"));
        Assert.Equal(1, Hit(d, "file:///C:/Users/me/page_files/a.png"));
        Assert.Equal(1, Hit(d, "file:///c:/Users/me/page_files/a.png")); // Windows paths are not case-sensitive
    }

    [Fact]
    public void AnHtmlBaseElementChangesTheBase()
    {
        MhtmlDocument d = Doc("mht-11-base-argument.eml");
        Assert.Equal(1, Hit(d, "img.png", "http://y.example/z/"));
        Assert.Equal(2, Hit(d, "img.png"));
        Assert.Equal(-1, Hit(d, "img.png", "/z/")); // "/z/" resolves against the root's host, x.example, which has no such part
    }

    [Fact]
    public void ACidInAContentLocationIsNotAContentIdLabel()
    {
        MhtmlDocument d = Doc("mht-12-cid-location.eml");
        Assert.Equal(-1, Hit(d, "cid:foo@bar.net"));
        Assert.Equal(1, Hit(d, "cid:other@bar.net"));
    }

    [Fact]
    public void TheExampleOfRfc2557()
    {
        MhtmlDocument d = Doc("mht-13-rfc-example.eml");
        Assert.Equal(1, Hit(d, "fiction1/fiction2"));
        Assert.Equal(2, Hit(d, "cid:97116092811xyz@foo.bar.net"));
        Assert.Equal(2, Hit(d, "fiction1/fiction3"));
        Assert.Equal(-1, Hit(d, "fiction1/fiction4"));
        Assert.Equal("thismessage:/fiction1/fiction2", d.Resources[1].AbsoluteLocation);
    }

    [Fact]
    public void ALoneHtmlPartIsADocument()
    {
        MhtmlDocument d = Doc("mht-14-single.eml");
        Assert.Single(d.Resources);
        Assert.Equal(0, Hit(d, "page.html"));
        Assert.Equal(-1, Hit(d, "other.html"));
    }

    [Fact]
    public void ARelatedInsideMixedIsFound()
    {
        MhtmlDocument d = Doc("mht-15-nested.eml");
        Assert.Equal(2, d.Resources.Count);
        Assert.Equal(1, Hit(d, "a.png"));
        Assert.Equal(0, Hit(d, "p.html"));
    }

    [Fact]
    public void AReferenceNotInThisRelatedIsLookedForInTheEnclosingOne()
    {
        // mht-16: the innermost related holds only the page; the image is in the outer one (RFC 2557 section 7).
        var outer = (MimeMultipart)MimeEntity.Parse(File.ReadAllBytes(Path.Combine(Dir(), "mht-16-outer-resource.eml")));
        var inner = (MimeMultipart)outer.Children[0];
        MhtmlDocument d = MhtmlDocument.TryCreate(inner)!;
        Assert.Single(d.Resources);
        MhtmlResource? hit = d.Resolve("a.png");
        Assert.NotNull(hit);
        Assert.Same(outer.Children[1], hit.Entity);
        Assert.Equal("image/png", hit.MediaType);
        Assert.NotEmpty(hit.GetContent()!);
    }

    [Fact]
    public void MidReferencesNeedTheMessageId()
    {
        MhtmlDocument d = Doc("mht-17-mid.eml");
        Assert.Equal(1, Hit(d, "mid:msg1@example.org/img1@example.org"));
        Assert.Equal(1, Hit(d, "mid:msg1%40example.org/img1%40example.org"));
        Assert.Equal(-1, Hit(d, "mid:other@example.org/img1@example.org"));
        Assert.Equal(-1, Hit(d, "mid:msg1@example.org"));
    }

    [Fact]
    public void ARelatedWithNoParts()
    {
        MhtmlDocument d = Doc("mht-18-no-parts.eml");
        Assert.Null(d.Root);
        Assert.Empty(d.Resources);
        Assert.Equal(-1, Hit(d, "a.png"));
    }

    [Fact]
    public void ResolveToAbsolute()
    {
        MhtmlDocument d = Doc("mht-02-absolute.eml");
        Assert.Equal("http://www.example.org/dir/z.png", d.ResolveToAbsolute("z.png"));
        Assert.Equal("http://q.example/z.png", d.ResolveToAbsolute("z.png", "http://q.example/"));
    }

    [Fact]
    public void NotMhtmlIsNull()
    {
        Assert.Null(MhtmlDocument.TryCreate(MimeEntity.Parse(B("Content-Type: text/html\r\n\r\n<p>"))));
        Assert.Null(MhtmlDocument.Find(MimeEntity.Parse(B("Content-Type: text/plain\r\n\r\nx"))));
    }

    [Fact]
    public void BuiltAggregateWritesAndReadsBackThroughAFile()
    {
        byte[] png = Convert.FromBase64String("iVBORw0KGgo=");
        var b = new MhtmlBuilder { Subject = "Pa\u00e9ge", From = "Saved <saved@example.org>", Date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), Boundary = "xyz" };
        b.SetRoot("<html><img src=\"logo.png\"><img src=\"cid:i1@example.org\"></html>", "http://www.example.org/index.html", "root@example.org");
        b.AddResource(png, "image/png", "http://www.example.org/logo.png");
        b.AddResource(png, "image/png", null, "i1@example.org");

        string path = Path.Combine(Path.GetTempPath(), "onemail-" + Guid.NewGuid().ToString("N") + ".mht");
        try
        {
            using (FileStream fs = File.Create(path))
            {
                b.WriteTo(fs);
            }

            using MhtmlDocument d = MhtmlDocument.Open(path)!;
            Assert.Empty(d.Defects);
            Assert.Equal(3, d.Resources.Count);
            Assert.True(d.Resources[0].IsRoot);
            Assert.Equal("http://www.example.org/index.html", d.BaseUri);
            Assert.Equal(1, d.Resources.ToList().IndexOf(d.Resolve("logo.png")!));
            Assert.Equal(2, d.Resources.ToList().IndexOf(d.Resolve("cid:i1@example.org")!));
            Assert.Equal(png, d.Resolve("logo.png")!.GetContent());
            Assert.Contains("<img src=\"logo.png\">", Encoding.UTF8.GetString(d.RootResource!.GetContent()!), StringComparison.Ordinal);
            Assert.Equal("Pa\u00e9ge", d.Entity.Headers.GetFirst("Subject")!.Value);

            // The written file is a normal message: it writes back byte for byte.
            byte[] first = File.ReadAllBytes(path);
            using var ms = new MemoryStream();
            d.WriteTo(ms);
            Assert.Equal(first, ms.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ABuilderNeedsARoot() => Assert.Throws<InvalidOperationException>(() => new MhtmlBuilder().Build());

    [Fact]
    public void OpenReturnsNullForOtherFilesAndReleasesThem()
    {
        string path = Path.Combine(Path.GetTempPath(), "onemail-" + Guid.NewGuid().ToString("N") + ".eml");
        File.WriteAllText(path, "Subject: x\r\n\r\nbody\r\n");
        try
        {
            Assert.Null(MhtmlDocument.Open(path));
        }
        finally
        {
            File.Delete(path); // would fail on Windows if the file were still open
        }
    }
}
