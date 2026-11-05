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

## Results, 2026-11-04 (publishable)

Medians in ns per operation (per message for mbox); B/op is bytes allocated per operation. Ratio is OneMail / MimeKit. Two filtered
bench-class groups per machine: parse, headers and bodies; then write, build and mbox. Peak working set (x64, one process per side,
`peak.tsv`, KiB) is in the last column as ours / MimeKit.

| Row | Input | OneMail x64 | MimeKit x64 | ratio x64 | ratio arm64 | B/op ours | B/op MimeKit | peak ours / MimeKit |
|---|---|---:|---:|---:|---:|---:|---:|---|
| parse | enron-2k | 1,074 | 13,692 | 0.078 | 0.107 | 5,280 | 19,448 | 89 / 128 MiB |
| parse | enron-20k | 928 | 22,273 | 0.042 | 0.053 | 4,360 | 39,360 | 89 / 136 MiB |
| parse | dovecot-multipart | 4,993 | 71,883 | 0.069 | 0.110 | 9,920 | 171,802 | 89 / 145 MiB |
| parse | generated 6 MB | 98,988 | 1,974,279 | 0.050 | 0.073 | 5,344 | 6,318,608 | 134 / 221 MiB |
| headers | enron-2k | 2,901 | 17,307 | 0.168 | 0.207 | 10,328 | 22,344 | 100 / 127 MiB |
| headers | enron-20k | 2,332 | 24,425 | 0.095 | 0.109 | 8,328 | 41,400 | 100 / 134 MiB |
| headers | dovecot-multipart | 6,840 | 79,482 | 0.086 | 0.129 | 15,088 | 174,570 | 100 / 148 MiB |
| headers | generated 6 MB | 157,446 | 1,929,998 | 0.082 | 0.073 | 10,688 | 6,321,655 | 141 / 220 MiB |
| bodies | enron-2k | 1,460 | 13,914 | 0.105 | 0.127 | 8,160 | 20,904 | 90 / 124 MiB |
| bodies | enron-20k | 3,861 | 22,355 | 0.173 | 0.127 | 49,496 | 101,208 | 93 / 112 MiB |
| bodies | dovecot-multipart | 166,053 | 352,097 | 0.472 | 0.352 | 503,617 | 556,690 | 44 / 55 MiB |
| bodies | generated 6 MB | 7,008,343 | 13,375,963 | 0.524 | 0.330 | 16,778,003 | 18,583,957 | 144 / 201 MiB |
| write | enron-2k | 65 | 1,640 | 0.039 | 0.032 | 184 | 1,752 | 89 / 90 MiB |
| write | enron-20k | 364 | 15,057 | 0.024 | 0.018 | 184 | 4,696 | 89 / 90 MiB |
| write | dovecot-multipart | 1,773 | 105,210 | 0.017 | 0.020 | 184 | 6,912 | 89 / 94 MiB |
| write | generated 6 MB | 99,975 | 3,678,831 | 0.027 | 0.026 | 184 | 5,912 | 94 / 101 MiB |
| build | text + 1 MB attachment | 371,640 | 933,535 | 0.398 | 0.223 | 2,744,100 | 36,782 | 58 / 118 MiB |
| mbox | dovecot, per message | 2,905 | 24,404 | 0.119 | 0.187 | 8,475 | 28,393 | 101 / 143 MiB |

Geometric mean 0.089 (x64) and 0.092 (arm64); worst row 0.524 (bodies of the 6 MB message) and 0.352. The bar holds on both.
Arm64 peak working set is not measured: the memory helper reads Linux's `/proc`, so the Mac column is zero in `peak.tsv`; its B/op
are the same as x64's. OneMail allocates more than MimeKit only in `build`, which base64-encodes the attachment when the content is set
(MimeKit encodes while writing).

**Tuning (before / after, same machine and job type, x64, indicative heavy-class runs with 4 cores):** `TransferEncoder.Base64` encoded
per 57-byte line and copied the result; it now encodes once and spreads the 76-character lines in place. `build`: 750,527 ns and
4,112,670 B before (ratio 0.777), 444,552 ns and 2,744,093 B after (0.447). The bench-class row above is 371,640 ns.

**Native reference (not gating, target <= 1.5x), x64.** Rust `mail-parser` 1.0.1 and `mail-builder` 2.0.1 from crates.io (released
crates, built with cargo, documented API only; `bench/native/mimeparse`), same four inputs, median of seven timed batches in one
process. Ratio is OneMail's bench-class median / native median (the two were not measured at the same instant):

