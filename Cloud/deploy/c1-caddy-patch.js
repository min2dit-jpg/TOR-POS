'use strict';
// C-1: rewrites the catch-all request_body limit of the API site in an existing
// Caddyfile into the two mutually exclusive limits of Caddyfile.example
// (TOR Mail 12 MiB, everything else 2 MB). Used by apply-c1-caddy.sh.
//
//   node c1-caddy-patch.js <Caddyfile> [site-prefix, default "api."]
//
// Prints CHANGED, ALREADY or an error; exit code 0 for CHANGED/ALREADY.
// The receipt site (bon.*) and every other block stay untouched.
const fs = require('node:fs');

function patch(text, sitePrefix = 'api.') {
  if (/@torMail\s+path\s+\/api\/v1\/devices\/mail\/send/.test(text)) return { status: 'ALREADY', text };

  const lines = text.split(/\r?\n/);
  const eol = text.includes('\r\n') ? '\r\n' : '\n';
  // Find the site block: a top-level line starting with the prefix and ending in "{".
  const start = lines.findIndex(l => l.startsWith(sitePrefix) && l.trimEnd().endsWith('{'));
  if (start < 0) throw new Error(`Kein Block "${sitePrefix}…" gefunden.`);
  let depth = 0, end = -1;
  for (let i = start; i < lines.length; i++) {
    const code = lines[i].replace(/#.*$/, '');
    depth += (code.match(/\{/g) || []).length - (code.match(/\}/g) || []).length;
    if (depth === 0) { end = i; break; }
  }
  if (end < 0) throw new Error(`Block "${sitePrefix}…" ist nicht geschlossen.`);

  const found = [];
  for (let i = start + 1; i < end; i++) {
    if (/^\s*request_body\s*\{\s*$/.test(lines[i])) found.push(i);
    else if (/^\s*request_body\b/.test(lines[i])) throw new Error(`Unerwartete request_body-Zeile ${i + 1}: ${lines[i].trim()} – bitte von Hand anpassen.`);
  }
  if (found.length !== 1) throw new Error(`Erwartet genau einen "request_body {"-Block im ${sitePrefix}-Block, gefunden: ${found.length}.`);
  const at = found[0];
  if (!/^\s*max_size\s+\S+\s*$/.test(lines[at + 1] || '') || !/^\s*\}\s*$/.test(lines[at + 2] || ''))
    throw new Error(`request_body in Zeile ${at + 1} hat nicht die Form "request_body { max_size … }" – bitte von Hand anpassen.`);

  const indent = lines[at].match(/^\s*/)[0];
  const inner = lines[at + 1].match(/^\s*/)[0];
  const replacement = [
    `${indent}# C-1: TOR Mail bis 12 MiB (Anhänge bis 8 MB, base64), alles andere 2 MB.`,
    `${indent}@torMail path /api/v1/devices/mail/send`,
    `${indent}request_body @torMail {`,
    `${inner}max_size 12MiB`,
    `${indent}}`,
    `${indent}@notTorMail not path /api/v1/devices/mail/send`,
    `${indent}request_body @notTorMail {`,
    `${inner}max_size 2MB`,
    `${indent}}`
  ];
  lines.splice(at, 3, ...replacement);
  return { status: 'CHANGED', text: lines.join(eol) };
}

module.exports = { patch };

if (require.main === module) {
  const [file, prefix] = process.argv.slice(2);
  try {
    if (!file) throw new Error('Aufruf: node c1-caddy-patch.js <Caddyfile> [api.]');
    const result = patch(fs.readFileSync(file, 'utf8'), prefix || 'api.');
    if (result.status === 'CHANGED') fs.writeFileSync(file, result.text);
    console.log(result.status);
  } catch (err) {
    console.error('FEHLER: ' + err.message);
    process.exitCode = 1;
  }
}
