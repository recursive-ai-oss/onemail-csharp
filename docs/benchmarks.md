# Benchmarks: MIME rows

`bench/OneMail.Benchmarks` compares OneMail with MimeKit 4.18.2 (a released NuGet package, referenced only from `bench/`) on
the same bytes, in memory, with BenchmarkDotNet 0.15.8. `bench/run-mime-rows.sh OUTDIR` builds, runs every row
(3 launches, 3 warm-up and 10 measured iterations each), measures peak memory in separate processes and prints the table.
Run it alone on the machine. The ratio is OneMail median time / MimeKit median time; below 1 is faster. The bar is a geometric
mean ≤ 1.0 across rows and no row above 1.25.

## Inputs

Picked deterministically from the corpora that `tools/corpus/fetch.sh` fetches (never committed):

| Name | What |
|---|---|
| `enron-2k`, `enron-20k` | the first file, in path order, of the 1-in-100 Enron sample of 1.5–2.5 KB and of 15–25 KB |
| `dovecot-multipart` | the first multipart message of 40–300 KB in the Dovecot test mbox |
| `generated-attachment-6m` | a text part and a 4.5 MB random attachment as base64 (about 6 MB), fixed seed |
| mbox | the whole Dovecot test mbox, 2,859 messages |

## What each side does

| Row | OneMail | MimeKit |
|---|---|---|
| parse | `MimeEntity.Parse` over the bytes | `MimeMessage.Load` over a `MemoryStream` of the bytes |
| headers | parse, then Subject, From and To (address lists), Date, Message-ID, decoded | parse, then the same five properties |
| bodies | parse, then `GetDecodedContent()` of every leaf | parse, then `Content.DecodeTo` a fresh `MemoryStream` for every `MimePart` in `BodyParts` |
| write | `WriteTo(Stream.Null)` of a parsed, unmodified message (parse not timed) | `WriteTo(Stream.Null)` of a parsed message (parse not timed) |
| build | a multipart with a text part and a 1 MB attachment (base64), written to `Stream.Null` | the same with `Multipart`, `TextPart`, `MimePart` |
| mbox | one shared splitter (a `From ` line after a blank line opens a message), then each message parsed whole | the same splitter, then `MimeMessage.Load` of each range |

Things that make the rows differ in kind, not only speed:

- OneMail's parse reads headers and structure and leaves bodies in the input as ranges; MimeKit's default parse copies bodies
  into content streams. The parse and write rows therefore reward the lazy design; the bodies row is the like-for-like one.
- MimeKit re-serialises its model on write; OneMail copies untouched ranges, which is what keeps output byte-identical.
- The mbox row is parse per message over a split, not a mailbox reader. MimeKit's own mbox-mode parser is not used: it
  starts a message at every `From ` line, including ones inside bodies, and throws on the fragments of this file.
- Peak memory is measured by running one row's work in a loop for four seconds in its own process. The working-set figure is
  dominated by the runtime's allocation budget on a machine with a large cache, so read the allocation columns for memory behaviour.

## Results (first run, no tuning needed)

Medians in ns per operation (per message for mbox); B/op is bytes allocated per operation.

