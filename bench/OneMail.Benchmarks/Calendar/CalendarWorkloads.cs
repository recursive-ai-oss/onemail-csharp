using System.Globalization;
using System.Text;
using FolkerKinzel.VCards;
using FolkerKinzel.VCards.Enums;
using FolkerKinzel.VCards.Models;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using RecursiveAI.OneMail.Calendar;
using RecursiveAI.OneMail.Calendar.ContentLines;
using RecursiveAI.OneMail.Calendar.Forms;
using RecursiveAI.OneMail.Calendar.Ics;
using RecursiveAI.OneMail.Calendar.Recurrence;
using RecursiveAI.OneMail.Calendar.VCard;
using IcalCalendar = Ical.Net.Calendar;

namespace RecursiveAI.OneMail.Benchmarks;

/// <summary>
/// The work of each calendar and contact row, on both sides. Every method does its set-up (reading the input, parsing what a row
/// only edits or expands) once and returns the timed operation, which returns a checksum of what it did so the two sides can be
/// checked for equal work (<c>cverify</c>).
/// </summary>
internal static class CalendarWorkloads
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- ICS parse: bytes (string for Ical.Net, which takes text) to the tree, then a typed pass over the events ----

    public static Func<long> IcsParse(bool ours, string input)
    {
        byte[] data = CalendarInputs.Ics(input);
        if (ours)
        {
            return () => OursEvents(ContentDocument.Parse(data));
        }

        string text = Encoding.UTF8.GetString(data);
        return () => TheirEvents(IcalCalendar.Load(text)!);
    }

    // The typed pass: the fields a calendar view shows. Both sides read the same ones and return the same sum.
    private static long OursEvents(ContentDocument doc)
    {
        long sum = 0;
        foreach (IcsComponent cal in IcsDocument.Calendars(doc))
        {
            foreach (IcsComponent e in cal.Events)
            {
                sum += 1 + (e.Uid?.Length ?? 0) + (e.Summary?.Length ?? 0) + (e.Description?.Length ?? 0) + (e.Location?.Length ?? 0)
                    + (e.Start?.Value.Ticks ?? 0) % 1000 + (e.End?.Value.Ticks ?? 0) % 1000 + Math.Min(1, e.RecurrenceRules.Count) + e.Attendees.Count;
            }
        }

        return sum;
    }

    private static long TheirEvents(IcalCalendar cal)
    {
        long sum = 0;
        foreach (CalendarEvent e in cal.Events)
        {
            sum += 1 + (e.Uid?.Length ?? 0) + (e.Summary?.Length ?? 0) + (e.Description?.Length ?? 0) + (e.Location?.Length ?? 0)
                + (e.Start?.Value.Ticks ?? 0) % 1000 + (e.End?.Value.Ticks ?? 0) % 1000 + (e.RecurrenceRule is null ? 0 : 1) + e.Attendees.Count;
        }

        return sum;
    }

    /// <summary>How many events the input holds (the per-item divisor for allocations).</summary>
    public static long IcsItems(string input) => CountEvents(CalendarInputs.Ics(input));

    private static long CountEvents(byte[] data)
    {
        long n = 0;
        foreach (IcsComponent cal in IcsDocument.Calendars(ContentDocument.Parse(data)))
        {
            n += cal.Events.Count();
        }

        return n;
    }

    // ---- ICS edit: change every event's SUMMARY in a parsed calendar and write it all out ----

    public static Func<long> IcsEdit(bool ours, string input)
    {
        byte[] data = CalendarInputs.Ics(input);
        if (ours)
        {
            ContentDocument doc = ContentDocument.Parse(data);
            List<IcsComponent> events = [.. IcsDocument.Calendars(doc).SelectMany(c => c.Events)];
            return () =>
            {
                int i = 0;
                foreach (IcsComponent e in events)
                {
                    e.SetText("SUMMARY", "Edited " + (i++).ToString(Inv));
                }

                return doc.ToArray().Length;
            };
        }

        IcalCalendar cal = IcalCalendar.Load(Encoding.UTF8.GetString(data))!;
        List<CalendarEvent> theirs = [.. cal.Events];
        CalendarSerializer serializer = new();
        return () =>
        {
            int i = 0;
            foreach (CalendarEvent e in theirs)
            {
                e.Summary = "Edited " + (i++).ToString(Inv);
            }

            return Encoding.UTF8.GetByteCount(serializer.SerializeToString(cal)!);
        };
    }

    // ---- ICS build: create the events from fields and write them (no parse) ----

    public static Func<long> IcsBuild(bool ours, string input)
    {
        int n = CalendarInputs.BuildCount(input);
        if (ours)
        {
            return () =>
            {
                ContentDocument doc = new();
                Component cal = new("VCALENDAR");
                doc.Root.Add(cal);
                cal.Add(new ContentProperty("VERSION", "2.0"));
                cal.Add(new ContentProperty("PRODID", "-//OneMail bench//EN"));
                for (int i = 0; i < n; i++)
                {
                    var f = CalendarInputs.Event(i);
                    Component ev = new("VEVENT");
                    cal.Add(ev);
                    IcsComponent e = new(ev);
                    e.SetText("UID", f.Uid);
                    e.SetTime("DTSTAMP", new IcsTime(new DateTime(2024, 1, 1), IcsTimeKind.Utc));
                    e.SetTime("DTSTART", new IcsTime(f.Start, IcsTimeKind.Utc));
                    e.SetTime("DTEND", new IcsTime(f.End, IcsTimeKind.Utc));
                    e.SetText("SUMMARY", f.Summary);
                    e.SetText("DESCRIPTION", f.Description);
                    e.SetText("LOCATION", f.Location);
                    ContentProperty org = new("ORGANIZER", "mailto:alice@example.com");
                    org.Parameters.Set("CN", "Alice Example");
                    ev.Add(org);
                    foreach (string a in f.Attendees)
                    {
                        ContentProperty at = new("ATTENDEE", "mailto:" + a);
                        at.Parameters.Set("CN", a[..a.IndexOf('@', StringComparison.Ordinal)]);
                        at.Parameters.Set("PARTSTAT", "ACCEPTED");
                        at.Parameters.Set("RSVP", "TRUE");
                        ev.Add(at);
                    }

                    ev.Add(new ContentProperty("CATEGORIES", "Work,Planning"));
                    ev.Add(new ContentProperty("STATUS", "CONFIRMED"));
                    ev.Add(new ContentProperty("SEQUENCE", "0"));
                }

                return doc.ToArray().Length;
            };
        }

        CalendarSerializer serializer = new();
        return () =>
        {
            IcalCalendar cal = new();
            for (int i = 0; i < n; i++)
            {
                var f = CalendarInputs.Event(i);
                CalendarEvent e = new()
                {
                    Uid = f.Uid,
                    DtStamp = new CalDateTime(new DateTime(2024, 1, 1), "UTC"),
                    Start = new CalDateTime(f.Start, "UTC"),
                    End = new CalDateTime(f.End, "UTC"),
                    Summary = f.Summary,
                    Description = f.Description,
                    Location = f.Location,
                    Organizer = new Organizer("mailto:alice@example.com") { CommonName = "Alice Example" },
                    Status = "CONFIRMED",
                    Sequence = 0,
                };
                foreach (string a in f.Attendees)
                {
                    e.Attendees.Add(new Attendee("mailto:" + a) { CommonName = a[..a.IndexOf('@', StringComparison.Ordinal)], ParticipationStatus = "ACCEPTED", Rsvp = true });
                }

                e.Categories.Add("Work");
                e.Categories.Add("Planning");
                cal.Events.Add(e);
            }

            return Encoding.UTF8.GetByteCount(serializer.SerializeToString(cal)!);
        };
    }

    // ---- vCard ----

    public static Func<long> VcfParse(bool ours, string input)
    {
        byte[] data = CalendarInputs.Vcf(input);
        if (ours)
        {
            return () =>
            {
                long sum = 0;
                foreach (VCardView c in VCardDocument.Cards(ContentDocument.Parse(data)))
                {
                    sum += 1 + (c.FormattedName?.Length ?? 0) + (c.Name?.Family.Sum(s => s.Length) ?? 0) + c.Emails.Count + c.Phones.Count + c.Addresses.Count;
                }

                return sum;
            };
        }

        string text = Encoding.UTF8.GetString(data);
        return () =>
        {
            long sum = 0;
            foreach (VCard c in Vcf.Parse(text))
            {
                sum += 1 + (c.DisplayNames?.FirstOrDefault()?.Value?.Length ?? 0) + (c.NameViews?.FirstOrDefault()?.Value?.Surnames.Sum(s => s.Length) ?? 0)
                    + (c.EMails?.Count() ?? 0) + (c.Phones?.Count() ?? 0) + (c.Addresses?.Count() ?? 0);
            }

            return sum;
        };
    }

    public static long VcfItems(string input) => CountCards(CalendarInputs.Vcf(input));

    private static long CountCards(byte[] data) => VCardDocument.Cards(ContentDocument.Parse(data)).LongCount();

    public static Func<long> VcfEdit(bool ours, string input)
    {
        byte[] data = CalendarInputs.Vcf(input);
        if (ours)
        {
            ContentDocument doc = ContentDocument.Parse(data);
            List<ContentProperty> fns = [.. doc.Components.Select(c => c.FindProperty("FN")!)];
            return () =>
            {
                int i = 0;
                foreach (ContentProperty p in fns)
                {
                    p.Value = "Edited " + (i++).ToString(Inv);
                }

                return doc.ToArray().Length;
            };
        }

        List<VCard> cards = [.. Vcf.Parse(Encoding.UTF8.GetString(data))];
        return () =>
        {
            int i = 0;
            foreach (VCard c in cards)
            {
                c.DisplayNames = new FolkerKinzel.VCards.Models.Properties.TextProperty("Edited " + (i++).ToString(Inv));
            }

            return Encoding.UTF8.GetByteCount(Vcf.AsString(cards, VCdVersion.V3_0));
        };
    }

    public static Func<long> VcfBuild(bool ours, string input)
    {
        int n = CalendarInputs.BuildCount(input);
        if (ours)
        {
            return () =>
            {
                ContentDocument doc = new();
                for (int i = 0; i < n; i++)
                {
                    var f = CalendarInputs.Card(i);
                    Component card = new("VCARD");
                    doc.Root.Add(card);
                    card.Add(new ContentProperty("VERSION", "3.0"));
                    card.Add(new ContentProperty("N", $"{f.Family};{f.Given};;;"));
                    card.Add(new ContentProperty("FN", $"{f.Given} {f.Family}"));
                    card.Add(new ContentProperty("ORG", ContentValues.Escape(f.Org)));
                    card.Add(Typed("EMAIL", f.Email1, "INTERNET,PREF"));
                    card.Add(Typed("EMAIL", f.Email2, "INTERNET,WORK"));
                    card.Add(Typed("TEL", f.Phone1, "CELL"));
                    card.Add(Typed("TEL", f.Phone2, "WORK,VOICE"));
                    card.Add(Typed("ADR", $";;{f.Street};{f.City};;12345;USA", "HOME"));
                    card.Add(new ContentProperty("NOTE", ContentValues.Escape(f.Note)));
                }

                return doc.ToArray().Length;
            };
        }

        return () =>
        {
            List<VCard> cards = new(n);
            for (int i = 0; i < n; i++)
            {
                var f = CalendarInputs.Card(i);
                cards.Add(VCardBuilder.Create()
                    .NameViews.Add(NameBuilder.Create().AddSurname(f.Family).AddGiven(f.Given).Build())
                    .DisplayNames.Add($"{f.Given} {f.Family}")
                    .Organizations.Add(f.Org)
                    .EMails.Add(f.Email1)
                    .EMails.Add(f.Email2)
                    .Phones.Add(f.Phone1)
                    .Phones.Add(f.Phone2)
                    .Addresses.Add(AddressBuilder.Create().AddStreet(f.Street).AddLocality(f.City).AddPostalCode("12345").AddCountry("USA").Build())
                    .Notes.Add(f.Note)
                    .VCard);
            }

            return Encoding.UTF8.GetByteCount(Vcf.AsString(cards, VCdVersion.V3_0));
        };
    }

    private static ContentProperty Typed(string name, string value, string types)
    {
        ContentProperty p = new(name, value);
        p.Parameters.Set("TYPE", types.Split(','));
        return p;
    }

    // ---- recurrence: the occurrences of one rule in a bounded window, from an already-parsed calendar ----

    public static Func<long> Recurrence(bool ours, string input)
    {
        (string ics, DateTime start, DateTime end) = CalendarInputs.Recurrence(input);
        if (ours)
        {
            IcsComponent cal = IcsDocument.Calendars(ContentDocument.Parse(Encoding.UTF8.GetBytes(ics))).Single();
            OccurrenceWindow window = new(start, end);
            ExpansionOptions options = new() { MaxOccurrences = 1_000_000 };
            return () =>
            {
                long n = 0;
                foreach (RecursiveAI.OneMail.Calendar.Recurrence.Occurrence o in CalendarRecurrence.Expand(cal, window, options))
                {
                    n += 1 + (o.Start.Value.Ticks & 1);
                }

                return n;
            };
        }

        IcalCalendar their = IcalCalendar.Load(ics)!;
        CalDateTime from = new(start.Year, start.Month, start.Day, 0, 0, 0), to = new(end.Year, end.Month, end.Day, 0, 0, 0);
        return () =>
        {
            long n = 0;
            foreach (Ical.Net.DataTypes.Occurrence o in their.GetOccurrences(from))
            {
                if (o.Period.StartTime.Value >= to.Value)
                {
                    break;
                }

                n += 1 + (o.Period.StartTime.Value.Ticks & 1);
            }

            return n;
        };
    }

    /// <summary>The occurrence count of a recurrence case (the per-item divisor): ours.</summary>
    public static long RecurrenceItems(string input)
    {
        (string ics, DateTime start, DateTime end) = CalendarInputs.Recurrence(input);
        IcsComponent cal = IcsDocument.Calendars(ContentDocument.Parse(Encoding.UTF8.GetBytes(ics))).Single();
        return CalendarRecurrence.Expand(cal, new OccurrenceWindow(start, end), new ExpansionOptions { MaxOccurrences = 1_000_000 }).LongCount();
    }

    // ---- jCal / jCard: ours only ----

    public static Func<long> FormsWrite(string input)
    {
        ContentDocument doc = ContentDocument.Parse(input.StartsWith("vcf", StringComparison.Ordinal) ? CalendarInputs.Vcf(input) : CalendarInputs.Ics(input));
        return () => JsonForms.ToJson(doc).Bytes.Length;
    }

    public static Func<long> FormsRead(string input)
    {
        ContentDocument doc = ContentDocument.Parse(input.StartsWith("vcf", StringComparison.Ordinal) ? CalendarInputs.Vcf(input) : CalendarInputs.Ics(input));
        byte[] json = JsonForms.ToJson(doc).Bytes;
        return () => JsonForms.FromJson(json).Document.Root.Items.Count;
    }
}