| Row | enron-2k | enron-20k | dovecot-multipart | generated 6 MB |
|---|---:|---:|---:|---:|
| parse, native ns | 763 | 583 | 3,736 | 52,515 |
| parse, ours / native | 1.41 | 1.59 | 1.34 | 1.88 |
| headers, native ns | 867 | 681 | 3,850 | 52,391 |
| headers, ours / native | 3.35 | 3.42 | 1.78 | 3.01 |
| bodies, native ns | 773 | 609 | 9,092 | 297,139 |
| bodies, ours / native | 1.89 | 6.34 | 18.3 | 23.6 |

`build` (text part plus a 1 MB attachment, written to memory): native 49,085 to 50,075 ns, ours 371,640 ns (7.4x). Parse is near the
target; decoded headers, the base64 body decode (the 6 MB input decodes 4.5 MB in 7.0 ms and allocates 16.8 MB, about 3.7x the output)
and the build row are the gaps to native. None gates. GMime has no distribution package on either machine, so it isn't measured.

## Tuning against the native reference (before / after, indicative)

The native Rust references (above) were 23.6x ahead on decoding the 6 MB message's bodies, 1.8x to 3.4x on decoded headers, 7.4x on building a
message with a 1 MB attachment, and 1.3x to 1.9x on parse. This round of tuning changed the MIME hot paths only; parse results, defects and written
bytes are unchanged (below). All numbers are indicative: one launch of ten iterations per row, run as ordinary (not bench-class) jobs on the same
x64 machine (AMD Ryzen 9 7950X3D, 32 cores, .NET 10.0.403, BenchmarkDotNet 0.15.8, MimeKit 4.18.2), the "before" tree and the "after" tree one after
the other in one job. The machine was shared with other work (load average 13 at the start, 6.6 in the middle and 15 at the end of the job), so
absolute times move by tens of percent between runs; the ratios between rows and the allocation figures are the reliable part. The native column is the
median of the earlier native run, not measured at the same instant.

| Row | Input | Before ns | After ns | Native ns | After / native | B/op before | B/op after | MimeKit B/op |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| bodies | generated 6 MB | 8,073,002 | 777,847 | 304,146 | 2.6 | 16,778,019 | 4,521,822 | 18,583,884 |
| bodies | dovecot-multipart | 192,033 | 60,248 | 9,201 | 6.5 | 503,617 | 124,143 | 556,690 |
| bodies | enron-20k | 4,615 | 3,015 | 645 | 4.7 | 49,496 | 25,672 | 101,208 |
| bodies | enron-2k | 1,630 | 1,217 | 843 | 1.4 | 8,160 | 5,152 | 20,904 |
| headers | generated 6 MB | 182,926 | 98,634 | 69,773 | 1.4 | 10,688 | 5,936 | 6,321,655 |
| headers | dovecot-multipart | 7,288 | 5,913 | 3,793 | 1.6 | 15,088 | 10,096 | 174,570 |
| headers | enron-20k | 2,947 | 1,419 | 702 | 2.0 | 8,328 | 4,336 | 41,400 |
| headers | enron-2k | 3,522 | 1,610 | 1,021 | 1.6 | 10,328 | 5,256 | 22,344 |
| build | text + 1 MB attachment | 396,657 | 141,172 | 54,995 | 2.6 | 2,744,091 | 1,007,219 | 36,782 |
| parse | generated 6 MB | 102,688 | 96,000 to 104,000 (150,000 in this job) | 69,500 | 1.4 | 5,344 | 4,688 | 6,318,608 |
| parse | dovecot-multipart | 5,303 | 5,224 | 3,766 | 1.4 | 9,920 | 8,864 | 171,802 |
| parse | enron-20k | 965 | 902 | 652 | 1.4 | 4,360 | 3,368 | 39,360 |
| parse | enron-2k | 1,202 | 1,063 | 815 | 1.3 | 5,280 | 4,008 | 19,448 |
| mbox | dovecot, per message | 3,092 | 2,866 | | | 8,475 | 7,106 | 28,393 |

