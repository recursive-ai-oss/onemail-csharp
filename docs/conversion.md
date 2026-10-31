# Conversions and loss reports

A conversion turns an item of one model into an item of another and says what it could not carry. Nothing converts behind the
caller's back: a conversion is a call, and its result has two parts.

```csharp
public interface IItemConversion<in TSource, TResult>
{
    ConversionResult<TResult> Convert(TSource source);   // Value + Report; never throws for malformed content
}
```

Namespace `RecursiveAI.OneMail.Conversion`. Calendar, contact and store-to-store conversions implement the same interface and return
the same report.

## The loss report

`LossReport` is a list of `LossEntry(Family, Name, Kind, Code, Reason, Path)`.

| Field | Meaning |
|---|---|
| `Family` | `mapi` (a property of the source) or `mime` (a header field or part of the source) |
| `Name` | the property (`PidTagSubject`; a named property as `{set}/"name"`; `0x1234001F` when it has no canonical name) or the header field name |
| `Kind` | `Dropped` (nothing carried), `Approximated` (carried in a weaker form), `Overridden` (two sources disagreed, one set aside), `Internal` (store bookkeeping: entry IDs, search keys, access flags; listed so nothing is silent, not a loss of content) |
| `Code` | a stable identifier of the rule, catalogued below |
| `Path` | empty for the message, else `attachment[2]`, `recipient`, `attachment[0]/message/attachment[1]` |

An identical entry is added once (the same property on ten recipients is one fact about "recipient"). A report from a nested item is
merged into its parent's with the item's path in front. `HasContentLoss` is true when any entry is not `Internal`.

Every property of the source is either carried, listed in the report, or part of the documented derived set (a `PidTagSubject` that
comes back with a subject prefix and normalised subject added is not a loss). That statement is what the round-trip checks below test.

## MAPI to MIME: `MapiToMimeConverter` (MS-OXCMAIL 2.1)

`Convert(MapiMessage)` returns a new `MimeEntity` tree built from `MimePart`, `MimeMultipart` and `MimeMessagePart`; `ToArray()` writes it.
Options (`OxcMailExportOptions`): `UseTransportHeaders` (default true), `IncludeBcc` (false), `BoundaryFactory` (random boundaries
unless given), `MaxEmbeddedDepth` (16).

**Header block.** From `PidTagTransportMessageHeaders` when the message has it and the option is on, else built from the properties.

- The received fields are the base, in their order. `MIME-Version` and every `Content-*` field except `Content-Language` and
  `Content-Class`, and `Lines`, are dropped: the new message's structure is its own. Lines that are not header fields are not written
  (`TransportHeaderLineSkipped`). A `Bcc` field is not written unless `IncludeBcc`.
- For the fields the properties define (Subject, From, Sender, To, Cc, Reply-To, Date, Message-ID, In-Reply-To, References,
  Thread-Topic), the received field is kept as received when it **agrees** with the properties: same decoded subject (white space
  collapsed), same set of addresses, same instant (within a second), same identifiers. When it does not, the properties' value is
  written and `TransportHeaderOverridden` says so: the properties are the item's truth (OX2). When the addresses agree but a display
  name or the spelling of an address differs, the received spelling is kept and `AddressSpellingFromTransportHeader` says so.
- Fields the properties define that the received block lacks are added after it. `PidTagTransportMessageHeaders` itself is then an
  `Approximated` entry (`TransportHeadersRewritten`) whenever the written block differs from the stored one.
- Without a transport block the fields are written in this order: From, Sender, To, Cc, Bcc (option), Reply-To, Subject, Date,
  Message-ID, In-Reply-To, References, Importance, Sensitivity, Keywords, Thread-Topic, Thread-Index, Disposition-Notification-To,
  Return-Receipt-To, the `PS_INTERNET_HEADERS` properties, then `MIME-Version`.

**Properties to fields.**