| Row | Input | OneMail x64 | MimeKit x64 | ratio x64 | ratio arm64 | OneMail B/op | MimeKit B/op |
|---|---|---:|---:|---:|---:|---:|---:|
| parse | enron-2k | 1,020 | 13,451 | 0.076 | 0.106 | 5,280 | 19,448 |
| parse | enron-20k | 865 | 21,389 | 0.040 | 0.052 | 4,360 | 39,360 |
| parse | dovecot-multipart | 4,830 | 68,927 | 0.070 | 0.110 | 9,920 | 171,802 |
| parse | generated 6 MB | 96,646 | 1,924,495 | 0.050 | 0.072 | 5,344 | 6,318,608 |
| headers | enron-2k | 2,797 | 16,699 | 0.167 | 0.205 | 10,328 | 22,344 |
| headers | enron-20k | 2,267 | 22,356 | 0.101 | 0.110 | 8,328 | 41,400 |
| headers | dovecot-multipart | 6,588 | 76,763 | 0.086 | 0.128 | 15,088 | 174,570 |
| headers | generated 6 MB | 156,043 | 1,877,793 | 0.083 | 0.073 | 10,688 | 6,321,655 |
| bodies | enron-2k | 1,393 | 13,371 | 0.104 | 0.125 | 8,160 | 20,904 |
| bodies | enron-20k | 3,723 | 21,579 | 0.173 | 0.128 | 49,496 | 101,208 |
| bodies | dovecot-multipart | 154,182 | 321,836 | 0.479 | 0.351 | 503,617 | 556,690 |
| bodies | generated 6 MB | 6,057,218 | 13,025,692 | 0.465 | 0.331 | 16,778,003 | 18,583,957 |
| write | enron-2k | 63 | 1,598 | 0.039 | 0.033 | 184 | 1,752 |
| write | enron-20k | 352 | 14,862 | 0.024 | 0.018 | 184 | 4,696 |
| write | dovecot-multipart | 1,965 | 88,760 | 0.022 | 0.020 | 184 | 6,912 |
| write | generated 6 MB | 94,852 | 3,611,187 | 0.026 | 0.022 | 184 | 5,912 |
| build | text + 1 MB attachment | 530,959 | 908,433 | 0.584 | 0.331 | 4,112,652 | 36,782 |
| mbox | dovecot, per message | 2,792 | 23,410 | 0.119 | 0.184 | 8,475 | 28,393 |

Geometric mean 0.091 (x64, AMD Ryzen 9 7950X3D, 32 logical cores) and 0.093 (arm64, Apple M2, 8 cores); worst row 0.584 and 0.351.
The one row where OneMail allocates more is `build`: it base64-encodes the attachment when the content is set, where MimeKit
encodes while writing.

# Benchmarks: Outlook-format rows (PST/OST, MSG, TNEF)

`bench/run-outlook-rows.sh OUTDIR` runs these rows. They use the same harness as the MIME rows (BenchmarkDotNet 0.15.8, 3
launches, 3 warm-up and 10 measured iterations; the 10k and 50k stores use 3 launches of 1 warm-up and 3 iterations because one
walk takes seconds), the same ratio (OneMail median / comparator median, below 1 is faster) and the same bar: geometric mean ≤ 1.0
per area, no row above 1.25. Arguments after `OUTDIR` replace the default BenchmarkDotNet filter and skip the extra steps.

## Comparators

Each is the released NuGet package, referenced only from `bench/` and used through its public API. Versions are pinned in
`Directory.Packages.props`.

| Area | Comparator | Version | Licence | Benchmarking permitted |
|---|---|---|---|---|
| PST/OST | `XstReader.Api` | 1.0.6 (2022-12) | Microsoft Public License (MS-PL): "Copyright (c) 2016, Dijji; 2021, iluvadev, and released under Ms-PL License" | Yes: MS-PL grants the right to use the software; nothing is copied, bundled or redistributed |
| MSG | `MsgReader` | 6.2.0 | MIT | Yes |
| TNEF | `MimeKit` (`TnefPart`) | 4.18.2 | MIT | Yes |
| PST/OST, native reference | libpff `pffexport` | 20180714 (Ubuntu noble `pff-tools`) | LGPL-3.0-or-later | Run as a binary only; reported, not gating |

The comparator packages pull in a `System.Security.Cryptography.Pkcs` build that carries an advisory (NU1902/NU1903). The bench
project is never shipped, so it suppresses those two audit warnings and nothing else.

## Inputs

| Name | What |
|---|---|
| `cfreds-ost` | the NIST CFReDS informant's Outlook 2013 OST (14 messages, 49 folders); x64 only (the fetch needs a 7-Zip and 20 GB) |
| `tika-pst`, `tika-bodytypes` | Apache Tika's two Unicode PSTs (7 and 4 messages) |
| `gen-u1k`, `gen-u10k`, `gen-u50k` | generated Unicode stores of 1,000, 10,000 and 50,000 messages (17 MB, 165 MB, 826 MB) over 20 folders; per message a plain body, an HTML body for every second one, one to four recipients, and an attachment on 70 % (2 to 22 KB, one in 500 is 1 MB); 50,000 messages carry 490 MB of attachments |
| `gen-k4-1k`, `gen-k4-10k` | the same mail in the 4 KiB-page layout (OneMail only, see below) |
| `msg-smallest`, `msg-median`, `msg-largest`, `msg-corpus` | the 53 Tika and POI MSG files that both libraries open (of 58): the smallest, the median and the largest by size (one has 1,321 recipients), and all 53 per operation |
| `poi-*`, `tika-testwinmail` | the six TNEF streams of the POI and Tika corpora with content (the 53-byte `oom.tnef` has none) |