Against MimeKit in the same job the geometric mean over these 14 rows moved from 0.116 to 0.069 and the worst row from 0.574 (bodies of the 6 MB message)
to 0.152 (build). The 6 MB parse row in this job was disturbed by another job; five other runs the same day gave 95,600 to 104,000 ns for the unchanged
code. The publishable rows are re-run at the freeze.

What changed:

- **Base64 bodies.** `Base64Decoder` cuts out the lines of one width at their stride (checking only the line ends) into a scratch buffer and hands
  whole groups to `System.Buffers.Text.Base64.DecodeFromUtf8`. A stretch the BCL decoder does not accept (a bad character, padding in the middle,
  a group split by white space) goes back to the byte-at-a-time decoder, which makes the same repairs and reports the same issues as before; so does
  anything not wrapped at one width. `GetDecodedContent()` sizes its result from the first line's width and the closing padding, so the usual case fills one
  array with no trim copy, decodes straight into it, and `TransferDecodingStream` borrows its two buffers from the pool and sizes them to the body (up to
  60 KiB).
- **Build.** `SetDecodedContent` with base64 keeps the attachment bytes once, in a source that produces the wrapped base64 text as it is read, so the
  encoded text of a large attachment is never held and the writer pulls it a 64 KiB chunk at a time. The remaining 1 MB is the copy of the caller's span;
  the native crate borrows the buffer instead.
- **Decoded headers.** Plain address lists (`addr`, `Name <addr>`, `"Quoted, Name" <addr>`, comma separated, unfolded) and dates in the form
  `[Www, ]D Mon YYYY HH:MM:SS +ZZZZ[ (comment)]` are read straight from the bytes; everything else, and anything that would raise a defect, goes to the
  general parsers unchanged. A header look-up compares the name in place instead of building a string for every header, the message-id reader decodes in
  place, and the reader allocates its boundary and frame tables only when a multipart needs them.
- **Equivalence.** Randomised tests compare each fast path with the general path (`Base64DecoderEquivalenceTests`, `DateParserFastPathTests`,
  `AddressParserFastPathTests`, `Base64WrappedSourceTests`), and the `mime-tree` fuzz target checks bulk base64 decoding against the byte-at-a-time
  decoder on every input it generates.

Equality checks run on the same job: the OneMail dump driver built from the tree before the tuning and from the tuned tree produced byte-identical
dumps (addresses, dates, defects, decoded body hashes) for 517,401 of 517,401 messages of the CMU Enron maildir, and for the Dovecot (2,859), Mime4j (80) and
probe (224) sets; the differential run against MimeKit, Python and Go over a 20,000-message Enron sample and those three sets has no unexplained difference;
the Enron round trip is byte-identical, 517,401 of 517,401.

## Machines and runs, 2026-11-04

| | x64 | arm64 |
|---|---|---|
| CPU | AMD Ryzen 9 7950X3D, 32 logical cores, AVX-512 | Apple M2, 8 cores |
| Runtime, SDK | .NET 10.0.14, SDK 10.0.403 | .NET 10.0.14, SDK 10.0.403 |
| Commit | 3b51281 (rows) and 4b962c1 (servers script) | the same |
| Jobs, `wq` class bench | 003583 (MIME parse/headers/bodies, 20:57 to 21:12 UTC), 003584 (MIME write/build/mbox and native, 21:13 to 21:21), 003585 (vCard parse and write, 21:21 to 21:26), 003588 (IMAP and SMTP, 21:26 to 21:31) | 000599 (20:49 to 21:03), 000600 (21:03 to 21:10), 000601 (vCard, 21:10 to 21:16) |
| Bench-class run time | 14.8 + 8.0 + 4.9 + 4.7 = 32 min | 14.0 + 7.1 + 5.1 = 26 min |
| Load before, 1/5/15 min | 3.76 / 7.84 / 9.65 (M1); 3.25 / 2.96 / 5.04 (M2); 1.60 / 2.02 / 3.71 (vCard); 2.57 / 2.37 / 3.39 (P) | 1.70 / 2.64 / 2.63 (M1); 2.06 / 2.19 / 2.40; 2.12 / 2.39 / 2.49 |
| Load during (1 min, every 2 min) | 1.7 to 3.0, most of it the job | 1.9 to 3.0 |
| Load after | 2.62 / 2.71 / 5.16 (M1); 1.71 / 2.05 / 3.75; 1.05 / 1.75 / 2.87 (P) | 2.06 / 2.19 / 2.40 (M1); 2.13 / 2.39 / 2.50; 2.00 / 2.43 / 2.54 |
| Non-timing stages (peak, equal work, B/op) | heavy job 003551, 4 cores | heavy job 000598, 6 cores |

