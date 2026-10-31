# The calendar card

`RecursiveAI.OneMail.Display.CalendarCard` turns a calendar object in a message into the compact card mail clients show above the body, as an
HTML fragment (`ToHtml()`) and as plain text (`ToText()`). It is format core: it runs in the browser runtime and under NativeAOT.

## Inputs

All inputs end up as an iCalendar object (`IcsComponent`), so there is one card builder:

| Source | Entry point | Path |
|---|---|---|
| iCalendar bytes, an `.ics` file | `CalendarCard.FromIcs(bytes)` | parsed by `OneMail.Calendar` |
| a `text/calendar` part (RFC 6047) | `FromMime(part)` | decoded content, then as above |
| an Outlook appointment or meeting item from MSG, PST, OST, TNEF | `FromMapi(message)` | MS-OXCICAL export (`MapiToIcsConverter`), plus `PidLidNetShowUrl` as `X-MS-OLK-NETSHOWURL` |
| an Outlook for Mac appointment | `FromItem(olmItem)` | `OlmToCalendarConverter` |
| any shared-view item | `FromItem(item)` | the item's `text/calendar` parts, or the Outlook item |
| a parsed component | `Create`, `CreateAll` | one card per event series (same UID; overrides join their master) |

A message that is not a calendar item gives no card. To-dos get a card too (their due time stands in for the end). Nothing is wired into the
display HTML of a whole message; a caller composes the card above it.

## What the card says

- **Action** from METHOD (RFC 5546): `REQUEST` "Invitation" ("Updated invitation" when SEQUENCE is above 0), `CANCEL` "Cancelled", `REPLY` "*name* accepted /
  declined / tentatively accepted" (the first attendee with a PARTSTAT), `COUNTER` "Counter-proposal", `DECLINECOUNTER` "Counter-proposal declined",
  `ADD`, `REFRESH`, `PUBLISH` and no method "Event"; a method the enum does not name "Calendar item". STATUS `CANCELLED` or `TENTATIVE` is shown as a badge
  (a `CANCEL` method is cancelled whatever STATUS says).
