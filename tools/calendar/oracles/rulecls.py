"""Features of a case's rules, shared by the expansion comparison (explained_expand) and the triage helpers."""
import re


def rules(ics):
    text = re.sub(r'\r?\n[ \t]', '', ics)
    return re.findall(r'^(RRULE|EXRULE)[^:\n]*:(.*?)\r?$', text, re.M)


def parse(rule):
    d = {}
    for p in rule.split(';'):
        if '=' in p:
            k, v = p.split('=', 1)
            d[k.upper()] = v
    return d


def not_applicable(rule):
    """RFC 5545 section 3.3.10: the BYxxx parts the frequency table marks N/A (the result is implementation-defined)."""
    d = parse(rule)
    f = d.get('FREQ', '')
    out = []
    if 'BYWEEKNO' in d and f != 'YEARLY':
        out.append('BYWEEKNO')
    if 'BYYEARDAY' in d and f in ('DAILY', 'WEEKLY', 'MONTHLY'):
        out.append('BYYEARDAY')
    if 'BYMONTHDAY' in d and f == 'WEEKLY':
        out.append('BYMONTHDAY')
    if 'BYSETPOS' in d and not any(k in d for k in ('BYMONTH', 'BYWEEKNO', 'BYYEARDAY', 'BYMONTHDAY', 'BYDAY', 'BYHOUR', 'BYMINUTE', 'BYSECOND')):
        out.append('BYSETPOS-alone')
    if 'BYDAY' in d and re.search(r'(^|,)[+-]?\d', d['BYDAY']) and (f not in ('MONTHLY', 'YEARLY') or 'BYWEEKNO' in d):
        out.append('BYDAY-ordinal')
    return out


def weekno_without_day(rule):
    d = parse(rule)
    return 'BYWEEKNO' in d and not any(k in d for k in ('BYDAY', 'BYMONTHDAY', 'BYYEARDAY'))