The generated stores come from the test-side PST writer (`tests/OneMail.Tests/Pst`), which now leaves a free slot for each AMap
page a larger file needs (MS-PST 2.2.2.7.2) and sets `MSGFLAG_HASATTACH`. libpff 20180714 opens the stores (`pffinfo`, and
`pffexport -m items` exports them), OneMail and XstReader read them to the same counts, and a unit test builds a 20 MB store in every
layout and reads it back with no defects. libpff's recovery scan (`pffexport -m all`) fails on them, so the reference below uses
`-m items`. The writer holds the file in one array, so the largest store it can make is about 1.5 GB; the 826 MB store is the
largest measured.

## What each side does

Every message: subject, sender name and address, submit time, every recipient (name and address), the body, and every attachment's
bytes read to the end into a discarded buffer. A `verify` step runs both sides over every input first and prints what each saw
(`verify.tsv`); messages, recipients, attachments and attachment bytes agree everywhere except as listed below.

| Row | OneMail | Comparator |
|---|---|---|
| PST | `PstStore.TryOpen(path)`, every folder, `EnumerateMessages(Normal)`, best body (stored HTML, else RTF decompressed and de-encapsulated, else text), `OpenData()` per attachment | `new XstFile(path)`, `RootFolder`, `Folders`/`Messages` recursively, `Body.Text`, `SaveToStream` into a counting stream per file attachment; `message.ClearContents()` off |
| MSG | `MsgFile.TryOpen(path)`, then the same fields and all three bodies (text, HTML from the property or de-encapsulated from RTF, RTF decompressed) | `new Storage.Message(path)`, `Subject`, `Sender`, `Recipients`, `SentOn`, `BodyText`, `BodyHtml`, `BodyRtf`, `Attachment.Data` |
| TNEF | `TnefMessage.Read(bytes)`, then the PST fields | `TnefPart.ConvertToMessage()` over the bytes, then the message's fields, body and `Attachments` decoded to a counting stream |

Things that make the rows differ in kind or in work:

- **`ClearContents`.** XstReader documents `ClearContents()` as the way to release what a message cached. On the 50,000-message store
  it made the walk 14 times slower (139 s against 9.9 s) and its peak working set no lower (157 MiB against 150), so the timed rows
  leave it off: the comparator's fastest correct use. The memory section shows both.
- **`XstFile.Dispose`.** It starts a background release that reopens the file; in a loop over a short walk that thread threw after
  the file was closed and ended the process, on Linux and on macOS. The rows leave the file to the garbage collector instead.
- **4 KiB layout.** XstReader.Api 1.0.6 throws `XstException: Unexpected header in compressed data stream` on every 4 KiB-page store
  this writer makes, so those rows have no comparator and no ratio (OneMail alone: 1,000 messages in 92 ms on x64 and 100 ms on
  arm64, 10,000 in 1.19 s and 1.35 s). No real 4 KiB store was available to tell a writer fault from a reader gap
  (libpff 20180714 opens the layout but cannot read tables stored in data trees, which these stores use).
- **MSG corpus.** MsgReader read 40 attachments (268,647 bytes) where OneMail read 41 (274,743 bytes) across the 53 files: one
  6,096-byte attachment is not handed out by MsgReader. The ratio is on slightly unequal work, in the comparator's favour.
- **Folders.** The folder counts differ (OneMail also counts search folders, 49 against 41 in the OST); messages agree.
- **PST bodies.** XstReader hands out one body (`Body.Text`); OneMail's PST row reads the best one, so the work is matched. MSG
  reads all three on both sides because MsgReader computes all three on request.