BenchmarkDotNet 0.15.8, MimeKit 4.18.2. Raw `report.tsv`, `peak.tsv`, `native.tsv` and `machine.txt` per group are in the job directories on each machine and were pulled to the
build's results directory. Rows other than these (PST/OST, MSG, TNEF, ICS, recurrence, the forms, vCard edit) have no code change in these
areas since their last publishable run and keep the dated rows below; they are re-run in the 1.0 matrix after the code freeze.

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

_Rows of the 0.4 bench-class run (2026-11-01, commit 9ec14a0, .NET 10.0.402/10.0.400). No code in these areas has changed since; re-run in the 1.0 matrix after the code freeze._

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

## Results, x64, 2026-11-01 (0.4 run, commit 9ec14a0; kept for 1.0 re-run; the vCard parse rows are superseded below) (AMD Ryzen 9 7950X3D, 32 cores, .NET 10.0.402, BenchmarkDotNet 0.15.8, load about 2.6 before, during and after)

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

## Tuning (before / after, same machine)

| Row | before | after | change |
|---|---:|---:|---|
| vCard parse 10k | ratio 1.882, 11,484 B/card | 1.046, 5,500 B/card | shared strings for known names, no per-line parameter list or object until asked, parameter values read straight into their array, a card's item list allocated once, typed views without LINQ |
| vCard parse 1 card | 1.516 | 0.880 | same |
| Recurrence yearly Berlin | 1.284 | 0.470 | a yearly rule with `BYMONTH` scans the selected months, not 365 days |
| ICS parse (all) | 0.133 geomean | 0.087 | same parse changes |

## Results, arm64, 2026-11-01 (kept for 1.0 re-run; the vCard parse rows are superseded below) (Apple M2, 8 cores, .NET 10.0.400, load 1.7-2.1 before, 3.0-4.3 shortly after start, 1.9-2.4 at the end of each group)

Three filtered groups, each its own queued bench job of at most 20 minutes (ICS parse and edit; ICS build and vCard; recurrence and
jCal), run after the tuning above. The first column is before tuning (one 43-minute matrix), the second after.

| Area | geomean before | geomean after | worst after | status |
|---|---:|---:|---:|---|
| ICS parse | 0.132 | 0.104 | 0.151 | pass |
| ICS build-write | 0.132 | 0.153 | 0.248 | pass |
| ICS edit-write | 0.059 | 0.040 | 0.074 | pass |
| vCard parse | 1.530 (worst 1.707) | **1.093** | **1.181** (10k cards; 1 card 1.011) | **misses the bar** |
| vCard build-write | 0.442 | 0.558 | 0.674 | pass |
| vCard edit-write | 0.121 | 0.111 | 0.146 | pass |
| Recurrence | 0.412 (worst 1.527) | 0.334 | 0.589 (daily NY; yearly Berlin 0.551) | pass |

vCard parse, 10k cards: 79.4 ms against 67.2 ms, 5,500 B per card against 7,770. The M2 runs the comparator's parser relatively
faster than the x64 machine does (x64 ratio 1.046 on the same rows); a further cut needs fewer objects per property in the tree.

## Results, vCard parse and write, 2026-11-04 (publishable, both machines)

One bench-class job per machine (x64 `wq` 003585, 21:21 to 21:26 UTC, 4.9 min; arm64 `wq` 000601, 21:10 to 21:16 UTC, 5.1 min), commit 3b51281,
after the typed lists were made lazy. Load before / after (1/5/15): x64 1.60 / 2.02 / 3.71 and 1.05 / 1.75 / 2.87 (through the next job); arm64 2.12 / 2.39 / 2.49 and 2.00 / 2.43 / 2.54.
This replaces the arm64 vCard rows above (parse 1.093, worst 1.181), which missed the bar before the change.

| Row | Input | x64 ours ns | x64 FolkerKinzel ns | x64 ratio | arm64 ratio | B/card ours | B/card comparator |
|---|---|---:|---:|---:|---:|---:|---:|
| parse | 1 card | 1,837 | 2,511 | 0.732 | 0.864 | 4,872 | 8,856 |
| parse | 10,000 cards | 41,404,072 | 60,614,280 | 0.683 | 0.827 | 4,101 | 7,770 |
| build-write | 1 card | 2,040 | 5,159 | 0.395 | 0.461 | 7,736 | 20,800 |
| build-write | 10,000 cards | 75,567,889 | 126,408,232 | 0.598 | 0.608 | 7,466 | 12,171 |

