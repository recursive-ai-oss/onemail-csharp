#!/usr/bin/env bash
# Fetches the two fuzzing tools by pinned version and hash into a directory (default ~/onemail/tools):
#   libfuzzer-dotnet   the libFuzzer host from the SharpFuzz project (MIT), prebuilt Ubuntu release binary
#   sharpfuzz          the instrumenter, dotnet tool SharpFuzz.CommandLine
# Then point the fuzz scripts at them:  export LIBFUZZER_DOTNET=<dir>/libfuzzer-dotnet SHARPFUZZ=<dir>/sharpfuzz
# Needs curl and a .NET 10 SDK on PATH. Safe to run again: a tool that is already present with the right pin is left alone.
set -euo pipefail
dir="${1:-${ONEMAIL_FUZZ_TOOLS:-$HOME/onemail/tools}}"
host_url="https://github.com/Metalnem/libfuzzer-dotnet/releases/download/v2025.05.02.0904/libfuzzer-dotnet-ubuntu"
host_sha256="c2c2a90d94c409a4af339a0d4f244e0442c5a5d249be0f1252ba07871f285958"
sharpfuzz_version="2.3.0"
mkdir -p "$dir"
host="$dir/libfuzzer-dotnet"
if [ ! -x "$host" ] || [ "$(sha256sum "$host" | cut -d' ' -f1)" != "$host_sha256" ]; then
  curl -fsSL "$host_url" -o "$host.part"
  echo "$host_sha256  $host.part" | sha256sum -c - >/dev/null || { rm -f "$host.part"; echo "libfuzzer-dotnet: hash mismatch" >&2; exit 1; }
  chmod +x "$host.part"; mv "$host.part" "$host"
fi
if ! dotnet tool list --tool-path "$dir" 2>/dev/null | grep -qi "^sharpfuzz.commandline *$sharpfuzz_version "; then
  dotnet tool uninstall SharpFuzz.CommandLine --tool-path "$dir" >/dev/null 2>&1 || true
  dotnet tool install SharpFuzz.CommandLine --version "$sharpfuzz_version" --tool-path "$dir" >/dev/null
fi
echo "fuzz tools in $dir"