- **Memory.** Peak working set is measured in a separate process that repeats one row's work for at least four seconds, with
  `DOTNET_GCgen0MaxBudget` set to 32 MiB so the first-generation budget of a large-cache machine does not hide the live set. A
  background thread samples the working set every 10 ms (the runtime's own peak counter reads 0 on macOS). Single-message
  rows (MSG, TNEF) sit on the runtime's baseline and say nothing; the PST rows and the curves are the memory evidence.

## Results

Medians; B/msg is bytes allocated per message (for `msg-corpus`, per file). Ratios are OneMail / comparator.

| Area | Input | Msgs | OneMail x64 | Comparator x64 | ratio x64 | ratio arm64 | OneMail B/msg | Comparator B/msg |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| PST | cfreds-ost | 14 | 3.71 ms | 194.15 ms | 0.019 | n/a | 95,386 | 605,609 |
| PST | tika-pst | 7 | 596.4 µs | 2.29 ms | 0.261 | 0.252 | 84,463 | 437,083 |
| PST | tika-bodytypes | 4 | 490.6 µs | 3.00 ms | 0.164 | 0.169 | 84,298 | 767,458 |
| PST | gen-u1k | 1,000 | 71.63 ms | 176.79 ms | 0.405 | 0.409 | 19,485 | 220,845 |
| PST | gen-u10k | 10,000 | 714.7 ms | 1,800.5 ms | 0.397 | 0.408 | 19,374 | 219,661 |
| PST | gen-u50k | 50,000 | 4,117 ms | 9,205 ms | 0.447 | 0.475 | 19,371 | 220,179 |
| MSG | msg-smallest | 1 | 21.4 µs | 46.5 µs | 0.460 | 0.569 | 107,362 | 80,801 |
| MSG | msg-median | 1 | 137.9 µs | 876.0 µs | 0.157 | 0.180 | 293,905 | 1,272,393 |
| MSG | msg-largest | 1 | 11.71 ms | 440.99 ms | 0.027 | 0.023 | 17,491,388 | 53,247,584 |
| MSG | msg-corpus | 53 | 21.75 ms | 484.67 ms | 0.045 | 0.043 | 656,360 | 1,925,639 |
| TNEF | poi-simple | 1 | 9.1 µs | 38.7 µs | 0.234 | 0.243 | 110,069 | 62,446 |
| TNEF | poi-sample1 | 1 | 35.7 µs | 140.2 µs | 0.255 | 0.281 | 119,754 | 427,807 |
| TNEF | tika-testwinmail | 1 | 72.0 µs | 224.7 µs | 0.320 | 0.370 | 181,977 | 276,927 |
| TNEF | poi-quick | 1 | 84.1 µs | 230.6 µs | 0.365 | 0.372 | 181,982 | 276,930 |
| TNEF | poi-attachments | 1 | 93.7 µs | 260.7 µs | 0.360 | 0.239 | 124,163 | 1,204,040 |
| TNEF | poi-bug63955 | 1 | 146.1 µs | 397.1 µs | 0.368 | 0.248 | 173,098 | 1,880,228 |

| Area | Geomean x64 | Worst x64 | Geomean arm64 | Worst arm64 | Bar |
|---|---:|---:|---:|---:|---|
| PST/OST vs XstReader.Api | 0.197 (6 rows; 0.314 without the OST) | 0.447 | 0.320 (5 rows, no OST) | 0.475 | met |
| MSG vs MsgReader | 0.096 | 0.460 | 0.101 | 0.569 | met |
| TNEF vs MimeKit | 0.312 | 0.368 | 0.287 | 0.372 | met |

Both machines pass every area and every row with room to spare, so no tuning was done. Two rows allocate more than the comparator: the
smallest MSG (107 KB against 81 KB) and the smallest TNEF stream (110 KB against 62 KB). Both are fixed per-message costs that
vanish into the time ratio (0.46 and 0.23).

### Memory over the large stores

Peak working set, repeating the walk (KiB, OneMail / XstReader.Api, `ClearContents` off):

| Store | x64 | arm64 |
|---|---|---|
| gen-u1k (17 MB) | 102,136 / 116,900 | 66,672 / 90,416 |
| gen-u10k (165 MB) | 101,304 / 123,296 | 66,240 / 105,632 |
| gen-u50k (826 MB) | 101,080 / 152,792 | 66,320 / 119,488 |

OneMail's working set does not move between 1,000 and 50,000 messages (a file 50 times larger, 826 MB, in about 100 MB or 66 MB,
most of it the runtime and its allocation budget). XstReader.Api grows with the store. The `curve` samples agree (working set in
MiB at 10 %, 25 %, 50 %, 75 % and 95 % of the 50,000-message walk): OneMail x64 88, 95, 98, 98, 99 and arm64 58, 63, 63, 64, 64;
XstReader.Api x64 102, 118, 129, 142, 146 and arm64 70, 85, 95, 108, 114. With `ClearContents` after every message its working set
is flat but noisy (x64 75, 86, 135, 129, 119) and the walk takes 139 s on x64 and 144 s on arm64. The generated stores top out at
826 MB, so the claim is for that size; a 50 GB store needs a streaming test writer.

