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
| `Family` | `mapi` (a property of the source), `mime` (a header field or part of the source) or `ics` (a property, parameter or component of an iCalendar source) |
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
Options (`OxcMailImportOptions`): `Tnef` (`Merge`, the default, or `KeepAsAttachment`), `TnefReader` (limits for TNEF parts), `PartHandler`, `MaxEmbeddedDepth` (16), `MaxPartDepth` (64).

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
- **The hook.** `OxcMailImportOptions.PartHandler` sees every leaf part before it is classified; a caller that returns true has dealt with
  the part (it may add properties and attachments to the `MapiMessage` it is given). TNEF parts are handled by the converter itself
  (next section) after the handler has declined them.

### TNEF parts (MS-OXCMAIL 2.2.3.8, MS-OXTNEF)

A leaf part that holds a TNEF stream (found by content, as `MimeEntity.EnumerateTnefParts` does: `application/ms-tnef`, winmail.dat,
or any `application/*` part starting with the signature 0x223E9F78) is unpacked into the message. The rules, in the order the converter
applies them:

1. **The part qualifies** when the stream conforms to MS-OXTNEF (no wrong signature, version, truncation, checksum or limit defect), and its correlation key matches the message's `X-MS-TNEF-Correlator` field
   (step 4, OX21). A part that does not qualify is an ordinary attachment whose content type is `application/octet-stream`
   (step 4), reported as `TnefNotUsed` with the reason. The message is then pure MIME, as the spec says.
2. **Headers first.** The message is built from the header fields exactly as without a TNEF part. Then, for each TNEF property: not set
   yet, it is added as the TNEF reader gave it (named properties keep their name and are renumbered into the message's own named IDs);
   already set, it is ignored and, when its value differs, reported (`TnefPropertyIgnored`, `Overridden`). Properties that come from one
   header field are one group: subject (`PidTagSubject`, prefix, normalised subject), the sender, the sent-representing sender and the
   reply-to entries. When the headers set any of a group, the TNEF's other members of that group are ignored too (OX16).
3. **Bodies.** A body property is added when the MIME side has none of that kind, so a TNEF's compressed RTF or HTML stays next to the plain
   text of the MIME part (2.2: the plain text must not be preferred to a richer TNEF body). `PidTagNativeBody` and `PidTagInternetCodepage` are the
   TNEF's (the author's values) unless the HTML is the MIME part's; then `PidTagNativeBody` is the richest body the message has (HTML, then RTF,
   then text) (OX17).
4. **Message class.** `IPM.Note` is what the converter gives a message nothing says more about; a class the TNEF carries (`IPM.Appointment`,
   `IPM.Schedule.Meeting.Request`, ...) replaces it (`TnefMessageClassUsed`). A class the MIME structure gave (a report, S/MIME) stays (OX18).
5. **Recipients.** The headers' recipients win; the TNEF's recipient table is used only when the headers gave none, and ignored with a
   `TnefRecipientIgnored` entry for each address the headers lack (OX19).
6. **Attachments** of the TNEF (data, embedded messages, OLE storages) are the message's attachments in the TNEF part's place, with
   their properties as read; their 8-bit text keeps the code page of the TNEF. The 8-bit strings that were merged into the message are read
   with the TNEF's code page too (a second TNEF part with another code page has its single 8-bit strings stored as Unicode,
   `TnefCodePageDiffers`).
7. A stream that ends in CR LF outside any attribute is unpacked although 2.2.3.8 step 3 says to ignore it (OX20); the message gets an `Internal`
   `TnefTrailingCrLfIgnored` entry and the view an `AmbiguousTnefTrailingCrLf` defect.
8. The part itself is a `TnefPartUnpacked` entry (`Internal`: the content is carried, the carrier is not).

`OxcMailImportOptions.Tnef = KeepAsAttachment` leaves every TNEF part an attachment as for any binary part.

## MIME from a TNEF message (`OxcMailExportOptions.Tnef`)

