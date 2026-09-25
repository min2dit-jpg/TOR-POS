'use strict';
// C-1: the edge proxy must not reject what server.js and managed-mail.js accept.
// Verified once against a real Caddy 2.10; this keeps the example file honest.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const {readFileSync}=require('node:fs');
const path=require('node:path');
const {MAX_TOTAL_ATTACHMENT_BYTES}=require('../managed-mail');

const caddy=readFileSync(path.join(__dirname,'..','deploy','Caddyfile.example'),'utf8');
const server=readFileSync(path.join(__dirname,'..','server.js'),'utf8');
const UNITS={B:1,KB:1e3,MB:1e6,GB:1e9,KIB:1024,MIB:1024**2,GIB:1024**3};
function bytes(text){const m=/^(\d+)([A-Za-z]+)$/.exec(text);return Number(m[1])*UNITS[m[2].toUpperCase()];}
function apiSite(){const start=caddy.indexOf('api.torpos.de {'),end=caddy.indexOf('\nbon.torpos.de {');return caddy.slice(start,end);}
function requestBodies(site){return [...site.matchAll(/^\s*request_body(?:\s+(@\w+))?\s*\{\s*max_size\s+(\S+)\s*\}/gm)].map(m=>({matcher:m[1]||'',max:bytes(m[2])}));}

test('C-1 Caddy lets a full TOR Mail request through and keeps 2 MB elsewhere',()=>{
 const site=apiSite(),limits=requestBodies(site);
 // A catch-all request_body would also wrap the mail route and win with the smaller limit.
 assert.ok(limits.every(l=>l.matcher),'every request_body in the api site needs a matcher');
 const mailPath='/api/v1/devices/mail/send';
 const mail=limits.find(l=>new RegExp(`^\\s*${l.matcher}\\s+path\\s+${mailPath.replace(/\//g,'\\/')}\\s*$`,'m').test(site));
 const rest=limits.find(l=>new RegExp(`^\\s*${l.matcher}\\s+not\\s+path\\s+${mailPath.replace(/\//g,'\\/')}\\s*$`,'m').test(site));
 assert.ok(mail&&rest,'mail route and all other routes need their own, mutually exclusive limit');
 assert.equal(rest.max,2e6);
 // Worst case the till may send: all attachments at the managed-mail cap, base64, plus JSON and text.
 const worstCase=Math.ceil(MAX_TOTAL_ATTACHMENT_BYTES/3)*4+100_000;
 assert.ok(mail.max>=worstCase,`Caddy mail limit ${mail.max} < worst-case request ${worstCase}`);
 const m=/pathname==='\/api\/v1\/devices\/mail\/send'[\s\S]*?readJson\(req,(\d+)\*(\d+)\*(\d+)\)/.exec(server);
 assert.ok(m,'mail route body limit not found in server.js');
 const serverMax=Number(m[1])*Number(m[2])*Number(m[3]);
 assert.ok(serverMax>=worstCase,'server.js mail limit is below the worst-case request');
 assert.ok(mail.max<=serverMax,'Caddy should not let through more than server.js reads anyway');
});
