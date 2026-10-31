# Replies and forwards

`RecursiveAI.OneMail.Composition` makes the replies and forwards a mail client would, from any item in the shared view (MIME, MSG,
PST/OST, TNEF, OLM, DBX). `item.CreateReply()`, `item.CreateReplyAll()` and `item.CreateForward()` are one call each over
`MessageComposer`. The result (`ComposedMessage`) holds the new message as a MIME entity tree, and, for Outlook originals, the same
message in the MAPI property model. Nothing is sent, and the original is not changed. The same inputs and options always give the same bytes
when the clock (`TimeProvider`) and the random source (`RandomBytes`) are fixed.

## Subject

One prefix, never stacked. Every leading prefix of the original is removed (`Re: RE: Fwd: AW: topic` becomes `topic`), then the new
prefix is put on: `Re: ` for replies, `Fwd: ` for forwards (both can be replaced in the options). A prefix is a label from
`SubjectPrefixes.Labels`, ignoring case, an optional counter in square brackets or parentheses (`Re[2]:`, `Re(3):`), optional blanks, and
a colon (ASCII or full-width). A word that is not in the list (`Status: x`) is part of the subject. A subject that is only prefixes
becomes empty. Control characters and line separators in the original's subject become spaces, and trailing white space is dropped, so a
decoded subject can never start a new header.

The labels are the ones language packs of common clients write; they are an agent default and not a specification: Re, Fw, Fwd (English); AW, WG
(German); SV, Svar, VS, VB, ATB (Scandinavian); Antw, Doorst (Dutch); TR, Ré, Réf, Rép (French); RV, PD, Reenv (Spanish); ENC, RES, Resp
(Portuguese); R, RIF, Inoltro (Italian); Odp (Polish); VL (Finnish); YNT, ILT (Turkish). Labels in non-Latin scripts are not recognised.

MAPI: the MS-OXCMAIL import splits the subject (MS-OXCMAIL 2.2.3.2.6.1), so `PidTagSubjectPrefix` is `Re: ` or `Fwd: ` and
`PidTagNormalizedSubject` is the rest.

## Threading headers (RFC 5322 §3.6.4)

Replies only. A forward starts no reply thread: it has no `In-Reply-To` or `References` (agent default; clients differ), but keeps the
conversation topic and index.

- `In-Reply-To`: the original's `Message-ID`.
- `References`: the original's `References`, or its `In-Reply-To` when it has none, then its `Message-ID` (not repeated when it is already
  last). Identifiers that cannot be written (white space, angle brackets, control characters, longer than 512 characters) are dropped with
  `InvalidMessageIdDropped`.
- Too long a chain is cut to `MaxReferences` (default 20, at least 2) by keeping the first identifier and the most recent ones, and raises
  `ReferencesTruncated`.
- An original without a `Message-ID` gets no `In-Reply-To` (`ParentHasNoMessageId`); `References` holds what it had.
- The new `Message-ID` comes from `MessageIdFactory`, or `random@MessageIdDomain`; with neither, none is written and the sending server adds one.

## Conversation topic and index (MS-OXOMSG 2.2.1.3)

`Thread-Topic` and `Thread-Index` (MAPI `PidTagConversationTopic`, `PidTagConversationIndex`) are written when the original is an Outlook item
or already has an index, or when `ConversationIndexMode.Always` is set; MIME messages from other clients get none by default.

- The topic is the original's topic, else its normalised subject.
- A valid index (six bytes of FILETIME, a 16-byte GUID, then five-byte levels) gets one more level: a delta code bit and 31 bits of time
  delta from the header's time, then a random byte. The delta is `TimeDiff >> 18` when the high 32 bits of the difference AND
  `0x00FE0000` are zero (under about 1.7 years), else `TimeDiff >> 23` with the delta code set. A clock before the header's time gives
  delta 0 (`ConversationIndexTimeClamped`).
- No index, or one that is not in the form (`ConversationIndexInvalid`), starts a new thread: a header block with the original's delivery or
  sent time (never later than now) and a new GUID, then the level.
- An index that has reached 255 bytes is kept as it is (`ConversationIndexFull`).

`ConversationIndex.TryParse`, `CreateHeader` and `AppendLevel` are public and decode and extend indexes by themselves.

**Observed against the spec.** MS-OXOMSG draws the header as a reserved byte 0x01 followed by a 40-bit time (the high 32 bits of the FILETIME and
the top 8 bits of its low half). Indexes in real Outlook items (45 MSG files and the CFReDS OST) carry the time from the first byte on: `01 CD 50
A5 70 16` is the FILETIME's top six bytes, whose first byte is 0x01 for every date between 1829 and 2057. OneMail reads and writes that form, so the
header time has 2^16 FILETIME ticks (6.5 ms) of resolution. The spec's delta rules are followed as written.

