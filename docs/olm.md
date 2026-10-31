# Outlook for Mac archives (OLM)

`RecursiveAI.OneMail.Stores.Olm` reads `.olm` files, the archives Outlook for Mac exports. Reading only: OneMail never writes
an OLM. This page is the normative description of how a record becomes an item, what is reported and what is left alone.

## What an OLM is

A zip file of XML records and attachment files, read through OneArchive's zip reader (OneMail has no zip code of its own).
The layout in both public sample archives (Outlook 16):

| Entry | Holds |
|---|---|
| `Local/com.microsoft.__Messages/<folder>/message_NNNNN.xml` | one `<emails><email>…</email></emails>` record per file; folders nest to any depth |
| `Local/com.microsoft.__Messages/<folder>/com.microsoft.__Attachments/<id>_NNNN` | the bytes of one attachment, named by the record's `OPFAttachmentURL` |
| `Local/Calendar/Calendar.xml`, `Local/Address Book/Contacts.xml`, `Groups.xml`, `Local/Tasks/Tasks.xml`, `Local/Notes/Notes.xml` | all records of one kind in one file |
| `Categories.xml` | category names and colours |
| `Accounts/…` | empty folders in the samples |

Attachments sit in the zip before or after their message; the reader does not depend on the order. Every XML value is an
attribute or the text of a leaf element; whole numbers and flags are written as `1E0`, `0E0`, `5.0614E4`.

What a file is comes from its content, never its name: a zip with an entry under `Local/` in one of the five trees above is an
OLM (`OlmArchive.IsOlm`, and `MailFormatSniffer.Sniff` returns `MailFormat.Olm`); an XML entry is a record file when its root
element is one of `emails`, `appointments`, `contacts`, `tasks`, `notes`, `groups` or `categories`. A different root is the
`UnrecognisedRoot` defect. Only the Outlook 16 layout has been seen; the reader does not depend on the path beyond the folder
names above, so a layout that keeps the record element names reads the same.

## API

| Call | Does |
|---|---|
| `OlmArchive.Open(path or source, options)` | opens the zip directory; throws `InvalidDataException` when no entry can be listed |
| `OlmArchive.Enumerate()` / `EnumerateAsync()` | the records in zip order, one record built at a time |
| `OlmArchive.GetFolders()` | the folder tree: message folders and the per-kind folders |
| `OlmArchive.Defects` | defects of the archive and of the entries read so far |
| `OlmItem` | a record: the shared view (`IMailItem`), the whole XML tree (`Element`), `Folder`, `MimeSource`, `Categories`, `UnmappedElements()`, `OlmDefects` |
| `OlmToCalendarConverter`, `OlmToVCardConverter` | appointment, task and note to iCalendar; contact and group to vCard; each returns a loss report |
| `Mail.Open("x.olm")` | the same records as `IMailItem`s through `MailContainer`; `onemail inspect x.olm` lists them |

An item and its attachment streams are valid until the archive is disposed. One archive is not thread-safe.

## The shared view

