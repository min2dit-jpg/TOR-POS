'use strict';

const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { DatabaseSync } = require('node:sqlite');
const {normalizeEvent,canonical,berlinParts}=require('./validation');
// R122 (audit finding İ6): qrSvg was imported here and never used. The server
// returns the QR as a matrix (qr_matrix) and the browser draws it; nothing
// renders SVG server-side.
const {makeQrV6L}=require('./qr-v6');
// R125: shared with tools/provision.js so both hash credentials identically.
const {hashPassword,hashToken}=require('./credentials');
const {normalizeManagedMailPayload,smtpConfigFromEnv,isManagedMailConfigured,sendManagedMail}=require('./managed-mail');
// R145: the public digital receipt (TOR Digital Receipt Cloud).
const {validateReceipt,renderReceiptPage,renderNotFoundPage,renderHomePage,renderReceiptPdf,ASSETS:RECEIPT_ASSETS}=require('./receipts');
// R127: the version string was typed twice (startup log and /api/health) and
// both still said R62 many revisions later. One constant now.
const CLOUD_VERSION='0.13.0-R145';
const DEMO=process.env.TOR_CLOUD_DEMO==='true';
const REQUIRE_OWNER_2FA = String(process.env.TOR_CLOUD_REQUIRE_OWNER_2FA ?? (!DEMO ? 'true' : 'false')).toLowerCase()==='true';

const ROOT = __dirname;
const PUBLIC = path.join(ROOT, 'public');
const DATA = path.join(ROOT, 'data');
const UPDATES = process.env.TOR_CLOUD_UPDATES || path.join(ROOT, 'updates');
fs.mkdirSync(DATA, { recursive: true });
fs.mkdirSync(UPDATES, { recursive: true });

const PORT = Number(process.env.PORT || 8787);
const HOST = process.env.HOST || '127.0.0.1';
const DB_PATH = process.env.TOR_CLOUD_DB || path.join(DATA, 'tor-cloud.db');
const SESSION_TTL_MS = 12 * 60 * 60 * 1000;
const COOKIE_SECURE = String(process.env.COOKIE_SECURE || '').toLowerCase() === 'true';
const TOTP_KEY_MATERIAL = String(process.env.TOR_CLOUD_TOTP_KEY || (DEMO ? 'TOR-POS-DEMO-TOTP-KEY-ONLY-LOCAL' : ''));
if (!DEMO && REQUIRE_OWNER_2FA && TOTP_KEY_MATERIAL.length < 24) throw new Error('Live-2FA benötigt TOR_CLOUD_TOTP_KEY mit mindestens 24 Zeichen.');
const TOTP_KEY = crypto.createHash('sha256').update(TOTP_KEY_MATERIAL || 'disabled').digest();

// R62 Google OAuth relay. The POS never stores a Gmail password or Google refresh token.
// A short-lived QR pairing is completed in the user's phone browser. The Cloud keeps only
// an encrypted refresh token and returns short-lived access tokens to the authenticated POS.
const CLOUD_PUBLIC_URL = String(process.env.TOR_CLOUD_PUBLIC_URL || '').trim().replace(/\/$/, '');
const GOOGLE_OAUTH_CLIENT_ID = String(process.env.TOR_GOOGLE_OAUTH_CLIENT_ID || '').trim();
const GOOGLE_OAUTH_CLIENT_SECRET = String(process.env.TOR_GOOGLE_OAUTH_CLIENT_SECRET || '').trim();
const GOOGLE_TOKEN_KEY_MATERIAL = String(process.env.TOR_CLOUD_GOOGLE_TOKEN_KEY || '').trim();
if (GOOGLE_TOKEN_KEY_MATERIAL && GOOGLE_TOKEN_KEY_MATERIAL.length < 32) throw new Error('TOR_CLOUD_GOOGLE_TOKEN_KEY muss mindestens 32 Zeichen lang sein.');
const GOOGLE_SCOPE = 'openid email https://www.googleapis.com/auth/gmail.send';
const GOOGLE_PAIR_TTL_MS = 10 * 60 * 1000;
const GOOGLE_TOKEN_KEY = GOOGLE_TOKEN_KEY_MATERIAL ? crypto.createHash('sha256').update(GOOGLE_TOKEN_KEY_MATERIAL).digest() : null;
const GOOGLE_OAUTH_READY = !!(CLOUD_PUBLIC_URL && GOOGLE_OAUTH_CLIENT_ID && GOOGLE_OAUTH_CLIENT_SECRET && GOOGLE_TOKEN_KEY && (DEMO || CLOUD_PUBLIC_URL.startsWith('https://')));
const GOOGLE_REDIRECT_URI = CLOUD_PUBLIC_URL ? `${CLOUD_PUBLIC_URL}/google/oauth/callback` : '';

// R155: server-managed TOR Mail. Customer PCs store no SMTP password and need
// no Google account. The relay identity lives only in Cloud environment secrets.
const TOR_MAIL_CONFIG = smtpConfigFromEnv();
const TOR_MAIL_READY = isManagedMailConfigured(TOR_MAIL_CONFIG);
const TOR_MAIL_HOURLY_LIMIT = Math.min(100, Math.max(1, Math.floor(Number(process.env.TOR_MAIL_HOURLY_LIMIT || 20))));
const TOR_MAIL_DAILY_LIMIT = Math.min(1000, Math.max(TOR_MAIL_HOURLY_LIMIT, Math.floor(Number(process.env.TOR_MAIL_DAILY_LIMIT || 100))));

// R145: TOR Digital Receipt Cloud. The customer's copy of a Kassenbon lives on
// its own domain (bon.<domain>), a security domain of its own next to the API
// and portal (api.<domain>): no login, no cookies, nothing but receipts behind
// a 256-bit token. The till uploads through the authenticated device API. It is
// not the fiscal archive - that stays in TOR POS on the till (§ 147 AO); links
// and their content are deleted after TOR_CLOUD_RECEIPT_TTL_DAYS.
function hostOf(value){return new URL(value).hostname.toLowerCase();}
const RECEIPT_URL_SETTING=String(process.env.TOR_CLOUD_RECEIPT_URL||'').trim();
let RECEIPT_ORIGIN='';
if(RECEIPT_URL_SETTING){
  let parsed;
  try{parsed=new URL(RECEIPT_URL_SETTING);}catch{throw new Error('TOR_CLOUD_RECEIPT_URL ist keine gültige URL.');}
  if(!['http:','https:'].includes(parsed.protocol)||parsed.pathname!=='/'||parsed.search||parsed.hash||parsed.username||parsed.password)
    throw new Error('TOR_CLOUD_RECEIPT_URL enthält nur Schema und Domain, z. B. https://bon.torpos.de');
  if(!DEMO&&parsed.protocol!=='https:')throw new Error('TOR_CLOUD_RECEIPT_URL muss im Livebetrieb HTTPS (TLS) verwenden.');
  if(CLOUD_PUBLIC_URL&&hostOf(CLOUD_PUBLIC_URL)===parsed.hostname.toLowerCase())
    throw new Error('Der digitale Kassenbon braucht eine eigene Domain, getrennt von TOR_CLOUD_PUBLIC_URL (z. B. bon.torpos.de neben api.torpos.de).');
  RECEIPT_ORIGIN=parsed.origin;
}
const RECEIPT_HOST=RECEIPT_ORIGIN?hostOf(RECEIPT_ORIGIN):'';
const RECEIPT_HTTPS=RECEIPT_ORIGIN.startsWith('https://');
const RECEIPT_TTL_DAYS=Math.min(366,Math.max(1,Math.floor(Number(process.env.TOR_CLOUD_RECEIPT_TTL_DAYS||90))||90));
// Impressum (§ 5 DDG) and privacy notice (Art. 13 DSGVO) of the operator of the
// receipt domain, linked on every page there.
function legalUrl(name){
  const value=String(process.env[name]||'').trim();
  if(!value)return '';
  let parsed;
  try{parsed=new URL(value);}catch{throw new Error(`${name} ist keine gültige URL.`);}
  if(parsed.protocol!=='https:'&&!(DEMO&&parsed.protocol==='http:'))throw new Error(`${name} muss HTTPS verwenden.`);
  return parsed.href;
}
const RECEIPT_LINKS={imprint:legalUrl('TOR_CLOUD_IMPRINT_URL'),privacy:legalUrl('TOR_CLOUD_PRIVACY_URL')};

if (!['127.0.0.1','localhost','::1'].includes(HOST) && (!COOKIE_SECURE || DEMO)) throw new Error('Externer Betrieb benötigt sichere Cookies und deaktivierten Demomodus.');
const db = new DatabaseSync(DB_PATH);
db.function('berlin_day', x=>berlinParts(x).day);
db.function('berlin_hour', x=>berlinParts(x).hour);
// R125: busy_timeout, because tools/provision.js now writes to this same file
// while the server runs; without it a colliding write fails at once with
// "database is locked" instead of waiting a few milliseconds for its turn.
// R145: secure_delete, so a digital receipt deleted at the end of its lifetime
// is overwritten in the database file instead of lingering in free pages.
db.exec('PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA secure_delete=ON;');

function nowIso() { return new Date().toISOString(); }
function randomId(bytes = 24) { return crypto.randomBytes(bytes).toString('base64url'); }
function timingSafeEqualText(a, b) {
  const ba = Buffer.from(String(a));
  const bb = Buffer.from(String(b));
  return ba.length === bb.length && crypto.timingSafeEqual(ba, bb);
}
async function verifyPassword(password, salt, expected) {
  const actual = (await new Promise((resolve,reject)=>crypto.scrypt(password,salt,64,(error,key)=>error?reject(error):resolve(key)))).toString('hex');
  return timingSafeEqualText(actual, expected);
}
const BASE32='ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
function base32Encode(buffer){let bits=0,value=0,out='';for(const byte of buffer){value=(value<<8)|byte;bits+=8;while(bits>=5){out+=BASE32[(value>>>(bits-5))&31];bits-=5;}}if(bits>0)out+=BASE32[(value<<(5-bits))&31];return out;}
function base32Decode(text){const clean=String(text||'').toUpperCase().replace(/[^A-Z2-7]/g,'');let bits=0,value=0;const out=[];for(const ch of clean){const idx=BASE32.indexOf(ch);if(idx<0)continue;value=(value<<5)|idx;bits+=5;if(bits>=8){out.push((value>>>(bits-8))&255);bits-=8;}}return Buffer.from(out);}
function totpCode(secret,timeMs=Date.now()){const counter=BigInt(Math.floor(timeMs/30000));const msg=Buffer.alloc(8);msg.writeBigUInt64BE(counter);const h=crypto.createHmac('sha1',base32Decode(secret)).update(msg).digest();const off=h[h.length-1]&15;const bin=((h[off]&127)<<24)|(h[off+1]<<16)|(h[off+2]<<8)|h[off+3];return String(bin%1000000).padStart(6,'0');}
function verifyTotp(secret,code){const c=String(code||'').replace(/\s/g,'');if(!/^\d{6}$/.test(c))return false;const now=Date.now();return [-30000,0,30000].some(delta=>timingSafeEqualText(totpCode(secret,now+delta),c));}
function recoveryCode(){const raw=base32Encode(crypto.randomBytes(8)).slice(0,12);return raw.match(/.{1,4}/g).join('-');}
function protectTotpSecret(secret){const iv=crypto.randomBytes(12);const cipher=crypto.createCipheriv('aes-256-gcm',TOTP_KEY,iv);const enc=Buffer.concat([cipher.update(String(secret),'utf8'),cipher.final()]);const tag=cipher.getAuthTag();return ['v1',iv.toString('base64url'),tag.toString('base64url'),enc.toString('base64url')].join(':');}
function unprotectTotpSecret(value){const text=String(value||'');if(!text)return '';if(!text.startsWith('v1:'))return text;const parts=text.split(':');if(parts.length!==4)throw new Error('2FA-Schlüssel ist beschädigt.');const iv=Buffer.from(parts[1],'base64url'),tag=Buffer.from(parts[2],'base64url'),enc=Buffer.from(parts[3],'base64url');const dec=crypto.createDecipheriv('aes-256-gcm',TOTP_KEY,iv);dec.setAuthTag(tag);return Buffer.concat([dec.update(enc),dec.final()]).toString('utf8');}

