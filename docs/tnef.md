# TNEF (winmail.dat)

Normative notes for `RecursiveAI.OneMail.Tnef`, written from MS-OXTNEF (v20250520) and checked against public files. Layers:

| Type | Layer |
|---|---|
| `TnefDocument`, `TnefAttributeRecord`, `TnefWriter` | Attribute stream: signature, legacy key, then attributes (level, id, length, data, checksum). Lazy data. |
| `TnefPropertyReader` | A property list (attMsgProps, attAttachment, recipient rows): types, named properties, multi-valued values. |
| `TnefMessage`, `TnefAttachment` | A `MapiMessage` (same shape as MSG and PST items), with embedded messages as lazy slices. |
| `TnefMessageWriter` | `MapiMessage` -> TNEF. Returns warnings naming each property the format cannot carry. |

`MimeEntity.EnumerateTnefParts()` finds `application/*` parts and `winmail.dat`/`*.tnef` names; `IsTnef`/`ReadTnef` open one.
`Mail.Open` opens a bare TNEF file as one item.

## Fidelity

- **Attribute layer is byte-identical.** `TnefDocument.WriteTo` reproduces any input: wrong checksums, unknown attributes, a truncated last
  attribute and trailing bytes are kept as they were.
- **MAPI layer is property-exact.** Tag, type, named-property GUID and name, and raw value survive write then read. Properties TNEF cannot
  carry (server IDs, named properties without a name) are reported by name in the writer's warnings rather than dropped silently.
- Legacy attributes (subject, dates, body, recipients, sender, priority, ...) become properties per MS-OXTNEF 2.1.3.3. The writer emits
  the legacy attributes old readers look for, but only for text the code page can hold; the rest stays in the Unicode property.

## Defects

Typed, in `TnefMessage.TnefDefects` (kind, byte offset in the stream concerned, detail) and mirrored as `LowerLayerDefect` in the MAPI
defect list. Parsers never throw on content. Kinds named `Ambiguous*` are the documented resolutions below; the others are repairs
(`AttributeTruncated`, `TrailingBytes`, `InvalidLevel`, `ChecksumMismatch`, `LimitReached`, `UnexpectedVersion`, `MissingCodePage`, ...).

## Limits (defaults, on)

| Limit | Default |
|---|---|
| attribute data | 1 GiB |
| attributes in a stream | 1,000,000 |
| attachments, recipients | 65,536 each |
| properties in a list, values in a multi-valued property | 1,000,000 |
| embedding depth | 16 |
| announced decompressed RTF size | 256 MiB (header check only; RTF stays compressed) |
| values read eagerly | 64 KiB; larger ones stay lazy |

Reaching a limit raises `LimitReached` and stops that part. The public file `oom.tnef` (a property count of 873,267,203) opens in
about 3 KB of allocations with `ChecksumMismatch`, `LimitReached`, `MissingCodePage` and `UnexpectedVersion`.

## Code pages

8-bit strings are read with attOemCodepage when present and nonzero (MS-OXTNEF 2.3.3.2, rule 2), reported as
`MapiCodePageSource.Declared`; otherwise the shared model's rules apply. Rule 1 (a MIME charset on the enclosing part) is not applied:
`MimeEntity.ReadTnef` does not pass it down. The writer uses the message's resolved code page, falling back to windows-1252, writes it as
attOemCodepage and reads back to the same message with no extra property.

Ambiguity entries are in `ambiguities.md` (T1-T8).

## TNEF inside MIME

`MimeEntity.EnumerateTnefParts()` and `ReadTnef` read the part on their own. `MimeToMapiConverter` merges a qualifying part into the
`MapiMessage` (MS-OXCMAIL 2.2.3.8; the rules and loss codes are in [`conversion.md`](conversion.md)) and `MimeMailItem` shows its attachments and
bodies in place of winmail.dat ([`shared-view.md`](shared-view.md), V9). A part qualifies when the stream has none of `NotTnef`,
`UnexpectedVersion`, `AttributeTruncated`, `ChecksumMismatch` or `LimitReached`, and its `PidTagTnefCorrelationKey` matches the message's `X-MS-TNEF-Correlator`. Of the six corpus
files, only `oom.tnef` (damaged) is not unpacked. `bug52400-winmail-simple.dat` and `bug52400-winmail-with-attachments.dat` end in CR LF outside the
attributes; MS-OXCMAIL 2.2.3.8 says to ignore such a stream but all four oracles unpack them, so they are unpacked and reported (`AmbiguousTnefTrailingCrLf`, OX20).
`MapiToMimeConverter` writes a TNEF message on request (`OxcMailExportOptions.Tnef`).
