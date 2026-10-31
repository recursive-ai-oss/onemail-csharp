#!/usr/bin/env python3
"""Writes an Outlook for Mac archive (.olm) that follows the element names and layout of the sample archives, from a seed.

usage: make_olm.py OUT.olm [--seed N] [--messages N] [--attach-bytes N] [--body-bytes N] [--folders N] [--no-pim]

Every item kind is written (mail, appointments, contacts, groups, tasks, notes, categories) unless --no-pim. Mail goes into nested
folders, with attachments in each folder's com.microsoft.__Attachments directory, non-ASCII text, XML-special characters, empty
subjects, duplicate attachment names, HTML and plain bodies, and one message with a raw MIME source. Output is deterministic for a
given seed and arguments. Streams entries into the zip, so a multi-gigabyte archive needs no more memory than one message.
"""
import argparse
import random
import re
import zipfile
from xml.sax.saxutils import escape, quoteattr

X = ' xml:space="preserve"'
WORDS = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima", "mike", "november"]
NAMES = ["Zoë Müller", "山田 太郎", "Алексей Иванов", "Ana María", "O'Brien, Pat", "Jean-Luc Picard", "משה כהן", "李小龍"]
SUBJECTS = ["Quarterly report", "Re: lunch?", "日本語の件名", "Привет, мир", "Tom & Jerry <test>", "Emoji 😀 subject", "", "Fwd: \"quoted\" 'single'"]


def ordered(parts):
    """The sample archives write the child elements of every record in name order, and the schemas are sequences, so do the same."""
    return "".join(sorted(parts, key=lambda x: re.match(r"<(\w+)", x).group(1)))


def t(name, text):
    return f"<{name}{X}>{escape(text)}</{name}>"


def addr_list(name, people):
    inner = "".join(
        f'<emailAddress{X} OPFContactEmailAddressAddress={quoteattr(a)} OPFContactEmailAddressName={quoteattr(n)} OPFContactEmailAddressType="0"/>' for n, a in people
    )
    return f"<{name}{X}>{inner}</{name}>"


def date(rng, base=2018):
    return f"{base + rng.randint(0, 6)}-{rng.randint(1, 12):02d}-{rng.randint(1, 28):02d}T{rng.randint(0, 23):02d}:{rng.randint(0, 59):02d}:{rng.randint(0, 59):02d}"


def body_text(rng, n):
    out, size = [], 0
    while size < n:
        line = " ".join(rng.choice(WORDS) for _ in range(rng.randint(5, 14))) + "\n"
        out.append(line)
        size += len(line)
    return "".join(out)[:n]


def person(rng):
    n = rng.choice(NAMES)
    return n, f"user{rng.randint(1, 999)}@example.org"


def message_xml(rng, i, folder_path, attach, body_bytes, special):
    subj = SUBJECTS[i % len(SUBJECTS)] + (f" #{i}" if SUBJECTS[i % len(SUBJECTS)] else "")
    html = rng.random() < 0.3
    text = body_text(rng, body_bytes)
    if html:
        text = "<html><body><p>" + escape(text).replace("\n", "<br>\n") + "</p></body></html>"
    mid = f"gen-{i}-{rng.randint(0, 10**9)}@example.org"
    parts = []
    parts.append(t("OPFMessageCopyBody", text))
    parts.append(addr_list("OPFMessageCopyFromAddresses", [person(rng)]))
    if rng.random() < 0.7:
        parts.append(addr_list("OPFMessageCopyToAddresses", [person(rng) for _ in range(rng.randint(1, 3))]))
    if rng.random() < 0.3:
        parts.append(addr_list("OPFMessageCopyCCAddresses", [person(rng)]))
    if rng.random() < 0.1:
        parts.append(addr_list("OPFMessageCopyBCCAddresses", [person(rng)]))
    parts.append(t("OPFMessageCopyHTMLBody", text))
    parts.append(t("OPFMessageCopyMessageID", mid))
    parts.append(t("OPFMessageCopyModDate", date(rng)))
    parts.append(t("OPFMessageCopyReceivedTime", date(rng)))
    parts.append(t("OPFMessageCopySentTime", date(rng)))
    if subj:
        parts.append(t("OPFMessageCopySubject", subj))
        parts.append(t("OPFMessageCopyThreadTopic", subj))
    parts.append(t("OPFMessageGetHasHTML", "1E0" if html else "0E0"))
    parts.append(t("OPFMessageGetIsRead", "1E0" if rng.random() < 0.5 else "0E0"))
    parts.append(t("OPFMessageGetPriority", str(rng.choice([1, 3, 5]))))
    if special == "source":
        parts.append(t("OPFMessageCopySource", "From: a@example.org\nTo: b@example.org\nSubject: raw\n\nraw source body\n"))
    if attach:
        atts = "".join(
            f'<messageAttachment{X} OPFAttachmentContentExtension={quoteattr(name.rsplit(".", 1)[-1])} OPFAttachmentContentFileSize="{len(data)}E0" '
            f'OPFAttachmentContentType="application/octet-stream" OPFAttachmentName={quoteattr(name)} OPFAttachmentURL={quoteattr(url)}/>'
            for name, url, data in attach
        )
        parts.append(f"<OPFMessageCopyAttachmentList{X}>{atts}</OPFMessageCopyAttachmentList>")
    return f'<emails{X} elementCount="1"><email{X}>' + ordered(parts) + "</email></emails>"