function protectGoogleSecret(value){
  if(!GOOGLE_TOKEN_KEY)throw Object.assign(new Error('Google-Token-Schutz ist nicht konfiguriert.'),{statusCode:503});
  const iv=crypto.randomBytes(12),cipher=crypto.createCipheriv('aes-256-gcm',GOOGLE_TOKEN_KEY,iv);
  const enc=Buffer.concat([cipher.update(String(value),'utf8'),cipher.final()]),tag=cipher.getAuthTag();
  return ['v1',iv.toString('base64url'),tag.toString('base64url'),enc.toString('base64url')].join(':');
}
function unprotectGoogleSecret(value){
  if(!GOOGLE_TOKEN_KEY)throw Object.assign(new Error('Google-Token-Schutz ist nicht konfiguriert.'),{statusCode:503});
  const parts=String(value||'').split(':');if(parts.length!==4||parts[0]!=='v1')throw new Error('Google-Verbindung ist beschädigt.');
  const iv=Buffer.from(parts[1],'base64url'),tag=Buffer.from(parts[2],'base64url'),enc=Buffer.from(parts[3],'base64url');
  const dec=crypto.createDecipheriv('aes-256-gcm',GOOGLE_TOKEN_KEY,iv);dec.setAuthTag(tag);
  return Buffer.concat([dec.update(enc),dec.final()]).toString('utf8');
}
function ensureGoogleOAuthReady(){
  if(GOOGLE_OAUTH_READY)return;
  throw Object.assign(new Error('Google OAuth ist im TOR POS Cloud Server noch nicht konfiguriert. Erforderlich: TOR_CLOUD_PUBLIC_URL (HTTPS), TOR_GOOGLE_OAUTH_CLIENT_ID, TOR_GOOGLE_OAUTH_CLIENT_SECRET und TOR_CLOUD_GOOGLE_TOKEN_KEY.'),{statusCode:503});
}
function cleanupGooglePairs(){
  db.prepare('DELETE FROM google_oauth_pairs WHERE expires_at<=?').run(nowIso());
}
function htmlEscape(value){return String(value??'').replace(/[&<>"']/g,ch=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));}
function googlePairRow(id,claimToken){
  cleanupGooglePairs();
  const row=db.prepare('SELECT * FROM google_oauth_pairs WHERE id=?').get(String(id||''));
  if(!row||!claimToken||!timingSafeEqualText(row.claim_token_hash,hashToken(claimToken)))return null;
  return row;
}
async function googleTokenRequest(params){
  const response=await fetch('https://oauth2.googleapis.com/token',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:new URLSearchParams(params),signal:AbortSignal.timeout(30000)});
  let payload={};try{payload=await response.json();}catch{}
  if(!response.ok)throw Object.assign(new Error('Google-Tokenaustausch wurde abgelehnt.'),{googleError:String(payload.error||''),googleDescription:String(payload.error_description||''),statusCode:502});
  return payload;
}
async function googleUserInfo(accessToken){
  const response=await fetch('https://openidconnect.googleapis.com/v1/userinfo',{headers:{Authorization:`Bearer ${accessToken}`},signal:AbortSignal.timeout(20000)});
  if(!response.ok)throw Object.assign(new Error('Google-Konto konnte nach der Anmeldung nicht bestätigt werden.'),{statusCode:502});
  return await response.json();
}
async function refreshGoogleAccess(refreshToken){
  const payload=await googleTokenRequest({client_id:GOOGLE_OAUTH_CLIENT_ID,client_secret:GOOGLE_OAUTH_CLIENT_SECRET,refresh_token:refreshToken,grant_type:'refresh_token'});
  if(!payload.access_token)throw Object.assign(new Error('Google hat kein Zugriffstoken geliefert.'),{statusCode:502});
  return payload;
}
async function revokeGoogleRefresh(refreshToken){
  try{
    const response=await fetch('https://oauth2.googleapis.com/revoke',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:new URLSearchParams({token:refreshToken}),signal:AbortSignal.timeout(20000)});
    return response.ok || response.status===400;
  }catch{return false;}
}
function createSession(res,user){const sid=randomId(32);const expires=new Date(Date.now()+SESSION_TTL_MS).toISOString();db.prepare('INSERT INTO sessions(id,user_id,created_at,expires_at) VALUES(?,?,?,?)').run(sid,user.id,nowIso(),expires);setCookie(res,'tor_session',sid,{maxAge:SESSION_TTL_MS/1000});return sid;}
function parseCookies(req) {
  const result = {};
  for (const part of String(req.headers.cookie || '').split(';')) {
    const idx = part.indexOf('=');
    if (idx <= 0) continue;
    result[part.slice(0, idx).trim()] = decodeURIComponent(part.slice(idx + 1).trim());
  }
  return result;
}
function setCookie(res, name, value, options = {}) {
  const parts = [`${name}=${encodeURIComponent(value)}`, 'Path=/', 'HttpOnly', 'SameSite=Lax'];
  if (options.maxAge != null) parts.push(`Max-Age=${Math.max(0, Math.floor(options.maxAge))}`);
  if (COOKIE_SECURE || options.secure) parts.push('Secure');
  res.setHeader('Set-Cookie', parts.join('; '));
}
function json(res, status, payload, extraHeaders = {}) {
  const body = Buffer.from(JSON.stringify(payload));
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': body.length,
    'Cache-Control': 'no-store',
    ...extraHeaders
  });
  res.end(body);
}
function text(res, status, body, type = 'text/plain; charset=utf-8') {
  const buf = Buffer.from(body);
  res.writeHead(status, { 'Content-Type': type, 'Content-Length': buf.length });
  res.end(buf);
}
function html(res,status,body){
  const buf=Buffer.from(body);
  res.writeHead(status,{'Content-Type':'text/html; charset=utf-8','Content-Length':buf.length,'Cache-Control':'no-store','Pragma':'no-cache','Referrer-Policy':'no-referrer','X-Frame-Options':'DENY','X-Content-Type-Options':'nosniff'});
  res.end(buf);
}
async function readBody(req, maxBytes = 1024 * 1024) {
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > maxBytes) throw Object.assign(new Error('Request too large'), { statusCode: 413 });
    chunks.push(chunk);
  }
  return Buffer.concat(chunks);
}
async function readJson(req, maxBytes = 1024 * 1024) {
  const raw = await readBody(req, maxBytes);
  if (!raw.length) return {};
  try { return JSON.parse(raw.toString('utf8')); }
  catch { throw Object.assign(new Error('Ungültiges JSON'), { statusCode: 400 }); }
}

function initSchema() {
  db.exec(`
    CREATE TABLE IF NOT EXISTS businesses(
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL,
      customer_number TEXT NOT NULL UNIQUE,
      created_at TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS users(
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      business_id INTEGER NOT NULL,
      email TEXT NOT NULL UNIQUE COLLATE NOCASE,
      display_name TEXT NOT NULL,
      role TEXT NOT NULL DEFAULT 'OWNER',
      password_salt TEXT NOT NULL,
      password_hash TEXT NOT NULL,
      is_active INTEGER NOT NULL DEFAULT 1,
      created_at TEXT NOT NULL,
      FOREIGN KEY(business_id) REFERENCES businesses(id)
    );
    CREATE TABLE IF NOT EXISTS sessions(
      id TEXT PRIMARY KEY,
      user_id INTEGER NOT NULL,
      created_at TEXT NOT NULL,
      expires_at TEXT NOT NULL,
      FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
    );
    CREATE TABLE IF NOT EXISTS login_challenges(
      id TEXT PRIMARY KEY,
      user_id INTEGER NOT NULL,
      created_at TEXT NOT NULL,
      expires_at TEXT NOT NULL,
      attempts INTEGER NOT NULL DEFAULT 0,
      FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
    );
    CREATE TABLE IF NOT EXISTS branches(
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      business_id INTEGER NOT NULL,
      name TEXT NOT NULL,
      city TEXT NOT NULL DEFAULT '',
      created_at TEXT NOT NULL,
      FOREIGN KEY(business_id) REFERENCES businesses(id)
    );
    CREATE TABLE IF NOT EXISTS registers(
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      branch_id INTEGER NOT NULL,
      device_code TEXT NOT NULL UNIQUE,
      name TEXT NOT NULL,
      edition TEXT NOT NULL DEFAULT 'KIOSK',
      last_seen_at TEXT NOT NULL DEFAULT '',
      software_version TEXT NOT NULL DEFAULT '',
      tse_status TEXT NOT NULL DEFAULT 'UNBEKANNT',
      printer_status TEXT NOT NULL DEFAULT 'UNBEKANNT',
      created_at TEXT NOT NULL,
      FOREIGN KEY(branch_id) REFERENCES branches(id)
    );
    CREATE TABLE IF NOT EXISTS device_tokens(
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      register_id INTEGER NOT NULL,
      token_hash TEXT NOT NULL UNIQUE,
      label TEXT NOT NULL DEFAULT '',
      created_at TEXT NOT NULL,
      revoked_at TEXT NOT NULL DEFAULT '',
      FOREIGN KEY(register_id) REFERENCES registers(id)
    );
    CREATE TABLE IF NOT EXISTS cloud_events(
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      register_id INTEGER NOT NULL,
      event_id TEXT NOT NULL,
      event_type TEXT NOT NULL,
      occurred_at TEXT NOT NULL,
      received_at TEXT NOT NULL,
      payload_json TEXT NOT NULL,
      UNIQUE(register_id,event_id),
      FOREIGN KEY(register_id) REFERENCES registers(id)
    );
    CREATE TABLE IF NOT EXISTS cloud_sales(
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      register_id INTEGER NOT NULL,
      event_id TEXT NOT NULL UNIQUE,
      receipt_number INTEGER NOT NULL,
      occurred_at TEXT NOT NULL,
      payment_method TEXT NOT NULL,
      subtotal_cents INTEGER NOT NULL,
      discount_cents INTEGER NOT NULL,
      total_cents INTEGER NOT NULL,
      operator_name TEXT NOT NULL DEFAULT '',
      item_count REAL NOT NULL DEFAULT 0,
      FOREIGN KEY(register_id) REFERENCES registers(id)
    );
    CREATE TABLE IF NOT EXISTS cloud_sale_items(
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      sale_id INTEGER NOT NULL,
      position_no INTEGER NOT NULL,
      product_key TEXT NOT NULL DEFAULT '',
      name TEXT NOT NULL,
      quantity REAL NOT NULL DEFAULT 1,
      unit_price_cents INTEGER NOT NULL DEFAULT 0,
      line_total_cents INTEGER NOT NULL DEFAULT 0,
      vat_rate REAL NOT NULL DEFAULT 19,
      FOREIGN KEY(sale_id) REFERENCES cloud_sales(id) ON DELETE CASCADE
    );
    CREATE INDEX IF NOT EXISTS idx_cloud_sale_items_sale_id ON cloud_sale_items(sale_id);
    CREATE TABLE IF NOT EXISTS cash_movements(
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      register_id INTEGER NOT NULL,
      event_id TEXT NOT NULL UNIQUE,
      occurred_at TEXT NOT NULL,
      movement_type TEXT NOT NULL,
      amount_cents INTEGER NOT NULL,
      reason TEXT NOT NULL DEFAULT '',
      actor TEXT NOT NULL DEFAULT '',
      FOREIGN KEY(register_id) REFERENCES registers(id)
    );
    CREATE TABLE IF NOT EXISTS z_reports(
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      register_id INTEGER NOT NULL,
      event_id TEXT NOT NULL UNIQUE,
      z_number TEXT NOT NULL,
      occurred_at TEXT NOT NULL,
      gross_cents INTEGER NOT NULL,
      sale_count INTEGER NOT NULL,
      FOREIGN KEY(register_id) REFERENCES registers(id)
    );
    CREATE TABLE IF NOT EXISTS stock_items(
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      register_id INTEGER NOT NULL,
      product_key TEXT NOT NULL,
      name TEXT NOT NULL,
      sku TEXT NOT NULL DEFAULT '',
      barcode TEXT NOT NULL DEFAULT '',
      group_name TEXT NOT NULL DEFAULT '',
      category_name TEXT NOT NULL DEFAULT '',
      unit TEXT NOT NULL DEFAULT 'Stück',
      price_cents INTEGER NOT NULL DEFAULT 0,
      purchase_price_cents INTEGER NOT NULL DEFAULT 0,
      min_stock_quantity REAL NOT NULL DEFAULT 0,
      quantity REAL NOT NULL,
      updated_at TEXT NOT NULL,
      UNIQUE(register_id,product_key),
      FOREIGN KEY(register_id) REFERENCES registers(id)
    );
    CREATE TABLE IF NOT EXISTS google_oauth_connections(
      id TEXT PRIMARY KEY,
      register_id INTEGER NOT NULL,
      account_email TEXT NOT NULL,
      refresh_token_protected TEXT NOT NULL,
      scope TEXT NOT NULL DEFAULT '',
      created_at TEXT NOT NULL,
      updated_at TEXT NOT NULL,
      revoked_at TEXT NOT NULL DEFAULT '',
      last_error TEXT NOT NULL DEFAULT '',
      FOREIGN KEY(register_id) REFERENCES registers(id) ON DELETE CASCADE
    );
    CREATE INDEX IF NOT EXISTS idx_google_oauth_connections_register ON google_oauth_connections(register_id,revoked_at);
    CREATE TABLE IF NOT EXISTS google_oauth_pairs(
      id TEXT PRIMARY KEY,
      register_id INTEGER NOT NULL,
      pair_secret_hash TEXT NOT NULL,
      claim_token_hash TEXT NOT NULL,
      state_hash TEXT NOT NULL UNIQUE,
      state_protected TEXT NOT NULL,
      pkce_verifier_protected TEXT NOT NULL,
      status TEXT NOT NULL DEFAULT 'PENDING',
      connection_id TEXT NOT NULL DEFAULT '',
      account_email TEXT NOT NULL DEFAULT '',
      error TEXT NOT NULL DEFAULT '',
      created_at TEXT NOT NULL,
      expires_at TEXT NOT NULL,
      FOREIGN KEY(register_id) REFERENCES registers(id) ON DELETE CASCADE
    );
    CREATE INDEX IF NOT EXISTS idx_google_oauth_pairs_expiry ON google_oauth_pairs(expires_at);
    -- R145: the public customer copy of a receipt. Only what the receipt shows,
    -- only the hash of the link token, deleted entirely when it expires.
    CREATE TABLE IF NOT EXISTS public_receipts(
      id TEXT PRIMARY KEY,
      business_id INTEGER NOT NULL,
      register_id INTEGER NOT NULL,
      receipt_ref TEXT NOT NULL,
      token_hash TEXT NOT NULL UNIQUE,
      content_hash TEXT NOT NULL,
      document_json TEXT NOT NULL,
      pdf BLOB NOT NULL,
      created_at TEXT NOT NULL,
      expires_at TEXT NOT NULL,
      UNIQUE(register_id,receipt_ref),
      FOREIGN KEY(business_id) REFERENCES businesses(id),
      FOREIGN KEY(register_id) REFERENCES registers(id) ON DELETE CASCADE
    );
    CREATE INDEX IF NOT EXISTS idx_public_receipts_expiry ON public_receipts(expires_at);

    -- 7-day public desktop trial. The client generates a random Trial-ID
    -- stored machine-wide in ProgramData. No hardware identifier is collected.
    CREATE TABLE IF NOT EXISTS trial_ids(
      trial_id TEXT PRIMARY KEY,
      first_seen_at TEXT NOT NULL,
      expires_at TEXT NOT NULL,
      last_seen_at TEXT NOT NULL,
      activation_count INTEGER NOT NULL DEFAULT 1,
      last_version TEXT NOT NULL DEFAULT '',
      last_revision TEXT NOT NULL DEFAULT ''
    );
    CREATE INDEX IF NOT EXISTS idx_trial_ids_expiry ON trial_ids(expires_at);
  `);
}