### Native reference

`pffexport -q -m items -f text` (libpff 20180714, x64, writes every item to disk, so it is not like-for-like), wall seconds, median
of 3: gen-u1k 0.24 s (OneMail 0.072 s), gen-u10k 2.73 s (0.715 s), gen-u50k 20.09 s (4.12 s), peak RSS 207 MB for 50,000 messages.
The real files take under 0.03 s. Reported, not gating.

## Machines and runs

| | x64 | arm64 |
|---|---|---|
| CPU | AMD Ryzen 9 7950X3D, 32 logical cores, AVX-512 | Apple M2, 8 cores |
| Runtime, SDK | .NET 10.0.13, SDK 10.0.402 | .NET 10.0.11, SDK 10.0.400 |
| Job | `wq` bench 000847, 14:32 to 15:01 UTC, 28.5 min of running | `wq` bench on the Mac, 14:01 to 14:26 UTC |
| Load, 1/5/15 min, before | 6.01 / 8.49 / 10.41 (the previous job's tail) | 2.08 / 3.19 / 3.11 |
| Load, 1 min, during (every 2 min) | 1.56 to 2.93, almost all of it this job | 2.05 to 2.42 |
| Load after | 1.65 / 1.89 / 3.45 | 1.77 / 2.00 / 2.26 |

BenchmarkDotNet 0.15.8, MimeKit 4.18.2, MsgReader 6.2.0, XstReader.Api 1.0.6. The raw tables are `report.tsv`, `peak.tsv`,
`curve.tsv`, `verify.tsv` and `pffexport.tsv` in each run's output directory. Two script faults found after the runs and fixed in
the script: the TNEF peak row for `tika-testwinmail` was routed to the PST path and has no line (it is the same 66,276 bytes as
`poi-quick`), and on macOS the `theirs-clear` tag in `curve.tsv` was not applied (the second `theirs` block per store is the
`ClearContents` run).

# Benchmarks: calendar and contact rows

`bench/run-calendar-rows.sh OUTDIR` runs these rows with the same harness, ratio and bar as above (OneMail median / comparator
median; geometric mean ≤ 1.0 per area, no row above 1.25). The 50k-event rows use 3 launches of 1 warm-up and 3 iterations.

## Comparators and what each side does

Ical.Net 5.2.3 and FolkerKinzel.VCards 8.2.0, released NuGet packages used through their public API, referenced only from `bench/`.

| Area | OneMail.Calendar | comparator |
|---|---|---|
| ICS parse | `ContentDocument.Parse` over the bytes, then a typed pass over every event (uid, summary, description, location, start, end, rule count, attendees) | `Calendar.Load` over the text, then the same fields |
| ICS build-write | create the events from fields (typed setters), `ToArray` | create the events, `CalendarSerializer` |
| ICS edit-write | parsed calendar, set every SUMMARY, write (unchanged lines go out as read) | parsed calendar, set every Summary, serialise (everything is re-rendered) |
| vCard parse | `ContentDocument.Parse`, then name, FN, e-mails, phones, addresses | `Vcf.Parse`, then the same |
| vCard build-write / edit-write | as for ICS | `VCardBuilder` and `Vcf.AsString` (3.0) |
| Recurrence | `CalendarRecurrence.Expand` over a parsed calendar and a window | `Calendar.GetOccurrences` over a loaded one, stopping at the window end |
| jCal / jCard | `JsonForms.ToJson` / `FromJson`: OneMail only | none |

`cverify` checks equal work before any timing: the parse and expansion checksums are equal on all 10 rows (events, cards and
occurrence counts), and the written sizes agree to within 39 bytes (ICS) or 4% (vCard, whose comparator writes extra defaults).
Inputs: a generated invite (1 event, 833 B), the largest Thunderbird holiday calendar (700 events, 291 KB), a generated 50k-event
file (32.8 MB), a generated card (430 B) and a 10k-card address book (4.5 MB); recurrence cases daily UTC, weekly floating, monthly
last-Friday in New York, yearly second Sunday of March in Berlin and daily in New York over 50 years.

## Results, x64 (AMD Ryzen 9 7950X3D, 32 cores, .NET 10.0.402, BenchmarkDotNet 0.15.8, load ~2.6 before/during/after; wq job `princessDonut.edqli8ve`)

| Area | Input | OneMail ns | comparator ns | ratio | OneMail B/item | comparator B/item |
|---|---|---:|---:|---:|---:|---:|
| ICS parse | invite | 3,917 | 38,967 | 0.101 | 9,328 | 113,264 |
| ICS parse | holidays | 931,933 | 14,621,595 | 0.064 | 3,177 | 45,284 |
| ICS parse | 50k events | 420,817,599 | 4,160,494,118 | 0.101 | 6,033 | 66,904 |
| ICS build-write | 1 / 1k / 50k | 3,044 / 4.39 M / 632.6 M | 23,819 / 49.5 M / 3,876 M | 0.128 / 0.089 / 0.163 | 8,856 (50k) | 65,449 |
| ICS edit-write | invite (holidays and 50k in the job's report; worst 0.058) | 460 | 18,922 | 0.024 | 3,424 | 53,033 |
| vCard parse | 1 card | 2,242 | 2,548 | 0.880 | 6,256 | 8,856 |
| vCard parse | 10k cards | 64,428,106 | 61,611,407 | 1.046 | 5,500 | 7,770 |
| vCard build-write | 1 / 10k | 2,059 / 79.2 M | 5,296 / 127.8 M | 0.389 / 0.620 | 7,466 | 12,171 |
| vCard edit-write | 1 / 10k | 248 / 6.79 M | 3,263 / 47.6 M | 0.076 / 0.143 | 2,278 | 6,283 |
| Recurrence | daily UTC 3,653 / weekly 1,566 / monthly NY 240 / yearly Berlin 50 / daily NY 18,263 occurrences | 515 µs / 230 µs / 168 µs / 41 µs / 9.97 ms | 4.31 ms / 1.67 ms / 371 µs / 88 µs / 22.5 ms | 0.119 / 0.138 / 0.452 / 0.470 / 0.443 | 2–200 B/occurrence | 3.3–4.6 KB |

Geometric mean / worst: ICS parse 0.087 / 0.101, ICS build-write 0.123 / 0.163, ICS edit-write 0.040 / 0.058, vCard parse **0.959 / 1.046**,
vCard build-write 0.491 / 0.620, vCard edit-write 0.104 / 0.143, recurrence 0.274 / 0.470. Every area is inside the bar.
jCal/jCard (OneMail only): write 6.8 µs for the invite, 3.8 ms for the holidays, 1.30 s for 50k events; read 10.8 µs, 4.6 ms, 2.77 s.
Native reference (libical 3.0.17 from the distribution package, parse only): invite 14.1 µs, holidays 3.88 ms, 50k events 526 ms
(OneMail's parse to the tree and typed pass: 3.9 µs, 0.93 ms, 421 ms).

## Tuning (before / after, same machine, wq jobs `princessDonut.6b4aw8gk` and `princessDonut.edqli8ve`)

| Row | before | after | change |
|---|---:|---:|---|
| vCard parse 10k | ratio 1.882, 11,484 B/card | 1.046, 5,500 B/card | shared strings for known names, no per-line parameter list or object until asked, parameter values read straight into their array, a card's item list allocated once, typed views without LINQ |
| vCard parse 1 card | 1.516 | 0.880 | same |
| Recurrence yearly Berlin | 1.284 | 0.470 | a yearly rule with `BYMONTH` scans the selected months, not 365 days |
| ICS parse (all) | 0.133 geomean | 0.087 | same parse changes |

## arm64 (Apple M2, 8 cores), before tuning only so far (`kurtisMac.zv3qkr0l`)

ICS parse 0.132 geomean (worst 0.180), build-write 0.132 (0.171), edit-write 0.059 (0.107), vCard parse 1.530 (1.707), vCard
build-write 0.442 (0.461), vCard edit-write 0.121 (0.175), recurrence 0.412 (1.527, yearly Berlin). The tuned arm64 run is pending.
