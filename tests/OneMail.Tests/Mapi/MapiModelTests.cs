using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneMail.Mapi;
using RecursiveAI.OneMail.Text;

namespace RecursiveAI.OneMail.Tests.Mapi;

public class MapiModelTests
{
    private static MapiProperty P(uint tag, byte[] bytes) => MapiProperty.Create(new MapiTag(tag), bytes);

    [Fact]
    public void TagSplitsIdAndType()
    {
        var t = new MapiTag(0x0037001F);
        Assert.Equal(0x0037, t.Id);
        Assert.Equal(MapiType.UnicodeString, t.PropertyType);
        Assert.False(t.IsNamed);
        Assert.False(t.IsMultiValued);
        Assert.Equal("0037001F", t.ToHex());
        Assert.Equal(new MapiTag(MapiPid.Subject, MapiType.UnicodeString), t);
        Assert.True(new MapiTag(0x80C2, MapiType.MultipleBinary).IsNamed);
        Assert.True(new MapiTag(0x1, MapiType.MultipleBinary).IsMultiValued);
        Assert.Equal(MapiType.Binary, (MapiType)MapiTypes.ElementType((ushort)MapiType.MultipleBinary));
    }

    [Fact]
    public void FixedAccessorsDecodeLittleEndian()
    {
        Assert.Equal((short)-2, P(0x00010002, [0xFE, 0xFF]).AsInt16());
        Assert.Equal(-70000, P(0x00010003, BitConverter.GetBytes(-70000)).AsInt32());
        Assert.Equal(1L << 40, P(0x00010014, BitConverter.GetBytes(1L << 40)).AsInt64());
        Assert.Equal(1.5f, P(0x00010004, BitConverter.GetBytes(1.5f)).AsFloat());
        Assert.Equal(-2.25, P(0x00010005, BitConverter.GetBytes(-2.25)).AsDouble());
        Assert.True(P(0x0001000B, [1]).AsBoolean());
        Assert.False(P(0x0001000B, [0]).AsBoolean());
        Assert.Equal(12345L, P(0x00010006, BitConverter.GetBytes(12345L)).AsCurrencyUnits());
        Assert.Equal(new Guid("00062008-0000-0000-c000-000000000046"), P(0x00010048, new Guid("00062008-0000-0000-c000-000000000046").ToByteArray()).AsGuid());
    }

    [Fact]
    public void AccessorsGiveNullForTheWrongTypeOrShortBytes()
    {
        Assert.Null(P(0x00010003, [1, 2]).AsInt32());
        Assert.Null(P(0x00010003, [1, 2, 3, 4]).AsInt16());
        Assert.Null(P(0x0001001F, [1, 2]).AsInt32());
        Assert.Null(P(0x00010003, [1, 2, 3, 4]).AsString());
        Assert.Null(P(0x00010040, [1, 2, 3]).AsTime());
    }

    [Fact]
    public void TimeConvertsFileTimeAndRejectsSentinels()
    {
        ulong ft = (ulong)new DateTime(2024, 2, 29, 12, 30, 45, DateTimeKind.Utc).ToFileTimeUtc();
        MapiProperty p = P(0x00010040, BitConverter.GetBytes(ft));
        Assert.Equal(new DateTimeOffset(2024, 2, 29, 12, 30, 45, TimeSpan.Zero), p.AsTime());
        Assert.Equal(ft, p.AsFileTime());
        Assert.Null(P(0x00010040, new byte[8]).AsTime());
        Assert.Null(P(0x00010040, BitConverter.GetBytes(0x7FFFFFFFFFFFFFFFUL)).AsTime());
        Assert.Equal(new DateTimeOffset(1899, 12, 30, 12, 0, 0, TimeSpan.Zero), P(0x00010007, BitConverter.GetBytes(0.5)).AsFloatingTime());
        Assert.Null(P(0x00010007, BitConverter.GetBytes(double.NaN)).AsFloatingTime());
    }

    [Fact]
    public void UnicodeStringsDropTerminatorsAndOddBytes()
    {
        Assert.Equal("caf\u00e9", P(0x0001001F, Encoding.Unicode.GetBytes("caf\u00e9\0\0")).AsString());
        var issues = new List<TextIssue>();
        Assert.Equal("ab", P(0x0001001F, [(byte)'a', 0, (byte)'b', 0, 0x41]).AsString(issues: issues));
        Assert.Contains(issues, i => i.Kind == TextIssueKind.InvalidBytes);
        Assert.Equal(string.Empty, P(0x0001001F, []).AsString());
    }

    [Fact]
    public void EightBitStringsUseTheGivenEncoding()
    {
        byte[] bytes = [0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2, 0];
        Assert.Equal("\u041f\u0440\u0438\u0432\u0435\u0442", P(0x0001001E, bytes).AsString(MailEncoding.ForCodePage(1251)));
        Assert.Equal("\u00cf\u00f0\u00e8\u00e2\u00e5\u00f2", P(0x0001001E, bytes).AsString());
    }

