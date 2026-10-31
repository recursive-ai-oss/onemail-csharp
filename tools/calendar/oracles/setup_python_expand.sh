#!/usr/bin/env bash
# Create the pinned Python environment for the expansion oracles (black-box: only the released PyPI packages are run).
#   setup_python_expand.sh [venv dir]      default: $HOME/onemail/oracles/recurrence-venv
# Licences: icalendar BSD-2-Clause, python-dateutil Apache-2.0 / BSD-3-Clause, recurring-ical-events LGPL-3.0-or-later and
# x-wr-timezone LGPL-3.0-or-later (both run as separate processes only, never linked or shipped), tzdata Apache-2.0.
set -euo pipefail
venv="${1:-$HOME/onemail/oracles/recurrence-venv}"
uv venv --python 3.13 "$venv"
uv pip install --python "$venv/bin/python" icalendar==7.3.0 python-dateutil==2.9.0.post0 recurring-ical-events==3.8.2 x-wr-timezone==2.0.1 tzdata==2026.6
"$venv/bin/python" -c "import importlib.metadata as m; print({p: m.version(p) for p in ['icalendar','python-dateutil','recurring-ical-events','x-wr-timezone','tzdata']})"
