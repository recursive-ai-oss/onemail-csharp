# The MAPI property model

What `RecursiveAI.OneMail.Mapi` is: the in-memory form of an Outlook item (message, recipient, attachment), independent of
the file it came from. MSG files fill it today (`docs/msg.md`); the PST layers will fill the same types. Types and tags are
MS-OXCDATA 2.11.1 and MS-OXPROPS; nothing here is taken from another implementation.

## Objects

- `MapiMessage`, `MapiRecipient`, `MapiAttachment` derive from `MapiObject`, which owns a `MapiPropertyBag`, a list of
  `MapiDefect`s and a link to its parent.
- Recipients and attachments load on first use. An attachment that is an embedded message exposes it as `EmbeddedMessage`
  (a `MapiMessage` of its own); other object attachments expose `ObjectStorage`, a `MapiRawStorage` that keeps the storage's
  streams byte for byte so nothing is lost.
- `MapiMessage.Subject`, `Body`, `MessageClass`, recipient and attachment accessors are conveniences over `Properties`; the
  bag is the truth.

## The property bag

- A `MapiProperty` is `(tag, flags, value)`. The tag is a 16-bit ID plus a 16-bit `MapiType`; IDs 0x8000 and up are named
  properties and carry their `MapiNamedProperty` (property-set GUID plus a number or a string).
- **The raw value is always kept**: the bytes of the entry or value stream, read lazily through a `MapiValueSource`. Typed
  accessors (`AsInt32`, `AsDouble`, `AsBoolean`, `AsFileTime`, `AsGuid`, `AsString(encoding)`, `CurrencyUnits` ...) decode on
  demand and never change what is stored. Round-tripping a property never goes through a typed value.
- The bag keeps insertion order and duplicates. Lookups by ID (optionally restricted to types) return the **last** entry with
  that tag (`AmbiguousDuplicateProperty`). `Version` increments on change so callers can cache.
- A string property is `String8` (code page, see `docs/msg.md`) or `UnicodeString` (UTF-16LE). Asking for a string by ID
  accepts either type.

## Names

`tools/mapi/properties.tsv` lists the properties the readers use (271 tagged, 75 named), each with its canonical MS-OXPROPS
name. `tools/mapi/gen_properties.py` turns it into the generated `MapiPid.g.cs` constants and the `MapiPropertyNames` lookup.
The table is a static array: no reflection, so trimming and AOT keep it. Edit the TSV, never the generated file.

## Defects

`MapiDefect` has a `Kind`, a message and a location (object path plus stream name). Kinds named `Ambiguous*` are documented
resolutions of known ambiguities (`docs/ambiguities.md`, section MAPI/MSG); the rest are repairs. The reader never throws on
damaged content. Defect lists are capped by `MsgReaderOptions.MaxDefects`; later ones are only counted.