## Recipients

- **Reply**: `Mail-Reply-To` if present (MIME items), else `Reply-To`, else `From` (RFC 5322 §3.6.2). Replying to a message the user sent
  (every address is one of `OwnAddresses`) goes to that message's `To` instead.
- **Reply all**: the reply target plus the original's `To`, then `Cc`; the user's own addresses are removed; each address appears once, `To`
  taking precedence over `Cc`. A `Mail-Followup-To` header (MIME items) replaces the whole list. If only `Cc` is left it becomes `To`. If
  nobody is left, the people the user wrote to are used.
- Addresses are compared ignoring case (the local part is technically case-sensitive; no real server treats it so).
- Recipients without an SMTP form (an Exchange distinguished name only) are never written and never lost silently: they are in
  `ComposedMessage.Unaddressable` with `UnaddressableRecipient`. An address that is not usable (no single `@`, white space, specials, longer
  than 320 characters) is dropped with `InvalidAddressDropped`. More than `MaxRecipients` (default 500) stops the list with `TooManyRecipients`.
  No recipient at all raises `NoReplyTarget`; the message is still made so the caller can address it.
- Display names are written as atoms, quoted strings or RFC 2047 encoded words as needed; control characters become spaces.

## Quoting

Top posting: the author's `Text`/`Html` (if given), then the quote.

- **Text**: the attribution line (`On Mon, 4 Mar 2024 09:00:00 +0100, Ann <a@x.example> wrote:` by default; `Attribution` replaces it), then
  every line of the original's text body prefixed with `> ` (`>` alone for an empty line). A `format=flowed` original gives a `format=flowed`
  reply (RFC 3676 §4.5): quoted lines get `>` with no space, soft line breaks (trailing space) survive, our own lines are space-stuffed, and
  `delsp` is kept.
- **HTML**: `<div>attribution</div><blockquote type="cite">…</blockquote>`, the original's content between `<body>` and `</body>` (all of it when
  there is no body element). The quoted HTML is not sanitised: it is as hostile as the original, and anything that shows it goes through the display sanitiser.
- A body larger than `MaxQuoteCharacters` (default 1,048,576) is cut at a line end (`QuoteTruncated`).
- Both representations are quoted when the original has both (`multipart/alternative`); one when it has one. `QuoteBodyFormat.TextOnly` or
  `HtmlOnly` quotes only that one; if it does not exist, nothing is quoted (`BodyNotAvailable`): HTML is not turned into text here.
- Inline images (`cid:`) that the quoted HTML refers to are carried in a `multipart/related`. A reply carries other attachments only with
  `IncludeAttachments`.
- **Inline forward**: the banner `---------- Forwarded message ----------` and the header block (From, Date, Subject, To, Cc), then the
  original's body unquoted, with all attachments carried. Embedded messages (`message/rfc822`) are carried as messages.
- **Attachment forward**: a `message/rfc822` part with `Content-Disposition: attachment; filename="<subject>.eml"`. MIME originals are
  embedded as their own bytes (a fresh parse of the item's message gives back exactly the source bytes, malformed input included). Outlook
  items are exported by the MS-OXCMAIL mapping. An original that is neither (an OLM record) is rebuilt from the shared view with
  `ParentSynthesized`.
- At most `MaxCarriedBytes` (default 64 MiB) of attachments and embedded originals are carried; the rest is skipped with `AttachmentSkipped`.

## MAPI result

When the original is an Outlook item (or `CreateMapi` is true) the finished MIME message is imported by `MimeToMapiConverter`, so the
MAPI message is exactly what the MS-OXCMAIL mapping makes of the MIME one, and `ComposedMessage.Report` is that import's loss report.
`PidTagInReplyToId` holds the one identifier, `PidTagInternetReferences` the chain, `PidTagConversationTopic` and `PidTagConversationIndex` the topic and
the index. An attachment forward carries the original as an embedded message.

## Not covered

Sending, signing, sanitising the quote, HTML to text, meeting responses (accept/decline), drafts' `X-Unsent`, `Disposition-Notification-To`, and
translated attribution or banner text (pass your own).

## How it is checked

`tools/composition/run.sh`: replies, reply-alls and both forwards of every sampled item, parsed back by OneMail, MimeKit, Python `email` and Go
`net/mail`, with subject, From, To, Cc, In-Reply-To and References compared to what the composer says it wrote. See `docs/compatibility.md` for the
latest numbers. `fuzz/` has the `composition` target.