function seedDemo() {
  const exists = db.prepare('SELECT id FROM businesses WHERE customer_number=?').get('TOR-DEMO-001');
  if (exists) return;
  const created = nowIso();
  const business = db.prepare('INSERT INTO businesses(name,customer_number,created_at) VALUES(?,?,?) RETURNING id')
    .get('Muster Späti Berlin', 'TOR-DEMO-001', created);
  const branch = db.prepare('INSERT INTO branches(business_id,name,city,created_at) VALUES(?,?,?,?) RETURNING id')
    .get(business.id, 'Berlin Friedrichshain', 'Berlin', created);
  const register = db.prepare('INSERT INTO registers(branch_id,device_code,name,edition,last_seen_at,software_version,tse_status,printer_status,created_at) VALUES(?,?,?,?,?,?,?,?,?) RETURNING id')
    .get(branch.id, 'DEMO-KASSE-01', 'Kasse 1', 'KIOSK', nowIso(), '0.7.33-R49-Demo', 'TESTBETRIEB', 'BEREIT', created);
  const pwd = hashPassword('TorDemo2026!');
  db.prepare('INSERT INTO users(business_id,email,display_name,role,password_salt,password_hash,created_at) VALUES(?,?,?,?,?,?,?)')
    .run(business.id, 'demo@torpos.local', 'Demo Inhaber', 'OWNER', pwd.salt, pwd.hash, created);
  db.prepare('INSERT INTO device_tokens(register_id,token_hash,label,created_at) VALUES(?,?,?,?)')
    .run(register.id, hashToken('tor-demo-device-token-2026'), 'Lokaler Entwicklungstest', created);

  const base = new Date();
  base.setHours(9, 5, 0, 0);
  const sales = [
    [101, 985, 'CASH', 3, 'kassierer1'], [102, 1460, 'CARD', 4, 'kassierer1'],
    [103, 720, 'CASH', 2, 'admin'], [104, 1890, 'CARD', 5, 'admin'],
    [105, 540, 'CASH', 2, 'kassierer1'], [106, 1275, 'CARD', 3, 'kassierer1'],
    [107, 2330, 'CASH', 6, 'admin'], [108, 890, 'CARD', 2, 'admin']
  ];
  sales.forEach((s, i) => {
    const at = new Date(base.getTime() + i * 38 * 60 * 1000).toISOString();
    const eventId = `demo-sale-${s[0]}`;
    db.prepare('INSERT INTO cloud_sales(register_id,event_id,receipt_number,occurred_at,payment_method,subtotal_cents,discount_cents,total_cents,operator_name,item_count) VALUES(?,?,?,?,?,?,?,?,?,?)')
      .run(register.id, eventId, s[0], at, s[2], s[1], 0, s[1], s[4], s[3]);
    db.prepare('INSERT INTO cloud_events(register_id,event_id,event_type,occurred_at,received_at,payload_json) VALUES(?,?,?,?,?,?)')
      .run(register.id, eventId, 'sale.completed', at, at, JSON.stringify({ receipt_number: s[0], total_cents: s[1], payment_method: s[2] }));
  });
  const stocks = [
    ['10001','Coca-Cola 0,33l','COLA033','4000000000011','Getränke','Softdrinks','Stück',250,95,8,18],
    ['10002','Red Bull 0,25l','RB025','4000000000028','Getränke','Energy','Stück',300,135,8,7],
    ['10003','Wasser 0,5l','WASS05','4000000000035','Getränke','Wasser','Stück',180,55,10,24],
    ['10004','Chips Paprika','CHIPS-P','4000000000042','Snacks','Chips','Stück',220,80,6,4],
    ['10005','Marlboro Gold','MAR-GOLD','4000000000059','Tabak','Zigaretten','Stück',900,760,5,12]
  ];
  for (const row of stocks) {
    db.prepare('INSERT INTO stock_items(register_id,product_key,name,sku,barcode,group_name,category_name,unit,price_cents,purchase_price_cents,min_stock_quantity,quantity,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)')
      .run(register.id, row[0], row[1], row[2], row[3], row[4], row[5], row[6], row[7], row[8], row[9], row[10], nowIso());
  }
}

initSchema();
db.exec(`
  CREATE TABLE IF NOT EXISTS managed_mail_log(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    register_id INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    recipient_hash TEXT NOT NULL,
    subject_hash TEXT NOT NULL,
    attachment_count INTEGER NOT NULL DEFAULT 0,
    total_bytes INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL,
    last_error TEXT NOT NULL DEFAULT '',
    FOREIGN KEY(register_id) REFERENCES registers(id)
  );
  CREATE INDEX IF NOT EXISTS idx_managed_mail_register_created
    ON managed_mail_log(register_id,created_at);
`);
function ensureColumn(table,name,definition){
  const exists=db.prepare(`PRAGMA table_info(${table})`).all().some(c=>c.name===name);
  if(!exists)db.exec(`ALTER TABLE ${table} ADD COLUMN ${name} ${definition}`);
}
ensureColumn('stock_items','sku',"TEXT NOT NULL DEFAULT ''");
ensureColumn('stock_items','barcode',"TEXT NOT NULL DEFAULT ''");
ensureColumn('stock_items','group_name',"TEXT NOT NULL DEFAULT ''");
ensureColumn('stock_items','category_name',"TEXT NOT NULL DEFAULT ''");
ensureColumn('stock_items','unit',"TEXT NOT NULL DEFAULT 'Stück'");
ensureColumn('stock_items','price_cents','INTEGER NOT NULL DEFAULT 0');
ensureColumn('stock_items','purchase_price_cents','INTEGER NOT NULL DEFAULT 0');
ensureColumn('stock_items','min_stock_quantity','REAL NOT NULL DEFAULT 0');
ensureColumn('cloud_sales','pickup_number','INTEGER NOT NULL DEFAULT 0');
ensureColumn('cloud_sales','transaction_type',"TEXT NOT NULL DEFAULT 'SALE'");
ensureColumn('cloud_sales','original_receipt_number','INTEGER NOT NULL DEFAULT 0');
ensureColumn('cloud_sales','cash_portion_cents','INTEGER NOT NULL DEFAULT 0');
ensureColumn('cloud_sales','card_portion_cents','INTEGER NOT NULL DEFAULT 0');
ensureColumn('users','totp_enabled','INTEGER NOT NULL DEFAULT 0');
ensureColumn('users','totp_secret',"TEXT NOT NULL DEFAULT ''");
ensureColumn('users','totp_pending_secret',"TEXT NOT NULL DEFAULT ''");
ensureColumn('users','recovery_hashes',"TEXT NOT NULL DEFAULT '[]'");
// R128: set by tools/provision.js for a one-time password; cleared when the
// owner chooses their own. Existing accounts default to 0 and are unaffected.
ensureColumn('users','must_change_password','INTEGER NOT NULL DEFAULT 0');
if(!DEMO && db.prepare("SELECT 1 FROM businesses WHERE customer_number='TOR-DEMO-001'").get())throw new Error('Demodatenbank nur mit TOR_CLOUD_DEMO=true verwenden.');
db.exec(`CREATE TABLE IF NOT EXISTS cloud_migrations(id TEXT PRIMARY KEY);
CREATE TABLE IF NOT EXISTS stock_sync_state(register_id INTEGER PRIMARY KEY,occurred_at TEXT NOT NULL);`);
if(!db.prepare('SELECT 1 FROM cloud_migrations WHERE id=?').get('R41-scoped-events')){
  db.exec('BEGIN IMMEDIATE');
  try{
    for(const table of ['cloud_sales','cash_movements','z_reports'])db.exec(`UPDATE ${table} SET event_id=register_id || ':' || event_id`);
    db.prepare('INSERT INTO cloud_migrations(id) VALUES(?)').run('R41-scoped-events');db.exec('COMMIT');
  }catch(e){db.exec('ROLLBACK');throw e;}
}

if (DEMO) seedDemo();

function ensureDemoReceiptItems() {
  const demo = db.prepare(`
    SELECT s.id sale_id,s.receipt_number
    FROM cloud_sales s
    JOIN registers r ON r.id=s.register_id
    JOIN branches br ON br.id=r.branch_id
    JOIN businesses b ON b.id=br.business_id
    WHERE b.customer_number='TOR-DEMO-001'
  `).all();
  if (!demo.length) return;
  const templates = {
    101: [['10001','Coca-Cola 0,33l',2,250,500,19],['10004','Chips Paprika',1,320,320,7],['10003','Wasser 0,5l',1,165,165,19]],
    102: [['10002','Red Bull 0,25l',1,350,350,19],['10001','Coca-Cola 0,33l',1,250,250,19],['10005','Marlboro Gold',1,695,695,19],['10003','Wasser 0,5l',1,165,165,19]],
    103: [['10004','Chips Paprika',1,320,320,7],['10002','Red Bull 0,25l',1,400,400,19]],
    104: [['10001','Coca-Cola 0,33l',1,250,250,19],['10003','Wasser 0,5l',1,165,165,19],['10002','Red Bull 0,25l',1,350,350,19],['10004','Chips Paprika',1,320,320,7],['10005','Marlboro Gold',1,805,805,19]],
    105: [['10001','Coca-Cola 0,33l',1,250,250,19],['10004','Chips Paprika',1,290,290,7]],
    106: [['10002','Red Bull 0,25l',1,350,350,19],['10005','Marlboro Gold',1,760,760,19],['10003','Wasser 0,5l',1,165,165,19]],
    107: [['10001','Coca-Cola 0,33l',1,250,250,19],['10003','Wasser 0,5l',1,165,165,19],['10004','Chips Paprika',1,320,320,7],['10002','Red Bull 0,25l',1,350,350,19],['10005','Marlboro Gold',1,850,850,19],['10006','Snack Mix',1,395,395,7]],
    108: [['10005','Marlboro Gold',1,725,725,19],['10003','Wasser 0,5l',1,165,165,19]]
  };
  const existsStmt = db.prepare('SELECT 1 FROM cloud_sale_items WHERE sale_id=? LIMIT 1');
  const insertStmt = db.prepare('INSERT INTO cloud_sale_items(sale_id,position_no,product_key,name,quantity,unit_price_cents,line_total_cents,vat_rate) VALUES(?,?,?,?,?,?,?,?)');
  for (const sale of demo) {
    if (existsStmt.get(sale.sale_id)) continue;
    const rows = templates[sale.receipt_number] || [];
    rows.forEach((row, idx) => insertStmt.run(sale.sale_id, idx + 1, ...row));
  }
}
if (DEMO) ensureDemoReceiptItems();

function getSessionUser(req) {
  const sid = parseCookies(req).tor_session;
  if (!sid) return null;
  const row = db.prepare(`
    SELECT u.id AS user_id,u.business_id,u.email,u.display_name,u.role,u.totp_enabled,u.totp_secret,u.recovery_hashes,u.must_change_password,s.expires_at
    FROM sessions s JOIN users u ON u.id=s.user_id
    WHERE s.id=? AND u.is_active=1
  `).get(sid);
  if (!row) return null;
  if (Date.parse(row.expires_at) <= Date.now()) {
    db.prepare('DELETE FROM sessions WHERE id=?').run(sid);
    return null;
  }
  return row;
}

function requireUser(req,res,{allowUnenrolled=false,allowPasswordChange=false}={}){
  const user=getSessionUser(req);
  if(!user){json(res,401,{ok:false,error:'Nicht angemeldet'});return null;}
  if(user.role!=='OWNER'){json(res,403,{ok:false,error:'Portalzugriff nur für freigegebene Rollen'});return null;}
  // R128: an account still on the one-time password from tools/provision.js
  // gets nothing but the password change - checked before 2FA, so the owner
  // first replaces a password that was handed over, then enrols 2FA with it.
  if(user.must_change_password && !allowPasswordChange){json(res,428,{ok:false,error:'Bitte zuerst das Einmal-Passwort durch ein eigenes Passwort ersetzen.',code:'PASSWORD_CHANGE_REQUIRED'});return null;}
  if(REQUIRE_OWNER_2FA && !allowUnenrolled && !user.totp_enabled){json(res,428,{ok:false,error:'Zwei-Faktor-Anmeldung muss zuerst eingerichtet werden.',code:'TWO_FACTOR_SETUP_REQUIRED'});return null;}
  return user;
}

// R128: deliberately simple and explainable to a shop owner: long enough to
// resist guessing, not the password it replaces, not the e-mail address.
const MIN_PASSWORD_LENGTH=12;
function passwordProblem(next,current,email){
  if(next.length<MIN_PASSWORD_LENGTH)return `Das neue Passwort muss mindestens ${MIN_PASSWORD_LENGTH} Zeichen lang sein.`;
  if(next.length>200)return 'Das neue Passwort ist zu lang (höchstens 200 Zeichen).';
  if(!next.trim())return 'Das neue Passwort darf nicht nur aus Leerzeichen bestehen.';
  if(next===current)return 'Das neue Passwort muss sich vom bisherigen unterscheiden.';
  if(next.trim().toLowerCase()===String(email||'').toLowerCase())return 'Die E-Mail-Adresse ist als Passwort nicht erlaubt.';
  return '';
}

function requireDevice(req, res) {
  const token = String(req.headers['x-device-token'] || '').trim();
  const deviceCode = String(req.headers['x-device-code'] || '').trim();
  if (!token || !deviceCode) {
    json(res, 401, { ok:false, error:'Geräteauthentifizierung fehlt' });
    return null;
  }
  const row = db.prepare(`
    SELECT r.id AS register_id,r.device_code,b.business_id
    FROM device_tokens t
    JOIN registers r ON r.id=t.register_id
    JOIN branches b ON b.id=r.branch_id
    WHERE t.token_hash=? AND r.device_code=? AND t.revoked_at=''
  `).get(hashToken(token), deviceCode);
  if (!row) {
    json(res, 403, { ok:false, error:'Gerät nicht autorisiert' });
    return null;
  }
  return row;
}

