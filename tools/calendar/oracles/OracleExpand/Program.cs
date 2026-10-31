using System.Globalization;
using System.Text;
using System.Text.Json;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;

// OracleExpand <cases.jsonl> <out.jsonl>: for every case, the start of each occurrence Ical.Net's public API returns in the case's
// window, as Z (UTC), F (floating) or D (date) strings, or an error. The released package is run; nothing else of it is used.
// A rule the library cannot finish (it just keeps searching) must not stall the run: after 20 s the case is recorded as a timeout
// and the process exits with 75, and the caller restarts it at the next case (run_expand_diff.sh does).
int first = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 0;
using StreamWriter w = new(args[1], first > 0, new UTF8Encoding(false));
int index = -1;
foreach (string line in File.ReadLines(args[0]))
{
    if (++index < first)
    {
        continue;
    }

    JsonElement c = JsonSerializer.Deserialize<JsonElement>(line);
    Dictionary<string, object> rec = new() { ["case"] = c.GetProperty("case").GetString()! };
    Thread worker = new(() => Run(c, rec), 64 * 1024 * 1024) { IsBackground = true };
    worker.Start();
    if (!worker.Join(TimeSpan.FromSeconds(20)))
    {
        rec.Clear();
        rec["case"] = c.GetProperty("case").GetString()!;
        rec["error"] = "Timeout: no answer in 20 seconds";
        w.WriteLine(JsonSerializer.Serialize(rec));
        w.Flush();
        Environment.Exit(75);
    }

    w.WriteLine(JsonSerializer.Serialize(rec));
}

return 0;

static void Run(JsonElement c, Dictionary<string, object> rec)
{
    try
    {
        Ical.Net.Calendar cal = Ical.Net.Calendar.Load(c.GetProperty("ics").GetString()!)!;
        string uid = c.GetProperty("uid").GetString()!;
        DateTime ws = DateTime.ParseExact(c.GetProperty("start").GetString()!, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        DateTime we = DateTime.ParseExact(c.GetProperty("end").GetString()!, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        int cap = c.GetProperty("cap").GetInt32();
        List<string> starts = [];
        string kind = "F";
        // The calendar-level call applies overrides (RECURRENCE-ID) and EXDATEs the way the library does.
        foreach (Occurrence o in cal.GetOccurrences(new CalDateTime(ws.Year, ws.Month, ws.Day, 0, 0, 0)))
        {
            if (o.Period.StartTime.Value >= we)
            {
                break;
            }

            if (o.Source is not CalendarEvent ev || ev.Uid != uid)
            {
                continue;
            }

            CalDateTime s = o.Period.StartTime;
            if (!s.HasTime)
            {
                kind = "D";
                starts.Add(s.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            }
            else if (s.IsUtc || s.TzId is not null)
            {
                kind = "Z";
                starts.Add(s.AsUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            }
            else
            {
                starts.Add(s.Value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
            }

            if (starts.Count >= cap * 4)
            {
                break;
            }
        }

        starts.Sort(StringComparer.Ordinal);
        rec["kind"] = kind;
        rec["starts"] = starts.Take(cap).ToList();
    }
    catch (Exception e)
    {
        rec["error"] = e.GetType().Name + ": " + e.Message.Split('\n')[0];
    }
}