    [Fact]
    public void MultiValuedPropertiesDecodePerElement()
    {
        MapiProperty p = MapiProperty.CreateMulti(new MapiTag(0x00010003 | 0x1000), [BitConverter.GetBytes(7), BitConverter.GetBytes(8)]);
        Assert.Equal(2, p.ValueCount);
        Assert.True(p.IsMultiValued);
        Assert.Equal(8, p.AsInt32(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.GetBytes(2));
        MapiProperty s = MapiProperty.CreateMulti(new MapiTag(0x0001101F), [Encoding.Unicode.GetBytes("a\0"), Encoding.Unicode.GetBytes("b\0")]);
        Assert.Equal("b", s.AsString(index: 1));
    }

    [Fact]
    public void BagLookupsPreferUnicodeAndTheLastDuplicate()
    {
        var bag = new MapiPropertyBag();
        bag.Add(MapiProperty.Create(new MapiTag(MapiPid.Subject, MapiType.String8), "old"u8));
        bag.Add(MapiProperty.Create(new MapiTag(MapiPid.Subject, MapiType.UnicodeString), Encoding.Unicode.GetBytes("first")));
        bag.Add(MapiProperty.Create(new MapiTag(MapiPid.Subject, MapiType.UnicodeString), Encoding.Unicode.GetBytes("second")));
        Assert.Equal(3, bag.Count);
        Assert.Equal("second", bag.Get(MapiPid.Subject)!.AsString());
        Assert.Equal("old", bag.Get(new MapiTag(MapiPid.Subject, MapiType.String8))!.AsString());
        Assert.Null(bag.Get(MapiPid.Body));
        bag.Set(MapiProperty.Create(new MapiTag(MapiPid.Subject, MapiType.UnicodeString), Encoding.Unicode.GetBytes("set")));
        Assert.Equal(2, bag.Count);
        Assert.True(bag.Remove(new MapiTag(MapiPid.Subject, MapiType.String8)));
        Assert.Single(bag);
    }

    [Fact]
    public void NamedPropertiesAreFoundByName()
    {
        var name = new MapiNamedProperty(MapiPropertySets.Common, 0x8503u);
        var bag = new MapiPropertyBag();
        bag.Add(MapiProperty.Create(new MapiTag(0x8007, MapiType.Boolean), [1], name));
        Assert.True(bag.Get(MapiNames.ReminderSet)!.AsBoolean());
        Assert.Null(bag.Get(MapiNames.ReminderDelta));
        Assert.Equal("PidLidReminderSet", MapiPropertyNames.GetName(name));
        Assert.NotEqual(new MapiNamedProperty(MapiPropertySets.Common, "x"), new MapiNamedProperty(MapiPropertySets.Common, "X"));
    }

    [Fact]
    public void TheNameTableCoversTheConstants()
    {
        Assert.Equal("PidTagSubject", MapiPropertyNames.GetName(MapiPid.Subject));
        Assert.Equal("PidTagRtfCompressed", MapiPropertyNames.GetName(MapiPid.RtfCompressed));
        Assert.Equal("PidTagHtml", MapiPropertyNames.GetName(MapiPid.Html));
        Assert.Equal(MapiPid.Html, MapiPid.BodyHtml);
        Assert.Null(MapiPropertyNames.GetName(0x7001));
    }

    [Fact]
    public void ObjectCodePageFollowsMessageThenLocaleThenInternetThenParent()
    {
        var parent = new MapiMessage();
        Assert.Equal(1252, parent.String8Encoding.CodePage);
        Assert.Equal(MapiCodePageSource.Default, parent.String8EncodingSource);
        parent.Properties.Add(MapiProperty.Create(new MapiTag(MapiPid.InternetCodepage, MapiType.Integer32), BitConverter.GetBytes(65001)));
        Assert.Equal(MapiCodePageSource.Default, parent.String8EncodingSource);
        parent.Properties.Set(MapiProperty.Create(new MapiTag(MapiPid.InternetCodepage, MapiType.Integer32), BitConverter.GetBytes(1251)));
        Assert.Equal(1251, parent.String8Encoding.CodePage);
        Assert.Equal(MapiCodePageSource.InternetCodepage, parent.String8EncodingSource);
        parent.Properties.Add(MapiProperty.Create(new MapiTag(MapiPid.MessageLocaleId, MapiType.Integer32), BitConverter.GetBytes(0x0407)));
        Assert.Equal(1252, parent.String8Encoding.CodePage);
        Assert.Equal(MapiCodePageSource.LocaleId, parent.String8EncodingSource);
        parent.Properties.Add(MapiProperty.Create(new MapiTag(MapiPid.MessageCodepage, MapiType.Integer32), BitConverter.GetBytes(932)));
        Assert.Equal(932, parent.String8Encoding.CodePage);
        Assert.Equal(MapiCodePageSource.MessageCodepage, parent.String8EncodingSource);
        var child = new MapiRecipient(parent);
        Assert.Equal(932, child.String8Encoding.CodePage);
        Assert.Equal(MapiCodePageSource.Enclosing, child.String8EncodingSource);
        parent.Properties.Set(MapiProperty.Create(new MapiTag(MapiPid.MessageCodepage, MapiType.Integer32), BitConverter.GetBytes(99999)));
        Assert.Equal(1252, child.String8Encoding.CodePage);
    }

    [Theory]
    [InlineData(0x0407, 1252)]
    [InlineData(0x0419, 1251)]
    [InlineData(0x0404, 950)]
    [InlineData(0x0804, 936)]
    [InlineData(0x0411, 932)]
    [InlineData(0x0412, 949)]
    [InlineData(0x0415, 1250)]
    [InlineData(0x041F, 1254)]
    [InlineData(0x0C1A, 1251)]
    [InlineData(0x081A, 1250)]
    [InlineData(0x0439, 0)]
    public void LocalesMapToTheirAnsiCodePage(int lcid, int codePage) => Assert.Equal(codePage, MapiCodePages.AnsiCodePageForLocale(lcid));

    [Fact]
    public void ValueEqualsComparesTagNameAndBytes()
    {
        MapiProperty a = P(0x00010003, [1, 0, 0, 0]);
        Assert.True(a.ValueEquals(P(0x00010003, [1, 0, 0, 0])));
        Assert.False(a.ValueEquals(P(0x00010003, [2, 0, 0, 0])));
        Assert.False(a.ValueEquals(P(0x00020003, [1, 0, 0, 0])));
    }
}
