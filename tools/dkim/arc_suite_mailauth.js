// Runs the ARC test suite's scenarios (suite.json) through mailauth; writes name -> chain status.
//   node arc_suite_mailauth.js <suite.json> <out.json>
'use strict';
const fs = require('fs');
const { authenticate } = require('mailauth');
const suite = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const hv = (n, v) => n + ': ' + v.replace(/\n+$/, '').split('\n').join('\r\n ') + '\r\n';
(async () => {
  const out = {};
  for (const t of suite) {
    let msg = t.message;
    if (t.kind === 'sign') {
      if (!(t.AS || '').trim()) continue;
      msg = hv('ARC-Seal', t.AS) + hv('ARC-Message-Signature', t.AMS) + hv('ARC-Authentication-Results', t.AAR) + msg;
    }
    const resolver = async (name, rr) => {
      const v = t.txt[name.replace(/\.$/, '')];
      if (rr !== 'TXT' || !v) { const e = new Error('nf'); e.code = 'ENOTFOUND'; throw e; }
      return [[v]];
    };
    try {
      const r = await authenticate(Buffer.from(msg.replace(/\r?\n/g, '\r\n')), { resolver, ip: '192.0.2.1', helo: 'a.example', sender: 'a@example.org', mta: 'mx.test', disableSpf: true, disableDmarc: true, disableBimi: true });
      out[t.kind + '/' + t.name] = String(r.arc && r.arc.status && r.arc.status.result).toLowerCase();
    } catch (e) { out[t.kind + '/' + t.name] = 'error: ' + e; }
  }
  fs.writeFileSync(process.argv[3], JSON.stringify(out, null, 1));
  console.log(Object.keys(out).length, 'scenarios');
})();
