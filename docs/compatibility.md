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

## Calendar card

`CalendarCard` ([`calendar-card.md`](calendar-card.md)), checked on the calendar corpora (libical, ical.js, biweekly, sabre/vobject, Thunderbird holidays, the
RFC examples) and the generated fixtures `testdata/calendar/card`, the MSG, OLM, TNEF and PST/OST corpora, and Outlook items generated from the
iCalendar files by the MS-OXCICAL import. Cards: 3,524 from the fetched corpora (ics 3,502, msg 2, olm 8 appointments; Add 96, Cancel 161, Counter 65,
DeclineCounter 32, Publish 171, Refresh 64, Reply 309, Request 790, none 1,816, other 10), 0 exceptions, 0 OneHtml parse errors over 7,048 documents.
Recurrence sentences: 336 of 337 real rules described, the other "Repeats (custom rule)"; every described rule of the corpora (1,070) and of 2,700
generated shapes parses back to the same frequency, interval, weekdays, days, month, count and until date. Occurrence lists (50 per series) against the
expansion they are built on and against `recurring-ical-events` 3.8.2 over 2,384 recurring components: 2,357 equal, 14 stopped early by the card's own scan
limit with a typed defect, 13 outside the naive case window, 0 unexplained; against the oracle 2,248 equal, 98 where the expansion itself differs
from the oracle (explained in `recurrence.md`), 0 unexplained. XSS: the 233 sanitiser vectors in every text field (summary, location, description,
URL, conference, attachment and its name, organizer and attendee names and addresses, role, status), rendered in headless Chromium without a sanitiser:
0 scripts run, 0 requests (the raw vectors as control: 42 scripts, 58 requests). The Outlook meeting items in the corpora are few (two appointments in
MSG); the Outlook-item path is checked on generated items only, and Outlook-produced meeting items wait for Outlook.

## 0.3.0-preview.1

Calendar and contacts, the other stores and the Outlook item semantics on read are complete for this stage; the sections below
hold each format's numbers. Fuzzing, 600 s per target with 0 findings: `dbx` 8.1M executions, `olm` 17.5M, `oxocal` 0.3M,
`rtf-html` 5.0M, `jcal-jcard` 12.8M, `xcal-xcard` 19.7M, `contentlines` 20.4M, `calendar` 11.2M, `vcard-view` 11.7M,
`recurrence` 2.1M, plus the display, bodies and composition targets in their sections. Known gaps: no Outlook-produced
calendar, contact or distribution-list fixtures yet (needs Outlook).

## 0.2.0-preview.1

