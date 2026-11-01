# DKIM and ARC (`RecursiveAI.OneMail.Cryptography`)

Normative notes for signing and verifying DKIM signatures (RFC 6376, with RFC 8301's algorithm rules and RFC 8463's Ed25519) and ARC chains (RFC 8617), and for the DNS TXT client that fetches key records. RSA runs on the .NET base class library; Ed25519 comes from `RecursiveAI.OneCrypto`. Native platforms only (not browser WebAssembly). An independent review of this package is recorded in the Review section below.

## What it does

| Area | Types (`RecursiveAI.OneMail.Cryptography.*`) |
|---|---|
| DKIM verify | `Dkim.DkimVerifier` → one `DkimSignatureResult` per `DKIM-Signature` header |
| DKIM sign | `Dkim.DkimSigner`, `DkimSigningKey` (RSA ≥ 2048 bits, Ed25519 seed or PKCS #8 PEM), `DkimSignerOptions` |
| Key records | `Dkim.DkimKeyRecord.TryParse` (hostile input, typed `DkimKeyError`) |
| ARC | `Arc.ArcVerifier` → `ArcVerificationResult` (chain status, every set, oldest-pass); `Arc.ArcSealer` adds a set |
| DNS | `Dns.ITxtResolver`; `Dns.DnsTxtClient` (UDP, TCP on truncation); `Dns.InMemoryTxtResolver` for tests |
| `Authentication-Results` | `DkimSignatureResult.ToString()` and `ArcVerificationResult.ToString()` render RFC 8601 method results; `Arc.AuthenticationResultsFormatter.Format` joins them |

The core package's `AuthenticationResults.Parse` reads what is rendered here. Inputs are message bytes (or a parsed `MimeEntity`, whose `ToArray()` is the bytes it was read from). Canonicalisation always works on those bytes, never a re-serialised tree.

## Verification results

A result is never a bare bool. `DkimSignatureResult` carries `Status`, `Reason` (a `DkimReason`), `Detail`, domain, selector, identity, algorithm, key size, canonicalisations, the `h=` names and which of them are over-signed, the byte ranges of the covered headers and body, the `l=` value, the canonical body bytes hashed, whether unsigned content follows (`BodyHasUnsignedContent`), `SignedAt`/`ExpiresAt`/`TimeValidity`, and `Warnings`.

Status mapping (RFC 8601 §2.7.1):

| Status | When |
|---|---|
| `Pass` | the signature verified |
| `Fail` | well formed and usable but wrong: body hash or signature mismatch, `x=` in the past, `l=` larger than the body, `l=` with content after the signed part (`AllowBodyLengthLimit` off), a `From` field the signature does not cover (`AllowUnsignedFromInstances` off), key revoked (`p=` empty), RSA key under `MinimumRsaBits`, `rsa-sha1` when `AllowRsaSha1` is false, strict-key (`t=s`) identity mismatch |
| `Neutral` | the signature itself is unusable: malformed tag list, duplicate or empty tags, a missing required tag, bad `v=`, unknown algorithm or canonicalisation, `q=` without `dns/txt`, `i=` outside `d=`, `h=` without From, invalid `d=`/`s=` |
| `TempError` | the key lookup failed temporarily |
| `PermError` | no key record, an unusable record, a record whose type, `h=` or `s=` does not allow the signature |

Warnings on a passing signature: `WeakAlgorithm` (rsa-sha1), `WeakKey` (RSA under 2048 bits), `TestingKey` (`t=y`), `BodyLengthLimit` (`l=` used), `SignedInFuture`, `NoExpiry`.

## Rules and choices

- **Header selection** (RFC 6376 §5.4.2): for each name in `h=`, the nearest not yet used instance from the bottom of the header block; a name with no instance left adds nothing (that is how over-signing and absent headers work). The header being verified is never in its own scope.
- **Line ends.** Bare LF is treated as the CRLF it would be on the wire, on both signing and verifying. A message signed as LF verifies after a hop that converted it to CRLF, and the signature header written for an LF message uses LF.
- **What a pass covers.** A `DKIM-Signature` passes only if the bytes a reader would show are the bytes that were hashed. Two cases the RFC allows to verify are therefore not a pass by default. (1) `l=` shorter than the body (RFC 6376 §8.2): anyone can append text and the signature still verifies, so the result is `Fail` with `BodyLengthLimited`; `AllowBodyLengthLimit = true` gives the pass back, with `BodyHasUnsignedContent` set. An `l=` that covers the whole body is fine. (2) A `From` field above the one the signature covered (RFC 6376 §5.4.2, §8.15): `h=from` covers one instance, the nearest the body, and a reader may show another; the result is `Fail` with `UnsignedFromHeader`; `AllowUnsignedFromInstances = true` gives the pass back. For every other name in `h=` that the message holds more often than the signature lists it, the signature passes and `UnsignedHeaderInstances` names it (warning `UnsignedHeaderInstances`); signing with `OverSign` makes the signature fail instead when such a field is added. ARC message signatures follow (1) and not (2).
- **Body** (RFC 6376 §3.4.3–3.4.4): simple drops trailing empty lines and turns an empty body into one CRLF; relaxed collapses whitespace, strips line-end whitespace, drops trailing empty lines, and keeps an empty body empty. `l=` covers that many canonical bytes; the result reports the source range up to the end of the line holding the last one.
- **Several key records** at one name: the signature passes if any record verifies.
- **Key records** are bounded before they are decoded: 8,192 characters, 2,400 decoded bytes, RSA up to 8,192 bits; `p=` accepts a SubjectPublicKeyInfo or a bare PKCS #1 key (RSA) and the raw 32 bytes or a SubjectPublicKeyInfo (Ed25519). `v=` may appear anywhere.
- **Tag lists** follow RFC 6376 §3.2: case-sensitive names, no repeats, and no empty element except after the last semicolon. Whitespace is tolerated everywhere the grammar allows folding.
- **Weak algorithms.** Verification accepts RSA from `MinimumRsaBits` (default 1024) and `rsa-sha1`, and flags them. RFC 8301 §3.1 tells verifiers not to accept rsa-sha1: set `AllowRsaSha1 = false` for that. Signing refuses RSA under 2048 bits, `rsa-sha1`, and algorithms that do not match the key.
- **Names** in `d=`/`s=` must be plain ASCII DNS names; internationalised names (RFC 8616) are not supported and give `Neutral`.
- **Limits.** `MaxSignatures` (10; the rest are reported, not examined), `MaxSignedHeaders` (100), signature `b=` at most 4,096 bytes, 64 tags per list, `MaxHeaderFields` (10,000 header fields; a message with more is not verified and gives one `Neutral` result with `TooManyHeaders`, or a failed ARC chain). Header selection costs the number of names in `h=`, not the number of fields in the message.
- **Time.** `x=` in the past fails; a `t=` more than `ClockSkew` (5 minutes) in the future is flagged `NotYetValid`, not failed. Pass a `TimeProvider` to fix the clock.

## ARC

`ArcVerifier` follows RFC 8617 §5.2: (1) more than 50 sets fail; (2) a newest seal with `cv=fail` fails; (3) every instance 1..N must have exactly one of each header, `cv` must be `none` at 1 and `pass` above, and none may be `fail`; (4) the newest ARC-Message-Signature must verify; (5) optionally the older ones give `OldestPass`; (6) every seal verifies, newest first, over the sets 1..i in the order results, message signature, seal, relaxed, with the seal's own `b=` emptied. All failures are final for the chain, but each signature keeps its own `DkimSignatureResult`.

`ArcSealer` follows §5.1: it adds the instance after the highest, writes the three headers (seal, message signature, results, top to bottom) and returns the sealed message. A chain whose newest seal says `cv=fail` is not sealed again. A failing chain is sealed with `cv=fail` and the seal covers only the new set. The caller validates the incoming chain when it arrives (`ArcVerifier`), modifies the message, and passes the status it found; with no status passed, the sealer validates the message it is given, which is only right when nothing changed since arrival.

Choices where the RFC and implementations differ, settled by the ARC test suite and black-box runs of dkimpy 1.1.8 and mailauth 8.0.4 (all three agree):

- **Tag order.** `i=` may be anywhere in an ARC-Seal or ARC-Message-Signature (the suite sorts tags alphabetically). In ARC-Authentication-Results it must come first, as the grammar says.
- **Default canonicalisation.** An ARC-Message-Signature with no `c=` is checked as relaxed/relaxed, not RFC 6376's simple/simple. The suite's `ams_fields_c_na` scenario, dkimpy and mailauth all verify it that way. Our sealer always writes `c=`.
- **`rsa-sha1`.** ARC never accepts it, whatever `AllowRsaSha1` says for DKIM (the suite's `ams_fields_a_sha1` and `as_fields_a_sha1`).
- **`h=` of an ARC-Message-Signature** naming `arc-seal` fails it. `arc-authentication-results` and an earlier `arc-message-signature` in `h=` are tolerated, as the suite's own passing messages need.

## DNS

`ITxtResolver.LookupTxtAsync(name, ct)` returns a status (`Success`, `NameNotFound`, `NoRecords`, `TemporaryFailure`) and the records, each with its character-strings joined without separators (RFC 6376 §3.6.2.2). `DnsTxtClient` asks the configured (or system) recursive resolvers: UDP with an EDNS0 payload size of 1232, then TCP when the reply is truncated; a random query id; replies are accepted only from the connected server and only if id and question match; CNAME chains in the answer are followed (8 hops); compression pointers must point backwards (32 jumps at most); a TXT record whose strings do not exactly fill its length is dropped; an answer over 64 KiB of TXT data is a failure. A hostile reply produces a failed lookup, never an exception. There is no DNSSEC: the resolver is trusted.

## Checks

Everything below ran on the worker (x64, .NET 10.0.402) unless marked local.

| Check | Input | Result |
|---|---|---|
| RFC 8463 Appendix A | both signatures (Ed25519 and RSA) | verify |
| RFC 6376 Appendix A.2 | the example message | **does not verify as printed**: its `bh=` is the hash of the *relaxed* body while it says `c=simple/simple` (the double space in "game.  Are" decides). We report `BodyHashMismatch`; the test documents it |
| Two-way interop, DKIM | 320 messages: 10 bodies (ASCII, empty, whitespace runs, 8-bit UTF-8, multipart with base64, folded headers, no final line end, repeated headers, LF-only, blank-only) × RSA/Ed25519 × 4 canonicalisations × `l=` on/off × over-signing on/off | dkimpy 1.1.8: ours→theirs 320/320, theirs→ours 320/320. mailauth 8.0.4: 320/320 and 320/320 (its `l=` signs only the first 5 bytes, which also checks our `l=` handling). Both oracles and this library reject 24/24 tampered copies |
| ARC, interop | 6 chains sealed by us (RSA, Ed25519, 2 and 3 hops, a hop that edited the body after validating) | mailauth: 6/6 pass, and 6/6 when it seals one more hop on top, which we validate. mailauth-sealed → ours: 1/1; ours sealed on top of mailauth's: 1/1 verified by mailauth. dkimpy verifies the RSA chain (1/1) and cannot verify any Ed25519 ARC ("unknown signature algorithm: ed25519-sha256") |
| ARC test suite (MIT, data files only) | 171 validation scenarios | 171/171 agree after the four choices above; dkimpy 168/171 (3 scenarios whose expected status the suite leaves blank), mailauth 166/171 (it accepts duplicate or odd-cased tags and `;;` in a seal, which the suite expects to fail; it fails an empty `h=`). The suite's 17 signing scenarios compare generator output and do not verify as assembled messages with any of the three implementations, so they are not counted |
| Unit tests | full Checked suite 2,861/2,861 pass (worker; includes the DKIM, ARC and DNS tests and the live-DNS test with `ONEMAIL_DNS_LIVE=1`) | vectors, 4×2×2 sign/verify matrix, tamper classes, key-record and tag-list grammar, DNS stub server (UDP, TCP fallback, CNAME, NXDOMAIN, hostile replies), ARC chains |
| Fuzzing | `dkim-tags`, `dkim-key`, `dns-reply`, 600 s each, one process, Checked build | `dkim-key` 30.9 M executions, `dns-reply` 34.0 M (about 51 k and 57 k per second, peak RSS 27 MB); no crashes, timeouts or leaks in any of the three; the `dkim-tags` totals were not captured, its findings directory is empty |
| Native AOT, pack | smoke app publish and run with the cryptography block; `dotnet pack` | zero trim/AOT warnings, `cryptography: ok`; nupkg and snupkg for the Cryptography package produced with package validation on |

Method: `tools/dkim/` (`setup.sh` installs the oracles, `run.sh` runs the DKIM interop, `fetch-arc-suite.sh` fetches the suite). The oracles are run as released binaries; their source is not read.

## Review

An independent review of DKIM, ARC and the DNS client: the code read against RFC 6376, 8301, 8463 and 8617, hostile-input tests, the ARC test suite, dkimpy and mailauth as oracles, and fuzzing. Verdicts by question; what changed is in the changelog.

1. **Covered bytes.** Two cases made a pass mean less than the reader sees, and are fixed (see *What a pass covers*): `l=` with content after the signed part, and a `From` field the signature does not cover. Other unsigned duplicates are reported, not failed (`UnsignedHeaderInstances`); that is a choice, since a duplicate `Subject` is not an identity. Not covered by any DKIM verifier: which header field a mail reader shows (it may differ from the one nearest the body), and how a reader splits headers from body when a line has no colon or holds only whitespace. This verifier reads such a line as part of the header block, a reader may start the body there; the exposure is hiding signed fields, not forging ones, and it is not an ambiguity-table entry because no client has been observed. Verification works on the bytes passed in, never a re-serialised tree.
2. **Results.** Always a `DkimSignatureResult` (status, reason, signer domain and identity, key size, covered ranges, warnings), never a bool; `IsPass` is a convenience over `Status`. Binding the signer to the sender is the caller's: compare `Domain` (or `Identity`) with the domain of the `From` they show; the result guarantees only that, by default, that `From` is the one that was signed. No alignment helper exists yet. Weak but readable signatures pass with warnings (`WeakAlgorithm`, `WeakKey`, `TestingKey`); to gate on them set `AllowRsaSha1 = false` and raise `MinimumRsaBits`. A DNS failure is `TempError`, a missing key `PermError`, never a pass.
3. **Safety defaults.** Signing refuses RSA under 2048 bits, `rsa-sha1` and mismatched algorithms, and header names that could add tags. Reading accepts rsa-sha1 and RSA from 1024 bits, flagged, as the family rule says; RFC 8301 §3.1 says verifiers must not accept rsa-sha1, so the default is looser than the RFC and the opt-out is `AllowRsaSha1 = false`. ARC never accepts rsa-sha1. MAC and hash comparisons use `CryptographicOperations.FixedTimeEquals`.
4. **Hostile input.** Tag lists (64 tags, 4 KiB `b=`), key records (8,192 characters, 2,400 decoded bytes, 8,192-bit RSA), `h=` (100 names), header fields (10,000), signatures (10) and ARC sets (50) are bounded before work is done. DNS replies: the id comes from the system CSPRNG and the UDP socket is connected to the resolver, so the source port is the operating system's random ephemeral one and datagrams from other sources are dropped; id and question must match; compression pointers must point backwards and are limited to 32 jumps; names to 255 bytes; answers examined to 128; TXT total to 64 KiB; TCP replies framed and bounded; a truncated UDP reply is retried over TCP and is not trusted. There is no DNSSEC: the resolver is trusted. Before the review, a message of millions of tiny header fields cost time proportional to fields times names in `h=`; header selection is now indexed and capped.
5. **ARC.** The chain rules follow RFC 8617 §5.2 as listed above; a missing or doubled header, a gap or repeat in instances, a wrong `cv`, an unverifiable newest message signature or any seal fails the chain, and sets over 50 are refused before parsing. The ARC test suite gives 171 agreeing of the 188 scenarios (the other 17 are signing scenarios that compare generator output and verify with none of the three implementations).

Not verified: how mail clients choose and display `From` and headers (no client observed), live-signed mail from large providers, and ed25519 ARC against an independent verifier (dkimpy cannot verify it).

Follow-ups, not blockers: a helper that compares signer domain with the shown `From` under the relaxed/strict alignment rules; the whitespace-only-line header split as an ambiguity entry once a client is observed; no cache of key lookups across messages (each message does its own lookups).

Runs after the fixes: full Checked suite 3,348 of 3,348; DKIM interop with the 320-message set: dkimpy 1.1.8 and mailauth 8.0.4 verify ours 320/320 each, ours verify dkimpy's 320/320 and mailauth's 320/320 with `l=` allowed (192/320 by default: the 128 whose `l=` covers 5 bytes of a longer body are now `BodyLengthLimited`, as intended); ARC suite 171 agree, 17 differ of 188, the same 17 non-verifying signing scenarios as before; `dkim-tags` 17.3 M executions, `dkim-key` 31.4 M, `dns-reply` 38.0 M, 600 s each, no findings, peak RSS 27 MB; native AOT publish with warnings as errors runs `cryptography: ok`; the package packs.
