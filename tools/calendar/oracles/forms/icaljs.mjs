// Black-box driver for the released ical.js package. Manifest lines: <job>\t<input>\t<output>.
//   text2json   ICAL.parse (jCal for iCalendar, jCard for vCard)
//   json2json   ICAL.Component(json).toJSON(): read a jCal or jCard document into the library's model, write it again
//   json2text   ICAL.stringify of the parsed JSON
import fs from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const mod = require(process.env.ICALJS ?? 'ical.js');
const ICAL = mod.parse ? mod : (mod.default ?? mod);
for (const line of fs.readFileSync(process.argv[2], 'utf8').split('\n')) {
  const [job, input, output] = line.split('\t');
  if (!output) continue;
  let out;
  try {
    const data = fs.readFileSync(input, 'utf8');
    if (job === 'text2json') out = JSON.stringify(ICAL.parse(data));
    else if (job === 'json2json') {
      const j = JSON.parse(data);
      const one = (x) => new ICAL.Component(x).toJSON();
      out = JSON.stringify(Array.isArray(j) && Array.isArray(j[0]) ? j.map(one) : one(j));
    } else if (job === 'json2text') out = ICAL.stringify(JSON.parse(data));
    else throw new Error('job ' + job);
  } catch (e) {
    out = 'ERROR ' + (e && e.name) + ': ' + String(e && e.message).replace(/\n/g, ' ');
  }
  fs.writeFileSync(output, out);
}
