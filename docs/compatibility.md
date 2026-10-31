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

## Speed

MIME rows against MimeKit 4.18.2: see [`benchmarks.md`](benchmarks.md) (geometric mean of the OneMail/MimeKit time ratio
0.091 on x64 and 0.093 on arm64; worst row 0.584).
