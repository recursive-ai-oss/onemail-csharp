#!/usr/bin/env python3
"""Cut the worked examples out of RFC 5545 section 3.8.5.3 into testdata/calendar/recurrence/rfc5545-3.8.5.3.jsonl.

  make_rfc_recurrence.py <rfc5545.txt> [out.jsonl]

One JSON object per example: the title, the DTSTART line, the alternative rule property lines (the RFC's "or"), and the
occurrences the RFC lists as local wall-clock times and UTC instants (the listing names EDT/EST). Listings that end in "..."
are marked "prefix": only the part before the ellipsis is the RFC's own list. The RFC text is IETF Trust material; see THIRD-PARTY-NOTICES.md.
"""
import hashlib, json, os, re, sys

src = sys.argv[1]
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(os.path.abspath(__file__)), '../../testdata/calendar/recurrence/rfc5545-3.8.5.3.jsonl')
data = open(src, 'rb').read()
want = [l.split()[1] for l in open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'rfc.sha256')) if l.startswith('5545 ')][0]
assert hashlib.sha256(data).hexdigest() == want, 'rfc5545.txt does not match the pinned hash'
text = data.decode('utf-8').replace('\f', '')
start = text.index('\n3.8.5.3.  Recurrence Rule\n')
end = text.index('\n3.8.6.  Alarm Component Properties')
sec = text[start:end]
lines = []
for l in sec.split('\n'):
    if l.startswith('Desruisseaux') or l.startswith('RFC 5545  ') or not l.strip():
        continue
    lines.append(l.rstrip())
lines = lines[lines.index(next(l for l in lines if l.strip().startswith('Example:'))) + 1:]

MONTHS = {m: i + 1 for i, m in enumerate('January February March April May June July August September October November December'.split())}
OFFSET = {'EDT': -4, 'EST': -5}


def indent(l):
    return len(l) - len(l.lstrip())


def parse_listing(text, dt_time):
    """-> (list of (local iso, offset hours), prefix flag)"""
    res, prefix = [], False
    for m in re.finditer(r'\(([^)]*)\)\s*([^()]*)', text):
        head, body = m.group(1).strip(), m.group(2)
        body = body.replace('\n', ' ')
        mt = re.match(r'(\d{4}) (?:(\d+):(\d+) (AM|PM) )?(EDT|EST)$', head)
        if mt:
            year = int(mt.group(1)); off = OFFSET[mt.group(5)]
            if mt.group(2):
                hh = int(mt.group(2)) % 12 + (12 if mt.group(4) == 'PM' else 0); mm = int(mt.group(3))
            else:
                hh, mm = dt_time
            month = None
            for seg in body.split(';'):
                seg = seg.strip()
                if not seg:
                    continue
                mm_ = re.match(r'([A-Z][a-z]+)\s+(.*)$', seg)
                if mm_:
                    month = MONTHS[mm_.group(1)]; seg = mm_.group(2)
                for tok in seg.split(','):
                    tok = tok.strip()
                    if not tok:
                        continue
                    if '...' in tok:
                        head_tok = tok.split('...')[0].strip()
                        if head_tok:
                            res.append(('%04d-%02d-%02dT%02d:%02d:00' % (year, month, int(head_tok), hh, mm), off))
                        return res, True
                    r = re.match(r'(\d+)(?:-(\d+))?$', tok)
                    a, b = int(r.group(1)), int(r.group(2) or r.group(1))
                    for d in range(a, b + 1):
                        res.append(('%04d-%02d-%02dT%02d:%02d:00' % (year, month, d, hh, mm), off))
            continue
        mt = re.match(r'([A-Z][a-z]+) (\d+), (\d{4}) (EDT|EST)$', head)
        if mt:
            y, mo, d, off = int(mt.group(3)), MONTHS[mt.group(1)], int(mt.group(2)), OFFSET[mt.group(4)]
            for tok in re.split(r'[,;]', body):
                tok = tok.strip()
                if not tok:
                    continue
                if '...' in tok:
                    ht = tok.split('...')[0].strip()
                    if ht:
                        h, mi = ht.split(':')
                        res.append(('%04d-%02d-%02dT%02d:%02d:00' % (y, mo, d, int(h), int(mi)), off))
                    return res, True
                h, mi = tok.split(':')
                res.append(('%04d-%02d-%02dT%02d:%02d:00' % (y, mo, d, int(h), int(mi)), off))
            continue
        mt = re.match(r'(\d{4}) (EDT|EST)$', head)
        if mt:
            y, off = int(mt.group(1)), OFFSET[mt.group(2)]
            month = None
            for seg in body.split(';'):
                seg = seg.strip()
                mm_ = re.match(r'([A-Z][a-z]+)\s+(.*)$', seg)
                if mm_:
                    month = MONTHS[mm_.group(1)]; seg = mm_.group(2)
                for tok in seg.split(','):
                    res.append(('%04d-%02d-%02dT%02d:%02d:00' % (y, month, int(tok.strip()), dt_time[0], dt_time[1]), off))
            continue
        raise SystemExit('cannot parse listing header: ' + head)
    if re.search(r'\.\.\.\s*$', text.strip()):
        prefix = True
    return res, prefix


examples = []
i, title = 0, []
while i < len(lines):
    l = lines[i]
    s = l.strip()
    if s.startswith('DTSTART'):
        dtstart = s
        i += 1
        alts, cur = [], []
        while not lines[i].strip().startswith('==>'):
            t = lines[i].strip()
            if t == 'or':
                alts.append(cur); cur = []
            elif t.startswith(('RRULE', 'EXDATE', 'RDATE', 'EXRULE')):
                cur.append(t)
            else:  # continuation of the previous property line
                cur[-1] += t
            i += 1
        alts.append(cur)
        listing = []
        while i < len(lines) and (lines[i].strip().startswith('==>') or indent(lines[i]) >= 11):
            listing.append(lines[i].strip()[3:] if lines[i].strip().startswith('==>') else lines[i].strip())
            i += 1
        ttl = ' '.join(x.strip() for x in title).strip()
        tm = re.search(r'T(\d{2})(\d{2})\d{2}$', dtstart)
        res, prefix = parse_listing('\n'.join(listing), (int(tm.group(1)), int(tm.group(2))))
        examples.append({'title': ttl, 'dtstart': dtstart, 'alternatives': alts, 'expected': [{'local': a, 'offset': o} for a, o in res], 'prefix': prefix})
        title = []
        continue
    if s.startswith('Note:') or indent(l) >= 9 and not title and not s[0].isupper():
        i += 1
        continue
    if indent(l) == 6:
        if title and title[-1].rstrip().endswith((':', '.')) and not s[0].islower() and not re.match(r'^\d', s):
            title = []
        title.append(l)
    i += 1

with open(out, 'w', encoding='utf-8', newline='\n') as f:
    for e in examples:
        f.write(json.dumps(e, ensure_ascii=False) + '\n')
print(len(examples), 'examples,', sum(len(e['expected']) for e in examples), 'listed occurrences,', sum(1 for e in examples if e['prefix']), 'prefix-only')
