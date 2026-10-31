# RTF body checks

Checks for `RecursiveAI.OneMail.Rtf` (compressed RTF, de-encapsulation). Nothing here ships in a package. Method and results: [docs/rtf.md](../../docs/rtf.md).

## Corpus

The RTF bodies come from the public `.msg` files that `tools/corpus/fetch-msg.sh` fetches into `$ONEMAIL_CORPUS/msg` (Apache Tika and Apache POI test documents, Apache-2.0; data files only, never committed). Nothing from them is stored in this repository.

## Commands

    dotnet build tools/rtf/RtfTool -c Checked
    RtfTool extract $ONEMAIL_CORPUS/msg $WORK/rtf        # RTF, HTML and text body streams of every .msg (OneArchive's compound-file reader)
    RtfTool run $WORK/rtf                                # OneMail's decompression, compression and de-encapsulation; report.tsv
    compare.py $WORK/rtf                                 # oracles + the messages' own bodies; writes <id>.oracle.rtfz and compare.txt
    RtfTool verify $WORK/rtf                             # OneMail reads the oracle's compression of each body
    RtfTool roundtrip $WORK/rtf                          # encapsulate every extracted body (ours, and the messages' own) and take it back out
    mutants.py make $WORK/rtf $WORK/mut                  # 3,000 damaged streams and the oracle's answer to each
    RtfTool decompress-all $WORK/mut && mutants.py compare $WORK/mut
    RtfTool throughput $WORK/rtf 5                       # MB/s and allocations (a light check, not a benchmark)

## Oracles (run as released packages, never built from source, source not read)

    uv venv --python 3.13 $WORK/venv
    uv pip install --python $WORK/venv/bin/python compressed-rtf==1.0.7 RTFDE==0.1.2.2

| Package | Version | Licence | Used for |
|---|---|---|---|
| compressed-rtf | 1.0.7 | MIT | decompression and compression |
| RTFDE | 0.1.2.2 | LGPL-3.0 | de-encapsulation (run only; not linked, not shipped) |

`compare.py` and `mutants.py` import them in a venv of their own; their output goes to `$WORK`, never to the repository.