`OxcMailTnefExport.TnefMessage` writes the TNEF form of MS-OXCMAIL 2.1.3.7 instead of a pure MIME message: `multipart/mixed` holding a
`text/plain` rendering of the body and one `application/ms-tnef; name="winmail.dat"` part, plus `X-MS-TNEF-Correlator`. The envelope fields
are still header fields (the reader takes them first). The TNEF holds every property, the attachments and the embedded messages; the
top-level recipient table is left to the headers (2.1.3.1.1), so a `Bcc` recipient is not written unless `IncludeBcc` puts it in the header.
Properties the TNEF format cannot carry are reported by name (`TnefWriterLimit`). A message with no plain-text body gets an empty
`text/plain` part (`TnefPlainTextNotRendered`; HTML to text is not done here). The correlation key is the message's own
`PidTagTnefCorrelationKey` when it has one (so the property is unchanged), else a value derived from the message so that output is
reproducible (OX22). Properties that travel in the TNEF are not listed as `NoInternetMapping`. The default is a pure MIME message.

## Outlook calendar and iCalendar: `MapiToIcsConverter`, `IcsToMapiConverter` (MS-OXCICAL)

Both live in `RecursiveAI.OneMail.Conversion.OxcIcal` and are described, with the item model they work on, in [`calendar-items.md`](calendar-items.md).
The `ics` family is the third `Family` value: its `Name` is the iCalendar property (`RRULE`, `ATTENDEE;ROLE`) or component.
Codes added by these two converters:

| Code | Kind | Direction | Meaning |
|---|---|---|---|
| `NoIcalMapping` | Dropped | item to ICS | the MAPI property has no iCalendar form in MS-OXCICAL |
| `NoMapiMapping` | Dropped | ICS to item | the iCalendar property has no MAPI form in MS-OXCICAL |
| `DerivedProperty` | Internal | both | a property the other side recomputes (end time from duration, clean global object ID) |
| `StoreBookkeeping` | Internal | item to ICS | entry IDs, search keys, flags |
| `MessageClassNotCarried` | Dropped | item to ICS | the class is not an appointment, meeting or task |
| `ForwardNotificationHasNoMethod` | Approximated | item to ICS | RFC 5546 has no method for a forward notification |
| `UidSynthesized` | Approximated | item to ICS | no usable global object ID: the UID was made from subject and times |
| `TaskStatusWeakened` | Approximated | item to ICS | waiting and deferred tasks are written IN-PROCESS |
| `RecurrenceNotCarried` | Dropped | item to ICS | a task recurrence with no iCalendar form |
| `RtfBodyNotCarried` | Dropped | item to ICS | the compressed RTF body; the text is the DESCRIPTION |
| `OrganizerWithoutSmtpAddress` | Dropped | item to ICS | an organizer with no SMTP address cannot be a mailto |
| `AmbiguousShortMonth`, `AmbiguousFirstInstance` | Approximated | item to ICS | see ambiguities OC1, OC2 |
| `NoEventOrTask` | Dropped | ICS to item | the calendar holds no VEVENT or VTODO |
| `MissingTime` | Dropped | both | the VEVENT has no DTSTART (the item starts at the epoch) or the item has no start time |
| `UnsupportedCalendar` | Dropped | item to ICS | a recurrence in another calendar (Hijri, Hebrew and the like) or a pattern with no iCalendar form: written as a single event |
| `AttachmentNotCarried` | Dropped | item to ICS | an attachment of the item other than an exception object: iCalendar attachments by value are not written |
| `AttendeeWithoutAddress` | Approximated | ICS to item | an ATTENDEE without a mailto address is kept by name only |
| `AdditionalAlarms`, `AlarmWithoutTrigger` | Dropped | ICS to item | Outlook keeps one reminder (the first alarm); a VALARM without a usable TRIGGER |
| `AlarmAfterStart`, `AlarmRelatedToEnd` | Approximated | ICS to item | a reminder is always before the start: an alarm after the start, or relative to the end, is stored relative to the start |
| `UnknownTimeZone`, `FloatingTimeAssumedUtc` | Approximated | ICS to item | no zone, or a zone not defined: UTC |
| `MethodNotMapped` | Approximated | ICS to item | the METHOD has no message class in MS-OXCICAL 2.1.3.1.1.1 (REFRESH, ADD, DECLINECOUNTER, anything else): an appointment |
| `MultipleRRules`, `RecurrenceNotMapped` | Dropped | ICS to item | the rule does not fit an Outlook recurrence pattern; one event is kept and the entry's reason says which part is outside MS-OXCICAL 2.1.3.2.2 (see "Recurrence rules Outlook cannot hold" below) |
| `DatesWithoutRecurrence`, `ExdateNotOnPattern`, `RdateWithoutException`, `RdatePeriodNotCarried` | Dropped | ICS to item | EXDATE/RDATE with no series, a day the series does not occupy, an added instance that is not the new place of an EXDATEd one, or an RDATE with a period |
| `OverrideWithoutSeries`, `OverrideWithoutTimes`, `OverrideNotOnPattern`, `DuplicateUidNotConverted` | Dropped | ICS to item | a RECURRENCE-ID override that cannot become an exception, or a second VEVENT of one UID that is neither a series nor an override |
| `RangeThisAndFutureNotCarried` | Approximated | ICS to item | `RECURRENCE-ID;RANGE=THISANDFUTURE` changes every later instance; an Outlook exception changes one |
| `SubMinuteTimeDropped` | Approximated | ICS to item | a series keeps its times in whole minutes (MS-OXOCAL 2.2.1.44.5) |
| `ClassNotMapped`, `OrganizerNotMailto`, `ParameterNotMapped` | Dropped | ICS to item | CLASS value, ORGANIZER URI or ATTENDEE parameter with no MAPI field |
| `AttendeeRoleNotCarried`, `AttendeeTypeApproximated`, `PartstatNotCarried`, `RsvpIsPerMessage` | Approximated or Dropped | ICS to item | ROLE=CHAIR and other roles with no recipient type, CUTYPE other than individual and resource (ROOM becomes a resource), PARTSTAT values with no track status, and one response-requested flag for the whole item (MS-OXCICAL 2.1.3.1.1.20.2) |
| `AdditionalAttendeesInReply` | Dropped | ICS to item | a REPLY or COUNTER names one ATTENDEE, the one who answers; the others are not converted |
| `StatusNotExported` | Dropped | ICS to item | STATUS other than CANCELLED only stands in for a missing busy status and is never written back (MS-OXCICAL 2.1.3.1.1.20.23); `CANCELLED` is kept in the appointment state flags |
| `StatusNotCarried` | Approximated | ICS to item | a VTODO STATUS that is not one of the four RFC 5545 values: the task is complete or not started |
| `DtstampAsLastModified` | Approximated | ICS to item | DTSTAMP stands in for the last-modified time |
| `TaskCancelledAsDeferred` | Approximated | ICS to item | a cancelled VTODO is stored as deferred |