| View member | Source |
|---|---|
| `Kind` | email: message; appointment; contact; task; note; group: distribution list; category: other |
| `Subject` | `OPFMessageCopySubject`; appointment `OPFCalendarEventCopySummary`; contact `OPFContactCopyDisplayName` (else the name parts); task `OPFTaskCopyName`; note `OPFNoteCopyTitle`; group `OPFGroupCopyDisplayName`; category `OPFCategoryCopyName` |
| `From`, `ToRecipients`, `Cc`, `Bcc`, `ReplyTo`, `Sender` | `OPFMessageCopyFromAddresses`, `…ToAddresses`, `…CCAddresses`, `…BCCAddresses`, `…ReplyToAddresses`, `…SenderAddress`: `emailAddress` elements with `OPFContactEmailAddressAddress` and `…Name`. An appointment's attendees are its `ToRecipients`. |
| `Sent`, `TryGetDate` | `OPFMessageCopySentTime` is the client-submit time, `…ReceivedTime` the delivery time; a note's `OPFNoteCopyCreationDate` is its creation time. Dates are UTC with `ZoneKnown` false (O1). |
| `MessageId`, `InReplyTo`, `References` | `OPFMessageCopyMessageID`, `…InReplyTo`, `…References`, without angle brackets |
| `ConversationIndex`, `ConversationTopic` | `OPFMessageCopyThreadIndex` (base64), `…ThreadTopic` |
| `TextBody`, `HtmlBody` | email: the body element, as HTML when `OPFMessageGetHasHTML` is set, else as plain text (O2); appointment, contact and task: the `…Plain` element and the HTML element; note: its text is HTML. Bodies are the XML's Unicode text presented as UTF-8; the original character set is not in the file. |
| `Attachments` | the `messageAttachment` elements: `OPFAttachmentName`, `…ContentType`, `…ContentID`; the stream is the zip entry named by `OPFAttachmentURL`; size is the entry's size (O4); an attachment whose entry is missing is listed with no content (O9) |
| `Provenance` | format Olm, the file, the folder, the zip entry's local header offset and size, the item's index |
| `MimeSource` | `OPFMessageCopySource` when the record has one, as the XML text decodes. Neither sample archive has one; the element is in the published schema. |

## Calendar and vCard mappings

`OlmToCalendarConverter`: appointment to `VEVENT`, task to `VTODO`, note to `VJOURNAL`; `OlmToVCardConverter`: contact to a vCard 3.0,
group to a vCard 4.0 with `KIND:group`. Both return a loss report (`docs/conversion.md`) with one `Dropped` entry for every top-level
element they do not read (code `Unmapped`) and an `Approximated` entry wherever a value is written in a weaker form.