Geometric mean, parse: 0.707 (x64), 0.846 (arm64); worst 0.732 and 0.864. Build-write: 0.486 and 0.530; worst 0.598 and 0.608. The bar holds on both.
Peak working set, x64 (heavy-class job 003551, `peak.tsv`): parse 10k cards 252 / 183 MiB (ours / comparator), write 10k 213 / 262 MiB; the
Mac's helper doesn't report peak working set. vCard edit-write and every other calendar row keep the dated rows above (0.4 run); they are re-run in the 1.0 matrix after the code freeze.

# Benchmarks: PST building (memory)

`tools/pst/memory.sh` runs `PstOracle pstgen`, which writes a generated store through `PstBuilder` (messages of about 2.5 KB with two recipients, spread over the
given number of folders), and prints the peak resident set of each run (GNU time). Only memory is reported: there is no comparator that builds PSTs in .NET, so there is no
speed gate for this area. Release build, one process per run, x64 (AMD Ryzen 9 7950X3D, 32 cores, .NET 10.0.402); run as a light job beside other work (load 37-40 throughout), so
the times are indicative only.

| Messages | Folders | File | Peak RSS | Elapsed |
|---|---|---|---|---|
| 10,000 | 10 | 37 MiB | 105 MiB | 1.2 s |
| 100,000 | 10 | 370 MiB | 110 MiB | 3.7 s |
| 1,000,000 | 10 | 3,704 MiB | 113 MiB | 25.3 s |
| 600,000 | 1 | 2,222 MiB | 123 MiB | 15.2 s |

The resident set stops growing at about 110 MiB from 40,000 messages on (the runtime and its allocation budget are most of it; the managed heap swings between 7 and 54 MiB).
A single folder of 600,000 messages adds about 10 MiB: the row index of the folder being closed, about 12 bytes a row. Files of this size were not read back or checked with
libpff; the 20,000-message run was (`propexact` 0 differences, 0 defects).

# PST in-place writing: durability, consistency, memory

Run by `tools/pst/inplace-check.sh` and `Durability memory` (`tests/Durability`, Checked build), x64 worker (AMD Ryzen 9 7950X3D, 32 cores, .NET 10.0.402), as queued jobs of 4-8 cores beside other work.
Three bases: a file `PstBuilder` wrote (`ours`: three folders of 40 items) and the two Outlook-written Tika files (`tika1` = `testPST.pst`, `tika2` = `testPST_variousBodyTypes.pst`).
Seven scenarios on each: `add` items, `delete` items, `folders` (create, rename, move, delete), `grow` (past an allocation-map range), `split` (a contacts table of 1,200 rows that splits pages), `named` (new named properties), `mixed`.

**Power-cut replays.** Every write and flush of a scenario is recorded; the file is then rebuilt as a power cut could leave it (all writes up to a point, a random subset of the writes since the last flush, the last write torn at a sector boundary)
and opened. Each leftover must read as the state before the scenario, the state after it, or (scenarios with several commits) a state between; anything else fails.
15,200 leftovers over the 21 base/scenario pairs: **0 failures**. They split into 10,305 old, 2,960 new and 1,935 between states; 891 had `fAMapValid` = 0 (the maps are rebuilt from the trees on an opted-in open; repair failed 0 times).
With the commit's invalid-flag header write skipped on purpose, 35 of 240 replays failed, so the check does see that rule.

**Process kills.** The scenario runs in a child process on a real file and receives SIGKILL at the Nth write or flush (deterministic) or after a random delay: 3,150 runs, **2,539 killed** (2,100 at a chosen write or flush, 439 at random times; the rest of the random runs finished first), **0 failures**.
Every leftover opened as the old state, the new state or (several-commit scenarios) a state between. (The kill phase ran before the folder-move fix below, which only changes what one commit stages in memory.)

