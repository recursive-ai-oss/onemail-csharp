# Building and testing

You need the .NET SDK pinned in `global.json` (10.0.400 or a later feature band). Tests run on Microsoft.Testing.Platform with xUnit v3.

    dotnet build -c Release          # all projects; warnings are errors
    dotnet test -c Release --no-build
    dotnet pack -c Release -o out    # five library packages and the onemail tool, with .snupkg symbols

## Where output goes

`bin/`, `obj/` and `package/` for every project live under one directory, never beside the sources. Set `ONEMAIL_ARTIFACTS` to choose it; the default is `artifacts/` at the repository root (ignored by git). Test stores and other files tests write belong under the system temp directory or `$ONEMAIL_ARTIFACTS`, never in the checkout.

## Layout

- `src/` one project per package. Package ID, assembly name and root namespace are `RecursiveAI.` plus the project name; the prefix, the shared version and the package metadata are set once in `Directory.Build.props` and `src/Directory.Build.props`.
- `tests/OneMail.Tests` unit tests. `bench/` BenchmarkDotNet. `fuzz/` SharpFuzz targets under libFuzzer (`fuzz/run.sh`, see `fuzz/README.md`).
- `smoke/Aot` is published with NativeAOT and trimming and must produce no warnings; `smoke/Wasm` builds the format core for the browser.
- `Directory.Packages.props` holds every package version. Library projects may reference only the sibling family libraries listed there.

## Configurations

`Debug`, `Release` and `Checked`. `Checked` is Release with the `CHECKED` symbol defined, for asserting the bounds that low-level decoders state in comments; run tests and fuzzing in it.

## Smoke apps

    dotnet publish smoke/Aot -c Release -r linux-x64 -o out/aot -warnaserror   # needs clang and zlib headers on Linux
    out/aot/RecursiveAI.OneMail.AotSmoke
    dotnet workload install wasm-tools     # once, for the browser build
    dotnet build smoke/Wasm -c Release

## Rules the build enforces

Nullable on, analyzers at `latest-recommended`, warnings as errors, deterministic builds, no unsafe code unless a project opts in, no reflection-based discovery (the packages are trimming- and AOT-compatible).