def rec(tag, parts, attrs=""):
    return f"<{tag}{X}{attrs}>" + ordered(parts) + f"</{tag}>"


TZ_ICS = ("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//generated//EN\r\nBEGIN:VTIMEZONE\r\nTZID:Generated Standard Time\r\nBEGIN:STANDARD\r\n"
          "DTSTART:16010101T000000\r\nTZOFFSETFROM:-0500\r\nTZOFFSETTO:-0500\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\nEND:VCALENDAR\r\n")


def pim_files(rng):
    cats = [("Blue Category", "r:0.2 g:0.4 b:0.9"), ("Family", "r:0.6 g:0.2 b:0.7"), ("Größe & Ärger", "r:0.9 g:0.9 b:0.1")]
    cat_xml = f'<categories elementCount="{len(cats)}">' + "".join(
        f"<category{X} OPFCategoryCopyBackgroundColor={quoteattr(c)} OPFCategoryCopyName={quoteattr(n)}></category>" for n, c in cats) + "</categories>"
    appts = []
    days = ["allDays", "friday", "monday", "saturday", "sunday", "thursday", "tuesday", "wednesday", "weekdays", "weekenddays"]
    for i in range(4):
        parts = [
            t("OPFCalendarEventCopyEndTime", f"2019-0{i + 1}-1{i}T10:30:00"), t("OPFCalendarEventCopyLocation", NAMES[i] + " room"),
            t("OPFCalendarEventCopyModDate", "2019-01-01T00:00:00"), t("OPFCalendarEventCopyReminderDelta", "900"),
            t("OPFCalendarEventCopyStartTime", f"2019-0{i + 1}-1{i}T09:00:00"), t("OPFCalendarEventCopySummary", f"Meeting {i} — {SUBJECTS[i]}"),
            t("OPFCalendarEventCopyUUID", f"00000000-0000-4000-8000-00000000000{i}"), t("OPFCalendarEventGetHasReminder", "1"),
            t("OPFCalendarEventGetIsAllDayEvent", "1" if i == 3 else "0"), t("OPFCalendarEventGetIsPrivate", "0"),
            t("OPFCalendarEventIsRecurring", "1" if i == 1 else "0"), t("OPFCalendarEventCopyStartTimeZone", "(UTC+00:00) UTC"),
            t("OPFCalendarEventCopyEndTimeZone", "(UTC+00:00) UTC"), t("InvitationsHaveBeenSent", "0"), t("OPFCalendarEventAllowNewTimeProposal", "1"),
            t("OPFCalendarEventCopyFreeBusyStatus", "0"), t("OPFCalendarEventCopyTravelTime", "0"), t("OPFCalendarEventGetAcceptStatus", "0"),
            t("OPFCalendarEventGetEndTimeZoneSetToDefault", "0"), t("OPFCalendarEventGetStartTimeZoneSetToDefault", "0"), t("OPFCalendarEventSetIsOwner", "1"),
            t("OPFCalendarEventGetStartTimeZoneICSData", TZ_ICS),
        ]
        if i == 1:
            pattern = rec("OPFRecurrencePattern", [
                rec("OPFRecurrencePatternDaysOfWeek", [t(d, "1" if d in ("monday", "wednesday") else "0") for d in days]),
                t("OPFRecurrencePatternInterval", "2"), t("OPFRecurrencePatternType", "OPFRecurrencePatternWeekly")])
            parts.append(rec("OPFCalendarEventCopyRecurrence", [
                t("OPFRecurrenceCopyEndDate", "2019-12-31T07:00:00"), t("OPFRecurrenceCopyStartDate", "2019-02-11T07:00:00"),
                t("OPFRecurrenceGetOccurenceCount", "10E0"), t("OPFRecurrenceHasEndDate", "1"), t("OPFRecurrenceIsNoEnd", "0"),
                t("OPFRecurrenceIsNumbered", "1"), pattern]))
        appts.append(rec("appointment", parts))
    appt_xml = f'<appointments{X} elementCount="{len(appts)}">' + "".join(appts) + "</appointments>"
    contacts = []
    for i in range(3):
        n, a = person(rng)
        email = f'<contactEmailAddress{X} OPFContactEmailAddressAddress={quoteattr(a)} OPFContactEmailAddressIsDefault="1" OPFContactEmailAddressType="0"></contactEmailAddress>'
        contacts.append(rec("contact", [
            t("OPFContactCopyDisplayName", n), t("OPFContactCopyFirstName", n.split()[0]), t("OPFContactCopyLastName", n.split()[-1]),
            f"<OPFContactCopyEmailAddressList{X}>{email}</OPFContactCopyEmailAddressList>", t("OPFContactCopyBusinessPhone", f"+1 555 010{i}"),
            t("OPFContactCopyBusinessCompany", "Example & Sons"), t("OPFContactCopyModDate", "2019-03-0%dT10:00:00" % (i + 1)),
            t("OPFContactCopyNotesPlain", "Note with <angle> brackets & ampersand"), t("OPFContactCanDelete", "1"), t("OPFContactCanEdit", "1"),
            t("OPFContactType", "0")]))
    contact_xml = f'<contacts{X} elementCount="{len(contacts)}">' + "".join(contacts) + "</contacts>"
    group_xml = f'<groups{X} elementCount="1">' + rec("group", [
        t("OPFGroupCopyDisplayName", "Team 山"), addr_list("OPFGroupCopyMemberList", [person(rng), person(rng)]),
        t("OPFGroupCopyModDate", "2019-04-01T00:00:00")]) + "</groups>"
    task_xml = f'<tasks{X} elementCount="2">' + rec("task", [
        t("OPFTaskCopyDueDateTime", "2019-05-01T06:00:00"), t("OPFTaskCopyModDate", "2019-04-01T00:00:00"), t("OPFTaskCopyName", "Write tests"),
        t("OPFTaskCopyNotePlain", "plain note"), t("OPFTaskGetPriority", "3")]) + rec("task", [
        t("OPFTaskCopyCompletedDateTime", "2019-05-02T06:00:00"), t("OPFTaskCopyName", "Done thing"), t("OPFTaskCopyModDate", "2019-05-02T06:00:00"),
        t("OPFTaskGetPriority", "3")]) + "</tasks>"
    note_xml = f'<notes{X} elementCount="1">' + rec("note", [
        t("OPFNoteCopyCreationDate", "2019-06-01T00:00:00"), t("OPFNoteCopyModDate", "2019-06-02T00:00:00"),
        t("OPFNoteCopyText", "<html><body>sticky &amp; note</body></html>"), t("OPFNoteCopyTitle", "Sticky 📝")]) + "</notes>"
    return {
        "Categories.xml": cat_xml,
        "Local/Address Book/Contacts.xml": contact_xml,
        "Local/Address Book/Groups.xml": group_xml,
        "Local/Calendar/Calendar.xml": appt_xml,
        "Local/Tasks/Tasks.xml": task_xml,
        "Local/Notes/Notes.xml": note_xml,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--messages", type=int, default=40)
    ap.add_argument("--attach-bytes", type=int, default=2048)
    ap.add_argument("--body-bytes", type=int, default=600)
    ap.add_argument("--folders", type=int, default=4)
    ap.add_argument("--no-pim", action="store_true")
    ap.add_argument("--stored", action="store_true", help="store entries uncompressed, as the sample archives do")
    a = ap.parse_args()
    rng = random.Random(a.seed)
    method = zipfile.ZIP_STORED if a.stored else zipfile.ZIP_DEFLATED
    root = "Local/com.microsoft.__Messages/"
    folders = ["Inbox", "Sent Items", "Inbox/Projects", "Inbox/Projects/2019 ☀", "Archive/Old & New"][: max(1, a.folders)]
    with zipfile.ZipFile(a.out, "w", method, allowZip64=True) as z:
        def put(name, data):
            zi = zipfile.ZipInfo(name, (2019, 1, 1, 0, 0, 0))
            zi.compress_type = method
            zi.external_attr = 0o644 << 16
            z.writestr(zi, data)

        for f in folders:
            z.writestr(zipfile.ZipInfo(root + f + "/"), b"")
        if not a.no_pim:
            for name, xml in pim_files(rng).items():
                put(name, xml)
        counters = {f: 0 for f in folders}
        for i in range(a.messages):
            f = folders[i % len(folders)]
            n = counters[f]
            counters[f] += 1
            attach = []
            if a.attach_bytes and i % 3 == 0:
                for k in range(1 + i % 3):
                    name = ("same.bin" if k == 1 and i % 6 == 0 else f"file{i}_{k}.dat")
                    url = f"{root}{f}/com.microsoft.__Attachments/gen-{i}@example.org_{k:04d}"
                    attach.append((name, url, rng.randbytes(a.attach_bytes)))
            special = "source" if i == 5 else None
            xml = message_xml(rng, i, f, attach, a.body_bytes, special)
            for _, url, data in attach:
                put(url, data)
            put(f"{root}{f}/message_{n:05d}.xml", xml)


if __name__ == "__main__":
    main()
