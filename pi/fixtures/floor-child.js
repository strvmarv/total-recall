// Fixture child for pi/floor.test.js: stands in for `total-recall pinned-floor`.
// argv[2] selects the behavior; stdin is a JSON payload with a session_id.
const mode = process.argv[2];
const chunks = [];
process.stdin.on('data', (c) => chunks.push(c));
process.stdin.on('end', () => {
  const payload = JSON.parse(Buffer.concat(chunks).toString('utf8') || '{}');
  const envelope = (ctx) => JSON.stringify({
    hookSpecificOutput: { hookEventName: 'UserPromptSubmit', additionalContext: ctx },
  });
  switch (mode) {
    case 'ok':
      process.stdout.write(envelope(payload.session_id) + '\n');
      break;
    case 'warn':
      process.stderr.write('some warning on stderr\n');
      process.stdout.write('stray warning line\n' + envelope(payload.session_id) + '\n');
      break;
    case 'exit1':
      process.stdout.write(envelope('SHOULD-NOT-SURFACE') + '\n');
      process.exit(1);
      break;
    case 'garbage':
      process.stdout.write('not json at all\n');
      break;
    case 'utf8': {
      // Split inside a multi-byte character (an em dash is 3 bytes) with a timer
      // between the writes so a per-chunk decoder cannot get away with it.
      const bytes = Buffer.from(envelope('pin \u2014 \ud83d\ude00 end') + '\n', 'utf8');
      const cut = bytes.indexOf(Buffer.from('\u2014', 'utf8')) + 1;
      process.stdout.write(bytes.subarray(0, cut));
      setTimeout(() => process.stdout.write(bytes.subarray(cut)), 50);
      break;
    }
    case 'hang':
      setInterval(() => {}, 1000);
      break;
    default:
      process.exit(2);
  }
});
