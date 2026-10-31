#!/usr/bin/env bash
# The Checked suite, the browser smoke and a 600 s run of the oxocal fuzz target, in one job (run from the repository root).
set -u
export PATH=~/dotnet10:~/.local/bin:$PATH DOTNET_ROOT=~/dotnet10 ONEMAIL_ARTIFACTS=~/onemail/oxocal/artifacts DOTNET_CLI_TELEMETRY_OPTOUT=1
export LIBFUZZER_DOTNET=~/onemail/tools/libfuzzer-dotnet SHARPFUZZ=~/onemail/tools/sharpfuzz ONEMAIL_FUZZ_WORK=~/onemail/oxocal/fuzzwork
L=~/onemail/oxocal/logs; mkdir -p $L
echo "== suite"; dotnet build tests/OneMail.Tests -c Checked -v quiet -clp:NoSummary > $L/build.log 2>&1; tail -2 $L/build.log
dotnet test --project tests/OneMail.Tests -c Checked --no-build > $L/suite.log 2>&1; tail -7 $L/suite.log
echo "== wasm"; bash smoke/Wasm/run-headless.sh ~/onemail/oxocal/wasm > $L/wasm.log 2>&1; echo "wasm exit $?"; grep -E 'oxocal|wasm smoke|exit:' $L/wasm.log | head
echo "== fuzz oxocal"
mkdir -p ~/onemail/oxocal/seeds
python3 - <<'E'
import os
d=os.path.expanduser('~/onemail/oxocal/seeds')
blobs={'weekly':"0430 0430 0B20 0100 0000 C0210000 01000000 00000000 32000000 22200000 0C000000 00000000 00000000 00000000 80 20 BC 0C 20 AD BC 0C 06300000 09300000 58020000 76020000 0000 00000000 00000000",
'daily':"0430 0430 0A20 0000 0000 A0050000 E0100000 00000000 21200000 0C000000 00000000 02000000 A0C1DC0C 80D2DC0C 00000000 207EDC0C 0016DD0C 06300000 09300000 E0010000 FE010000 0000 00000000 00000000"}
for k,v in blobs.items():
    open(f'{d}/rec-{k}','wb').write(b'\x00'+bytes.fromhex(v.replace(' ','')))
open(f'{d}/tz-struct','wb').write(b'\x01'+bytes.fromhex('2C010000'+'00000000'+'C4FFFFFF'+'00000000000B00010002000000000000'+'0000000000030002000200000000000000'[:32]))
open(f'{d}/ics-patch','wb').write(b'\x02'+bytes([5,0,65]))
open(f'{d}/item-damage','wb').write(b'\x03'+bytes([1,2,3,4,9,8,7,6]))
E
bash fuzz/run.sh oxocal 600 ~/onemail/oxocal/seeds > $L/fuzz-oxocal.log 2>&1; echo "fuzz exit $?"; tail -6 $L/fuzz-oxocal.log; ls $ONEMAIL_FUZZ_WORK/findings/oxocal 2>/dev/null | head
