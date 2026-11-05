# PST fixtures

`outlook-ansi.pst` is a small Outlook-written ANSI store (wVer 14, 512-byte pages), made by `tools/outlook/Make-Fixtures.ps1 -Kind big -Items 8 -StoreType 3`
with Outlook 16.0.20527: folders `Big` (8 messages), `Recipients` (8 messages, 3 recipients and one attachment each) and `Deleted Items/To delete` (20 messages).
All content is generated text with `example.com` addresses. It is the sample for the ANSI paths (2-byte row indexes, NUL-padded strings).