**Soak.** 1,000 random operations (about 455 adds, 210 deletes, 130 moves, 60 replaces, 130 folder operations) in 10 commits on each base, seeds 11 to 13 locally, seed 11 on the worker. After every commit: allocation maps, counts, hierarchy rows, read-back of every kept item property by property,
and a scan of freed space for non-zero bytes. **0 problems** after the fix below. Before it, the first worker run found 1 to 3 problems on two bases, all from one cause: a folder moved twice in one commit left a stale hierarchy row in the middle parent.
**libpff** (`pffinfo`, `pffexport` against our reader, `libpff-messaging.sh`) on the three soak files: folders, items, recipients, attachments and every exported field agree, 0 differences, 0 defects. The same on the 21 scenario files:
18 of 21 agree entirely. Four `tika1` files (add, grow, named, split) show 17 or 18 differences, all about one item that libpff does not export through the live tree even in the unedited file (it reports it as *recovered* from free space, where our reader reads it from the tree) with an embedded message;
our read of that item is unchanged by the edits (the corpus test compares every untouched item's properties before and after), and the recovered copy libpff finds lies in space our writes reuse. That reading is not separately verified.

**Freed space.** The soak checks every free range after each commit and the unit tests do after every operation: 0 non-zero bytes with the default, and the bytes are left in place with `ZeroFreedSpace = false`.

**Memory** (`Durability memory`): a store of 100,000 items in 100 folders built by `PstBuilder` (96 MiB file; 2.2 s, peak working set 116 MiB), then 1,000 in-place operations (adds, with every third followed by a delete, committed every 50 operations): 20.1 s, **peak working set 123 MiB**.
Edits hold state only for the changes since the last commit, the touched tables' rows and the page cache. A big folder table changes row by row at a commit (see the next block); cost is proportional to the rows changed plus the table's block count, memory does not grow with the rows.

**Big folder tables, row by row** (`tools/pst/tables-bench.sh`, `Durability tables`; indicative, one run each, `wq` heavy 4 cores, not a bar row). A store with one folder of N items; one commit adds, deletes or moves 1, 100 or 1,000 items; `rewrite` writes the folder's table whole at commit, `incremental` changes it row by row. Commit time (ms) / bytes written by the commit (MiB) / peak working set (MiB, x64 only):

| rows | change | rewrite 1 / 100 / 1,000 | incremental 1 / 100 / 1,000 |
|---|---|---|---|
| 1,000 | add | 73 / 68 / 77 ms; 0.3-0.7 MiB | 27 / 33 / 42 ms; 0.06-0.44 MiB |
| 1,000 | delete | 94 / 102 / 77 ms | 44 / 58 / 89 ms (1,000 falls back to the whole table) |
| 100,000 | add | 2,853 / 2,590 / 2,519 ms; 32.8-33.2 MiB; peak 102-105 | 163 / 166 / 129 ms; 0.18 / 0.29 / 0.63 MiB; peak 68-95 |
| 100,000 | delete | 2,386 / 2,459 / 2,698 ms; 32.8-36.3 MiB | 120 / 164 / 311 ms; 0.26 / 5.4 / 28.7 MiB |
| 100,000 | move | 2,541 / 2,532 / 2,467 ms; 32.8-34.1 MiB | 126 / 166 / 300 ms; 0.26 / 5.0 / 26.5 MiB |
| 600,000 | add | 10,719 / 12,756 / 10,217 ms; 199 MiB; peak 118-120 | 436 / 528 / 452 ms; 0.19 / 0.23 / 1.5 MiB; peak 105-112 |
| 600,000 | delete | 10,586 / 10,198 / 9,996 ms; 199-204 MiB | 459 / 500 / 743 ms; 0.67 / 6.8 / 47.6 MiB; peak up to 160 |
| 600,000 | move | 10,054 / 10,181 / 10,080 ms; 199-201 MiB | 428 / 490 / 696 ms; 0.66 / 6.3 / 44.4 MiB; peak up to 159 |

The whole-table commit scales with the rows (100k to 600k rows: about 2.5 s to 10-13 s, 33 to 199 MiB written); the row-by-row commit with the rows changed (1 change: 120-160 ms at 100k, 430-460 ms at 600k, under 1 MiB written; the growth is the number of blocks in the table's trees). Deleting or moving many rows touches many blocks: 1,000 changes at 600k rows write 45-48 MiB, about a quarter of the whole table, and commit in 0.7-0.8 s.
The edit phase (`DeleteMessage`/`MoveMessage` calls before the commit) takes 0.2-0.4 s at 600k rows in both modes; before the folder's table was cached per transaction it was linear in the table for each call (100,000 rows, 1,000 deletes: 36 s; now 0.2 s).
The same run on the arm64 Mac mini (Apple, 10.0.400): 100k rows rewrite 2.9-3.1 s against incremental 99-350 ms; 600k rows rewrite 13.0-13.9 s against incremental 431-826 ms (same bytes written; peak working set not recorded there).
Machine for the x64 rows: AMD Ryzen 9 7950X3D, 32 cores, .NET 10.0.402, load 16 at the start of the run (other jobs beside it).

**Suite.** Checked build, full suite 3,450 passed, 63 skipped by design (server tests, absent corpora), 0 failed, with the Outlook-written PSTs present; fuzz targets build; NativeAOT publish with warnings as errors runs all smoke checks including `pst-edit`; browser WebAssembly smoke (headless Chromium) passes with the in-memory writable store; all packages pack with package validation.


# Benchmarks: protocol rows (IMAP fetch, SMTP send)

`bench/ProtocolRows` compares the IMAP and SMTP clients with MailKit 4.18.2 (a released NuGet package, referenced only from `bench/`)
over a real connection to local servers: IMAP against Dovecot 2.4.5 (implicit TLS), SMTP submission against Postfix 3.7 in its
guest (STARTTLS, authenticated). `bench/run-protocol-rows.sh OUTDIR [imap|smtp]` starts the servers in a private network namespace
(`tools/servers`), runs every side in its own process and prints three timed repetitions after a warm-up with their median. Run it
alone on the machine. The ratio is our median / MailKit's median; below 1 is faster. The bar is a geometric mean ≤ 1.0 across rows
and no row above 1.25.

## Inputs

- IMAP: the first 5,000 messages of the CMU Enron maildir appended to a Dovecot mailbox (`imap-prepare`).
  `imap-summary` fetches UID, FLAGS, INTERNALDATE, RFC822.SIZE and ENVELOPE of all 5,000 in one command. `imap-full` fetches
  `BODY[]` of the first 1,000, one FETCH each, into memory.
- SMTP: `smtp-small` is 1,000 generated messages of about 4 KB and `smtp-big` 100 of about 1 MB, each over one connection to one
  recipient. Messages differ only in Subject and Message-ID.

## What each side does

- Ours: `ImapClient.FetchAsync` enumerating the results; `SmtpClient.SendAsync` with CHUNKING (BDAT) as the guest offers it.
  `ours-data` is the same with DATA, to show the transfer command does not matter.
- MailKit: `IMailFolder.FetchAsync` with the same items (summary); `GetStreamAsync(uid, "")` copied into a `MemoryStream`, ours
  `DownloadAsync(uid, "", stream)` (full body, one FETCH per message); `SmtpClient.SendAsync`.
- **SMTP times the send of messages that are already built, on both sides.** Building (our `OutgoingMessage.ToEntity`, MailKit's
  `MimeMessage`) is outside the timed region, and each repetition sends the same prepared messages again. Allocation per message
  is therefore the send's alone.
- **Two MailKit variants.** `mailkit` is MailKit as it ships: it connects its own socket without `TCP_NODELAY` and writes the
  command and the message in several small segments, so each message waits out Nagle's algorithm against the server's delayed ACK:
  about 42 ms per message on this path (the 42 s for 1,000 messages below). Setting `TCP_NODELAY` removes it, which is why we
  attribute it to Nagle against delayed ACK. It is real MailKit behaviour on this path, not a harness fault, but it measures TCP timers, so the
  gate comparator is `mailkit-nodelay`: the same MailKit calls on a socket we connect with `TCP_NODELAY` set, which is how our
  connections are opened. Both are reported.
- The Postfix guest relays every accepted message to a sink on the host; the harness waits (outside the timed region) until the
  sink has everything and clears it between repetitions, so a long row cannot fill the guest's disk.

## Results, x64, 2026-11-04 (publishable)

One bench-class job (`wq` 003588, 21:26 to 21:31 UTC, 4.7 min of running; the sides listed below, three repetitions each after a warm-up).
The gate side for SMTP is `mailkit-nodelay` (MailKit with Nagle off, which is its fastest). MailKit as it ships (Nagle on) was left out of this
run to stay inside the time budget; its last measurement (2026-11-01) was 42 s for smtp-small and 2.9 s for smtp-big.

| Row | Ours | MailKit | Ratio | Alloc/msg ours | Alloc/msg MailKit | Peak ours / MailKit |
|---|---|---|---|---|---|---|
| imap-summary, 5,000 messages | 144.1 ms | 148.0 ms | **0.97** | 2,494 B | 17,100 B | 77 / 143 MiB |
| imap-full, 1,000 messages | 179.2 ms | 184.7 ms | **0.97** | 11,325 B | 12,704 B | 71 / 79 MiB |
| smtp-small, 1,000 x 4 KB | 986.1 ms | 960.8 ms | **1.03** | 9,847 B | 30,889 B | 105 / 120 MiB |
| smtp-big, 100 x 1 MB | 591.2 ms | 794.3 ms | **0.74** | 12,779 B | 212,814 B | 299 / 193 MiB |

Geometric mean 0.92, worst row 1.03 (smtp-small): the bar holds. `ours-data` (the same send through the lower-level `DATA` path):
smtp-small 835.0 ms (0.87), smtp-big 646.0 ms (0.81). smtp-small is at parity with MailKit's fastest configuration: the 2026-11-01 ratio
of 0.68 compared against a `mailkit-nodelay` that ran at 1,555 ms; it ran at 961 ms this time on a quieter machine (same Postfix guest
and harness), so the SMTP comparator numbers moved more than ours. Ours holds more at the 1 MB peak because the prepared messages are
held as an entity tree plus the converted SMTP message. `imap-full` is dominated by the server and the copy of each body, so the two sides land together.

**arm64: protocol rows are gated on x64 only.** The servers run in a private network namespace on the worker, the Mac has no container
or VM runtime, and client-only rows against the worker over the LAN would measure the network. The protocol code has no architecture-specific paths.

### Allocation per message in enumeration and download

Measured by `ImapMemoryTests` (opt-in, `ONEMAIL_IMAP_PERF=1`) against the real servers, 10,000 results of
`UID FLAGS INTERNALDATE RFC822.SIZE ENVELOPE`, second pass, before (through the generic response tree, which is also what the old
code did) and after (plain lines decoded in place):

| Server | Before | After |
|---|---|---|
| Dovecot 2.4.5 | 6,909 B | 960 B |

The same test passes on Stalwart 1.0.1 and Cyrus 3.13 (Stalwart refuses the 100 MB message: its request limit is 50 MB); their byte counts were not captured.

The item itself (`ImapMessage`, its data, flags, envelope, strings and addresses) is about 900 B of that for these envelopes. The
download of a 1 MB and a 100 MB message from Dovecot allocates 38 KiB and 153 KiB (before: about 127 B per read, about 12 MB for 100 MB at 16 KB reads);
the remaining growth is about 24 B per read, against about 127 B before.

## Tuning

Before this work (same machine, indicative): imap-full 170 ms against 142 ms (1.20x), smtp-small 1.2 to 1.5x of `mailkit-nodelay`, FETCH
8.8 KB per message. What changed: a coarse timer instead of a timer per read, pooled async state machines on the hot reads,
plain FETCH lines decoded in place, SMTP scan buffers pooled, `CopyToAsync` on the literal stream.

## Machines and runs

| | x64 |
|---|---|
| CPU | AMD Ryzen 9 7950X3D, 32 logical cores |
| SDK | .NET 10.0.403, Release build |
| Servers | Dovecot `docker.io/dovecot/dovecot@sha256:1b3041c0...db4c6`; Postfix 3.7 in the Debian 12 guest (`tools/servers/guest-*.sh`) |
| Job | `wq` bench 003588, 2026-11-04 21:26 to 21:31 UTC, commit 4b962c1 |
| Load before (1/5/15 min) | 2.57 / 2.37 / 3.39 |
| Load during (every minute) | 1.05 to 2.57 |
| Load after | 1.05 / 1.75 / 2.87 |

MailKit 4.18.2. The harness's per-container session keyring is off (`tools/servers/setup-podman.sh`, `keyring=false`) and `userns.sh` stops
podman's pause helper after each run: before, every run left one holding kernel session keys, and the per-user quota of 200 ran out
(`crun: join keyctl: Quota exceeded`, Dovecot did not start). After, over repeated servers runs the user's key count stayed at 188 to 193 (other sessions of that user move it by one), and no pause helper was left behind. Measured on the worker before the change: 192 keys with 8 stale helpers present, quota 200.
