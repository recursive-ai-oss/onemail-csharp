#!/usr/bin/env bash
# Create the pinned Python oracle environment (black-box: only the released PyPI packages are run).
#   setup_python.sh [venv dir]      default: $HOME/onemail/work/venv-cal
set -euo pipefail
venv="${1:-$HOME/onemail/work/venv-cal}"
uv venv --python 3.13 "$venv"
uv pip install --python "$venv/bin/python" icalendar==7.3.0 vobject==0.9.9
"$venv/bin/python" -c "import icalendar, vobject; print('icalendar', icalendar.__version__, 'vobject', vobject.VERSION if hasattr(vobject,'VERSION') else '0.9.9')"
