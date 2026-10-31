using System.Globalization;
using System.Net;
using System.Text;

namespace RecursiveAI.OneMail.Display;

public sealed partial class CalendarCard
{
    // Fixed markup: only div, p, ul, li, span, strong and (optionally) a, with class and style attributes whose values are constants.
    // Event text goes in through HtmlEncode only; the one attribute that carries event data is href, and only for a normalised http(s)/mailto URL.
    private const string BoxStyle = "border:1px solid #c8c8c8;border-radius:4px;padding:8px 12px;margin:0 0 12px;font-family:sans-serif;font-size:14px;max-width:640px";
    private const string TitleStyle = "margin:0 0 4px;font-size:16px";
    private const string ActionStyle = "margin:0 0 2px;color:#555";
    private const string BadgeStyle = "margin-left:6px;padding:0 4px;border:1px solid #888;border-radius:3px;font-size:12px";
    private const string ListStyle = "list-style:none;margin:6px 0 0;padding:0";
    private const string ItemStyle = "margin:2px 0";
    private const string DescriptionStyle = "margin:2px 0 0;white-space:pre-wrap";

    /// <summary>The card as an HTML fragment (a single <c>div</c>), built from escaped text with fixed markup and fixed inline CSS.</summary>
    /// <param name="linkUrls">True to make the conference and event URLs <c>a</c> elements (<c>rel="noopener noreferrer"</c>); false (the default) shows them as text.</param>
    /// <returns>The fragment.</returns>
    public string ToHtml(bool linkUrls = false)
    {
        StringBuilder sb = new();
        sb.Append("<div class=\"onemail-calendar-card\" style=\"").Append(BoxStyle).Append("\">");
        sb.Append("<p class=\"onemail-card-action\" style=\"").Append(ActionStyle).Append("\">").Append(E(Action));
        if (Status is not null)
        {
            sb.Append("<span class=\"onemail-card-status\" style=\"").Append(BadgeStyle).Append("\">").Append(E(Status)).Append("</span>");
        }

        sb.Append("</p>");
        sb.Append("<p class=\"onemail-card-title\" style=\"").Append(TitleStyle).Append("\"><strong>").Append(E(Summary)).Append("</strong></p>");
        sb.Append("<ul style=\"").Append(ListStyle).Append("\">");
        Row(sb, CalendarCardStrings.When, When.Length > 0 ? When : null);
        if (OriginalTime is not null)
        {
            Row(sb, CalendarCardStrings.OriginalTime, OriginalTime);
        }

        Row(sb, CalendarCardStrings.Repeats, Recurrence);
        if (Occurrences.Count > 0)
        {
            sb.Append("<li style=\"").Append(ItemStyle).Append("\"><strong>").Append(E(CalendarCardStrings.Next)).Append(":</strong><ul style=\"").Append(ListStyle).Append("\">");
            foreach (string occ in Occurrences)
            {
                sb.Append("<li style=\"").Append(ItemStyle).Append("\">").Append(E(occ)).Append("</li>");
            }

            sb.Append("</ul></li>");
        }

        Row(sb, CalendarCardStrings.Where, Location);
        foreach (CalendarCardLink l in Links)
        {
            sb.Append("<li style=\"").Append(ItemStyle).Append("\"><strong>").Append(E(l.Label)).Append(":</strong> ");
            if (linkUrls)
            {
                sb.Append("<a href=\"").Append(E(l.Url)).Append("\" rel=\"noopener noreferrer\">").Append(E(l.Url)).Append("</a>");
            }
            else
            {
                sb.Append(E(l.Url));
            }

            sb.Append("</li>");
        }

        Row(sb, CalendarCardStrings.Organizer, Organizer);
        if (Attendees.Count > 0)
        {
            sb.Append("<li style=\"").Append(ItemStyle).Append("\"><strong>").Append(E(CalendarCardStrings.Attendees)).Append(":</strong><ul style=\"").Append(ListStyle).Append("\">");
            foreach (CalendarCardAttendee a in Attendees)
            {
                sb.Append("<li style=\"").Append(ItemStyle).Append("\">").Append(E(AttendeeText(a))).Append("</li>");
            }

            if (AttendeeCount > Attendees.Count)
            {
                sb.Append("<li style=\"").Append(ItemStyle).Append("\">").Append(E(CalendarCardStrings.More(AttendeeCount - Attendees.Count))).Append("</li>");
            }

            sb.Append("</ul></li>");
        }

        if (Attachments.Count > 0)
        {
            Row(sb, CalendarCardStrings.Attachments, string.Join(", ", Attachments));
        }

        if (Description is not null)
        {
            sb.Append("<li style=\"").Append(ItemStyle).Append("\"><strong>").Append(E(CalendarCardStrings.Description)).Append(":</strong><p style=\"").Append(DescriptionStyle).Append("\">")
                .Append(E(Description)).Append("</p></li>");
        }

        sb.Append("</ul></div>");
        return sb.ToString();
    }

    /// <summary>The card as plain text: a title line, then one <c>Label: value</c> line per field. Lines end with <c>\n</c>.</summary>
    /// <returns>The text.</returns>
    public string ToText()
    {
        StringBuilder sb = new();
        sb.Append(Action);
        if (Status is not null)
        {
            sb.Append(" [").Append(Status).Append(']');
        }

        sb.Append('\n').Append(Summary).Append('\n');
        Line(sb, CalendarCardStrings.When, When.Length > 0 ? When : null);
        Line(sb, CalendarCardStrings.OriginalTime, OriginalTime);
        Line(sb, CalendarCardStrings.Repeats, Recurrence);
        foreach (string occ in Occurrences)
        {
            sb.Append("  ").Append(occ).Append('\n');
        }

        Line(sb, CalendarCardStrings.Where, Location);
        foreach (CalendarCardLink l in Links)
        {
            Line(sb, l.Label, l.Url);
        }

        Line(sb, CalendarCardStrings.Organizer, Organizer);
        if (Attendees.Count > 0)
        {
            sb.Append(CalendarCardStrings.Attendees).Append(":\n");
            foreach (CalendarCardAttendee a in Attendees)
            {
                sb.Append("  ").Append(AttendeeText(a)).Append('\n');
            }

            if (AttendeeCount > Attendees.Count)
            {
                sb.Append("  ").Append(CalendarCardStrings.More(AttendeeCount - Attendees.Count)).Append('\n');
            }
        }

        if (Attachments.Count > 0)
        {
            Line(sb, CalendarCardStrings.Attachments, string.Join(", ", Attachments));
        }

        if (Description is not null)
        {
            sb.Append(CalendarCardStrings.Description).Append(":\n");
            foreach (string line in Description.Split('\n'))
            {
                sb.Append("  ").Append(line).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static void Row(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            sb.Append("<li style=\"").Append(ItemStyle).Append("\"><strong>").Append(E(label)).Append(":</strong> ").Append(E(value)).Append("</li>");
        }
    }

    private static void Line(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            sb.Append(label).Append(": ").Append(value).Append('\n');
        }
    }

    private static string AttendeeText(CalendarCardAttendee a)
    {
        string who = a.Name is { Length: > 0 } ? (a.Address.Length > 0 ? a.Name + " (" + a.Address + ")" : a.Name) : a.Address;
        string[] extra = [.. new[] { a.Kind, a.Role, a.Response }.Where(x => x is not null).Select(x => x!)];
        return extra.Length == 0 ? who : who + " – " + string.Join(", ", extra);
    }
}
