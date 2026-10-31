# Fidelity and compatibility

What OneMail reads and writes today, how each format is checked, and the gaps we know about. This table is updated with every
preview; a gap is listed here, never hidden.

**Oracles** (run as released programs, black-box): MimeKit 4.18.2 (NuGet), Python 3.13.17 `email` and `mailbox`, Go 1.26.8
`net/mail` and `mime/multipart`, Python `icalendar` 7.3.0 and `vobject` 0.9.9, libpff tools 20180714. Differences between
OneMail and an oracle are each mapped to an entry in [`ambiguities.md`](ambiguities.md), a documented oracle limit or a
normalisation of the comparison; "unexplained" counts the rest.

**Corpora:** the CMU Enron maildir (May 2015 version, 517,401 messages), Dovecot's `dovecot-crlf` mbox (2,859 messages by
the oracles' split), Apache James Mime4j test messages (80), our generated probes, and the calendar data files listed in
[`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md). None of these is committed; `tools/corpus/` fetches them by pinned URL and hash.

## 0.1.0-preview.1

| Format | Read | Write | Differential (unexplained / compared) | Round-trip of untouched input | Fuzzed | Known gaps |
|---|---|---|---|---|---|---|
| MIME / EML (RFC 5322, 2045-2049, 2047, 2231, 6532) | yes | yes | 0 against each of MimeKit, Python, Go on 517,401 + 2,859 + 80 + 224 messages | byte-identical: 517,401 / 517,401; 2,859 / 2,859; 80 / 80 | reader, tree | ambiguity resolutions are provisional until mail clients are observed |
| Charsets and encoded words (WHATWG encodings, UTF-7, IMAP UTF-7, ISO-2022-JP, RFC 2047/2231) | yes | yes | 0 against MimeKit (22,000 cases), Python (23,973), Go (21,412) | n/a | yes | |
| Structured headers (addresses, dates, message ids, parameters) | yes | yes | included in the MIME row | n/a | via tree | |
| multipart/report: DSN (RFC 3464), MDN (RFC 8098), ARF (RFC 5965) | yes | yes | 0 against MimeKit and Python on 125 messages | 125 / 125 | yes | no real-world reports in the corpora; checked on generated and RFC-derived fixtures |
| MHTML (RFC 2557) | yes | yes | 0 (same run); URL resolution 0 against Python `urljoin` on 218 pairs | yes | yes | |
| `Authentication-Results` (RFC 8601), `Received-SPF` (RFC 7208) | yes | no | 0 against MimeKit (34 fields); Python has no parser | n/a | yes | `Received-SPF` has no oracle |
| mbox: mboxo, mboxrd, mboxcl, mboxcl2, Gmail Takeout labels (RFC 4155) | yes | yes | message boundaries: 0 against Python and MimeKit on 17 files, 3,798 messages | record copy 16 / 16 | yes | MMDF not supported |
| Maildir (Courier and Dovecot flags and keywords) | yes | yes | 0 against Python `mailbox` | 48 / 48 | via store tests | Courier keyword file line format unverified against a real store; one directory listing held in memory |
| EMLX and Apple Mail `.mbox` bundles | yes | yes | ground truth 14 / 14 | 14 / 14 | yes | flag bits unverified against a real Apple Mail store |
| ICS (RFC 5545, 5546, 7986), vCard 2.1 / 3.0 / 4.0 | yes (content lines and component trees) | yes | see [`calendar.md`](calendar.md) | 1,372 / 1,372 | yes | recurrence expansion, time zones and j/x forms come later |
| The shared read view, `Mail.Open`, `Mail.Parse`, format sniffing | yes | n/a | agrees with the tree on 8,566 messages; attachment lists agree with Python except documented choices (V1-V8) | n/a | yes | MSG, PST, DBX and OLM items not in the view yet |
| PST / OST node database (MS-PST NDB) | yes (lower layer only) | no | NIST CFReDS OST: every node and block enumerates, header agrees with libpff | n/a | yes | no messages from PST yet; ANSI and 512-byte-page Unicode PSTs untested on real files (no public sample) |
| MSG/OFT, TNEF, OLM, DBX, compressed RTF | not yet | not yet | | | | planned |

## MS-OXCMAIL conversions (MAPI to MIME and back)

Checked on the 45 Tika and POI MSG files and the 25 Tika and CFReDS PST/OST items that are mail, and on message files for the other
direction. Method and rules: [`conversion.md`](conversion.md). No Outlook has been run; the oracles are MimeKit 4.18.2 and Python 3.13.17
`email`, black-box.

| Direction | Checked against | Result | Known gaps |
|---|---|---|---|
| MAPI to MIME (EML) | OneMail, MimeKit and Python read each exported EML; subject, From, To, Cc, Date, Message-ID, text and HTML body hashes, attachment names, sizes and SHA-256 compared with the source item's | 70 / 70 items parse with no defect the received headers did not already have; 0 unexplained differences in 630 fields per oracle (MSG 405 + PST 225); one field in MimeKit differs for a received Subject that uses the charset label `Cp1251` (listed in `tools/oxcmail/explained.tsv`; Python and OneMail agree with the source) | EX addresses with no SMTP form in the item are left out and reported (30 `AddressUnresolved` entries over the 70 items); Bcc is not written by default |
| MAPI to MIME to MAPI | every property of the source: exact, same text under another string type, or in the loss report | 11,142 properties: 6,406 exact, 446 same text, 4,290 reported, **0 silent** | `PidTagTransportMessageHeaders` is rewritten (reported) |
| MIME to MAPI to MIME | ten fields before and after (subject, From, To, Cc, Date, ids, bodies, attachments) on Mime4j 80, Dovecot 1,430, Enron sample 5,032 messages | 65,420 fields: 65,392 equal, 28 differ and each has a loss entry, **0 silent** | Bcc dropped on the way out (reported); display-name spelling of a kept received header |
| The view | `MapiMailItem` senders and recipients | all 50 EX recipients and 16 of 28 EX senders resolve to SMTP with the X.500 address kept; the other 12 senders (all in the MSG set) have no SMTP form; a further 9 PST senders have no address at all | no directory lookup |

## Outlook calendar and task items (MS-OXOCAL, MS-OXOTASK, MS-OXCICAL)

| Area | Checked against | Result | Known gaps |
|---|---|---|---|
| Typed views: appointments, meetings, tasks | extract-msg 0.55.0 on the corpus items (subject, location, attendees, task status and dates) | 14 of 14 comparable fields agree, 4 not exposed by the oracle (start, end), 0 unexplained | the corpus holds 2 distinct appointment and 2 task items and no meeting or recurring item: those are covered by generated items following the MS-OXOCAL examples |
| Recurrence blob and time zone structures | MS-OXOCAL 4.1.1 weekly and daily examples | byte for byte; TZ offsets for Pacific, Sydney, no DST | no Outlook-written recurring item yet |
| Item to ICS, expansion | 18 recurrences in 3 zones (54 series) against dateutil 2.9.0.post0, recurring-ical-events 3.8.2, Ical.Net 5.2.3 | dateutil 54/54, recurring-ical-events 54/54, Ical.Net 38/54 (16 explained: window wall clock 5, outvoted by the other two 11), 0 unexplained | |
| ICS we write | Python icalendar 7.3.0, Ical.Net 5.2.3 parse | 58 of 58 files parse in both; icalendar facts identical 58/58; Ical.Net facts identical 54/58 (the rest are typed properties its public API does not list) | |
| Item to ICS to item, ICS to item to ICS | `OxcIcalRoundTripTests`, `OxocalTool` | 13 generated items property-exact, corpus items 4 of 4 stable, generated ICS 54 of 54 stable on the second pass, 0 silent | Outlook-written meetings and exceptions unseen |

## Speed

MIME rows against MimeKit 4.18.2: see [`benchmarks.md`](benchmarks.md) (geometric mean of the OneMail/MimeKit time ratio
0.091 on x64 and 0.093 on arm64; worst row 0.584).
