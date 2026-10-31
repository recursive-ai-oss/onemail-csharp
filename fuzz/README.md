# Fuzzing

`fuzz/OneMail.Fuzz` holds the fuzz targets; SharpFuzz instruments `RecursiveAI.OneMail.dll` and libFuzzer drives it. Targets so far: `mime-reader` and `mime-tree`. Each parser adds its own (a static `Run(ReadOnlySpan<byte>)` plus one line in `Program.cs` and one in `run.sh`).

Only the library's typed results may come out of a target. Any other exception, a failed invariant, a hang (`-timeout`) or a memory blow-up (`-rss_limit_mb`) is a finding.

## Running

    export ONEMAIL_FUZZ_TOOLS=/path/to/tools     # holds fuzz/libfuzzer-dotnet and sharpfuzz/sharpfuzz
    export ONEMAIL_FUZZ_WORK=/path/to/workdir    # builds, corpora, findings; must be outside the checkout
    fuzz/run.sh <target> 600                     # fuzz for 10 minutes
    fuzz/run.sh replay <target> some-input...    # run saved inputs once, without libFuzzer

`run.sh` builds the harness in `Checked` configuration into `$ONEMAIL_FUZZ_WORK/bin`, instruments `RecursiveAI.OneMail.dll` and runs libFuzzer. Findings land in `$ONEMAIL_FUZZ_WORK/findings/<target>/`.

The tools:

* `libfuzzer-dotnet`: the C++ host from the SharpFuzz project, compiled with `clang++ -fsanitize=fuzzer`.
* `sharpfuzz`: `dotnet tool install SharpFuzz.CommandLine --tool-path <dir>/sharpfuzz`.

Run one fuzz process at a time.

## Findings

Minimize the input, save it under `testdata/fuzz/` (under 1 MB, from our own files), and add a test that feeds it and asserts a typed result.
