namespace RecursiveAI.OneMail.Calendar.Forms;

// How a property's value is split into parts.
internal enum Shape
{
    // One value.
    Single,

    // A comma-separated list; each item is a value of the property (CATEGORIES, EXDATE, FREEBUSY).
    List,

    // Semicolon-separated components, each possibly a comma list (N, ADR). RFC 7095 §3.3.1.3.
    Struct,

    // Semicolon-separated components with no comma lists (ORG, GENDER, CLIENTPIDMAP).
    SemiList,

    // Two floats separated by a semicolon (iCalendar GEO, vCard 2.1/3.0 GEO). RFC 7265 §3.4.1.1.
    Pair,

    // Two or three text components separated by semicolons (REQUEST-STATUS). RFC 7265 §3.4.1.2.
    RequestStatus,
}

internal readonly record struct PropInfo(string Type, Shape Shape, string[]? Parts = null);

// The property and parameter tables of RFC 5545, RFC 7986 and RFC 6350/2426 that decide default value types and shapes.
// They are static data; nothing is discovered by reflection.
internal static class FormSchema
{
    private static readonly Dictionary<string, PropInfo> Ics = BuildIcs();
    private static readonly Dictionary<string, PropInfo> Card = BuildCard();

    private static readonly Dictionary<string, string[]> XmlParts = new(StringComparer.Ordinal)
    {
        // RFC 6351 Appendix A.
        ["n"] = ["surname", "given", "additional", "prefix", "suffix"],
        ["adr"] = ["pobox", "ext", "street", "locality", "region", "code", "country"],
        ["gender"] = ["sex", "identity"],
        ["clientpidmap"] = ["sourceid", "uri"],
    };

    private static Dictionary<string, PropInfo> BuildIcs()
    {
        Dictionary<string, PropInfo> d = new(StringComparer.Ordinal);
        void Add(string type, Shape shape, params string[] names)
        {
            foreach (string n in names)
            {
                d[n] = new PropInfo(type, shape);
            }
        }

        // RFC 5545 §3.7, §3.8 and RFC 7986 §5.
        Add("text", Shape.Single, "calscale", "method", "prodid", "version", "class", "comment", "description", "location", "status", "summary", "transp",
            "tzid", "tzname", "contact", "related-to", "uid", "action", "name", "color", "proximity");
        Add("text", Shape.List, "categories", "resources");
        Add("uri", Shape.Single, "url", "tzurl", "attach", "source", "image", "conference");
        Add("integer", Shape.Single, "percent-complete", "priority", "repeat", "sequence");
        Add("date-time", Shape.Single, "completed", "created", "dtstamp", "last-modified", "dtend", "due", "dtstart", "recurrence-id", "acknowledged");
        Add("date-time", Shape.List, "exdate", "rdate");
        Add("duration", Shape.Single, "duration", "trigger", "refresh-interval");
        Add("period", Shape.List, "freebusy");
        Add("utc-offset", Shape.Single, "tzoffsetfrom", "tzoffsetto");
        Add("cal-address", Shape.Single, "attendee", "organizer");
        Add("recur", Shape.Single, "rrule", "exrule");
        d["geo"] = new PropInfo("float", Shape.Pair);
        d["request-status"] = new PropInfo("text", Shape.RequestStatus);
        return d;
    }

    private static Dictionary<string, PropInfo> BuildCard()
    {
        Dictionary<string, PropInfo> d = new(StringComparer.Ordinal);
        void Add(string type, Shape shape, params string[] names)
        {
            foreach (string n in names)
            {
                d[n] = new PropInfo(type, shape);
            }
        }

        // RFC 6350 §6, RFC 2426 §3, RFC 6474/6715 additions.
        Add("text", Shape.Single, "version", "fn", "title", "role", "note", "prodid", "email", "tel", "kind", "xml", "label", "mailer", "class", "sort-string", "name",
            "profile", "birthplace", "deathplace", "expertise", "hobby", "interest", "org-directory", "contact-uri");
        Add("text", Shape.List, "nickname", "categories");
        Add("text", Shape.SemiList, "org", "gender");
        Add("uri", Shape.Single, "url", "source", "photo", "logo", "sound", "key", "member", "related", "fburl", "caladruri", "caluri", "impp", "geo", "uid");
        Add("language-tag", Shape.Single, "lang");
        Add("date-and-or-time", Shape.Single, "bday", "anniversary", "deathdate");
        Add("timestamp", Shape.Single, "rev");
        Add("text", Shape.Single, "tz");
        d["n"] = new PropInfo("text", Shape.Struct);
        d["adr"] = new PropInfo("text", Shape.Struct);
        d["clientpidmap"] = new PropInfo("text", Shape.SemiList);
        return d;
    }

