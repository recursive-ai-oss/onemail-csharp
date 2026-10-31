# MSG tools

- `MsgTool summary|json|streams|gen`: dumps a message, checks stream re-encoding, regenerates `testdata/msg/generated`.
- `compare_extract_msg.py`: property-by-property comparison with the Python package **extract-msg 0.55.0** (GPL-3.0). It is run
  as an installed package through its public API in a throwaway virtual environment and is never shipped or linked; 0.58.0
  fails on embedded messages, so the version is pinned.

```
uv venv --python 3.13 venv && uv pip install --python venv/bin/python extract-msg==0.55.0
venv/bin/python tools/msg/compare_extract_msg.py <path to MsgTool.dll> <dir-or-file>...
```
