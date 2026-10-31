# Outlook calendar and task items, and their iCalendar conversion

Normative notes for `RecursiveAI.OneMail.Calendaring` (the typed views over a `MapiMessage`) and
`RecursiveAI.OneMail.Conversion.OxcIcal` (MS-OXCICAL). Specs: MS-OXOCAL, MS-OXOTASK, MS-OXCICAL, MS-OXPROPS. Losses are reported as in
[`conversion.md`](conversion.md); choices on ambiguous input are rows OC1-OC6 in [`ambiguities.md`](ambiguities.md).

## Typed views (read)

- `OutlookAppointment.TryCreate(message)` returns a view for the calendar message classes, or null: `IPM.Appointment`, the meeting classes
  (`IPM.Schedule.Meeting.Request`, `.Resp.Pos`, `.Resp.Tent`, `.Resp.Neg`, `.Canceled`, `.Notification.Forward`) and the exception-object class.
  `Kind` says which. The view reads the named properties on demand; nothing is copied, and the property bag stays the truth.
- Fields: subject, location, start and end (UTC instants) with `StartLocal`/`EndLocal` in the item's own zone, all-day, busy status, sensitivity,
  importance, response status, sequence, meeting type, reminder, global object ID and clean global object ID, organizer, attendees (recipient
  table rows with role and response), the recurrence (`AppointmentRecurrencePattern`), the time zone (`OutlookTimeZone`: TZDEFINITION when
  present, else the TIME_ZONE_STRUCT, and a `VTIMEZONE` component built from it), modified instances (the exception attachments joined
  with the `ExceptionInfo` records) and deleted instance dates.
- `OutlookTask.TryCreate(message)` reads `IPM.Task`: status, percent complete, start and due date, owner, assigner state, completion, task
  recurrence (the same `RecurrencePattern`).
- `MapiMailItem.Appointment` and `MapiMailItem.TaskItem` open the views from the shared view; `Kind` stays `Appointment`, `Task` or `Message`.
- Blob readers (`AppointmentRecurrencePattern.TryParse`, `TimeZoneDefinition.TryParse`, `TimeZoneStruct.TryParse`) are lenient: they never
  throw, return `CalendaringDefect`s with the byte offset, and are bounded by `CalendaringLimits` (exceptions, deleted/modified instances,
  rules, string lengths). Writing a parsed blob back gives the same bytes for well-formed input.
- A text/calendar part inside MIME (RFC 6047 iMIP) is a `MimePart`: `MimeMailItem.CalendarParts` lists every one, attachments and
  alternatives of the body alike (a text alternative is not in `Attachments`, RFC 2046 5.1.4). `IcsToMapiConverter` turns its content into the item.

## Recurrence

An Outlook recurrence (`RecurrencePattern`: frequency, pattern type, period, first date/day-of-week, end) maps to one RRULE by the templates of
MS-OXCICAL 2.1.3.2.2; `OutlookRecurrence.ToRRule` writes it and `OutlookRecurrence.Days` walks the valid days exactly as MS-OXOCAL 2.2.1.44.1
describes (used as the independent check in the tests). Expansion goes through `RecursiveAI.OneMail.Calendar.Recurrence`; there is no
second engine. Moved/changed instances become `RECURRENCE-ID` overrides, deleted ones `EXDATE`. The pattern's start offset and the series zone
give `DTSTART;TZID=`. `WKST` is always written (OC4) and `INTERVAL` is omitted when 1.

## Item to iCalendar (`MapiToIcsConverter`)

`Convert(MapiMessage)` returns the VCALENDAR bytes as a `ContentDocument` and a `LossReport`. Appointments and meeting objects give a VEVENT
(`METHOD` from the meeting type: REQUEST, REPLY, CANCEL; PUBLISH for a plain appointment; none for a forward notification), tasks a VTODO.
Mapped: UID (from the global object ID, OC5), DTSTAMP, DTSTART/DTEND with TZID or UTC or DATE (all-day), SUMMARY, LOCATION, DESCRIPTION,
CLASS, PRIORITY, TRANSP and `X-MICROSOFT-CDO-BUSYSTATUS`, SEQUENCE, CREATED, LAST-MODIFIED, ORGANIZER, ATTENDEE (CN, ROLE, PARTSTAT, RSVP),
VALARM from the reminder, RRULE/EXDATE/RDATE, exceptions, VTIMEZONE from the item's definition. Everything else is in the report
(`NoIcalMapping`, store bookkeeping as `Internal`).

## iCalendar to item (`IcsToMapiConverter`)

`Convert(IcsComponent)` converts the first component; `ConvertAll` gives one result per VEVENT series (overrides with the same UID become exception
attachments of their series) and VTODO. Zones: a TZID with a `VTIMEZONE` gives a TZDEFINITION by sampling the zone through the item's year (two
changes = a DST rule, OC6); a known Windows or IANA name is resolved through the `.Calendar` zone layer; otherwise UTC with `UnknownTimeZone`.
A floating time or a date takes UTC (`FloatingTimeAssumedUtc`, OC3). A task's UID must fit the 16-byte `PidLidTaskGlobalId`: a UID of ours
(32 hex digits, `@onemail`) comes back unchanged, any other is replaced by a derived GUID and reported (`UidReplacedByGuid`).

## How it is checked

- Spec blobs: the weekly and daily examples of MS-OXOCAL 4.1.1 read and write back byte for byte; TIME_ZONE_STRUCT offsets for Pacific, Sydney
  (southern hemisphere) and a zone without DST.
- 18 generated recurrences (daily, weekly, monthly, monthly-nth, yearly, yearly-nth; ending never, by count, by date) in three zones (UTC,
  Pacific, Sydney): the expansion of the RRULE we write equals the spec's day walk, 54 of 54.
- The same 54 ICS files (`testdata/calendar/oxocal/generated`, written by `ONEMAIL_OXOCAL_OUT=<dir>` on the tests) through
  `tools/oxocal/oracle-checks.sh`: dateutil 2.9.0.post0, recurring-ical-events 3.8.2 and Ical.Net 5.2.3 expand them; Python icalendar 7.3.0 and
  Ical.Net parse every file we produce.
- Item to ICS to item and ICS to item to ICS (`tools/oxocal/OxocalTool roundtrip|ics`, and the `OxcIcalRoundTripTests`): mapped properties equal,
  the rest in the report, nothing silent.
- `tools/oxocal/compare_extract_msg.py`: subject, location, attendees, task status and dates against extract-msg 0.55.0.
- Fuzz target `oxocal`: recurrence blobs, TZ structures, and conversion over patched iCalendar and damaged items.
