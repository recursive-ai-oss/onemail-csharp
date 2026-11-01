#!/usr/bin/env bash
# Builds generated stores of growing size with PstOracle pstgen and prints each run's peak resident set (GNU time), to show that the working
# set of the PST builder does not grow with the store.
#
#   tools/pst/memory.sh <work-dir> [count[:folders] ...]     default: 10000:10 100000:10 1000000:10 600000:1
#
# Needs PstOracle built (CONFIG, default release, under $ONEMAIL_ARTIFACTS/bin) and /usr/bin/time. Each file is deleted after its run.
set -u
work="${1:?work dir}"; shift
runs=("$@"); [ ${#runs[@]} -eq 0 ] && runs=(10000:10 100000:10 1000000:10 600000:1)
config="${CONFIG:-release}"
ora="${ONEMAIL_ARTIFACTS:?set ONEMAIL_ARTIFACTS}/bin/PstOracle/$config/RecursiveAI.OneMail.PstOracle.dll"
mkdir -p "$work"
echo "host: $(nproc) cores, $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2), load $(cut -d' ' -f1-3 /proc/loadavg)"
for r in "${runs[@]}"; do
  n="${r%%:*}"; f="${r##*:}"; [ "$f" = "$r" ] && f=10
  echo "== $n messages, $f folder(s)"
  /usr/bin/time -f 'peak RSS %M KiB, elapsed %e s, cpu %U s user %S s sys' dotnet "$ora" pstgen "$work/gen.pst" "$n" "$f" 10 2>&1 | grep -E 'working set|pstgen|peak RSS|complete|Unhandled|Exception' | sed 's/^/  /'
  rm -f "$work/gen.pst"
done
echo "load after: $(cut -d' ' -f1-3 /proc/loadavg)"