function dashboardSummary(businessId) {
  const today = berlinParts(new Date()).day;
  const totals = db.prepare(`
    SELECT COALESCE(SUM(s.total_cents),0) total_cents,
           COALESCE(SUM(CASE WHEN s.transaction_type='SALE' THEN 1 ELSE 0 END),0) sale_count,
           COALESCE(SUM(CASE WHEN s.cash_portion_cents<>0 OR s.card_portion_cents<>0 THEN s.cash_portion_cents WHEN s.payment_method='CASH' THEN s.total_cents ELSE 0 END),0) cash_cents,
           COALESCE(SUM(CASE WHEN s.cash_portion_cents<>0 OR s.card_portion_cents<>0 THEN s.card_portion_cents WHEN s.payment_method='CARD' THEN s.total_cents ELSE 0 END),0) card_cents,
           COALESCE(AVG(CASE WHEN s.transaction_type='SALE' THEN s.total_cents END),0) avg_cents
    FROM cloud_sales s
    JOIN registers r ON r.id=s.register_id
    JOIN branches br ON br.id=r.branch_id
    WHERE br.business_id=? AND berlin_day(s.occurred_at)=?
  `).get(businessId, today);

  const recentSales = db.prepare(`
    SELECT s.receipt_number,s.pickup_number,s.occurred_at,s.payment_method,s.transaction_type,s.original_receipt_number,s.total_cents,s.operator_name,r.name register_name,br.name branch_name
    FROM cloud_sales s
    JOIN registers r ON r.id=s.register_id JOIN branches br ON br.id=r.branch_id
    WHERE br.business_id=? ORDER BY s.occurred_at DESC LIMIT 12
  `).all(businessId);

  const registers = db.prepare(`
    SELECT r.device_code,r.name,r.edition,r.last_seen_at,r.software_version,r.tse_status,r.printer_status,br.name branch_name
    FROM registers r JOIN branches br ON br.id=r.branch_id
    WHERE br.business_id=? ORDER BY br.name,r.name
  `).all(businessId);

  const hourly = db.prepare(`
    SELECT berlin_hour(s.occurred_at) hour, SUM(s.total_cents) total_cents
    FROM cloud_sales s JOIN registers r ON r.id=s.register_id JOIN branches br ON br.id=r.branch_id
    WHERE br.business_id=? AND berlin_day(s.occurred_at)=?
    GROUP BY berlin_hour(s.occurred_at) ORDER BY hour
  `).all(businessId, today);

  const lowStock = db.prepare(`
    SELECT st.name,st.quantity,st.min_stock_quantity,r.name register_name,br.name branch_name
    FROM stock_items st JOIN registers r ON r.id=st.register_id JOIN branches br ON br.id=r.branch_id
    WHERE br.business_id=? AND st.min_stock_quantity>0 AND st.quantity<=st.min_stock_quantity
    ORDER BY (st.quantity-st.min_stock_quantity) ASC,st.name LIMIT 10
  `).all(businessId);

  return { today, totals, recentSales, registers, hourly, lowStock };
}

function portalData(businessId, offset=0) {
  const summary = dashboardSummary(businessId);
  const sales = db.prepare(`
    SELECT s.id sale_id,s.receipt_number,s.pickup_number,s.occurred_at,s.payment_method,s.transaction_type,s.original_receipt_number,s.cash_portion_cents,s.card_portion_cents,s.subtotal_cents,s.discount_cents,s.total_cents,s.operator_name,s.item_count,
           r.name register_name,br.name branch_name
    FROM cloud_sales s
    JOIN registers r ON r.id=s.register_id JOIN branches br ON br.id=r.branch_id
    WHERE br.business_id=? ORDER BY s.occurred_at DESC,s.id DESC LIMIT 100 OFFSET ?
  `).all(businessId,offset);
  const stock = db.prepare(`
    SELECT st.product_key,st.name,st.sku,st.barcode,st.group_name,st.category_name,st.unit,st.price_cents,st.purchase_price_cents,st.min_stock_quantity,st.quantity,st.updated_at,r.name register_name,br.name branch_name
    FROM stock_items st JOIN registers r ON r.id=st.register_id JOIN branches br ON br.id=r.branch_id
    WHERE br.business_id=? ORDER BY st.name COLLATE NOCASE
  `).all(businessId);
  const zReports = db.prepare(`
    SELECT z.z_number,z.occurred_at,z.gross_cents,z.sale_count,r.name register_name,br.name branch_name
    FROM z_reports z JOIN registers r ON r.id=z.register_id JOIN branches br ON br.id=r.branch_id
    WHERE br.business_id=? ORDER BY z.occurred_at DESC LIMIT 250
  `).all(businessId);
  const users = db.prepare(`
    SELECT email,display_name,role,is_active,created_at FROM users WHERE business_id=? ORDER BY display_name COLLATE NOCASE
  `).all(businessId);
  const operators = db.prepare(`
    SELECT COALESCE(NULLIF(s.operator_name,''),'Unbekannt') operator_name,COUNT(*) sale_count,COALESCE(SUM(s.total_cents),0) total_cents
    FROM cloud_sales s JOIN registers r ON r.id=s.register_id JOIN branches br ON br.id=r.branch_id
    WHERE br.business_id=? GROUP BY COALESCE(NULLIF(s.operator_name,''),'Unbekannt') ORDER BY total_cents DESC
  `).all(businessId);
  const branches = db.prepare(`
    SELECT br.name,br.city,COUNT(r.id) register_count
    FROM branches br LEFT JOIN registers r ON r.branch_id=br.id
    WHERE br.business_id=? GROUP BY br.id,br.name,br.city ORDER BY br.name COLLATE NOCASE
  `).all(businessId);
  const saleTotal=db.prepare('SELECT COUNT(*) n FROM cloud_sales s JOIN registers r ON r.id=s.register_id JOIN branches b ON b.id=r.branch_id WHERE b.business_id=?').get(businessId).n;
  return {...summary, sales, stock, zReports, users, operators, branches, saleTotal, offset};
}

function receiptDetail(businessId, saleId) {
  const sale = db.prepare(`
    SELECT s.id sale_id,s.receipt_number,s.pickup_number,s.occurred_at,s.payment_method,s.subtotal_cents,s.discount_cents,s.total_cents,s.operator_name,s.item_count,
           r.name register_name,r.device_code,br.name branch_name,br.city
    FROM cloud_sales s
    JOIN registers r ON r.id=s.register_id
    JOIN branches br ON br.id=r.branch_id
    WHERE br.business_id=? AND s.id=?
  `).get(businessId, saleId);
  if (!sale) return null;
  const items = db.prepare(`
    SELECT position_no,product_key,name,quantity,unit_price_cents,line_total_cents,vat_rate
    FROM cloud_sale_items WHERE sale_id=? ORDER BY position_no,id
  `).all(saleId);
  return { ...sale, items };
}

function ingestEvent(registerId, event) {
  const seen = db.prepare('SELECT * FROM cloud_events WHERE register_id=? AND event_id=?').get(registerId, event.eventId);
  if (seen) {
    if(seen.event_type!==event.type || Date.parse(seen.occurred_at)!==Date.parse(event.occurredAt) || canonical(JSON.parse(seen.payload_json))!==canonical(event.payload))
      throw Object.assign(new Error('Ereignis-ID bereits mit anderem Inhalt gespeichert'),{statusCode:409});
    return 'duplicate';
  }
  const projectionId=`${registerId}:${event.eventId}`;
  const receivedAt = nowIso();
  db.prepare('INSERT INTO cloud_events(register_id,event_id,event_type,occurred_at,received_at,payload_json) VALUES(?,?,?,?,?,?)')
    .run(registerId, event.eventId, event.type, event.occurredAt, receivedAt, JSON.stringify(event.payload));

  const p = event.payload;
  if (event.type === 'heartbeat') {
    db.prepare('UPDATE registers SET last_seen_at=?,software_version=?,tse_status=?,printer_status=? WHERE id=?')
      .run(receivedAt, String(p.software_version || ''), String(p.tse_status || 'UNBEKANNT'), String(p.printer_status || 'UNBEKANNT'), registerId);
  } else if (event.type === 'sale.completed') {
    const rawItems = Array.isArray(p.items) ? p.items : [];
    const transactionType=String(p.transaction_type||'SALE').toUpperCase();
    const sign=transactionType==='SALE'?1:-1;
    const total=Number(p.total_cents||0);
    const cash=Number(p.cash_portion_cents??(p.payment_method==='CASH'?total:0));
    const card=Number(p.card_portion_cents??(p.payment_method==='CARD'?total:0));
    const saleRow = db.prepare(`INSERT INTO cloud_sales(register_id,event_id,receipt_number,pickup_number,occurred_at,payment_method,transaction_type,original_receipt_number,cash_portion_cents,card_portion_cents,subtotal_cents,discount_cents,total_cents,operator_name,item_count)
      VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?) RETURNING id`)
      .get(registerId, projectionId, Number(p.receipt_number || 0), Number(p.pickup_number || 0), event.occurredAt, String(p.payment_method || 'UNKNOWN').toUpperCase(),
        transactionType,Number(p.original_receipt_number||0),sign*cash,sign*card,
        sign*Number(p.subtotal_cents || p.total_cents || 0),sign*Number(p.discount_cents || 0),sign*total,String(p.operator_name || ''),Number(p.item_count || rawItems.length || 0));
    if (saleRow?.id && rawItems.length) {
      const itemStmt = db.prepare('INSERT INTO cloud_sale_items(sale_id,position_no,product_key,name,quantity,unit_price_cents,line_total_cents,vat_rate) VALUES(?,?,?,?,?,?,?,?)');
      const stockState = db.prepare('SELECT occurred_at FROM stock_sync_state WHERE register_id=?').get(registerId);
      // If a newer full snapshot was already accepted, this delayed booking is already reflected
      // in that snapshot and must not change stock a second time.
      const applyStockDelta = !stockState || Date.parse(stockState.occurred_at) <= Date.parse(event.occurredAt);
      const stockStmt = applyStockDelta
        ? db.prepare('UPDATE stock_items SET quantity=quantity-?,updated_at=? WHERE register_id=? AND product_key=?')
        : null;
      rawItems.forEach((item, idx) => {
        const qty = Number(item.quantity ?? item.qty ?? 1) || 0;
        const unit = Number(item.unit_price_cents ?? item.price_cents ?? 0) || 0;
        const line = Number(item.line_total_cents ?? item.total_cents ?? Math.round(qty * unit)) || 0;
        const productKey = String(item.product_key || item.article_number || item.sku || '');
        itemStmt.run(saleRow.id, Number(item.position_no || idx + 1), productKey, String(item.name || item.article_name || 'Artikel'), sign*qty, unit, sign*line, Number(item.vat_rate ?? item.tax_rate ?? 19));
      });
      // R179: the same component snapshot is a stock consumption for SALE and a
      // stock restoration for STORNO/RETURN. sign=-1 therefore adds quantity back.
      const consumption = Array.isArray(p.stock_consumption) ? p.stock_consumption : rawItems;
      if (stockStmt) consumption.forEach(item => {
        const productKey=String(item.product_key || item.article_number || item.sku || '');
        const qty=Number(item.quantity ?? item.qty ?? 1) || 0;
        if(productKey && qty>0) stockStmt.run(sign*qty,event.occurredAt,registerId,productKey);
      });
    }
    db.prepare('UPDATE registers SET last_seen_at=? WHERE id=?').run(receivedAt, registerId);
  } else if (event.type === 'cash.movement') {
    db.prepare('INSERT INTO cash_movements(register_id,event_id,occurred_at,movement_type,amount_cents,reason,actor) VALUES(?,?,?,?,?,?,?)')
      .run(registerId, projectionId, event.occurredAt, String(p.movement_type || 'UNKNOWN'), Number(p.amount_cents || 0), String(p.reason || ''), String(p.actor || ''));
  } else if (event.type === 'z.closed') {
    db.prepare('INSERT INTO z_reports(register_id,event_id,z_number,occurred_at,gross_cents,sale_count) VALUES(?,?,?,?,?,?)')
      .run(registerId, projectionId, String(p.z_number || ''), event.occurredAt, Number(p.gross_cents || 0), Number(p.sale_count || 0));
  } else if (event.type === 'stock.snapshot') {
    const items = p.items;
    const last=db.prepare('SELECT occurred_at FROM stock_sync_state WHERE register_id=?').get(registerId);
    if(last && Date.parse(last.occurred_at)>Date.parse(event.occurredAt))return 'accepted';
    db.prepare('DELETE FROM stock_items WHERE register_id=?').run(registerId);
    db.prepare('INSERT INTO stock_sync_state(register_id,occurred_at) VALUES(?,?) ON CONFLICT(register_id) DO UPDATE SET occurred_at=excluded.occurred_at').run(registerId,event.occurredAt);
    const stmt = db.prepare(`INSERT INTO stock_items(register_id,product_key,name,sku,barcode,group_name,category_name,unit,price_cents,purchase_price_cents,min_stock_quantity,quantity,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)
      ON CONFLICT(register_id,product_key) DO UPDATE SET name=excluded.name,sku=excluded.sku,barcode=excluded.barcode,group_name=excluded.group_name,category_name=excluded.category_name,unit=excluded.unit,price_cents=excluded.price_cents,purchase_price_cents=excluded.purchase_price_cents,min_stock_quantity=excluded.min_stock_quantity,quantity=excluded.quantity,updated_at=excluded.updated_at`);
    for (const item of items) stmt.run(registerId, String(item.product_key || ''), String(item.name || ''), String(item.sku || ''), String(item.barcode || ''), String(item.group_name || ''), String(item.category_name || ''), String(item.unit || 'Stück'), Number(item.price_cents || 0), Number(item.purchase_price_cents || 0), Number(item.min_stock_quantity || 0), Number(item.quantity || 0), event.occurredAt);
  }
  return 'accepted';
}

const MIME = {
  '.html':'text/html; charset=utf-8', '.css':'text/css; charset=utf-8', '.js':'application/javascript; charset=utf-8',
  '.svg':'image/svg+xml', '.png':'image/png', '.jpg':'image/jpeg', '.jpeg':'image/jpeg', '.ico':'image/x-icon', '.json':'application/json; charset=utf-8'
};
function serveStatic(req, res, pathname) {
  let rel = pathname === '/' ? '/index.html' : pathname;
  if (rel === '/login') rel = '/login.html';
  if (rel === '/portal') rel = '/portal.html';
  const file = path.normalize(path.join(PUBLIC, rel));
  if (!file.startsWith(PUBLIC)) return false;
  if (!fs.existsSync(file) || !fs.statSync(file).isFile()) return false;
  const body = fs.readFileSync(file);
  res.writeHead(200, {
    'Content-Type': MIME[path.extname(file).toLowerCase()] || 'application/octet-stream',
    'Content-Length': body.length,
    'Cache-Control': path.extname(file) === '.html' ? 'no-store' : 'public, max-age=300',
    'X-Content-Type-Options':'nosniff',
    'Referrer-Policy':'strict-origin-when-cross-origin'
  });
  res.end(body);
  return true;
}