### Recurrence rules Outlook cannot hold

An RRULE becomes a recurrence pattern only when it fits a template of MS-OXCICAL 2.1.3.2.2 or means exactly what one of them means: a daily rule
that only filters weekdays with `INTERVAL=1` is the weekly template, `BYDAY=1MO` is `BYDAY=MO;BYSETPOS=1`, and a yearly `BYMONTHDAY` with no
`BYMONTH` and `INTERVAL=1` is the monthly template (RFC 5545 3.3.10 puts that rule in every month). A rule outside them is not converted: the
item is the single event at `DTSTART`, with a `RecurrenceNotMapped` entry whose reason names the cause, one of:

| Cause | Examples |
|---|---|
| a frequency below a day | `FREQ=HOURLY`, `MINUTELY`, `SECONDLY` |
| parts outside the templates | `BYSECOND`, `BYYEARDAY`, `BYWEEKNO`, `RSCALE` and unknown parts |
| several values where one is allowed | `BYHOUR=9,15`, `BYMINUTE=0,30`, several `BYMONTHDAY`, several `BYMONTH` |
| a part the frequency has no use for in Outlook | `BYMONTH` on a monthly rule, `BYMONTHDAY` or ordinal weekdays on a weekly rule, `BYSETPOS` on a daily one |
| a weekday position outside first to fourth and last | `BYDAY=5MO`, `-2FR`, several ordinals |
| the year's own weekday count | a yearly `BYDAY` with no `BYMONTH` (the 20th Monday of the year) |
| an interval beyond Outlook's | daily over 999, weekly or monthly over 99, yearly over 8 |

