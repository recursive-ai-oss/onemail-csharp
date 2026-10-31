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
