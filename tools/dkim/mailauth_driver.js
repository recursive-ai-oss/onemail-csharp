// mailauth as a black-box DKIM oracle (run only; its source is not read).
//
//   node mailauth_driver.js verify <dir>   verify <dir>/ours-signed/*.eml -> <dir>/mailauth-verifies-ours.json
//   node mailauth_driver.js sign <dir>     sign <dir>/unsigned/*.eml per cases.json -> <dir>/mailauth-signed/*.eml
// Run with NODE_PATH pointing at the npm directory holding mailauth.
'use strict';
const fs = require('fs');
const path = require('path');
const { authenticate } = require('mailauth');
const { dkimSign } = require('mailauth/lib/dkim/sign');

const [mode, dir] = process.argv.slice(2);
const dns = JSON.parse(fs.readFileSync(path.join(dir, 'dns.json'), 'utf8'));

async function resolver(name, rr) {
  const recs = dns[name.replace(/\.$/, '')];
  if (rr !== 'TXT' || !recs) {
    const e = new Error('not found');
    e.code = 'ENOTFOUND';
    throw e;
  }
  return recs.map(r => [r]);
}

(async () => {
  if (mode === 'verify') {
    const out = {};
    for (const f of fs.readdirSync(path.join(dir, 'ours-signed')).filter(x => x.endsWith('.eml')).sort()) {
      const msg = fs.readFileSync(path.join(dir, 'ours-signed', f));
      try {
        const r = await authenticate(msg, { resolver, ip: '192.0.2.1', helo: 'a.example', sender: 'joe@example.com', mta: 'mx.test', disableSpf: true, disableDmarc: true, disableArc: true, disableBimi: true });
        out[path.basename(f, '.eml')] = (r.dkim.results || []).map(x => ({ status: x.status.result, comment: x.status.comment, d: x.signingDomain, s: x.selector }));
      } catch (e) {
        out[path.basename(f, '.eml')] = [{ status: 'error', detail: String(e) }];
      }
    }
    fs.writeFileSync(path.join(dir, 'mailauth-verifies-ours.json'), JSON.stringify(out, null, 1));
    console.log('mailauth verified', Object.keys(out).length);
  } else {
    const cases = JSON.parse(fs.readFileSync(path.join(dir, 'cases.json'), 'utf8'));
    fs.mkdirSync(path.join(dir, 'mailauth-signed'), { recursive: true });
    let errors = 0;
    for (const c of cases) {
      const msg = fs.readFileSync(path.join(dir, 'unsigned', c.id + '.eml'));
      const priv = fs.readFileSync(path.join(dir, 'keys', c.alg + '.pem'));
      // The header list names every header we want covered; a repeated name over-signs.
      const hl = ['from', 'to', 'cc', 'subject', 'date', 'message-id', 'mime-version', 'content-type', 'content-transfer-encoding', 'received', 'comments'];
      const headerList = c.oversign ? [...hl, ...hl] : hl;
      const sd = { signingDomain: 'example.com', selector: c.selector, privateKey: priv };
      // l= covers only the first 5 canonical bytes, so the rest of the body is unprotected; an empty body has nothing to cover.
      if (c.length && c.body !== 'empty' && c.body !== 'blank') sd.maxBodyLength = 5;
      try {
        const r = await dkimSign(msg, { canonicalization: c.canon, headerList, signatureData: [sd] });
        if (r.errors.length || !r.signatures) throw new Error(JSON.stringify(r.errors.map(e => String(e.err))));
        fs.writeFileSync(path.join(dir, 'mailauth-signed', c.id + '.eml'), Buffer.concat([Buffer.from(r.signatures), msg]));
      } catch (e) {
        errors++;
        console.log('sign failed', c.id, String(e));
      }
    }
    console.log('mailauth signed', cases.length - errors, 'errors', errors);
  }
})();