More than one RRULE is `MultipleRRules`. EXDATE and RDATE need a mapped series (`DatesWithoutRecurrence` otherwise). Nothing in this list is
dropped silently: each case has its entry, and `tools/oxocal` counts them (`docs/calendar-items.md`, "How it is checked").

## Whole stores

`StoreConverter` (`RecursiveAI.OneMail.Conversion.Stores`) applies these converters to every item of a store and adds the store-level
codes (folders, flags, keywords, mbox line endings). The matrix of what each source and target carries, and those codes, are in
[`fidelity.md`](fidelity.md).

## Loss codes

`AddressSpellingFromTransportHeader`, `AddressUnresolved`, `AttachmentByReference`, `AttachmentWithoutData`, `BccNotWritten`,
`BodyLineBreaksNormalized`, `CompositeTypeOnFlatData`, `DateBeforeFileTimeEpoch`, `DateFromDeliveryTime`, `DateSubSecondDropped`, `DateZoneNotStored`,
`DefaultValueNotWritten`, `EmbeddedMessageTooDeep`, `EmptyValueNotWritten`, `ExchangeAddressWrittenAsSmtp`, `ExtraAuthorsNotMapped`,
`GroupFlattened`, `HtmlFromRtf`, `InReplyToFirstOnly`, `InvalidMediaType`, `MessageClassNotCarried`, `NamedPropertyValueSetAside`,
`NoInternetMapping`, `PartHeaderNotMapped`, `PartsTooDeep`, `RepeatedHeaderNotMapped`, `ReplyToUnresolved`, `RtfCarriedAsTextRtf`,
`RtfRedundant`, `RtfUndecodable`, `SignatureNotVerifiable`, `StoreBookkeeping`, `SubjectPrefixMarkerRemoved`,
`SubjectWhitespaceTrimmed`, `TextFromRtf`, `ThreadIndexNotBase64`, `TransportHeaderLineSkipped`, `TransportHeaderOverridden`,
`TransportHeadersRewritten`, `ValueOutOfRange`; TNEF: `TnefCodePageDiffers`, `TnefMessageClassUsed`, `TnefNotUsed`, `TnefPartUnpacked`, `TnefTrailingCrLfIgnored`,
`TnefPlainTextNotRendered`, `TnefPropertyIgnored`, `TnefPropertyWithoutName`, `TnefRecipientIgnored`, `TnefWriterLimit`.

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
5. **TNEF in MIME** (`TnefTool mime`, `tools/tnef/compare_mime.py`, `OxcMailTool rt-tnef`). Each TNEF file of the corpus is wrapped in four
   message shapes (Outlook's, a bare multipart, a single part, one with HTML from MIME). Every property of the TNEF is on the converted
   message exactly as the TNEF reader gave it or has a `TnefPropertyIgnored` entry; the attachments' properties are equal to a fresh read of the
   TNEF; a part that must not be unpacked is kept as an octet-stream attachment. The view's attachments (name, size, SHA-256) are compared with
   tnefparse 1.4.0, tnef 1.4.18, ytnef 2.1.2 and MimeKit 4.18.2 (`MimeKitTnef`: `TnefPart.ExtractAttachments`). `rt-tnef` is `rt-mapi` with the
   TNEF export.

## Contacts and vCards: `ContactToVCardConverter`, `VCardToContactConverter` (MS-OXVCARD)

Both implement `IItemConversion`; the reports use the families `mapi` (a contact property) and `vcard` (a card property, with its group). The mapping
tables, import rules and codes are in [contacts.md](contacts.md). The check (`ContactTool rt-contact`, `rt-vcard`) is the same statement as for MIME:
every property of the source is exact, in the documented derived set, or in the loss report. Codes added by this pair: `PhoneSlotChanged`,
`EmailSlotChanged` (Approximated), `EmptyValueNotWritten`, `DefaultValueNotWritten` (Internal), `PartialDate`, `TimeOfDayDropped`,
`OrgUnitsMerged`, `AddressExtendedMerged`, `PhotoNotInline`, `KeyNotX509`, `GenderIdentityDropped`.