- **When**: "Mon, 30 Nov 2026 14:00–15:00 (Europe/Paris, UTC+01:00)", "All day, Mon, 30 Nov 2026" (DTEND of a date is exclusive), a range across days as
  "… to …". `CalendarCardOptions.DisplayTimeZone` converts through the `.Calendar` zone resolution (the calendar's own VTIMEZONE, then Windows names,
  then the runtime's IANA database); the event's own time is kept as `OriginalTime` ("Organizer's time") when the zones differ. Floating times and dates
  are shown as written. An unresolvable TZID is shown as written with `UnresolvedZone`; an unknown display zone falls back to the event's zone with
  `DisplayZoneUnresolved`.
- **Recurrence** in plain English (below) plus the first `NextOccurrences` (default 5, at most 50) from the library's expansion (`RecurrenceSet`), starting
  at the series start or at `After` (a UTC instant the caller supplies; the card never reads the clock). EXDATEs are noted (first five), changed
  occurrences counted.
- **Where**: LOCATION; URLs from `CONFERENCE` (RFC 7986), `URL`, `X-MS-OLK-NETSHOWURL` (`PidLidNetShowUrl`, MS-OXOCAL 2.2.1.56.8),
  `X-MICROSOFT-ONLINEMEETINGCONFLINK`, `X-MICROSOFT-SKYPETEAMSMEETINGURL` and `X-GOOGLE-CONFERENCE`. Only absolute `http`, `https` and `mailto` URLs
  up to the field limit are kept (at most five); any other is dropped with `UrlRejected`. The Teams and Zoom values Outlook keeps in named properties
  that MS-OXPROPS does not define are not read; a meeting link Outlook exports as one of the properties above is.
- **People**: organizer, attendees with role (required, optional, chair, for information), participation status (accepted, declined, tentative,
  no reply yet, delegated) and a non-person kind (room, resource, group). The first 50 are listed, the rest counted (`AttendeesTruncated`).
- **Description**: truncated to 1,000 characters and 30 lines (`DescriptionTruncated`); attachments: at most ten file names or URLs, as text.
- Every one-line field is cut at 200 characters (`FieldTruncated`); the cards of one calendar object stop at 20 (`EventsTruncated` on the first).

All words are in `CalendarCardStrings`, one internal class, in English; dates use a fixed English layout and the invariant culture, so output does not
depend on the machine.

## Recurrence sentences

`CalendarCardRecurrence.Describe(rule, start)` returns a sentence only when it says exactly what the rule does:

- `Hourly`, `Every 2 hours`, `Every minute`, `Every 15 minutes`; `Daily`, `Every 3 days`, `Every weekday`;
- `Weekly on Monday and Wednesday`, `Every 2 weeks on Monday, Wednesday and Friday` (no BYDAY: DTSTART's weekday);
- `Monthly on day 15`, `Monthly on day 1 and day 15`, `Monthly on the last day`, `Monthly on the first Monday and the third Wednesday`,
  `Monthly on the last Friday` (also `BYDAY=FR;BYSETPOS=-1`), `Monthly on the last weekday` (Monday to Friday with one BYSETPOS), `the second to last weekend day`;
- `Yearly on 15 March`, `Yearly on 1 and 15 March`, `Yearly on the last day of March`, `Yearly on the first Monday of March`;
- each followed by `, until 30 Nov 2026` or `, 10 times`.

Everything else (BYHOUR, BYMINUTE, BYSECOND, BYYEARDAY, BYWEEKNO, RSCALE, SKIP, several BYSETPOS values, BYMONTHDAY with BYDAY, several months, SECONDLY,
COUNT with UNTIL, other parts) gives "Repeats (custom rule)" and `RecurrenceNotDescribed`. An interval above 1 with several weekdays depends on the week
start (WKST, RFC 5545 §3.3.10): a rule with `WKST=SU` (what Outlook writes on every rule) is described when neither DTSTART's day nor a listed day is a
Sunday, because then Monday-start and Sunday-start weeks group the days alike; any other week start is left to the custom text. A series with only RDATEs
is "Repeats on selected dates".

The sentences are checked by parsing them back (`tests/OneMail.Tests/Display/SentenceParser.cs`, written from the sentence forms and independent of the
generator): the parsed frequency, interval, weekday set with ordinals, days of the month, month, count and until date must equal the rule's, with what
the rule leaves out taken from DTSTART.

## Markup

The HTML is built from escaped text with fixed markup: `div`, `p`, `ul`, `li`, `span`, `strong` and, only when `ToHtml(linkUrls: true)` is asked, `a`.
Attributes are `class` and `style` with constant values, plus `href` and `rel="noopener noreferrer"` on links, where `href` is the normalised absolute
URL of an `http`, `https` or `mailto` link. Event text enters only through HTML encoding after control characters, bidirectional overrides and isolates,
zero-width characters and the line and paragraph separators are replaced by spaces. By default URLs are plain text. The card passes
`HtmlSanitizer.Sanitize` with no change and no reference other than its own links (a test over every fixture, with and without links).

## How it is checked

- `tests/OneMail.Tests/Display/CalendarCardTests.cs`: sentences, fallbacks, parse-back over a grid of rules, every method, zones, hostile and huge input
  (a 3 MB description, 10,000 attendees, 100 events, a rule that never matches), inertness, the sanitiser.
- `tools/calcard`: `run_sweep.sh` (cards for every calendar object in the fetched corpora and the fixtures, per source format and method, OneHtml parse
  errors, sentence parse-back), `run_occ_diff.sh` (occurrence lists against the library's expansion and `recurring-ical-events` 3.8.2 over the recurring
  components of the calendar corpus), `browser-check.sh` (the sanitiser's XSS vectors in every field, in headless Chromium with no sanitiser in between),
  `make_fixtures.py` (`testdata/calendar/card`: one file per method and variant, 2,700 rule shapes).
- Fuzz target `calcard`; AOT and browser smoke "calcard".
