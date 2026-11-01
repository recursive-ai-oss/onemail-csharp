// mailauth as a black-box ARC oracle (run only).
//   node mailauth_arc.js verify <dir> <subdir>        arc/<subdir>/*.eml -> arc/mailauth-<subdir>.json (chain status)
//   node mailauth_arc.js seal <dir> <subdir> <outsub> seal arc/<subdir>/*.eml (rsa key, then ed key for a name containing 'ed1') -> arc/<outsub>/
'use strict';
const fs = require('fs');
const path = require('path');
const { authenticate } = require('mailauth');
const [mode, dir, sub, outsub] = process.argv.slice(2);
const arc = path.join(dir, 'arc');
const dns = JSON.parse(fs.readFileSync(path.join(arc, 'dns.json'), 'utf8'));
const resolver = async (name, rr) => {
  const v = dns[name.replace(/\.$/, '')];
  if (rr !== 'TXT' || !v) { const e = new Error('nf'); e.code = 'ENOTFOUND'; throw e; }
  return v.map(x => [x]);
};
const base = { resolver, ip: '192.0.2.1', helo: 'a.example', sender: 'joe@example.com', mta: 'mx.example.net', disableSpf: true, disableDmarc: true, disableBimi: true };
(async () => {
  fs.mkdirSync(path.join(arc, outsub || 'x'), { recursive: true });
  const out = {};
  for (const f of fs.readdirSync(path.join(arc, sub)).filter(x => x.endsWith('.eml')).sort()) {
    const msg = fs.readFileSync(path.join(arc, sub, f));
    const name = path.basename(f, '.eml');
    try {
      if (mode === 'verify') {
        const r = await authenticate(msg, base);
        out[name] = String(r.arc && r.arc.status && r.arc.status.result);
      } else {
        const ed = name.includes('ed') && !name.startsWith('rsa-ed');
        const seal = { signingDomain: ed ? 'example.org' : 'example.net', selector: ed ? 'arced' : 'arc', privateKey: fs.readFileSync(path.join(dir, 'keys', ed ? 'ed.pem' : 'rsa.pem')) };
        const r = await authenticate(msg, { ...base, seal });
        fs.writeFileSync(path.join(arc, outsub, f), Buffer.concat([Buffer.from(r.headers), msg]));
        out[name] = 'sealed cv=' + (r.arc && r.arc.status && r.arc.status.result);
      }
    } catch (e) { out[name] = 'error: ' + e; }
  }
  if (mode === 'verify') fs.writeFileSync(path.join(arc, 'mailauth-' + sub + '.json'), JSON.stringify(out, null, 1));
  for (const [k, v] of Object.entries(out)) console.log('  mailauth ' + mode + ' ' + sub + '/' + k + ': ' + v);
})();