    internal static PropInfo Lookup(Dialect d, string lowerName)
    {
        if (d == Dialect.ICal)
        {
            return Ics.TryGetValue(lowerName, out PropInfo i) ? i : new PropInfo("unknown", Shape.Single);
        }

        if (Card.TryGetValue(lowerName, out PropInfo c))
        {
            // vCard 2.1/3.0 differences from 4.0 (RFC 2426 §3.4.2, §3.5.1, §3.6.7): GEO is two floats, UID is text.
            if (d != Dialect.VCard40)
            {
                if (lowerName == "geo")
                {
                    return new PropInfo("float", Shape.Pair);
                }

                if (lowerName == "uid")
                {
                    return new PropInfo("text", Shape.Single);
                }
            }

            return c;
        }

        return new PropInfo("unknown", Shape.Single);
    }

    // Names of the XML elements of a structured value, or null when the property has none (RFC 6351 Appendix A, RFC 6321 §3.4.1).
    internal static string[]? XmlPartNames(Dialect d, string lowerName)
    {
        if (d == Dialect.ICal)
        {
            return lowerName switch
            {
                "geo" => ["latitude", "longitude"],
                "request-status" => ["code", "description", "data"],
                _ => null,
            };
        }

        return XmlParts.TryGetValue(lowerName, out string[]? p) ? p : null;
    }

    // Comma-lists inside structured components exist for N and ADR from vCard 3.0 on.
    internal static bool CommaListsInStruct(Dialect d) => d is Dialect.VCard30 or Dialect.VCard40;

    // The number of components RFC 6350 §6.2.2 and §6.3.1 give the structured properties.
    internal static int StructCount(string lowerName) => lowerName == "n" ? 5 : 7;

    // vCard parameters whose comma-separated values are a list (RFC 6350 §5.6, §5.5, §5.9). A quoted value such as
    // TYPE="work,voice" is a list too (RFC 7095 Appendix B.1).
    internal static bool IsListParam(Dialect d, string lowerName) => d.IsVCard() && lowerName is "type" or "pid" or "sort-as";

    // The value type element an xCal/xCard parameter uses (RFC 6321 Appendix A, RFC 6351 Appendix A); text when not listed.
    internal static string ParamXmlType(Dialect d, string lowerName)
    {
        if (d == Dialect.ICal)
        {
            return lowerName switch
            {
                "altrep" or "dir" => "uri",
                "delegated-from" or "delegated-to" or "member" or "sent-by" => "cal-address",
                "rsvp" => "boolean",
                _ when lowerName.StartsWith("x-", StringComparison.Ordinal) => "unknown",
                _ => "text",
            };
        }

        return lowerName switch
        {
            "language" => "language-tag",
            "pref" => "integer",
            "geo" => "uri",
            _ when lowerName.StartsWith("x-", StringComparison.Ordinal) => "unknown",
            _ => "text",
        };
    }

    internal static bool IsKnownType(string t) => t is "text" or "uri" or "cal-address" or "binary" or "boolean" or "integer" or "float" or "date" or "time"
        or "date-time" or "date-and-or-time" or "timestamp" or "duration" or "period" or "recur" or "utc-offset" or "language-tag" or "unknown";

    internal static bool IsInlineBinaryProperty(string lowerName) => lowerName is "photo" or "logo" or "sound" or "key";
}
