'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const path = require('node:path');

test('Caddy API upload limit matches TOR Mail while receipt domain stays tiny', () => {
  const text = readFileSync(path.join(__dirname, '../deploy/Caddyfile.example'), 'utf8');
  const [apiBlock, bonRest] = text.split('bon.torpos.de {');
  assert.ok(bonRest, 'bon.torpos.de block must exist');
  assert.match(apiBlock, /api\.torpos\.de\s*\{/);
  assert.match(apiBlock, /request_body\s*\{[\s\S]*?max_size\s+12MB[\s\S]*?\}/);
  assert.doesNotMatch(apiBlock, /max_size\s+2MB/);
  assert.match(bonRest, /request_body\s*\{[\s\S]*?max_size\s+16KB[\s\S]*?\}/);
  assert.doesNotMatch(bonRest, /max_size\s+12MB/);
});
