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

## 0.2.0-preview.1

Added since 0.1. The 0.1 rows below still hold. Further oracles for these rows, all run as released programs: Python `extract-msg` 0.55.0,
`compressed-rtf` 1.0.7, `RTFDE` 0.1.2.2 and `tnefparse` 1.4.0; `tnef` 1.4.18 and `ytnef` 2.1.2 (distribution packages); `recurring-ical-events`
3.8.2 and `python-dateutil` 2.9.0; Ical.Net 5.2.3 (NuGet, public API). Further corpora, fetched and never committed: the NIST CFReDS Data
Leakage case OST, and MSG, PST and TNEF test files from Apache Tika and Apache POI ([`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md)).
No Outlook has been run yet.

| Format | Read | Write | Differential (unexplained / compared) | Round-trip | Fuzzed | Known gaps |
|---|---|---|---|---|---|---|
| MSG / OFT (MS-OXMSG) | yes | no | 0 against extract-msg on 58 files, 15,609 properties; view fields 0 on 53 files, 690 fields | property streams re-encode identical 60 / 60 | yes | two fuzz-minimised files from the corpus are refused by the compound file reader (typed defect); writing comes later |
| PST Unicode, OST (MS-PST: NDB, LTP, messaging) | yes | no | libpff `pffexport` agrees on folders, items, recipients and attachments: 0 differences on the CFReDS OST, Tika `testPST` and `testPST_variousBodyTypes`, and generated ANSI and Unicode stores (128 items); property contexts and tables read with 0 defects (3,771 + 998 + 733 properties) | n/a | node database, tables, messaging | no public ANSI or 512-byte-page Unicode PST from Outlook exists; those layouts and 4 KiB multi-map stores are checked only on files we generate. libpff cannot read 4 KiB tables whose rows sit in a data tree, so those have no differential yet. Associated (hidden) items are read but libpff does not export them |
| PST ANSI | yes | no | generated stores only (see above) | n/a | yes | no real-world sample |
| TNEF / winmail.dat (MS-OXTNEF) | yes | yes | 0 against tnefparse, tnef and ytnef: 563 checks, 125 explained | attributes identical 17 / 17; MAPI property-exact 17 / 17 | yes | no TNEF-in-MIME message in the public corpora; checked on generated wrappers (64 messages) |
| Compressed RTF (MS-OXRTFCP) | yes | yes | decompression identical to compressed-rtf on 45 / 45 bodies, CRC 45 / 45; our output read back by both readers 45 / 45 | 97 exact + 1 line-ending | yes | `\objattph` attachment positions not applied |
| HTML and text from RTF (MS-OXRTFEX) | yes | encapsulation only | against RTFDE on 35 bodies: only line-ending and no-break-space differences (documented) | n/a | yes | native RTF to HTML (non-encapsulated bodies) comes later |
| MAPI and MIME conversion (MS-OXCMAIL) | yes | yes | see the conversion table below | 0 silent losses both ways | yes | |
| ICS recurrence and time zones (RFC 5545 3.8.5, VTIMEZONE, IANA, Windows names) | yes | n/a | 2,384 recurring components: 0 unexplained against recurring-ical-events (2,372 compared), python-dateutil (2,378) and Ical.Net (2,188); every RFC 5545 example passes; DST transitions 108 / 108 in four zones | n/a | yes | |
| Outlook calendar and task items; DBX; OLM; jCal/jCard/xCal/xCard | not yet | not yet | | | | in progress |

| Outlook calendar, contact and task items; OLM; jCal/jCard/xCal/xCard | not yet | not yet | | | | in progress |

Speed: PST/OST against XstReader.Api, MSG against MsgReader and TNEF against MimeKit all meet the bar on x64 and arm64 (geometric means
0.197 / 0.320, 0.096 / 0.101, 0.312 / 0.287; worst row 0.569). Working set stays near 100 MB (x64) from 1,000 to 50,000 messages (826 MB
store). Details in [`benchmarks.md`](benchmarks.md).

## Windows

The suite also runs on win-x64. Two things behave differently there, both documented: IANA time zone names resolve through the Windows
zone each maps to (CLDR), so instants before the last rule change (US dates before 2007) follow today's rules rather than IANA's
([`recurrence.md`](recurrence.md), Z5); and Maildir files are written with `;` before the info section instead of `:`, which
Windows file names cannot hold, while both are read everywhere ([`mailboxes.md`](mailboxes.md), S19).

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
| Outlook Express DBX (5/6) | yes | no | 0 against undbx 0.21: 2,053 real records (34 with text) in 13 folders, 3,512 generated messages in 12 folders | n/a | dbx | one real store (a Windows XP laptop image, 28 files); OE4 files and deleted messages are not read |
| EMLX and Apple Mail `.mbox` bundles | yes | yes | ground truth 14 / 14 | 14 / 14 | yes | flag bits unverified against a real Apple Mail store |
| ICS (RFC 5545, 5546, 7986), vCard 2.1 / 3.0 / 4.0 | yes (content lines, component trees, typed views; contacts below) | yes | see [`calendar.md`](calendar.md) | 1,372 / 1,372 | yes | recurrence expansion, time zones and j/x forms come later |
| The shared read view, `Mail.Open`, `Mail.Parse`, format sniffing | yes | n/a | agrees with the tree on 8,566 messages; attachment lists agree with Python except documented choices (V1-V8) | n/a | yes | MSG, PST, DBX and OLM items not in the view yet |
| PST / OST node database (MS-PST NDB) | yes (lower layer only) | no | NIST CFReDS OST: every node and block enumerates, header agrees with libpff | n/a | yes | no messages from PST yet; ANSI and 512-byte-page Unicode PSTs untested on real files (no public sample) |
| MSG/OFT, TNEF, OLM, compressed RTF | not yet | not yet | | | | planned |

## Contacts (vCard typed view, Outlook contacts, MS-OXVCARD)

Method and rules: [`contacts.md`](contacts.md). No Outlook has been run. The corpus has two contact items (Tika and POI MSG, the same contact) and no
Outlook contact PST, so the MS-OXOCNTC properties are covered by 56 generated contacts and 8 lists (`ContactTool gen`, seed 20261031). Oracles run black-box:
vobject 0.9.9, FolkerKinzel.VCards 8.2.0 (public API), extract-msg 0.55.0.

| Check | Against | Result |
|---|---|---|
| vCard typed view, fetched corpus (28 .vcf) | vobject / FolkerKinzel.VCards | vobject: 25 compared (3 it cannot parse), 365 agree, 17 explained, **0 unexplained**; FolkerKinzel: 28 compared, 417 agree, 37 explained, **0 unexplained** |
| vCard typed view, generated 2.1/3.0/4.0 (60) + RFC examples + hand fixtures (86 files) | vobject / FolkerKinzel.VCards | vobject: 83 compared, 998 agree, 31 explained, **0**; FolkerKinzel: 86, 909 agree, 81 explained, **0** |
| vCards we produce (132 files from the 2 real and 64 generated items, 3.0 and 4.0) | vobject / FolkerKinzel.VCards | both parse **all 132**; vobject 2,393 agree, 17 explained, **0 unexplained**; FolkerKinzel 2,204 agree, 254 explained, **0** |
| Contact fields (2 real + 56 generated MSG) | extract-msg | 58 contacts, 4,408 field checks, **0 differences** |
| Contact to vCard to contact (3.0 / 4.0) | every property exact, derived or in the loss report | real (2): 44 / 42 exact, 168 / 170 reported, 16 derived, **0 mismatch, 0 silent**; generated (56): 2,016 / 2,002 exact, 713 / 717 reported, 103 / 113 derived, **0 mismatch, 0 silent** |
| vCard to contact to vCard (138 cards) | every card property carried or reported; contact stable on a second round | 1,311 carried, **0 not carried and not reported**; 137 / 138 stable (the one: a card without `FN` gets one composed from `N`) |
| Byte-identical round trip of untouched vCards | the content-line tree | 28 + 86 corpus and generated files and 132 produced cards: all byte-identical |
| Fuzz `vcard-view` | 600 s, `Checked` build | 5,049,889 executions, 0 findings |

Gaps: no Outlook-produced contact PST or MSG with the full property set, and no Outlook import/export of vCards to compare against (the Outlook oracle, before 0.5).
The slot order of telephone numbers and email addresses is approximated by a vCard and reported (`PhoneSlotChanged`, `EmailSlotChanged`).

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
