// Runs the test app and posts its console output to the page server (/log), so the run script can follow a long run and see
// where a hang stops. A "filter" query parameter selects test classes by name.
import { dotnet } from './_framework/dotnet.js';

const filter = new URLSearchParams(location.search).get('filter') ?? '';
const queue = [];
const write = (text) => { queue.push(text); };
const flush = async () => {
  if (queue.length === 0) { return; }
  const body = queue.splice(0).join('\n') + '\n';
  try { await fetch('/log', { method: 'POST', body }); } catch { /* the server is gone at the end of a run */ }
};
for (const name of ['log', 'info', 'warn', 'error', 'debug']) {
  console[name] = (...args) => { write(args.join(' ')); };
}
const timer = setInterval(flush, 500);

try {
  const runtime = await dotnet.withApplicationArguments(filter).create();
  // The tests read data files at the absolute paths of the machine that built them: copy those files into the runtime's file system.
  const FS = runtime.Module.FS;
  const paths = (await (await fetch('/__manifest')).text()).split('\n').filter((p) => p);
  for (const path of paths) {
    const bytes = new Uint8Array(await (await fetch('/__files' + path.split('/').map(encodeURIComponent).join('/'))).arrayBuffer());
    FS.mkdirTree(path.substring(0, path.lastIndexOf('/')));
    FS.writeFile(path, bytes);
  }
  write(`copied ${paths.length} files`);
  const code = await runtime.runMain();
  write(`exit: ${code}`);
} catch (e) {
  write(`exception: ${e}`);
}
clearInterval(timer);
await flush();
await fetch('/done', { method: 'POST', body: '' });