const loginAttempts=new Map();
const trialAttempts=new Map();
function compareVersion(a,b){const pa=String(a).split(/[^0-9]+/).filter(Boolean).map(Number),pb=String(b).split(/[^0-9]+/).filter(Boolean).map(Number);for(let i=0;i<Math.max(pa.length,pb.length);i++){const d=(pa[i]||0)-(pb[i]||0);if(d)return d>0?1:-1;}return 0;}

// R120: the client IP. Behind the reverse proxy this deployment requires,
// req.socket.remoteAddress is the PROXY's address, so every user shared a
// single 100-attempt bucket. Only a trusted proxy may be believed, hence the
// explicit TOR_CLOUD_TRUST_PROXY opt-in: taking X-Forwarded-For from an
// untrusted peer would let an attacker forge a new "IP" per request and
// bypass the limit entirely.
const TRUST_PROXY=String(process.env.TOR_CLOUD_TRUST_PROXY||'').toLowerCase()==='true';
function clientIp(req){
  if(TRUST_PROXY){
    const forwarded=String(req.headers['x-forwarded-for']||'').split(',')[0].trim();
    if(forwarded)return forwarded;
  }
  return req.socket.remoteAddress||'unknown';
}

// R145: the receipt domain. Every response there says: do not index, do not
// cache, send no referrer (the link itself is the key), no framing.
function requestHostname(req){
  try{return new URL(`http://${String(req.headers.host||'')}`).hostname.toLowerCase();}catch{return '';}
}
function forwardedProto(req){
  return TRUST_PROXY?String(req.headers['x-forwarded-proto']||'').split(',')[0].trim().toLowerCase():'';
}
// Receipt links are credentials; they never go into a log line.
function redactUrl(value){return String(value||'').replace(/\/r\/[^/?#]+/g,'/r/[token]');}
function receiptHeaders(extra={}){
  return {
    'Cache-Control':'private, no-store',
    'Pragma':'no-cache',
    'X-Robots-Tag':'noindex, nofollow, noarchive, nosnippet',
    'Referrer-Policy':'no-referrer',
    'X-Content-Type-Options':'nosniff',
    'X-Frame-Options':'DENY',
    'Content-Security-Policy':"default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
    'Permissions-Policy':'camera=(), microphone=(), geolocation=(), payment=()',
    'Cross-Origin-Opener-Policy':'same-origin',
    'Cross-Origin-Resource-Policy':'same-origin',
    ...(RECEIPT_HTTPS?{'Strict-Transport-Security':'max-age=31536000; includeSubDomains'}:{}),
    ...extra
  };
}
function receiptSend(req,res,status,body,type,extra={}){
  const buf=Buffer.isBuffer(body)?body:Buffer.from(body);
  res.writeHead(status,receiptHeaders({'Content-Type':type,'Content-Length':buf.length,...extra}));
  res.end(req.method==='HEAD'?undefined:buf);
}
// A valid link always works. Only an address that keeps asking for links that
// do not exist is slowed down - guessing a 256-bit token is hopeless anyway,
// this just keeps such traffic cheap and visible.
const receiptMisses=new Map();
const RECEIPT_MISS_LIMIT=60;
const RECEIPT_MISS_WINDOW_MS=10*60*1000;
function receiptMissLimited(req){
  const now=Date.now(),key=clientIp(req);
  let entry=receiptMisses.get(key);
  if(!entry||entry.until<=now)entry={count:0,until:now+RECEIPT_MISS_WINDOW_MS};
  entry.count++;
  receiptMisses.delete(key);
  receiptMisses.set(key,entry);
  for(const oldest of receiptMisses.keys()){if(receiptMisses.size<=50000)break;receiptMisses.delete(oldest);}
  return entry.count>RECEIPT_MISS_LIMIT;
}
function handleReceiptHost(req,res,url){
  const pathname=url.pathname;
  if(req.method!=='GET'&&req.method!=='HEAD')return receiptSend(req,res,405,'Methode nicht erlaubt','text/plain; charset=utf-8',{Allow:'GET, HEAD'});
  // Deliberately no Disallow: a search engine only obeys the noindex on every
  // response if it may fetch the page. A receipt link someone posted publicly
  // must drop out of the index entirely, not linger there as a bare URL.
  if(pathname==='/robots.txt')return receiptSend(req,res,200,'User-agent: *\nAllow: /\n','text/plain; charset=utf-8');
  const asset=Object.hasOwn(RECEIPT_ASSETS,pathname)?RECEIPT_ASSETS[pathname]:null;
  if(asset)return receiptSend(req,res,200,asset.body,asset.type,{'Cache-Control':'public, max-age=3600'});
  if(pathname==='/')return receiptSend(req,res,200,renderHomePage(RECEIPT_LINKS),'text/html; charset=utf-8');
  const match=/^\/r\/([^/]+)(\/pdf)?$/.exec(pathname);
  const row=match&&/^[A-Za-z0-9_-]{43}$/.test(match[1])
    ?db.prepare(`SELECT document_json,expires_at${match[2]?',pdf':''} FROM public_receipts WHERE token_hash=? AND expires_at>?`).get(hashToken(match[1]),nowIso())
    :null;
  if(!row){
    if(match&&receiptMissLimited(req))return receiptSend(req,res,429,renderNotFoundPage(RECEIPT_LINKS),'text/html; charset=utf-8',{'Retry-After':String(RECEIPT_MISS_WINDOW_MS/1000)});
    return receiptSend(req,res,404,renderNotFoundPage(RECEIPT_LINKS),'text/html; charset=utf-8');
  }
  const doc=JSON.parse(row.document_json);
  if(match[2]){
    const name=`Kassenbon-${String(doc.receipt_number).replace(/[^A-Za-z0-9-]/g,'')||'TOR'}.pdf`;
    return receiptSend(req,res,200,Buffer.from(row.pdf),'application/pdf',{'Content-Disposition':`attachment; filename="${name}"`});
  }
  return receiptSend(req,res,200,renderReceiptPage(doc,{token:match[1],expiresAt:row.expires_at,links:RECEIPT_LINKS}),'text/html; charset=utf-8');
}

const MAX_TRACKED_LOGIN_KEYS=50000;
const TRIAL_LIMIT_WINDOW_MS=15*60*1000;
const TRIAL_LIMIT_PER_IP=60;
function trialLimited(req){
  const now=Date.now(),key=clientIp(req);
  let entry=trialAttempts.get(key);
  if(!entry||entry.until<=now)entry={count:0,until:now+TRIAL_LIMIT_WINDOW_MS};
  entry.count++;
  trialAttempts.delete(key);
  trialAttempts.set(key,entry);
  for(const oldest of trialAttempts.keys()){if(trialAttempts.size<=50000)break;trialAttempts.delete(oldest);}
  return entry.count>TRIAL_LIMIT_PER_IP;
}

function loginLimited(req,email){
  const now=Date.now();
  for(const [k,v] of loginAttempts)if(v.until<now)loginAttempts.delete(k);

  // R120: this used to `return true` once the map exceeded 10000 entries,
  // which locked out EVERY user at once - an attacker only had to submit
  // enough distinct e-mail addresses to fill it. Now the oldest entries are
  // dropped instead, so the limiter degrades rather than becoming a global
  // denial of service against legitimate users.
  if(loginAttempts.size>MAX_TRACKED_LOGIN_KEYS){
    const excess=loginAttempts.size-MAX_TRACKED_LOGIN_KEYS;
    let dropped=0;
    for(const key of loginAttempts.keys()){
      loginAttempts.delete(key);
      if(++dropped>=excess)break;
    }
  }

  const keys=['ip:'+clientIp(req),'mail:'+email];
  let limited=false;
  for(const key of keys){const v=loginAttempts.get(key)||{count:0,until:now+15*60*1000};v.count++;loginAttempts.set(key,v);if(v.count>(key.startsWith('ip:')?100:10))limited=true;}
  return limited;
}
async function handler(req, res) {
  try {
    const url = new URL(req.url, `http://${req.headers.host || 'localhost'}`);
    const pathname = url.pathname;
    // R145: behind the proxy, a request that came in over plain HTTP goes to
    // HTTPS before anything else (Caddy redirects already; this is the second
    // line). The target is always the configured origin, never the Host header.
    if(forwardedProto(req)==='http'){
      const onReceipt=!!RECEIPT_HOST&&requestHostname(req)===RECEIPT_HOST;
      const origin=onReceipt?(RECEIPT_HTTPS?RECEIPT_ORIGIN:''):(CLOUD_PUBLIC_URL.startsWith('https://')?new URL(CLOUD_PUBLIC_URL).origin:'');
      if(origin){res.writeHead(308,{Location:origin+pathname,'Content-Length':0,'Cache-Control':'no-store'});return res.end();}
    }
    // R145: the receipt domain serves receipts and nothing else; no receipt is
    // served on any other host.
    if(RECEIPT_HOST&&requestHostname(req)===RECEIPT_HOST)return handleReceiptHost(req,res,url);
    res.setHeader('X-Content-Type-Options','nosniff');
    res.setHeader('Content-Security-Policy',"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'");
    if(req.method==='POST' && !pathname.startsWith('/api/v1/devices/')){
      const origin=req.headers.origin;
      if(req.headers['sec-fetch-site']==='cross-site' || (origin && new URL(origin).host!==req.headers.host)) return json(res,403,{ok:false,error:'Fremder Ursprung nicht erlaubt'});
      if(!String(req.headers['content-type']||'').startsWith('application/json'))return json(res,415,{ok:false,error:'JSON erforderlich'});
    }


    if (req.method === 'GET' && pathname === '/api/health') return json(res, 200, {ok:true, service:'TOR POS Cloud', version:CLOUD_VERSION, demo:DEMO, google_oauth_configured:GOOGLE_OAUTH_READY, managed_mail_configured:TOR_MAIL_READY, digital_receipts:!!RECEIPT_ORIGIN, time:nowIso()});

    // R155: TOR Mail is a fixed server-side sender. A till may provide exactly one
    // recipient, subject, body and PDF/CSV attachments. Device authentication, size
    // limits and per-register quotas prevent this endpoint from becoming an open relay.
    if(req.method==='POST' && pathname==='/api/v1/devices/mail/send'){
      const device=requireDevice(req,res);if(!device)return;
      if(!TOR_MAIL_READY)return json(res,503,{ok:false,error:'TOR Mail ist auf dem Cloud-Server noch nicht konfiguriert.'});
      const now=Date.now();
      const hourAgo=new Date(now-60*60*1000).toISOString();
      const dayAgo=new Date(now-24*60*60*1000).toISOString();
      const hourly=Number(db.prepare('SELECT COUNT(*) c FROM managed_mail_log WHERE register_id=? AND created_at>=?').get(device.register_id,hourAgo).c);
      const daily=Number(db.prepare('SELECT COUNT(*) c FROM managed_mail_log WHERE register_id=? AND created_at>=?').get(device.register_id,dayAgo).c);
      if(hourly>=TOR_MAIL_HOURLY_LIMIT||daily>=TOR_MAIL_DAILY_LIMIT)
        return json(res,429,{ok:false,error:'TOR Mail Versandlimit erreicht. Bitte später erneut versuchen.'},{'Retry-After':'3600'});

      const body=await readJson(req,12*1024*1024);
      const mail=normalizeManagedMailPayload(body);
      const created=nowIso();
      const log=db.prepare(`INSERT INTO managed_mail_log(register_id,created_at,recipient_hash,subject_hash,attachment_count,total_bytes,status)
                            VALUES(?,?,?,?,?,?,'SENDING') RETURNING id`)
        .get(device.register_id,created,hashToken(mail.recipient.toLowerCase()),hashToken(mail.subject),mail.attachments.length,mail.totalBytes);
      try{
        await sendManagedMail(TOR_MAIL_CONFIG,mail);
        db.prepare("UPDATE managed_mail_log SET status='SENT',last_error='' WHERE id=?").run(log.id);
        return json(res,200,{ok:true,sender:TOR_MAIL_CONFIG.from});
      }catch(err){
        const safe=String(err?.message||'Unbekannter SMTP-Fehler').replace(/[\r\n]+/g,' ').slice(0,500);
        db.prepare("UPDATE managed_mail_log SET status='FAILED',last_error=? WHERE id=?").run(safe,log.id);
        throw Object.assign(new Error('TOR Mail Versand fehlgeschlagen: '+safe),{statusCode:502});
      }
    }

    // R62: QR pairing + Google OAuth. The POS authenticates to TOR Cloud with its existing
    // device credentials. The phone only receives a one-time claim URL; the Gmail password
    // never enters TOR POS. Report PDFs are later sent directly from the POS via Gmail API.
    if(req.method==='POST' && pathname==='/api/v1/devices/google-oauth/pair/start'){
      const device=requireDevice(req,res);if(!device)return;ensureGoogleOAuthReady();cleanupGooglePairs();
      const existing=db.prepare("SELECT id,account_email FROM google_oauth_connections WHERE register_id=? AND revoked_at='' ORDER BY created_at DESC LIMIT 1").get(device.register_id);
      if(existing)return json(res,409,{ok:false,code:'ALREADY_CONNECTED',error:`Google ist bereits verbunden (${existing.account_email}). Zuerst Verbindung trennen.`});
      const pairId=randomId(14),pairSecret=randomId(32),claimToken=randomId(20),state=randomId(32),verifier=randomId(48);
      const challenge=crypto.createHash('sha256').update(verifier).digest('base64url');
      const created=nowIso(),expires=new Date(Date.now()+GOOGLE_PAIR_TTL_MS).toISOString();
      db.prepare(`INSERT INTO google_oauth_pairs(id,register_id,pair_secret_hash,claim_token_hash,state_hash,state_protected,pkce_verifier_protected,status,created_at,expires_at)
                  VALUES(?,?,?,?,?,?,?,?,?,?)`).run(pairId,device.register_id,hashToken(pairSecret),hashToken(claimToken),hashToken(state),protectGoogleSecret(state),protectGoogleSecret(verifier),'PENDING',created,expires);
      const displayUrl=`${CLOUD_PUBLIC_URL}/g/${encodeURIComponent(pairId)}?t=${encodeURIComponent(claimToken)}`;
      let matrix;try{matrix=makeQrV6L(displayUrl);}catch(err){db.prepare('DELETE FROM google_oauth_pairs WHERE id=?').run(pairId);throw Object.assign(new Error('Google-QR-Link ist zu lang für den eingebauten QR-Code. TOR_CLOUD_PUBLIC_URL kürzer wählen.'),{statusCode:500});}
      return json(res,200,{ok:true,pair_id:pairId,pair_secret:pairSecret,display_url:displayUrl,expires_at:expires,qr_matrix:matrix});
    }

    if(req.method==='POST' && pathname==='/api/v1/devices/google-oauth/pair/status'){
      const device=requireDevice(req,res);if(!device)return;cleanupGooglePairs();const body=await readJson(req);
      const pairId=String(body.pair_id||''),pairSecret=String(body.pair_secret||'');
      const row=db.prepare('SELECT * FROM google_oauth_pairs WHERE id=? AND register_id=?').get(pairId,device.register_id);
      if(!row||!pairSecret||!timingSafeEqualText(row.pair_secret_hash,hashToken(pairSecret)))return json(res,404,{ok:false,error:'Google-Anmeldung nicht gefunden oder abgelaufen.'});
      if(row.status==='COMPLETE')return json(res,200,{ok:true,status:'COMPLETE',connection_id:row.connection_id,account_email:row.account_email,expires_at:row.expires_at});
      if(row.status==='ERROR')return json(res,200,{ok:true,status:'ERROR',error:row.error||'Google-Anmeldung fehlgeschlagen.',expires_at:row.expires_at});
      return json(res,200,{ok:true,status:'PENDING',expires_at:row.expires_at});
    }

    if(req.method==='POST' && pathname==='/api/v1/devices/google-oauth/pair/ack'){
      const device=requireDevice(req,res);if(!device)return;const body=await readJson(req);
      const row=db.prepare('SELECT * FROM google_oauth_pairs WHERE id=? AND register_id=?').get(String(body.pair_id||''),device.register_id);
      const pairSecret=String(body.pair_secret||'');
      if(!row||!pairSecret||!timingSafeEqualText(row.pair_secret_hash,hashToken(pairSecret)))return json(res,404,{ok:false,error:'Google-Anmeldung nicht gefunden.'});
      if(row.status!=='COMPLETE')return json(res,409,{ok:false,error:'Google-Anmeldung ist noch nicht abgeschlossen.'});
      db.prepare('DELETE FROM google_oauth_pairs WHERE id=?').run(row.id);
      return json(res,200,{ok:true});
    }

    if(req.method==='POST' && pathname==='/api/v1/devices/google-oauth/access-token'){
      const device=requireDevice(req,res);if(!device)return;ensureGoogleOAuthReady();const body=await readJson(req);
      const connectionId=String(body.connection_id||'');
      const row=db.prepare("SELECT * FROM google_oauth_connections WHERE id=? AND register_id=? AND revoked_at=''").get(connectionId,device.register_id);
      if(!row)return json(res,401,{ok:false,code:'REAUTH_REQUIRED',error:'Google-Verbindung fehlt oder wurde getrennt.'});
      try{
        const token=await refreshGoogleAccess(unprotectGoogleSecret(row.refresh_token_protected));
        db.prepare("UPDATE google_oauth_connections SET updated_at=?,last_error='' WHERE id=?").run(nowIso(),row.id);
        return json(res,200,{ok:true,access_token:String(token.access_token),expires_in:Number(token.expires_in||3600),token_type:String(token.token_type||'Bearer'),account_email:row.account_email});
      }catch(err){
        const detail=[err.googleError,err.googleDescription].filter(Boolean).join(' · ');
        db.prepare('UPDATE google_oauth_connections SET last_error=?,updated_at=? WHERE id=?').run((detail||err.message).slice(0,500),nowIso(),row.id);
        const reauth=err.googleError==='invalid_grant';
        return json(res,reauth?401:502,{ok:false,code:reauth?'REAUTH_REQUIRED':'GOOGLE_TOKEN_ERROR',error:reauth?'Google-Zugriff wurde widerrufen oder ist abgelaufen. Bitte erneut mit Google anmelden.':'Google-Zugriffstoken konnte nicht erneuert werden.'});
      }
    }

    if(req.method==='POST' && pathname==='/api/v1/devices/google-oauth/disconnect'){
      const device=requireDevice(req,res);if(!device)return;const body=await readJson(req);const connectionId=String(body.connection_id||'');
      const row=db.prepare("SELECT * FROM google_oauth_connections WHERE id=? AND register_id=? AND revoked_at=''").get(connectionId,device.register_id);
      if(!row)return json(res,200,{ok:true,already_disconnected:true});
      const revoked=await revokeGoogleRefresh(unprotectGoogleSecret(row.refresh_token_protected));
      if(!revoked)return json(res,502,{ok:false,error:'Google-Verbindung konnte online nicht widerrufen werden. Bitte Internetverbindung prüfen und erneut versuchen.'});
      db.prepare("UPDATE google_oauth_connections SET revoked_at=?,refresh_token_protected='',updated_at=?,last_error='' WHERE id=?").run(nowIso(),nowIso(),row.id);
      return json(res,200,{ok:true});
    }

    const pairPageMatch=pathname.match(/^\/g\/([A-Za-z0-9_-]+)$/);
    if(req.method==='GET' && pairPageMatch){
      const token=String(url.searchParams.get('t')||''),row=googlePairRow(pairPageMatch[1],token);
      if(!row)return html(res,404,'<!doctype html><meta charset="utf-8"><title>TOR POS</title><h1>Link ungültig oder abgelaufen</h1><p>Bitte an der Kasse einen neuen QR-Code erzeugen.</p>');
      if(row.status==='COMPLETE')return html(res,200,'<!doctype html><meta charset="utf-8"><title>TOR POS</title><main style="font-family:system-ui;max-width:620px;margin:70px auto;padding:24px"><h1>Google verbunden ✓</h1><p>Sie können diese Seite schließen und zur Kasse zurückkehren.</p></main>');
      if(row.status==='ERROR')return html(res,400,`<!doctype html><meta charset="utf-8"><title>TOR POS</title><main style="font-family:system-ui;max-width:620px;margin:70px auto;padding:24px"><h1>Anmeldung fehlgeschlagen</h1><p>${htmlEscape(row.error)}</p><p>Bitte an der Kasse einen neuen QR-Code erzeugen.</p></main>`);
      const start=`/g/${encodeURIComponent(row.id)}/start?t=${encodeURIComponent(token)}`;
      return html(res,200,`<!doctype html><html lang="de"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>TOR POS · Mit Google anmelden</title><style>body{font-family:system-ui;background:#0d1b2a;color:#eef6ff;margin:0}.card{max-width:620px;margin:7vh auto;background:#14283b;padding:28px;border-radius:18px}.btn{display:block;background:#fff;color:#111;text-decoration:none;text-align:center;font-weight:700;padding:16px;border-radius:12px;margin-top:22px}.small{opacity:.75;line-height:1.5}</style></head><body><main class="card"><h1>TOR POS mit Google verbinden</h1><p>TOR POS fordert ausschließlich die Berechtigung an, E-Mails in Ihrem Namen zu senden. Das Programm erhält Ihr Google-Passwort nicht.</p><a class="btn" href="${htmlEscape(start)}">MIT GOOGLE ANMELDEN</a><p class="small">Der QR-Link ist nur wenige Minuten gültig. Nach erfolgreicher Anmeldung können Sie dieses Fenster schließen.</p></main></body></html>`);
    }

    const pairStartMatch=pathname.match(/^\/g\/([A-Za-z0-9_-]+)\/start$/);
    if(req.method==='GET' && pairStartMatch){
      ensureGoogleOAuthReady();const token=String(url.searchParams.get('t')||''),row=googlePairRow(pairStartMatch[1],token);
      if(!row||row.status!=='PENDING')return html(res,404,'<!doctype html><meta charset="utf-8"><title>TOR POS</title><h1>Link ungültig oder nicht mehr aktiv.</h1>');
      const state=unprotectGoogleSecret(row.state_protected),verifier=unprotectGoogleSecret(row.pkce_verifier_protected);
      const challenge=crypto.createHash('sha256').update(verifier).digest('base64url');
      const auth=new URL('https://accounts.google.com/o/oauth2/v2/auth');
      auth.search=new URLSearchParams({client_id:GOOGLE_OAUTH_CLIENT_ID,redirect_uri:GOOGLE_REDIRECT_URI,response_type:'code',scope:GOOGLE_SCOPE,access_type:'offline',prompt:'consent',include_granted_scopes:'true',state,code_challenge:challenge,code_challenge_method:'S256'}).toString();
      res.writeHead(302,{Location:auth.toString(),'Cache-Control':'no-store','Referrer-Policy':'no-referrer'});return res.end();
    }

    if(req.method==='GET' && pathname==='/google/oauth/callback'){
      ensureGoogleOAuthReady();cleanupGooglePairs();const state=String(url.searchParams.get('state')||'');
      const row=state?db.prepare('SELECT * FROM google_oauth_pairs WHERE state_hash=?').get(hashToken(state)):null;
      if(!row)return html(res,400,'<!doctype html><meta charset="utf-8"><title>TOR POS</title><h1>Anmeldung abgelaufen oder ungültig.</h1>');
      if(row.status!=='PENDING')return html(res,409,'<!doctype html><meta charset="utf-8"><title>TOR POS</title><h1>Diese Google-Anmeldung wurde bereits abgeschlossen oder beendet.</h1>');
      const oauthError=String(url.searchParams.get('error')||'');
      if(oauthError){const msg=oauthError==='access_denied'?'Google-Berechtigung wurde nicht erteilt.':`Google OAuth: ${oauthError}`;db.prepare("UPDATE google_oauth_pairs SET status='ERROR',error=? WHERE id=?").run(msg,row.id);return html(res,400,`<!doctype html><meta charset="utf-8"><title>TOR POS</title><main style="font-family:system-ui;max-width:620px;margin:70px auto"><h1>Anmeldung abgebrochen</h1><p>${htmlEscape(msg)}</p></main>`);}
      const code=String(url.searchParams.get('code')||'');if(!code)return html(res,400,'<!doctype html><meta charset="utf-8"><title>TOR POS</title><h1>Google hat keinen Autorisierungscode geliefert.</h1>');
      try{
        const verifier=unprotectGoogleSecret(row.pkce_verifier_protected);
        const token=await googleTokenRequest({client_id:GOOGLE_OAUTH_CLIENT_ID,client_secret:GOOGLE_OAUTH_CLIENT_SECRET,code,code_verifier:verifier,redirect_uri:GOOGLE_REDIRECT_URI,grant_type:'authorization_code'});
        if(!token.refresh_token)throw Object.assign(new Error('Google hat kein Refresh-Token geliefert. Bitte Verbindung erneut herstellen und Einwilligung bestätigen.'),{statusCode:502});
        const scope=String(token.scope||'');if(!scope.split(/\s+/).includes('https://www.googleapis.com/auth/gmail.send'))throw Object.assign(new Error('Die Berechtigung gmail.send wurde nicht erteilt.'),{statusCode:502});
        const info=await googleUserInfo(String(token.access_token||''));const email=String(info.email||'').trim();if(!email)throw Object.assign(new Error('Google-Konto-E-Mail konnte nicht ermittelt werden.'),{statusCode:502});
        const active=db.prepare("SELECT id FROM google_oauth_connections WHERE register_id=? AND revoked_at='' LIMIT 1").get(row.register_id);
        if(active)throw Object.assign(new Error('Für diese Kasse ist bereits ein Google-Konto verbunden.'),{statusCode:409});
        const connectionId=randomId(18),now=nowIso();db.exec('BEGIN IMMEDIATE');
        try{
          db.prepare('INSERT INTO google_oauth_connections(id,register_id,account_email,refresh_token_protected,scope,created_at,updated_at) VALUES(?,?,?,?,?,?,?)').run(connectionId,row.register_id,email,protectGoogleSecret(String(token.refresh_token)),scope,now,now);
          db.prepare("UPDATE google_oauth_pairs SET status='COMPLETE',connection_id=?,account_email=?,error='' WHERE id=?").run(connectionId,email,row.id);db.exec('COMMIT');
        }catch(e){db.exec('ROLLBACK');throw e;}
        return html(res,200,`<!doctype html><html lang="de"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>TOR POS</title><main style="font-family:system-ui;max-width:620px;margin:70px auto;padding:24px"><h1>Google erfolgreich verbunden ✓</h1><p>${htmlEscape(email)}</p><p>TOR POS darf E-Mails senden. Sie können diese Seite jetzt schließen und zur Kasse zurückkehren.</p></main></html>`);
      }catch(err){const msg=(err.message||'Google-Anmeldung fehlgeschlagen.').slice(0,600);db.prepare("UPDATE google_oauth_pairs SET status='ERROR',error=? WHERE id=?").run(msg,row.id);return html(res,502,`<!doctype html><meta charset="utf-8"><title>TOR POS</title><main style="font-family:system-ui;max-width:620px;margin:70px auto;padding:24px"><h1>Google-Verbindung fehlgeschlagen</h1><p>${htmlEscape(msg)}</p></main>`);}
    }

    // Public 7-day desktop trial. The random Trial-ID lives outside the
    // normal per-user application data so a regular uninstall/reinstall keeps
    // the original server-side first_seen_at/expires_at pair.
    if(req.method==='POST' && pathname==='/api/v1/trial/activate'){
      if(trialLimited(req))return json(res,429,{ok:false,error:'Zu viele Demo-Aktivierungen. Bitte später erneut versuchen.'});
      const body=await readJson(req);
      const trialId=String(body.trial_id||'').trim().toUpperCase();
      const version=String(body.version||'').trim().slice(0,80);
      const revision=String(body.revision||'').trim().slice(0,80);
      if(!/^[A-F0-9]{64}$/.test(trialId))return json(res,400,{ok:false,error:'Ungültige Demo-ID.'});

      const serverTime=nowIso();
      let row=db.prepare('SELECT * FROM trial_ids WHERE trial_id=?').get(trialId);
      let reused=true;
      if(!row){
        reused=false;
        const expiresAt=new Date(Date.parse(serverTime)+7*24*60*60*1000).toISOString();
        db.prepare('INSERT INTO trial_ids(trial_id,first_seen_at,expires_at,last_seen_at,activation_count,last_version,last_revision) VALUES(?,?,?,?,1,?,?)')
          .run(trialId,serverTime,expiresAt,serverTime,version,revision);
        row=db.prepare('SELECT * FROM trial_ids WHERE trial_id=?').get(trialId);
      }else{
        db.prepare('UPDATE trial_ids SET last_seen_at=?,activation_count=activation_count+1,last_version=?,last_revision=? WHERE trial_id=?')
          .run(serverTime,version,revision,trialId);
      }

      const state=Date.parse(row.expires_at)>Date.parse(serverTime)?'ACTIVE':'EXPIRED';
      return json(res,200,{
        ok:true,
        state,
        server_time:serverTime,
        started_at:row.first_seen_at,
        expires_at:row.expires_at,
        reused
      });
    }

    if(req.method==='GET' && pathname==='/api/v1/trial/download'){
      const manifestPath=path.join(UPDATES,'trial-manifest.json');
      if(!fs.existsSync(manifestPath))return json(res,503,{ok:false,error:'TOR POS Demo Setup ist noch nicht veröffentlicht.'});
      let m;try{m=JSON.parse(fs.readFileSync(manifestPath,'utf8'));}catch{return json(res,503,{ok:false,error:'Demo-Manifest ist ungültig.'});}
      const file=path.basename(String(m.filename||''));
      if(!m.enabled||!file||!/^[A-Fa-f0-9]{64}$/.test(String(m.sha256||'')))return json(res,503,{ok:false,error:'TOR POS Demo Setup ist noch nicht freigegeben.'});
      const origin=CLOUD_PUBLIC_URL?new URL(CLOUD_PUBLIC_URL.endsWith('/')?CLOUD_PUBLIC_URL:CLOUD_PUBLIC_URL+'/'):new URL(`${COOKIE_SECURE?'https':'http'}://${req.headers.host}`);
      res.writeHead(302,{Location:new URL(`/trial/${encodeURIComponent(file)}`,origin).toString(),'Cache-Control':'no-store'});
      return res.end();
    }

    if(req.method==='GET' && pathname.startsWith('/trial/')){
      const manifestPath=path.join(UPDATES,'trial-manifest.json');
      if(!fs.existsSync(manifestPath))return text(res,404,'Nicht gefunden');
      let m;try{m=JSON.parse(fs.readFileSync(manifestPath,'utf8'));}catch{return text(res,404,'Nicht gefunden');}
      const expected=path.basename(String(m.filename||'')),requested=decodeURIComponent(pathname.slice('/trial/'.length));
      if(!m.enabled||requested!==expected||requested!==path.basename(requested))return text(res,404,'Nicht gefunden');
      const full=path.join(UPDATES,expected);
      if(!fs.existsSync(full))return text(res,404,'Nicht gefunden');
      const served=crypto.createHash('sha256').update(fs.readFileSync(full)).digest('hex').toUpperCase();
      if(served!==String(m.sha256||'').toUpperCase()){
        console.error('Demo download refused: sha256 of',expected,'does not match manifest');
        return text(res,409,'Demo-Datei stimmt nicht mit dem Manifest überein.');
      }
      const stat=fs.statSync(full);
      res.writeHead(200,{
        'Content-Type':'application/vnd.microsoft.portable-executable',
        'Content-Length':stat.size,
        'Content-Disposition':'attachment; filename="TOR-POS-Demo-Setup.exe"',
        'Cache-Control':'no-store',
        'X-Content-Type-Options':'nosniff'
      });
      const stream=fs.createReadStream(full);
      stream.on('error',err=>{console.error('Demo stream failed:',err.message);res.destroy();});
      return stream.pipe(res);
    }

    if(req.method==='GET' && pathname==='/api/v1/updates/check'){
      const current=String(url.searchParams.get('version')||'0'),edition=String(url.searchParams.get('edition')||'KIOSK').toUpperCase();
      const manifestPath=path.join(UPDATES,'manifest.json');if(!fs.existsSync(manifestPath))return json(res,200,{ok:true,update_available:false});
      let m;try{m=JSON.parse(fs.readFileSync(manifestPath,'utf8'));}catch{return json(res,503,{ok:false,error:'Update-Manifest ist ungültig.'});}
      const allowed=Array.isArray(m.editions)?m.editions.map(x=>String(x).toUpperCase()):['KIOSK','IMBISS'];
      if(!m.enabled || !allowed.includes(edition) || compareVersion(String(m.version||'0'),current)<=0)return json(res,200,{ok:true,update_available:false});
      const file=path.basename(String(m.filename||''));const full=path.join(UPDATES,file);
      if(!file || !fs.existsSync(full) || !/^[A-Fa-f0-9]{64}$/.test(String(m.sha256||'')))return json(res,503,{ok:false,error:'Update-Datei/Prüfsumme nicht bereit.'});
      const publicRoot=String(process.env.TOR_CLOUD_PUBLIC_URL||'').trim();let origin;
      if(publicRoot){origin=new URL(publicRoot.endsWith('/')?publicRoot:publicRoot+'/');}
      else{const scheme=COOKIE_SECURE?'https':'http';origin=new URL(`${scheme}://${req.headers.host}`);}
      const downloadUrl=new URL(`/updates/${encodeURIComponent(file)}`,origin).toString();
      return json(res,200,{ok:true,update_available:true,manifest:{version:String(m.version),revision:String(m.revision||m.version),published_at:String(m.published_at||''),mandatory:!!m.mandatory,download_url:downloadUrl,sha256:String(m.sha256).toUpperCase(),signer_thumbprint:String(m.signer_thumbprint||''),release_notes:String(m.release_notes||'')}});
    }

    if(req.method==='GET' && pathname.startsWith('/updates/')){
      const manifestPath=path.join(UPDATES,'manifest.json');if(!fs.existsSync(manifestPath))return text(res,404,'Nicht gefunden');
      let m;try{m=JSON.parse(fs.readFileSync(manifestPath,'utf8'));}catch{return text(res,404,'Nicht gefunden');}
      const expected=path.basename(String(m.filename||'')),requested=decodeURIComponent(pathname.slice('/updates/'.length));
      if(!m.enabled || requested!==expected || requested!==path.basename(requested))return text(res,404,'Nicht gefunden');
      const full=path.join(UPDATES,expected);if(!fs.existsSync(full))return text(res,404,'Nicht gefunden');const stat=fs.statSync(full);
      // R120: verify the bytes actually being served against the manifest
      // hash. The publishing script checks Authenticode, but nothing checked
      // the file again at serve time - so anything that could write into the
      // updates directory bypassed that gate completely. Cheap enough here:
      // this endpoint is hit once per update, not per request.
      const served=crypto.createHash('sha256').update(fs.readFileSync(full)).digest('hex').toUpperCase();
      if(served!==String(m.sha256||'').toUpperCase()){
        console.error('Update refused: sha256 of',expected,'does not match manifest');
        return text(res,409,'Update-Datei stimmt nicht mit dem Manifest überein.');
      }
      res.writeHead(200,{'Content-Type':'application/vnd.microsoft.portable-executable','Content-Length':stat.size,'Content-Disposition':`attachment; filename="${expected}"`,'Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});
      // R120: an aborted download used to raise an unhandled 'error' on the
      // stream and take the whole server process down with it.
      const stream=fs.createReadStream(full);
      stream.on('error',err=>{console.error('Update stream failed:',err.message);res.destroy();});
      return stream.pipe(res);
    }

    if (req.method === 'POST' && pathname === '/api/login') {
      const body=await readJson(req);const email=String(body.email||'').trim().toLowerCase();const password=String(body.password||'');
      if(loginLimited(req,email))return json(res,429,{ok:false,error:'Zu viele Anmeldeversuche. Bitte 15 Minuten warten.'});
      const user=db.prepare('SELECT * FROM users WHERE email=? AND is_active=1').get(email);
      if(!user || !await verifyPassword(password,user.password_salt,user.password_hash))return json(res,401,{ok:false,error:'E-Mail oder Passwort ist falsch.'});
      if(user.totp_enabled){
        const challenge=randomId(24),expires=new Date(Date.now()+5*60*1000).toISOString();
        db.prepare('DELETE FROM login_challenges WHERE user_id=? OR expires_at<=?').run(user.id,nowIso());
        db.prepare('INSERT INTO login_challenges(id,user_id,created_at,expires_at) VALUES(?,?,?,?)').run(challenge,user.id,nowIso(),expires);
        return json(res,200,{ok:true,requires_2fa:true,challenge});
      }
      createSession(res,user);
      return json(res,200,{ok:true,requires_2fa:false,user:{display_name:user.display_name,role:user.role},two_factor_setup_required:REQUIRE_OWNER_2FA&&user.role==='OWNER'});
    }

    if(req.method==='POST' && pathname==='/api/login/2fa'){
      const body=await readJson(req),challenge=String(body.challenge||''),code=String(body.code||'').trim().toUpperCase();
      const row=db.prepare(`SELECT c.id,c.user_id,c.expires_at,c.attempts,u.* FROM login_challenges c JOIN users u ON u.id=c.user_id WHERE c.id=? AND u.is_active=1`).get(challenge);
      if(!row || Date.parse(row.expires_at)<=Date.now()){if(row)db.prepare('DELETE FROM login_challenges WHERE id=?').run(challenge);return json(res,401,{ok:false,error:'2FA-Anmeldung ist abgelaufen. Bitte erneut anmelden.'});}
      if(row.attempts>=5){db.prepare('DELETE FROM login_challenges WHERE id=?').run(challenge);return json(res,429,{ok:false,error:'Zu viele 2FA-Versuche. Bitte erneut anmelden.'});}
      let valid=verifyTotp(unprotectTotpSecret(row.totp_secret),code),usedRecovery=false;
      if(!valid){let hashes=[];try{hashes=JSON.parse(row.recovery_hashes||'[]');}catch{}const h=hashToken(code.replace(/-/g,''));const idx=hashes.findIndex(x=>timingSafeEqualText(x,h));if(idx>=0){valid=true;usedRecovery=true;hashes.splice(idx,1);db.prepare('UPDATE users SET recovery_hashes=? WHERE id=?').run(JSON.stringify(hashes),row.user_id);}}
      if(!valid){db.prepare('UPDATE login_challenges SET attempts=attempts+1 WHERE id=?').run(challenge);return json(res,401,{ok:false,error:'Sicherheitscode ist ungültig.'});}
      db.prepare('DELETE FROM login_challenges WHERE id=?').run(challenge);createSession(res,row);
      return json(res,200,{ok:true,user:{display_name:row.display_name,role:row.role},used_recovery_code:usedRecovery});
    }

    if (req.method === 'POST' && pathname === '/api/logout') {
      const sid = parseCookies(req).tor_session;
      if (sid) db.prepare('DELETE FROM sessions WHERE id=?').run(sid);
      setCookie(res, 'tor_session', '', {maxAge:0});
      return json(res, 200, {ok:true});
    }

    if (req.method === 'GET' && pathname === '/api/me') {
      const user=requireUser(req,res,{allowUnenrolled:true,allowPasswordChange:true});if(!user)return;
      const business=db.prepare('SELECT name,customer_number FROM businesses WHERE id=?').get(user.business_id);
      return json(res,200,{ok:true,user:{display_name:user.display_name,role:user.role,email:user.email},business,demo:DEMO,security:{totp_enabled:!!user.totp_enabled,setup_required:REQUIRE_OWNER_2FA&&user.role==='OWNER'&&!user.totp_enabled,password_change_required:!!user.must_change_password}});
    }

    // R128: until now an owner had no way at all to change their password -
    // the one-time password from tools/provision.js would have stayed theirs
    // for good. Shares the login rate limit, so this cannot be used to guess
    // the current password faster than the login form could.
    if(req.method==='POST' && pathname==='/api/password/change'){
      const user=requireUser(req,res,{allowUnenrolled:true,allowPasswordChange:true});if(!user)return;
      const body=await readJson(req);
      if(loginLimited(req,user.email))return json(res,429,{ok:false,error:'Zu viele Versuche. Bitte 15 Minuten warten.'});
      const current=String(body.current_password||'');const next=String(body.new_password||'');
      const row=db.prepare('SELECT password_salt,password_hash FROM users WHERE id=?').get(user.user_id);
      if(!row || !await verifyPassword(current,row.password_salt,row.password_hash))return json(res,401,{ok:false,error:'Aktuelles Passwort ist falsch.'});
      const problem=passwordProblem(next,current,user.email);
      if(problem)return json(res,400,{ok:false,error:problem});
      const secret=hashPassword(next);
      db.prepare('UPDATE users SET password_salt=?,password_hash=?,must_change_password=0 WHERE id=?').run(secret.salt,secret.hash,user.user_id);
      // Whoever knew the old password must not stay logged in elsewhere; the
      // session that made the change continues.
      const ended=Number(db.prepare('DELETE FROM sessions WHERE user_id=? AND id<>?').run(user.user_id,parseCookies(req).tor_session||'').changes);
      return json(res,200,{ok:true,sessions_ended:ended});
    }

    if(req.method==='POST' && pathname==='/api/2fa/setup/start'){
      const user=requireUser(req,res,{allowUnenrolled:true});if(!user)return;
      const secret=base32Encode(crypto.randomBytes(20));
      db.prepare('UPDATE users SET totp_pending_secret=? WHERE id=?').run(protectTotpSecret(secret),user.user_id);
      const label=encodeURIComponent(`TOR POS Cloud:${user.email}`);const issuer=encodeURIComponent('TOR POS Cloud');
      return json(res,200,{ok:true,secret,otpauth_uri:`otpauth://totp/${label}?secret=${secret}&issuer=${issuer}&algorithm=SHA1&digits=6&period=30`});
    }

    if(req.method==='POST' && pathname==='/api/2fa/setup/confirm'){
      const user=requireUser(req,res,{allowUnenrolled:true});if(!user)return;const body=await readJson(req);
      const row=db.prepare('SELECT totp_pending_secret FROM users WHERE id=?').get(user.user_id);const secret=unprotectTotpSecret(row?.totp_pending_secret||'');
      if(!secret)return json(res,400,{ok:false,error:'2FA-Einrichtung wurde noch nicht gestartet.'});
      if(!verifyTotp(secret,String(body.code||'')))return json(res,400,{ok:false,error:'Sicherheitscode passt nicht. Uhrzeit am Telefon prüfen und erneut versuchen.'});
      const recovery=Array.from({length:8},()=>recoveryCode());const hashes=recovery.map(x=>hashToken(x.replace(/-/g,'')));
      db.prepare("UPDATE users SET totp_enabled=1,totp_secret=?,totp_pending_secret='',recovery_hashes=? WHERE id=?").run(protectTotpSecret(secret),JSON.stringify(hashes),user.user_id);
      db.prepare('DELETE FROM sessions WHERE user_id=? AND id<>?').run(user.user_id,parseCookies(req).tor_session||'');
      return json(res,200,{ok:true,recovery_codes:recovery});
    }

    if(req.method==='POST' && pathname==='/api/2fa/disable'){
      const user=requireUser(req,res,{allowUnenrolled:true});if(!user)return;const body=await readJson(req);
      const row=db.prepare('SELECT * FROM users WHERE id=?').get(user.user_id);
      if(!row || !await verifyPassword(String(body.password||''),row.password_salt,row.password_hash) || !verifyTotp(unprotectTotpSecret(row.totp_secret),String(body.code||'')))return json(res,401,{ok:false,error:'Passwort oder Sicherheitscode ist falsch.'});
      if(REQUIRE_OWNER_2FA && row.role==='OWNER')return json(res,409,{ok:false,error:'2FA ist für Inhaber in diesem Cloud-Betrieb verpflichtend.'});
      db.prepare("UPDATE users SET totp_enabled=0,totp_secret='',totp_pending_secret='',recovery_hashes='[]' WHERE id=?").run(user.user_id);
      return json(res,200,{ok:true});
    }

    if (req.method === 'GET' && pathname === '/api/dashboard/summary') {
      const user = requireUser(req, res); if (!user) return;
      return json(res, 200, {ok:true, ...dashboardSummary(user.business_id)});
    }

    if (req.method === 'GET' && pathname === '/api/portal/data') {
      const user = requireUser(req, res); if (!user) return;
      return json(res, 200, {ok:true, ...portalData(user.business_id,Math.max(0,Math.min(10000000,parseInt(url.searchParams.get('offset')||'0',10)||0)))});
    }

    if (req.method === 'GET' && /^\/api\/receipts\/\d+$/.test(pathname)) {
      const user = requireUser(req, res); if (!user) return;
      const saleId = Number(pathname.split('/').pop());
      const receipt = receiptDetail(user.business_id, saleId);
      if (!receipt) return json(res, 404, {ok:false, error:'Bon nicht gefunden'});
      return json(res, 200, {ok:true, receipt});
    }

    if (req.method === 'POST' && pathname === '/api/v1/devices/sync') {
      const device = requireDevice(req, res); if (!device) return;
      const body = await readJson(req);
      if(!Array.isArray(body.events)||!body.events.length) return json(res,400,{ok:false,error:'events fehlt oder leer'});
      const events = body.events;
      if (events.length > 250) return json(res, 400, {ok:false, error:'Maximal 250 Ereignisse pro Batch'});
      const result = [];
      db.exec('BEGIN IMMEDIATE');
      try {
        for (const raw of events) {
          const event = normalizeEvent(raw);
          result.push({event_id:event.eventId, status:ingestEvent(device.register_id, event)});
        }
        db.exec('COMMIT');
      } catch (e) { db.exec('ROLLBACK'); throw e; }
      return json(res, 200, {ok:true, accepted:result.filter(x=>x.status==='accepted').length, duplicates:result.filter(x=>x.status==='duplicate').length, results:result, server_time:nowIso()});
    }

    // R145: the till publishes the customer's digital receipt after the sale is
    // final (TSE transaction finished, AEAO zu § 146a Nr. 2.5.2). The Cloud
    // keeps the presentation data and the PDF under a fresh 256-bit token and
    // returns the link for the QR code; only the token's hash is stored.
    if (req.method === 'POST' && pathname === '/api/v1/devices/receipts') {
      const device = requireDevice(req, res); if (!device) return;
      if (!RECEIPT_ORIGIN) return json(res, 503, {ok:false, error:'Digitaler Kassenbon ist in dieser TOR Cloud nicht eingerichtet (TOR_CLOUD_RECEIPT_URL fehlt).'});
      const body = await readJson(req);
      const ref = typeof body.receipt_ref === 'string' ? body.receipt_ref : '';
      if (!/^[A-Za-z0-9._:-]{8,120}$/.test(ref)) return json(res, 400, {ok:false, error:'receipt_ref ist ungültig.'});
      const doc = validateReceipt(body.receipt);
      const documentJson = JSON.stringify(doc);
      const contentHash = crypto.createHash('sha256').update(documentJson).digest('hex');
      const pdf = renderReceiptPdf(doc);
      const token = crypto.randomBytes(32).toString('base64url');
      const now = new Date();
      const createdAt = now.toISOString();
      db.exec('BEGIN IMMEDIATE');
      try {
        const existing = db.prepare('SELECT id,content_hash,created_at,expires_at FROM public_receipts WHERE register_id=? AND receipt_ref=?').get(device.register_id, ref);
        if (existing && existing.expires_at > createdAt) {
          if (existing.content_hash !== contentHash) {
            db.exec('ROLLBACK');
            return json(res, 409, {ok:false, error:'Für diesen Beleg ist bereits ein anderer Inhalt veröffentlicht. Ein Kassenbon ist unveränderlich.'});
          }
          // The till asks again because it never received the link (timeout,
          // lost connection). The same receipt gets a fresh token; the old one,
          // which nobody was shown, stops working. Lifetime is not extended.
          db.prepare('UPDATE public_receipts SET token_hash=? WHERE id=?').run(hashToken(token), existing.id);
          db.exec('COMMIT');
          return json(res, 200, {ok:true, receipt_id:existing.id, url:`${RECEIPT_ORIGIN}/r/${token}`, created_at:existing.created_at, expires_at:existing.expires_at, reissued:true});
        }
        if (existing) db.prepare('DELETE FROM public_receipts WHERE id=?').run(existing.id);
        const id = randomId(12);
        const expiresAt = new Date(now.getTime() + RECEIPT_TTL_DAYS * 24 * 60 * 60 * 1000).toISOString();
        db.prepare('INSERT INTO public_receipts(id,business_id,register_id,receipt_ref,token_hash,content_hash,document_json,pdf,created_at,expires_at) VALUES(?,?,?,?,?,?,?,?,?,?)')
          .run(id, device.business_id, device.register_id, ref, hashToken(token), contentHash, documentJson, pdf, createdAt, expiresAt);
        db.exec('COMMIT');
        return json(res, 201, {ok:true, receipt_id:id, url:`${RECEIPT_ORIGIN}/r/${token}`, created_at:createdAt, expires_at:expiresAt, reissued:false});
      } catch (e) {
        try { db.exec('ROLLBACK'); } catch {}
        throw e;
      }
    }

    if (req.method === 'GET' && pathname === '/api/v1/devices/ping') {
      const device = requireDevice(req, res); if (!device) return;
      db.prepare('UPDATE registers SET last_seen_at=? WHERE id=?').run(nowIso(), device.register_id);
      return json(res, 200, {ok:true, register_id:device.register_id, server_time:nowIso()});
    }

    if (req.method === 'GET' && pathname === '/portal') {
      if (!getSessionUser(req)) { res.writeHead(302, {Location:'/login'}); return res.end(); }
      return serveStatic(req, res, pathname);
    }

    if (req.method === 'GET' && serveStatic(req, res, pathname)) return;
    text(res, 404, 'Nicht gefunden');
  } catch (err) {
    // R120: log enough to actually diagnose a failure. It used to print only
    // the status code - no path, no method, no stack - which made a 500 in
    // production essentially uninvestigable.
    const status=err.statusCode||500;
    console.error(`[${nowIso()}] ${req.method} ${redactUrl(req.url)} -> ${status}: ${err.message}`);
    if(!err.statusCode && err.stack)console.error(err.stack);
    json(res, status, {ok:false, error:err.statusCode ? err.message : 'Interner Serverfehler'});
  }
}

// R125 (audit finding C4): housekeeping the prototype never did.
//
// Expired sessions and 2FA challenges were only deleted when that exact row was
// touched again - a session nobody returns to stayed forever, so the table only
// ever grew. And the whole Cloud lived in one SQLite file with no backup at all.
const CLEANUP_INTERVAL_MS=Math.max(200,Number(process.env.TOR_CLOUD_CLEANUP_INTERVAL_MS||60*60*1000));
function cleanupExpired(){
  const now=nowIso();
  const sessions=Number(db.prepare('DELETE FROM sessions WHERE expires_at<=?').run(now).changes);
  const challenges=Number(db.prepare('DELETE FROM login_challenges WHERE expires_at<=?').run(now).changes);
  cleanupGooglePairs();
  // R145: at the end of its lifetime a digital receipt is deleted entirely -
  // token hash, PDF and content. The fiscal records on the till are untouched.
  const receipts=Number(db.prepare('DELETE FROM public_receipts WHERE expires_at<=?').run(now).changes);
  return {sessions,challenges,receipts};
}

// Backups are off unless TOR_CLOUD_BACKUP_DIR is set, so a developer checkout
// does not quietly fill its disk. VACUUM INTO writes a transactionally
// consistent copy while the server keeps running - copying the .db file itself
// would miss whatever still sits in the WAL.
const BACKUP_DIR=String(process.env.TOR_CLOUD_BACKUP_DIR||'').trim();
const BACKUP_INTERVAL_MS=Math.max(60*1000,Number(process.env.TOR_CLOUD_BACKUP_INTERVAL_HOURS||24)*60*60*1000);
const BACKUP_KEEP=Math.max(1,Math.floor(Number(process.env.TOR_CLOUD_BACKUP_KEEP||14)));
const BACKUP_NAME=/^tor-cloud-(\d{8}T\d{6})Z\.db$/;
function backupStamp(date){return date.toISOString().replace(/[-:]/g,'').replace(/\.\d+Z$/,'');}
function listBackups(){
  if(!BACKUP_DIR||!fs.existsSync(BACKUP_DIR))return [];
  return fs.readdirSync(BACKUP_DIR).filter(name=>BACKUP_NAME.test(name)).sort();
}
function backupIfDue(now=new Date()){
  if(!BACKUP_DIR)return null;
  const newest=listBackups().at(-1);
  if(newest){
    const s=BACKUP_NAME.exec(newest)[1];
    const last=Date.parse(`${s.slice(0,4)}-${s.slice(4,6)}-${s.slice(6,8)}T${s.slice(9,11)}:${s.slice(11,13)}:${s.slice(13,15)}Z`);
    // A server that restarts often must not replace a week of daily backups
    // with a dozen copies from the same afternoon.
    if(Number.isFinite(last)&&now.getTime()-last<BACKUP_INTERVAL_MS)return null;
  }
  fs.mkdirSync(BACKUP_DIR,{recursive:true});
  const target=path.join(BACKUP_DIR,`tor-cloud-${backupStamp(now)}Z.db`);
  if(fs.existsSync(target))return null;
  db.prepare('VACUUM INTO ?').run(target);
  const all=listBackups();
  for(const old of all.slice(0,Math.max(0,all.length-BACKUP_KEEP)))fs.rmSync(path.join(BACKUP_DIR,old),{force:true});
  return target;
}
function runHousekeeping(){
  try{
    const removed=cleanupExpired();
    if(removed.sessions||removed.challenges||removed.receipts)console.log(`[${nowIso()}] Aufgeräumt: ${removed.sessions} abgelaufene Sitzung(en), ${removed.challenges} 2FA-Anfrage(n), ${removed.receipts} digitale(r) Kassenbon(s).`);
  }catch(err){console.error(`[${nowIso()}] Aufräumen fehlgeschlagen: ${err.message}`);}
  try{
    const backup=backupIfDue();
    if(backup)console.log(`[${nowIso()}] Datensicherung geschrieben: ${backup}`);
  }catch(err){console.error(`[${nowIso()}] Datensicherung fehlgeschlagen: ${err.message}`);}
}

const server = http.createServer(handler);
server.listen(PORT, HOST, () => {
  console.log(`TOR POS Cloud v${CLOUD_VERSION} läuft auf http://${HOST}:${server.address().port}`);
  if(DEMO) console.log('Lokaler Demomodus aktiv. Keine echten Umsätze.');
  if(TRUST_PROXY) console.log('X-Forwarded-For wird ausgewertet (TOR_CLOUD_TRUST_PROXY=true).');
  if(RECEIPT_ORIGIN) console.log(`Digitaler Kassenbon: ${RECEIPT_ORIGIN} (Links ${RECEIPT_TTL_DAYS} Tage abrufbar).`);
  if(TOR_MAIL_READY) console.log(`TOR Mail aktiv: ${TOR_MAIL_CONFIG.from} via ${TOR_MAIL_CONFIG.host}:${TOR_MAIL_CONFIG.port}.`);
  if(RECEIPT_ORIGIN&&!DEMO&&(!RECEIPT_LINKS.imprint||!RECEIPT_LINKS.privacy)) console.warn('WARNUNG: TOR_CLOUD_IMPRINT_URL und TOR_CLOUD_PRIVACY_URL setzen - die Bon-Domain ist ein öffentliches Angebot (Impressum, Datenschutzhinweis).');
  if(BACKUP_DIR) console.log(`Datensicherung aktiv: ${BACKUP_DIR} (alle ${BACKUP_INTERVAL_MS/3600000} h, ${BACKUP_KEEP} behalten).`);
  // R125: after the port is open, so a large database never delays startup.
  setImmediate(runHousekeeping);
  setInterval(runHousekeeping,Math.min(CLEANUP_INTERVAL_MS,BACKUP_INTERVAL_MS)).unref();
});

// R120: shut down in an orderly way instead of dropping in-flight requests and
// leaving the SQLite file mid-write. Tills retry their outbox, so a clean
// close costs nothing and avoids a half-applied batch on deploy/restart.
let shuttingDown=false;
function shutdown(signal){
  if(shuttingDown)return;
  shuttingDown=true;
  console.log(`[${nowIso()}] ${signal} empfangen - Server wird beendet.`);
  server.close(()=>{
    try{db.close();}catch(e){console.error('DB close failed:',e.message);}
    process.exit(0);
  });
  // Never hang forever on a stuck keep-alive connection.
  setTimeout(()=>process.exit(0),10000).unref();
}
process.on('SIGTERM',()=>shutdown('SIGTERM'));
process.on('SIGINT',()=>shutdown('SIGINT'));

// R120: an unexpected error anywhere must be logged, not silently swallowed by
// the runtime's default handler.
process.on('unhandledRejection',reason=>console.error(`[${nowIso()}] Unhandled rejection:`,reason));
