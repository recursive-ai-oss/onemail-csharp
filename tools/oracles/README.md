# Oracle drivers and the differential runner

This folder holds the machinery behind one claim: OneMail parses mail the way the established libraries do, and where it
does not, the difference is written down. Three released libraries are run as black boxes, each through a small driver of
ours, and their results are compared in one normalised form. OneMail's own driver writes the same form, so the same runner
compares it too.

| Oracle | Pin | Driver |
|---|---|---|
| MimeKit (NuGet) | 4.18.2 | `mimekit-driver` (.NET 10 console app; public API only) |
| Python `email` | CPython 3.13.x, `policy.default` (`--policy compat32` for the legacy policy) | `python/pydump.py` |
| Go `net/mail`, `mime`, `mime/multipart` | Go 1.26.x, standard library only | `go/` |
| OneMail | the working tree | `onemail/` (a stub until the real driver lands) |

None of these is referenced by anything under `src/`.

## The driver contract

A driver is a command that takes `--root DIR` (or `-root DIR` for Go) and reads **relative file names on standard input**,
one per line. For every name it parses `DIR/name` and writes **one JSON line** to standard output, in the same order, then
flushes. It never stops on a bad message: a failure becomes a record with `"error"` set. `--version` prints
`<name> <version>` and exits. The runner starts a driver once per chunk of 1,000 files; if the process dies it halves
the chunk until it has found the file that kills it and records a `"crash": true` record for that file.

## The dump form (`onemail-dump/1`)

```jsonc
{
  "schema": "onemail-dump/1",
  "id": "relative/file/name",
  "oracle": { "name": "mimekit", "version": "4.18.2", "notes": "…", "headersOrdered": true },
  "error": null,            // string when the oracle could not parse the message at all
  "defects": null,          // oracle's own parse problems as ["part0:StartBoundaryNotFoundDefect", …]; null if it has none
  "addresses": {            // only headers that are present; keys lower-case
    "to": [ { "name": "Bob", "addr": "bob@example.com" } ]   // null = the oracle could not parse that header
  },
  "date": { "utc": "2001-05-14T23:39:00Z", "offsetMinutes": 0 },  // null if absent or not understood
  "root": Entity
}
```

An **Entity** is one MIME entity; the message itself is the root entity:

```jsonc
{
  "headers": [ { "name": "Subject", "raw": "…", "value": "decoded or null" } ],
  "contentType": "text/plain",         // lower-case, with the oracle's default applied when absent or unusable
  "params": { "charset": "utf-8" },    // lower-case names, values after RFC 2231 decoding; null if the oracle gave up
  "disposition": "attachment",         // lower-case, or null
  "filename": "a.txt",                 // the oracle's own filename resolution (disposition filename, then type name), or null
  "cteApplied": "none",                // the transfer decoding the oracle applied: none | base64 | quoted-printable | uuencode
  "preamble": false, "epilogue": false, // multipart only: any non-whitespace text; null where the oracle cannot say
  "body": { "len": 5, "sha256": "…", "eolSha256": "…", "trimSha256": "…" },  // decoded bytes; null for multipart and message/*
  "children": [ Entity ],              // multipart parts, in order
  "message": Entity,                   // for message/rfc822: the parsed inner message, else null
  "error": "…"                         // optional: a problem the oracle reported for this entity only
}
```

### Normalisation rules

Representation noise is removed, and nothing else.

1. **Raw header text** is the octets as ISO-8859-1 characters, so every byte survives JSON. Names keep the case the oracle
   reports; the runner lower-cases them to compare.
2. **Unfolding.** The runner compares `raw` after replacing each fold (optional whitespace, line break, whitespace) by one
   space and trimming both ends. Go's `net/mail` unfolds on its own and trims each line, so this is the common ground.
3. **Header order.** Go returns a map, so `oracle.headersOrdered` is false and only the order of values *within* one name
   is compared. MimeKit keeps `Content-*` fields apart from the rest of the message header, so order is compared without
   `Content-*` fields.
4. **Decoded values** (`value`) are given only for unstructured headers (`Subject`, `Comments`, `Keywords`,
   `Content-Description`, `Thread-Topic`) and only when the oracle decodes them (Go reports null for a charset its standard
   library does not know). Null never differs from anything.
5. **Bodies** are hashed after transfer decoding and before charset decoding. Three hashes are written: exact, with line
   endings folded to LF, and with line endings folded and trailing line breaks removed. The runner classes a mismatch as
   `body.eol` or `body.final-newline` when the weaker hash agrees, and `body.content` otherwise, so a line-ending policy is
   visible and not mistaken for corruption.
6. **Addresses** are flattened (group members join the list; the group name is not compared) and reduced to display name
   and address. Python and MimeKit report decoded display names; Go's own decoding is used as it comes.
7. **Dates** are compared as the UTC instant, then the zone offset when both oracles give one. Python reports no offset for
   `-0000` (unknown zone).
8. **Lone surrogates** in decoded text become U+FFFD in every driver.
9. A message/rfc822 part is parsed by every driver (Go's driver parses it with `net/mail`) and the inner message is
   compared like any entity. Other `message/*` types have no body hash.
10. Go's multipart reader is used in raw mode so that `Content-Transfer-Encoding` is left alone, and the driver then
    decodes `base64` (the standard stream decoder, which skips CR and LF only) and `quoted-printable`. Go has no
    uuencode, so `cteApplied` is `none` there.

Differences between oracles are classes in the report: `header.names`, `header.raw`, `header.decoded`, `header.order`,
`address.*`, `date.*`, `entity.type`, `entity.params`, `entity.filename`, `entity.cte`, `entity.children`,
`entity.preamble`, `body.*`, `parse-failed`, each with a sub-class (the header or parameter name, the oracle that failed).
Parts of a message use the same classes prefixed `part.` for headers.

## Running

```sh
tools/oracles/build.sh BIN                       # needs .NET 10 and Go; restores the pinned MimeKit
tools/corpus/make_differential.py                # regenerates testdata/differential (the ambiguity probes)
tools/oracles/run-corpus.sh NAME ROOT [--jobs 8 --chunk 1000 --max-seconds 1500]
```

`run-corpus.sh` writes dumps (gzip JSON lines) and per-chunk summaries under `$ONEMAIL_ORACLE_OUT/NAME/` and finishes with
`report.md` and `report.json`. It is resumable: run the same command again after exit status 75 (`--max-seconds` ran out).
`diffrun.py compare` redoes the comparison from stored dumps, which is how a fourth driver's dumps are added later;
`diffrun.py cases` prints a per-message digest for small corpora. Reports for corpora of real people's mail
(`enron*`, `edrm*`) contain counts, classes and file names, never message text.

Adding a driver: write it to the contract above, then `--driver NAME="command {root}"` (or an entry in
`default_commands`), and `--drivers mimekit,python,go,NAME`.

The corpora are fetched by `tools/corpus/fetch.sh` and are never committed.