| Property | Field |
|---|---|
| `PidTagSubject` without its prefix marker (U+0001 and one character), else `PidTagSubjectPrefix` + `PidTagNormalizedSubject` | `Subject` (an empty subject is an empty field) |
| `PidTagSentRepresenting*`, else `PidTagSender*` | `From`; `Sender` from `PidTagSender*` when it is another address |
| recipient rows by `PidTagRecipientType` 1, 2, 3 | `To`, `Cc`, `Bcc` |
| `PidTagReplyRecipientEntries` (one-off EntryIDs, MS-OXCDATA 2.2.5.1) | `Reply-To` |
| `PidTagClientSubmitTime` (else `PidTagMessageDeliveryTime`, `DateFromDeliveryTime`) | `Date`, in UTC (`+0000`), whole seconds |
| `PidTagInternetMessageId`, `PidTagInReplyToId`, `PidTagInternetReferences` | `Message-ID`, `In-Reply-To`, `References` (angle brackets added where missing) |
| `PidTagImportance` 0, 1, 2 | `Importance: Low`, `Normal`, `High` |
| `PidTagSensitivity` 1, 2, 3 | `Sensitivity: Personal`, `Private`, `Company-Confidential`; 0 has no field (`DefaultValueNotWritten`) |
| `PidNameKeywords` | `Keywords`, comma and space separated |
| `PidTagConversationTopic`, `PidTagConversationIndex` | `Thread-Topic`, `Thread-Index` (base64) |
| `PidTagReadReceiptRequested` true | `Disposition-Notification-To` (the sender's address); false has no field |
| `PidTagOriginatorDeliveryReportRequested` true | `Return-Receipt-To` (the sender's address); false has no field |
| string-named properties of `PS_INTERNET_HEADERS` | a field of that name |

**Addresses.** Fields carry SMTP addresses. An address stored as `EX` (an Exchange distinguished name) is resolved in this order
(OX1): its own `PidTagSmtpAddress`, `PidTagSenderSmtpAddress` or `PidTagSentRepresentingSmtpAddress`; the SMTP address of another
sender or recipient of the message with the same distinguished name; for the sender groups, the one mailbox of the matching
transport `From` (or `Sender`) field; the single address a transport `From`, `To`, `Cc`, `Sender` or `Reply-To` field gives for the
same display name. An address no step resolves is left out and reported (`AddressUnresolved`, or `ReplyToUnresolved`): nothing is
guessed from the distinguished name. An address that was resolved from `EX` is an `Approximated` entry (`ExchangeAddressWrittenAsSmtp`)
because reading the message back stores it as SMTP. A display name with non-ASCII characters is written as RFC 2047 encoded words
for the whole name (OX8).

**Bodies** (MS-OXCMAIL 2.1.3.3). `PidTagBody` becomes `text/plain; charset=utf-8` and `PidTagHtml` a `text/html` part whose bytes are
those of the property, with the charset of `PidTagInternetCodepage` (text and UTF-16 HTML are written as UTF-8). A message with
both is `multipart/alternative`; inline attachments (a content ID, not marked `attachment`) travel with the HTML in
`multipart/related`; other attachments make the whole `multipart/mixed`. A message with no body and no attachment is an empty
`text/plain`. The RTF body (`PidTagRtfCompressed`, MS-OXRTFCP) is decompressed and de-encapsulated (MS-OXRTFEX): HTML
encapsulation gives the HTML part when the message has none (`HtmlFromRtf`), plain-text encapsulation the text part (`TextFromRtf`).
RTF that encapsulates nothing is written as a `text/rtf` part when there is no HTML (`RtfCarriedAsTextRtf`), else not written
(`RtfRedundant`). A lone CR or LF in the text is written as CRLF (`BodyLineBreaksNormalized`).

**Attachments** (MS-OXCMAIL 2.1.3.4). By value: a part of type `PidTagAttachMimeTag` (`application/octet-stream` when absent or not a
media type), base64, `Content-Disposition` `inline` or `attachment` (from `PidTagAttachContentDisposition`, else a content ID or the
`ATT_MHTML_REF` flag means inline) with `filename` and `name` per RFC 2231, `Content-ID` in angle brackets and `Content-Location`. A
composite type on flat data (`multipart/*`, `message/*`) becomes `application/octet-stream` (`CompositeTypeOnFlatData`). An embedded
message becomes a `message/rfc822` part holding the converted message (limit `MaxEmbeddedDepth`). Attachments by reference, OLE
objects without flat data and attachments without data are not written and are reported.

**Everything else** a message, recipient or attachment holds is reported: `NoInternetMapping` for a property MS-OXCMAIL maps nowhere,
`StoreBookkeeping` for the store-internal ones. `PidTagMessageClass` other than `IPM.Note` is `MessageClassNotCarried`.

## MIME to MAPI: `MimeToMapiConverter` (MS-OXCMAIL 2.2)

`Convert(MimeEntity)` returns a new `MapiMessage` (property bag, recipient and attachment rows, embedded messages as items).
Options (`OxcMailImportOptions`): `PartHandler`, `MaxEmbeddedDepth` (16), `MaxPartDepth` (64).

- **Message class** `IPM.Note`; `REPORT.IPM.Note.NDR` and `.IPNRN` for `multipart/report` with the matching `report-type`;
  `IPM.Note.SMIME.MultipartSigned` for `multipart/signed` and `IPM.Note.SMIME` for `application/pkcs7-mime`. Signed content and
  signature become a body and an attachment and the signature can no longer be verified against them (`SignatureNotVerifiable`, OX12).
- **`PidTagTransportMessageHeaders`** keeps the whole header block as received (UTF-8, else Latin-1), except the fields starting
  `X-MS-Exchange-Organization-` and `X-MS-Exchange-Forest-` (MS-OXCMAIL 2.2.3.2.28). What a property cannot hold is therefore not lost.
- **Fields to properties** are the inverse of the table above: `Subject` (also `PidTagSubjectPrefix`, possibly empty, and
  `PidTagNormalizedSubject`, split by the rule of 2.2.3.2.6.1), `From` (sent-representing group and, unless a `Sender` field says
  otherwise, the sender group), `Sender`, recipients (display name, `SMTP`, address and `PidTagSmtpAddress`, row IDs, object type),
  `PidTagDisplayTo/Cc/Bcc`, `Reply-To` (one-off entry IDs and names), `Date` (UTC; a zone other than UTC is `DateZoneNotStored`; a
  missing or invalid Date gives no property, never "now" so that the result is reproducible, OX7), the first `Received` time as
  `PidTagMessageDeliveryTime`, identifiers with angle brackets, `Importance` (else `Priority`, `X-Priority`, `X-MSMail-Priority`),
  `Sensitivity`, `Keywords`, `Thread-Topic`, `Thread-Index` (base64; anything else is ignored), the two receipt fields. Every other
  field not listed in 2.2.3.2.29 becomes a string-named property of `PS_INTERNET_HEADERS`, name as written, value decoded.
- **Groups** are flattened to their members (`GroupFlattened`). Only the first of a repeated single-valued field is mapped
  (`RepeatedHeaderNotMapped`, `InReplyToFirstOnly`, `ExtraAuthorsNotMapped`).
- **Bodies** are chosen as the shared view chooses them (`shared-view.md`, bodies for MIME): `text/plain` becomes `PidTagBody`;
  `text/html` the binary `PidTagHtml` in its own charset when that is a Windows code page (`PidTagInternetCodepage`), else UTF-8;
  an unnamed `text/rtf` part `PidTagRtfCompressed`. `PidTagNativeBody` says which. Any other leaf part, and any rendering of the body
  that is not chosen, is an attachment.
- **Attachments**: data, `PidTagAttachLongFilename` (and short), extension, `PidTagAttachMimeTag`, content ID without angle
  brackets, content location, the disposition as written (`PidTagAttachContentDisposition`), the inline flag, size. `message/rfc822`
  is an embedded message (method 5) converted by the same rules. Part headers with no property are `PartHeaderNotMapped`.
- **The hook for TNEF.** `OxcMailImportOptions.PartHandler` sees every leaf part before it is classified. A caller that reads
  `winmail.dat` returns true after adding the TNEF message's properties and attachments to the `MapiMessage` it is given.

## Loss codes

`AddressSpellingFromTransportHeader`, `AddressUnresolved`, `AttachmentByReference`, `AttachmentWithoutData`, `BccNotWritten`,
`BodyLineBreaksNormalized`, `CompositeTypeOnFlatData`, `DateFromDeliveryTime`, `DateSubSecondDropped`, `DateZoneNotStored`,
`DefaultValueNotWritten`, `EmbeddedMessageTooDeep`, `EmptyValueNotWritten`, `ExchangeAddressWrittenAsSmtp`, `ExtraAuthorsNotMapped`,
`GroupFlattened`, `HtmlFromRtf`, `InReplyToFirstOnly`, `InvalidMediaType`, `MessageClassNotCarried`, `NamedPropertyValueSetAside`,
`NoInternetMapping`, `PartHeaderNotMapped`, `PartsTooDeep`, `RepeatedHeaderNotMapped`, `ReplyToUnresolved`, `RtfCarriedAsTextRtf`,
`RtfRedundant`, `RtfUndecodable`, `SignatureNotVerifiable`, `StoreBookkeeping`, `SubjectPrefixMarkerRemoved`,
`SubjectWhitespaceTrimmed`, `TextFromRtf`, `ThreadIndexNotBase64`, `TransportHeaderLineSkipped`, `TransportHeaderOverridden`,
`TransportHeadersRewritten`, `ValueOutOfRange`.

## Limits

Embedded messages are converted to a depth of 16 and multiparts walked to a depth of 64; deeper content is left out and reported.
Property values are read as the MAPI layer limits them. The converters allocate in proportion to the message.

## How the conversions are checked

`tools/oxcmail/run.sh` (see its header) runs, over the MSG, PST and OST files of the corpus:

1. **Export.** Every item is converted to EML. OneMail parses it (no defect beyond those already in the received headers), and
   MimeKit 4.18.2 and Python 3.13 `email` read it through the oracle drivers of `tools/oracles`. `check_export.py` compares each
   oracle's subject, From, To, Cc, Date, Message-ID, plain and HTML body hashes and attachment names, sizes and SHA-256 with what
   the source item says. A difference that only one oracle shows and that `tools/oxcmail/explained.tsv` explains (an oracle not
   knowing a charset label of the received header) is listed, never silent.
2. **MAPI to MIME to MAPI** (`OxcMailTool rt-mapi`). Every property of the source is exact, the same text under another string type,
   or listed in the loss report; anything else is a silent difference and fails the run.
3. **MIME to MAPI to MIME** (`OxcMailTool rt-mime`) over message files: ten fields compared before and after (subject, From, To,
   Cc, Date, Message-ID, In-Reply-To, References, text and HTML bodies, attachments); each difference needs a loss entry.
4. **The view** (`OxcMailTool view`): how many senders and recipients resolve to SMTP.
