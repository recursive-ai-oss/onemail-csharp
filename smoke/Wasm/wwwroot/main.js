// Runs the smoke app and copies its console output into the page, so a headless browser's --dump-dom shows the result.
import { dotnet } from './_framework/dotnet.js';

const out = document.getElementById('out');
const write = (text) => { out.textContent += text + '\n'; };
for (const name of ['log', 'info', 'warn', 'error', 'debug']) {
  const original = console[name].bind(console);
  console[name] = (...args) => { write(args.join(' ')); original(...args); };
}

try {
  const runtime = await dotnet.create();
  const code = await runtime.runMain();
  write(`exit: ${code}`);
} catch (e) {
  write(`exception: ${e}`);
}
document.title = 'done';
