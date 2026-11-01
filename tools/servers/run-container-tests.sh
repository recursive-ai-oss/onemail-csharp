#!/usr/bin/env bash
# Builds the test project, starts the published server images in a private network namespace and runs the container tests
# against them. Needs the runtime and images set up first (setup-podman.sh, pull-images.sh, setup-stalwart-cli.sh).
#
#   tools/servers/run-container-tests.sh [servers]      servers: comma list, default dovecot,stalwart,cyrus
#
# Environment: ONEMAIL_ARTIFACTS (build output), DOTNET_ROOT/PATH for the .NET SDK, ONEMAIL_CAPTURE_DIR (keep the server side of
# each session as a parser test input), ONEMAIL_TEST_FILTER (xunit query filter; default the container tests).
set -euo pipefail
D="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$D/../.." && pwd)"
servers=${1:-dovecot,stalwart,cyrus}
filter=${ONEMAIL_TEST_FILTER:-FullyQualifiedName~ServerTests}
cd "$ROOT"
dotnet build tests/OneMail.Tests -c Checked -v quiet -clp:NoSummary
# Stalwart and Cyrus both want the standard mail ports, so each server gets its own private network namespace and test run.
rc=0
for s in ${servers//,/ }; do
  echo "##### $s"
  if [ "$s" = postfix ]; then
    # The guest uses the host network (forwards on high ports) and needs no user namespace.
    "$D/servers.sh" --servers "$s" -- dotnet test --no-build -c Checked tests/OneMail.Tests --filter "$filter" || rc=1
  else
    "$D/userns.sh" -n "$D/servers.sh" --servers "$s" -- \
      dotnet test --no-build -c Checked tests/OneMail.Tests --filter "$filter" || rc=1
  fi
done
exit $rc