Added since 0.1; the 0.1 section further down lists what that first release read and wrote, and each row there is still current except where a later section says otherwise. Further oracles for these rows, all run as released programs: Python `extract-msg` 0.55.0,
`compressed-rtf` 1.0.7, `RTFDE` 0.1.2.2 and `tnefparse` 1.4.0; `tnef` 1.4.18 and `ytnef` 2.1.2 (distribution packages); `recurring-ical-events`
3.8.2 and `python-dateutil` 2.9.0; Ical.Net 5.2.3 (NuGet, public API). Further corpora, fetched and never committed: the NIST CFReDS Data
Leakage case OST, and MSG, PST and TNEF test files from Apache Tika and Apache POI ([`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md)).
No Outlook has been run yet.

| Format | Read | Write | Differential (unexplained / compared) | Round-trip | Fuzzed | Known gaps |
|---|---|---|---|---|---|---|
| MSG / OFT (MS-OXMSG) | yes | yes | 0 against extract-msg on 58 files, 15,609 properties; view fields 0 on 53 files, 690 fields | property streams re-encode identical 60 / 60 | yes | two fuzz-minimised files from the corpus are refused by the compound file reader (typed defect); writing: property-exact read-back (see below); **Outlook open: not yet verified** |
| PST Unicode, OST (MS-PST: NDB, LTP, messaging) | yes | no | libpff `pffexport` agrees on folders, items, recipients and attachments: 0 differences on the CFReDS OST, Tika `testPST` and `testPST_variousBodyTypes`, and generated ANSI and Unicode stores (128 items); property contexts and tables read with 0 defects (3,771 + 998 + 733 properties) | n/a | node database, tables, messaging | Apache Tika's `testPST.pst` and `testPST_variousBodyTypes.pst` are real Unicode PSTs with 512-byte pages (wVer 23, checked on the header); no ANSI PST and no 4 KiB multi-map store from Outlook is public, so those are checked only on files we generate. libpff cannot read 4 KiB tables whose rows sit in a data tree, so those have no differential yet. Associated (hidden) items are read but libpff does not export them |
| PST ANSI | yes | no | generated stores only (see above) | n/a | yes | no real-world ANSI sample; generated stores only |
| TNEF / winmail.dat (MS-OXTNEF) | yes | yes | 0 against tnefparse, tnef and ytnef: 563 checks, 125 explained | attributes identical 17 / 17; MAPI property-exact 17 / 17 | yes | no TNEF-in-MIME message in the public corpora; checked on generated wrappers (64 messages) |
| Compressed RTF (MS-OXRTFCP) | yes | yes | decompression identical to compressed-rtf on 45 / 45 bodies, CRC 45 / 45; our output read back by both readers 45 / 45 | 97 exact + 1 line-ending | yes | `\objattph` positions become placeholders |
| HTML and text from RTF (MS-OXRTFEX); native RTF to HTML | yes | encapsulation only | against RTFDE on 35 bodies: only line-ending and no-break-space differences (documented); native RTF to HTML on 123 documents: 0 unexplained against LibreOffice 24.2.7, pandoc 3.13 and unrtf 0.21.10 (pandoc/unrtf rules are class-level) | n/a | yes | |
| MAPI and MIME conversion (MS-OXCMAIL) | yes | yes | see the conversion table below | 0 silent losses both ways | yes | |
| ICS recurrence and time zones (RFC 5545 3.8.5, VTIMEZONE, IANA, Windows names) | yes | n/a | 2,384 recurring components: 0 unexplained against recurring-ical-events (2,372 compared), python-dateutil (2,378) and Ical.Net (2,188); every RFC 5545 example passes; DST transitions 108 / 108 in four zones | n/a | yes | |
| jCal, jCard, xCal, xCard (RFC 7265, 7095, 6321, 6351) | yes | yes, with a loss report | against ical.js, Python icalendar, biweekly and ez-vcard in three directions, every difference mapped to a rule ([`jcal-xcal.md`](jcal-xcal.md)); xCal and xCard have one oracle each | form to tree to form per [`jcal-xcal.md`](jcal-xcal.md) | yes | |

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
| ICS (RFC 5545, 5546, 7986), vCard 2.1 / 3.0 / 4.0 | yes (content lines, component trees, typed views; contacts below) | yes | see [`calendar.md`](calendar.md) | 1,372 / 1,372 | yes | recurrence expansion, time zones and the JSON and XML forms are in their own sections below |
| The shared read view, `Mail.Open`, `Mail.Parse`, format sniffing | yes | n/a | agrees with the tree on 8,566 messages; attachment lists agree with Python except documented choices (V1-V8) | n/a | yes | |
| PST / OST node database (MS-PST NDB) | yes (lower layer only) | no | NIST CFReDS OST: every node and block enumerates, header agrees with libpff | n/a | yes | messages from PST and OST are in the view (rows above); ANSI and 4 KiB multi-map stores untested on real files (no public sample) |


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

## OLM (Outlook for Mac) read

Added after 0.2.0-preview.1. Oracles, run as released programs and never linked: the olmreader 1.9.0 jar (Maven Central, BSD-2-Clause) and
the olm-reader 1.0.0 npm package (Apache-2.0). Corpus, fetched and never committed: the two sample archives of the OLMReader project
(BSD-2-Clause; [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md)). No Outlook for Mac has been run.

| Format | Read | Write | Differential (unexplained / compared) | Round-trip | Fuzzed | Known gaps |
|---|---|---|---|---|---|---|
| OLM (Outlook for Mac archive) | yes: mail, appointments, contacts, groups, tasks, notes, categories; attachments as lazy streams | no, by design | jar: 0 on both samples and on two generated archives (outlook_example 38 records, bill_rapp 492, generated 54); npm package: 0 on 485 messages, 12 documented tool differences | records keep every element (`OlmElement` tree); raw MIME source kept as text | yes | no Outlook 2011 layout sample; no archive with a raw MIME source, attendees or exceptions seen; a damaged zip is refused (recovery comes later); the working set is not constant in the entry count: OneArchive holds the zip directory (about 1 KB per entry; 123 MiB at 26,678 entries, 309 MiB at 213,345; see [`olm.md`](olm.md)) |

## Conversions and store to store

See [`fidelity.md`](fidelity.md) for the matrix (generated from the code; a test fails when a reported loss is not in its cell) and the
command line. Checked on the worker with `tools/convert/run.sh`:

| Check | Result |
|---|---|
| MIME to EML / mbox / Maildir / EMLX, read back byte-identical | probes 219/224, Mime4j 78/80, dovecot mbox 3,237/3,237, dovecot messages 2,858/2,859, Enron sample 5,032/5,032, DBX store 2,053/2,053; 0 changed (the skipped have no header block) |
| MAPI to EML to MAPI | MSG 45 items / 8,743 properties and PST 25 items / 2,399 properties: 0 silent; OLM mail 530 items / 10,070 fields: 0 silent |
| Store to store (OST, Tika PSTs, OLM, DBX, dovecot mbox, Maildir, Enron sample) | read = written + skipped everywhere; folder trees equal; Python `mailbox`/`email` agree on folders and counts in 18 of 18 runs |
| Memory | CMU Enron (517,401 messages, 3,311 folders) to Maildir: 119 MB peak; to mbox: 129 MB peak |

Gaps: flags and keywords from Outlook items (`PidTagFlagStatus`, last verb) are mapped by rule and not yet observed in Outlook; MSG and PST
are not destinations until 0.5 (the `IStoreSink` extension point is public).

## Replies and forwards ([`composition.md`](composition.md))

`CreateReply`, `CreateReplyAll` and `CreateForward` (inline and as an attachment) on any item. Each message is made, written, and parsed back by OneMail, MimeKit 4.18.2,
Python 3.13.17 `email` and Go 1.26.8 `net/mail`; subject, From, To, Cc, In-Reply-To and References are compared with what the composer says it wrote
(`tools/composition`). A reply, a reply-all and the two forwards are made of every item.

| Source | Items | Messages made | In-process checks failing | Oracle fields compared (MimeKit / Python / Go), differing | Notes |
|---|---|---|---|---|---|
| Enron maildir sample | 5,032 | 20,128 | 0 | 120,537 / 120,537 / 120,251, 0 / 0 / 0 | 1,839 parents already had a prefix; none stacked; 5,032 embedded originals byte-identical |
| Dovecot mbox | 2,858 | 11,432 | 0 | 68,592 each, 0 | 1,524 prefixed parents; 2,858 embedded originals byte-identical |
| Mime4j test messages (80 files, 4 mbox items) | 78 | 312 | 0 | 1,872 each, 0 | 78 embedded originals byte-identical |
| MSG (Tika, POI) | 45 | 180 | 0 | 1,080 each, 0 | MAPI threading properties unchanged through MAPI to MIME to MAPI on 180 / 180 |
| PST, OST | 25 | 100 | 0 | 600 each, 0 | same, 100 / 100 |
| TNEF | 7 | 28 | 0 | 168 each, 0 | same, 28 / 28 |
| OLM | 490 | 1,960 | 0 | 11,760 each, 0 | records have no MIME source: the embedded original is rebuilt from the view (`ParentSynthesized`, 490) |
| DBX | 2,053 | 8,212 | 0 | 49,272 each, 0 | most records of the corpus have no text (no sender, no ID: `NoReplyTarget` 4,038); 34 non-empty originals byte-identical |

Known gaps: addresses with a doubled or edge dot in the local part (`k..allen@x`) are written as they were; the three oracles drop or cut them (ambiguity A15, A16),
so they are left out of that comparison. Display text that the user would see in other languages (attribution, forward banner) is English unless replaced. The
subject prefix list is an agent default. No mail client has been compared yet (no Thunderbird run); the Outlook oracle will settle the forward-threading and
conversation-index choices. Real Outlook conversation indexes carry the FILETIME from their first byte, which differs from the layout MS-OXOMSG draws (see `composition.md`).

## Display HTML (sanitiser, remote-content policy)

Added after 0.2.0-preview.2. Vectors are our own, written from the categories of public evasion lists (no text copied). DOMPurify 3.4.17 (npm) is run as a released program
only. What the code does is in [`display-html.md`](display-html.md); the sanitiser's independent review passed (see its *Review* section).

| Area | Checked against | Result | Known gaps |
|---|---|---|---|
| Vectors in headless Chrome (per-vector iframes) | 233 vectors: raw page, sanitised with the block policy, sanitised with the allow policy | raw: 42 script executions and 58 requests; block policy: 0 executions, 0 requests; allow policy: 0 executions, 29 paths fetched, none from a removed element | one browser engine |
| DOMPurify 3.4.17, default configuration, same scorer in the same page | the same 233 vectors | ours 0 findings, DOMPurify 89, raw 194; ours looser in 0 categories | |
| Real mail HTML kept | HTML bodies of 45 MSG, 18 PST, 6 TNEF, 5 Mime4j messages, plus 123 RTF-to-HTML outputs | text preserved in every body (0 differ), output unchanged by a second pass (0 unstable); 22 remote references and 4 tracking indications reported in the mail bodies | the CMU Enron maildir has no HTML parts |
| Fuzzing | `sanitizer` (checks the output for banned elements, attributes and URL schemes) and `sanitizer-reparse` (output is a fixed point of parse and sanitise), 600 s each | `sanitizer` 6,533,781 executions, `sanitizer-reparse` 6,917,798, 0 findings (libFuzzer, Checked build). Earlier runs found a `pre` newline that did not survive a re-parse, a deeply nested misnested anchor that never settled (now unwrapped), and two checker false positives | |

## Rendering to PDF and PNG

`RecursiveAI.OneMail.Render` on OneRender 1.3.2 (OneHtml 1.5.1, the same as ours); poppler `pdftotext` and Google Chrome (headless) from the distribution packages, run only. Method and options: [`render.md`](render.md); the checks are `tools/render/run.sh`.

| Check | Input | Result | Known gaps |
|---|---|---|---|
| Every item rendered to PDF and to PNG pages | 8,757 items: MSG/OFT, PST/OST, TNEF, OLM, DBX, Mime4j, dovecot and Enron-sample files (every HTML, text and RTF-derived body), plus 16 generated cases (long and wide tables, 6,000 px image, RTL, CJK, emoji, 10,000 images, 3,000-deep nesting, 100,000-row table, 5,000 columns, 200,000-character word, 30,000-line text) | 8,757 and 16 rendered, **0 exceptions**; 8 files in the corpus folders are not mail (7z parts, three header-less messages, two fuzz-minimised MSGs OneArchive refuses) and are skipped | PNG pages are strips of one layout and can cut a line |
| PDF text against the display HTML's text (`pdftotext -raw`, white space and bidi controls removed, compatibility forms folded) | the same 8,757 | 8,751 equal, 6 the same characters in a different order, **0 missing or extra**: 1 Hebrew/Arabic (visual against logical order), 5 messages built from nested tables with percentage heights, where content-stream order differs from document order | the text check cannot see a glyph the fonts lack: OneRender 1.3.2 draws nothing for it and keeps the text extractable |
| Same with the CJK, emoji, Arabic and Hebrew font packages referenced | the same | identical counts (8,751 / 6); the generated CJK, emoji and RTL cases show their glyphs | without the packages those scripts render blank, silently |
| Generated cases | 16 | 13 equal, 1 reordered (RTL), 2 missing: a 60-column and a 5,000-column table are clipped at the right margin, as print layout does | no scale-to-fit |
| Nothing fetched | remote content allowed by policy, no `Resources` callback; 8,420 items | 10 items ask for 33 remote addresses (images, a background, a style sheet); all are listed in `UnservedRequests`, none is fetched (the loader decodes `data:` and otherwise asks only the caller). With the default policy no item asks for any | |
| Page count against headless Chrome printing the same HTML (A4, 48 px margins) | 75 documents (the first 60 corpus items, 15 generated) | 70 equal, 5 differ: three copies of one Outlook newsletter template with `height:100%` tables (ours 3 pages, Chrome 1), one other mail (3 against 2), the 200,000-character word (39 against 42) | the 100,000-row table could not be printed by Chrome within its limit |
| Page 1 pixels against Chrome (mean absolute grey difference at 48 dpi, 0 identical, 255 opposite) | the same 75 | median 1.6, p90 22.1, max 25.5 | different fonts and line breaking |
| Time and memory (indicative, one process on the worker, not a benchmark row) | the 8,757 items | PDF median 2 ms, p90 7 ms, max 276 ms; PNG median 14 ms, p90 39 ms, max 191 ms; process peak 697 MB resident over the whole sweep | the 3,000-row to 100,000-row hostile cases take 0.3 to 6.3 s and 1,662 pages (PDF) |
| Hostile bodies | 3,000-deep blockquotes, 150 nested tables, 100,000-row table, 10,000 images | the sanitiser's limits cut the deep-nesting, nested-table and 100,000-row cases (`DisplayTruncated`); the 10,000 images render in 0.26 s; `MaxPdfPages`, `MaxPngPages`, `MaxDuration` and a 40-million-pixel page cap bound the rest | |
| PDF structure | the 76 kept PDFs | poppler `pdfinfo` reads all 76 without a message, every one tagged, title = subject | `qpdf --check` and veraPDF were not run (no prebuilt copy on the worker) |
| AOT, trim | `smoke/Aot` NativeAOT publish with warnings as errors | 0 warnings; renders one message to PDF and to PNG (`render: ok`); `.Render` is not referenced by the browser smoke | |

## Speed

MIME rows against MimeKit 4.18.2: see [`benchmarks.md`](benchmarks.md) (geometric mean of the OneMail/MimeKit time ratio
0.091 on x64 and 0.093 on arm64; worst row 0.584).

## Bodies

HTML to text, text to HTML, `format=flowed` (RFC 3676) and best-body selection (`RecursiveAI.OneMail.Bodies`, [`bodies.md`](bodies.md)). Oracles, run as released
programs: `w3m` 0.5.3, `lynx` 2.9.0 (distribution packages) and Python `html2text` 2025.4.15.

| Format | Read | Write | Differential (unexplained / compared) | Round-trip | Fuzzed | Known gaps |
|---|---|---|---|---|---|---|
| HTML to text | yes | n/a | 0 / 461 against each of w3m, lynx and html2text (118 corpus bodies, 43 constructs, 300 generated); every difference classed | n/a | yes (`htmltext`) | rules from linked style sheets are not applied; no column alignment in tables |
| Text to HTML | n/a | yes | output of 1,715 inputs parses with 0 errors in OneHtml | quote levels kept | yes | link detection is conservative by design |
| `format=flowed` | yes | yes | no oracle (Python `email` has none); RFC 3676 cases | encode then decode returns the paragraphs, 600 generated cases | yes | trailing spaces of a paragraph are dropped on encode |
| Best body (MIME, MAPI, TNEF, OLM, DBX items) | yes | n/a | n/a | n/a | yes | no mail client observed for the selection rules (BD1 to BD5) |