| OLM | iCalendar / vCard |
|---|---|
| appointment `UUID`, `Summary`, `Location`, `DescriptionPlain`, `Description` | `UID`, `SUMMARY`, `LOCATION`, `DESCRIPTION`, `X-ALT-DESC;FMTTYPE=text/html` |
| `StartTime`, `EndTime` (wall-clock) with `GetStartTimeZoneICSData` | `DTSTART`/`DTEND` with `TZID`, and the record's `VTIMEZONE` copied in; no zone data: floating times (`FloatingTime`); `GetIsAllDayEvent`: `VALUE=DATE` with the exclusive end (`AllDayEnd`); a different end zone is not kept (`EndZoneIgnored`) |
| `Recurrence` / `RecurrencePattern` Daily, Weekly (`DaysOfWeek`), AbsoluteMonthly, AbsoluteYearly | `RRULE` `FREQ`, `INTERVAL`, `BYDAY`, `BYMONTHDAY`, `BYMONTH`; `COUNT` when the record is numbered, else `UNTIL` as the UTC end date (O8); RelativeMonthly and RelativeYearly are mapped by name with 5 meaning last (`RelativeRecurrence`); another type is dropped (`RecurrenceKind`) |
| `HasReminder` with `ReminderDelta` (seconds) | a `VALARM` display alarm with a negative `TRIGGER` |
| `IsPrivate`, `Organizer`, attendees, category list | `CLASS:PRIVATE`, `ORGANIZER` (when it is an address), `ATTENDEE` with `CN` (status, type and response flags are not mapped: `AttendeeStatus`), `CATEGORIES` |
| task name, notes, start, due, completed, reminder, recurrence | `SUMMARY`, `DESCRIPTION`, `X-ALT-DESC`, `DTSTART`, `DUE`, `COMPLETED` with `STATUS:COMPLETED`, `VALARM`, `RRULE`; task dates are written as the UTC instants they hold |
| note title, creation date, text | `SUMMARY`, `DTSTART`, `X-ALT-DESC` (the text is HTML; there is no plain text) |
| contact names, nickname, company and department, business title | `FN`, `N`, `NICKNAME`, `ORG`, `TITLE` |
| contact phones (business 1 and 2, home 1 and 2, fax, cell, pager, other, assistant) | `TEL` with `TYPE` |
| contact e-mail list (the numbered repeats are not written twice) | `EMAIL` with `TYPE` work, home or other (O7) and `pref` for the default |
| contact home, business and other address | `ADR;TYPE=home`, `work`, `postal` |
| contact web pages, birthday, anniversary, spouse, notes, categories, modification time | `URL`, `BDAY` and `X-ANNIVERSARY` (the UTC date, `DateOnlyZone`), `X-SPOUSE`, `NOTE`, `CATEGORIES`, `REV` |
| group name, members | `FN`, `KIND:group`, one `MEMBER:mailto:` per member (the member's name is dropped: `MemberName`) |

### What is not mapped

Nothing is lost: every element stays in `OlmItem.Element`. Counts of top-level elements neither the shared view nor a conversion
reads, from `OlmTool stats` over the two samples:

| Kind | Items | Distinct unmapped element names | Unmapped elements (total) | Typical members |
|---|---|---|---|---|
| email | 7 + 483 | 17 and 14 | 101 and 6,762 | read flag, priority, flag status, modification date, inference class, read-receipt flags |
| appointment | 8 | 12 | 80 | free/busy, travel time, accept status, dismiss and original reminder times, time-zone defaults |
| contact | 5 + 1 | 34 and 6 | 60 and 6 | custom fields, astrology sign, blood type, children, IM addresses, radio phone, image flag, edit flags |
| group | 2 | 6 | 8 | flag status, suppress-names flag, primary category |
| task | 5 | 2 | 6 | priority, primary category |
| note, category | 1, 18 | 0 | 0 | |

## Limits

All are on by default (`OlmReaderOptions`); a limit that is hit is a typed defect and the rest of the archive is still read.

| Limit | Default | What happens |
|---|---|---|
| `MaxXmlBytes` (decompressed bytes of one XML entry) | 1 GiB | `XmlTooLarge`; the records before the cut are kept |
| `MaxXmlDepth` | 64 | `XmlTooDeep`; that record is dropped |
| `MaxElementsPerItem` | 1,000,000 | `XmlTooManyElements`; that record is dropped |
| `MaxTextLength` (one text run or attribute) | 256 Mi characters | `XmlTooLarge`; that record is dropped |
| `MaxExpansionRatio` (decompressed per compressed byte, from OneArchive) | 1000, after 32 MiB | `ArchiveLimitExceeded` or `EntryUnreadable` |
| `MaxEntryCount`, `MaxTotalSize`, `MaxCentralDirectorySize` | 50 million, 1 TiB, 4 GiB | `ArchiveLimitExceeded`; entries listed before the limit are kept |

XML is read with DTDs ignored and no resolver, so an entity bomb has nothing to expand; invalid characters are read, not refused.

## Defects

`XmlMalformed`, `XmlTooLarge`, `XmlTooDeep`, `XmlTooManyElements`, `UnrecognisedRoot`, `ElementCountMismatch` (the container's
`elementCount` is not what was found), `EntryUnreadable` (a damaged, encrypted or unsupported entry, or a failed checksum),
`ArchiveLimitExceeded`, `ArchiveUnreadable`, `AttachmentMissing`, `BadDate`, `BadNumber`, `AddressMissing`, `UnrecognisedEntry`
(a link or device entry), and the ambiguity defects `AmbiguousDateZone` and `AmbiguousBodyKind`. The ambiguity rows are O1 to O9
in [`ambiguities.md`](ambiguities.md). Through the shared view they surface as `MailDefect` with family `olm`.

## Memory

Records, attachments and bodies stream: one record is built at a time and attachments are lazy streams over zip entries, so
the working set does not depend on the size of the data. It does depend on the **number of zip entries**, because OneArchive's
zip reader keeps the whole central directory in memory (about 1 KB per entry) and OneMail adds a table of attachment entries
and a list of record entries on top (about 0.3 KB per entry). Measured on the worker (AMD Ryzen 9 7950X3D, .NET 10.0.402,
Release, workstation GC), generated archives of stored entries (`tools/olm/memory.sh`), every field of every item read and every
attachment streamed:

| Archive | Entries | Zip reader alone, peak working set | OneMail, peak working set | Allocated per item |
|---|---|---|---|---|
| 20,000 messages, 191 MiB | 26,678 | 62 MiB | 123 MiB | 84 KB |
| 80,000 messages, 762 MiB | about 107,000 | not measured | 197 MiB | 84 KB |
| 160,000 messages, 1,524 MiB | 213,345 | 242 MiB | 309 MiB | 84 KB |

So the working set is flat in data size and per-item state, and grows with entry count at about 1.3 KB per entry; it is **not
constant in the number of entries**. A lazy directory in the zip reader would remove that term; it is raised with OneArchive's
owners rather than worked around with a second zip reader here.

## How it was checked

Black-box, against the olmreader 1.9.0 jar (Maven Central, sha1 `da66d6ee52b2e1782eb338772a0cdd998d874393`, BSD-2-Clause) driven by
`tools/olm/OlmDump.java`, and the olm-reader 1.0.0 npm package (Apache-2.0) run as `olm-extract` with `TZ=UTC`. Neither's source was
read. `tools/olm/run.sh` runs both over the two sample archives (`tools/corpus/fetch-olm.sh`) and two archives written by
`tools/olm/make_olm.py`. A generated archive is also a schema check: the jar validates every record against the published schemas.

| Archive | Records | Jar: compared / unexplained | npm package: messages paired / unexplained |
|---|---|---|---|
| `outlook_example.olm` | 38 | all fields of 7 emails, 8 appointments, 5 contacts, 5 tasks, 1 note, 2 groups, 10 categories / **0** | 7 / **0** |
| `bill_rapp.olm` | 492 | 483 emails, 1 contact, 8 categories / **0** | 483 / **0** |
| generated, seed 1 | 54 | 40 emails (nested folders, non-ASCII, 14 attachments), every PIM kind / **0** | 40 / **0** |
| generated, seed 2 (stored, 70 KB attachments) | 74 | 60 emails and every PIM kind / **0** | 60 / **0** |

Compared per email: subject, From, To, Cc, Bcc, Sender, sent and received time, Message-ID, body, and every attachment's name,
size and SHA-256; per other kind the fields the jar exposes (summary, location, UUID, start, end, names, due date, members).

Explained differences, all in the oracles:

- The jar keys attachment bytes by name, so two attachments with one name collapse to one (1 message in the Enron sample).
- The jar gives no folder; the npm package's EML path does, and every message's folder matches.
- The npm package reads dates in the machine's zone, writes `unknown@unknown.com` for a missing sender and `No subject`,
  drops `<…>` runs and addresses it cannot parse (446 recipients in the Enron sample), writes non-SMTP address text as an address
  (118), writes HTML bodies as text, adds no attachments to its EML, and in one message inserts a bullet character (12 classes in
  `tools/olm/compare_npm.py`, each counted in the run's output).

Fuzzing: the `olm` target (zip built around mutated record XML, or a mutated whole zip) ran 600 s in the `Checked` build: 9.18 million
executions, no findings. An earlier run found two escapes, both fixed and kept as seeds in `testdata/fuzz/olm`: a line break in a
UID or address reaching the calendar writer, and an XML reader failure on a UCS-4 entry that was not caught. The full `Checked`
suite (2,130 tests) passes.

## Not done

Recovery of a damaged zip (it uses OneArchive's recovery mode, which is not in the pinned release), Outlook 2011 layouts (no sample
archive), archives with a raw MIME source, attendees or recurrence exceptions (none seen), and a typed view of the many flag
elements. No Outlook for Mac has been run, so the date zone, the e-mail slot numbering and the relative-recurrence numbering are
agent defaults.
