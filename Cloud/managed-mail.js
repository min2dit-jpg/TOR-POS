'use strict';

const net = require('node:net');
const tls = require('node:tls');
const crypto = require('node:crypto');
const path = require('node:path');

const MAX_ATTACHMENTS = 10;
const MAX_ATTACHMENT_BYTES = 6 * 1024 * 1024;
const MAX_TOTAL_ATTACHMENT_BYTES = 8 * 1024 * 1024;
const MAX_BODY_CHARS = 20000;
const MAX_SUBJECT_CHARS = 200;
const EMAIL_RE = /^[^\s@<>]+@[^\s@<>]+\.[^\s@<>]+$/u;

function cleanHeader(value, name, max = 200) {
  const text = String(value ?? '').trim();
  if (!text || text.length > max || /[\r\n\0]/.test(text)) throw Object.assign(new Error(`${name} ist ungültig.`), { statusCode: 400 });
  return text;
}

function cleanEmail(value, name = 'E-Mail') {
  const text = cleanHeader(value, name, 320);
  if (!EMAIL_RE.test(text)) throw Object.assign(new Error(`${name} ist ungültig.`), { statusCode: 400 });
  return text;
}

function decodeBase64(value, name) {
  const raw = String(value ?? '').replace(/\s/g, '');
  if (!raw || !/^[A-Za-z0-9+/]*={0,2}$/.test(raw) || raw.length % 4 === 1)
    throw Object.assign(new Error(`${name}: ungültige Base64-Daten.`), { statusCode: 400 });
  const bytes = Buffer.from(raw, 'base64');
  const canonical = bytes.toString('base64').replace(/=+$/,'');
  if (canonical !== raw.replace(/=+$/,''))
    throw Object.assign(new Error(`${name}: ungültige Base64-Daten.`), { statusCode: 400 });
  return bytes;
}

function normalizeManagedMailPayload(raw) {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) throw Object.assign(new Error('E-Mail-Daten fehlen.'), { statusCode: 400 });
  const recipient = cleanEmail(raw.recipient, 'Empfänger-E-Mail');
  const subject = cleanHeader(raw.subject, 'Betreff', MAX_SUBJECT_CHARS);
  const body = String(raw.body ?? '');
  if (!body.trim() || body.length > MAX_BODY_CHARS || body.includes('\0'))
    throw Object.assign(new Error('E-Mail-Text ist ungültig.'), { statusCode: 400 });

  const list = raw.attachments == null ? [] : raw.attachments;
  if (!Array.isArray(list) || list.length > MAX_ATTACHMENTS)
    throw Object.assign(new Error(`Maximal ${MAX_ATTACHMENTS} Anhänge erlaubt.`), { statusCode: 400 });

  let totalBytes = 0;
  const attachments = list.map((item, index) => {
    if (!item || typeof item !== 'object' || Array.isArray(item))
      throw Object.assign(new Error(`Anhang ${index + 1} ist ungültig.`), { statusCode: 400 });
    const filename = path.basename(cleanHeader(item.filename, `Anhang ${index + 1} Dateiname`, 180));
    const ext = path.extname(filename).toLowerCase();
    if (!['.pdf', '.csv'].includes(ext))
      throw Object.assign(new Error('TOR Mail erlaubt nur PDF- und CSV-Anhänge.'), { statusCode: 400 });
    const bytes = decodeBase64(item.data_base64, filename);
    if (bytes.length > MAX_ATTACHMENT_BYTES)
      throw Object.assign(new Error(`${filename} ist zu groß.`), { statusCode: 413 });
    totalBytes += bytes.length;
    if (totalBytes > MAX_TOTAL_ATTACHMENT_BYTES)
      throw Object.assign(new Error('E-Mail-Anhänge sind zusammen zu groß.'), { statusCode: 413 });
    return {
      filename,
      contentType: ext === '.pdf' ? 'application/pdf' : 'text/csv; charset=utf-8',
      bytes
    };
  });

  return { recipient, subject, body, attachments, totalBytes };
}

function smtpConfigFromEnv(env = process.env) {
  const host = String(env.TOR_MAIL_SMTP_HOST || '').trim();
  const port = Math.floor(Number(env.TOR_MAIL_SMTP_PORT || 587));
  const username = String(env.TOR_MAIL_SMTP_USER || '').trim();
  const password = String(env.TOR_MAIL_SMTP_PASSWORD || '');
  const from = String(env.TOR_MAIL_FROM || '').trim();
  const fromName = String(env.TOR_MAIL_FROM_NAME || 'TOR POS').trim() || 'TOR POS';
  const mode = String(env.TOR_MAIL_SMTP_MODE || (port === 465 ? 'tls' : 'starttls')).trim().toLowerCase();
  return { host, port, username, password, from, fromName, mode };
}

function isManagedMailConfigured(config = smtpConfigFromEnv()) {
  if (!config.host || !Number.isInteger(config.port) || config.port < 1 || config.port > 65535) return false;
  if (!EMAIL_RE.test(config.from) || !['starttls','tls'].includes(config.mode)) return false;
  if (!!config.username !== !!config.password) return false;
  return true;
}

function b64Lines(buffer) {
  return Buffer.from(buffer).toString('base64').match(/.{1,76}/g)?.join('\r\n') || '';
}

function encodedWord(value) {
  const text = String(value);
  return /^[\x20-\x7E]*$/.test(text) ? text : `=?UTF-8?B?${Buffer.from(text,'utf8').toString('base64')}?=`;
}

function safeFilename(value) {
  const cleaned = path.basename(String(value)).replace(/[\r\n"\\]/g, '_');
  return cleaned || 'Anhang';
}

function buildManagedMime(config, mail) {
  const boundary = '=_TOR_POS_' + crypto.randomBytes(18).toString('hex');
  const domain = config.from.split('@')[1] || 'torpos.local';
  const lines = [
    `From: ${encodedWord(config.fromName)} <${config.from}>`,
    `To: <${mail.recipient}>`,
    `Subject: ${encodedWord(mail.subject)}`,
    `Date: ${new Date().toUTCString()}`,
    `Message-ID: <${crypto.randomBytes(18).toString('hex')}@${domain}>`,
    'MIME-Version: 1.0',
    `Content-Type: multipart/mixed; boundary="${boundary}"`,
    '',
    `--${boundary}`,
    'Content-Type: text/plain; charset=utf-8',
    'Content-Transfer-Encoding: base64',
    '',
    b64Lines(Buffer.from(mail.body, 'utf8'))
  ];

  for (const attachment of mail.attachments) {
    const filename = safeFilename(attachment.filename);
    lines.push(
      `--${boundary}`,
      `Content-Type: ${attachment.contentType}; name="${filename}"`,
      'Content-Transfer-Encoding: base64',
      `Content-Disposition: attachment; filename="${filename}"`,
      '',
      b64Lines(attachment.bytes)
    );
  }
  lines.push(`--${boundary}--`, '');
  return lines.join('\r\n');
}

function socketConnected(socket, secure = false, timeoutMs = 20000) {
  if (!secure && !socket.connecting) return Promise.resolve();
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => done(new Error('SMTP-Verbindung Zeitüberschreitung.')), timeoutMs);
    const event = secure ? 'secureConnect' : 'connect';
    const done = err => {
      clearTimeout(timer);
      socket.off(event, onReady);
      socket.off('error', onError);
      if (err) reject(err); else resolve();
    };
    const onReady = () => done();
    const onError = err => done(err);
    socket.once(event, onReady);
    socket.once('error', onError);
  });
}

function smtpResponse(socket, timeoutMs = 20000) {
  return new Promise((resolve, reject) => {
    let text = '';
    const timer = setTimeout(() => done(new Error('SMTP-Antwort Zeitüberschreitung.')), timeoutMs);
    const done = (err, value) => {
      clearTimeout(timer);
      socket.off('data', onData);
      socket.off('error', onError);
      socket.off('close', onClose);
      if (err) reject(err); else resolve(value);
    };
    const onError = err => done(err);
    const onClose = () => done(new Error('SMTP-Verbindung wurde unerwartet geschlossen.'));
    const onData = chunk => {
      text += chunk.toString('utf8');
      if (text.length > 64000) return done(new Error('SMTP-Antwort ist unerwartet groß.'));
      const complete = text.endsWith('\r\n') ? text.slice(0,-2).split('\r\n') : [];
      if (!complete.length) return;
      const last = complete[complete.length - 1];
      const match = /^(\d{3}) /.exec(last);
      if (match) done(null, { code: Number(match[1]), text });
    };
    socket.on('data', onData);
    socket.once('error', onError);
    socket.once('close', onClose);
  });
}

async function writeSocket(socket, data) {
  await new Promise((resolve, reject) => {
    const ok = socket.write(data, err => err ? reject(err) : resolve());
    if (!ok) socket.once('drain', () => {});
  });
}

async function smtpCommand(socket, command, expected) {
  const responsePromise = smtpResponse(socket);
  await writeSocket(socket, command + '\r\n');
  const response = await responsePromise;
  if (!expected.includes(response.code)) {
    const safe = response.text.replace(/[\r\n]+/g, ' ').slice(0, 500);
    throw new Error(`SMTP ${command.split(' ')[0]} abgelehnt: ${response.code} ${safe}`);
  }
  return response;
}

async function sendManagedMail(config, mail, timeoutMs = 60000) {
  if (!isManagedMailConfigured(config)) throw Object.assign(new Error('TOR Mail ist auf dem Cloud-Server noch nicht konfiguriert.'), { statusCode: 503 });
  const mime = buildManagedMime(config, mail);
  let timedOut = false;
  let socket;
  const timer = setTimeout(() => {
    timedOut = true;
    try { socket?.destroy(new Error('TOR Mail Versand Zeitüberschreitung.')); } catch {}
  }, timeoutMs);
  try {
    socket = config.mode === 'tls'
      ? tls.connect({ host: config.host, port: config.port, servername: config.host, minVersion: 'TLSv1.2', rejectUnauthorized: true })
      : net.connect({ host: config.host, port: config.port });
    await socketConnected(socket, config.mode === 'tls');
    let greeting = await smtpResponse(socket);
    if (greeting.code !== 220) throw new Error(`SMTP-Begrüßung abgelehnt: ${greeting.code}`);

    await smtpCommand(socket, 'EHLO torpos-cloud', [250]);

    if (config.mode === 'starttls') {
      await smtpCommand(socket, 'STARTTLS', [220]);
      const secured = tls.connect({ socket, servername: config.host, minVersion: 'TLSv1.2', rejectUnauthorized: true });
      await socketConnected(secured, true);
      socket = secured;
      await smtpCommand(socket, 'EHLO torpos-cloud', [250]);
    }

    if (config.username) {
      const token = Buffer.from(`\0${config.username}\0${config.password}`, 'utf8').toString('base64');
      await smtpCommand(socket, 'AUTH PLAIN ' + token, [235]);
    }

    await smtpCommand(socket, `MAIL FROM:<${config.from}>`, [250]);
    await smtpCommand(socket, `RCPT TO:<${mail.recipient}>`, [250,251]);
    await smtpCommand(socket, 'DATA', [354]);
    const data = mime.replace(/(^|\r\n)\./g, '$1..') + '\r\n.\r\n';
    const finalPromise = smtpResponse(socket, 45000);
    await writeSocket(socket, data);
    const final = await finalPromise;
    if (final.code !== 250) throw new Error(`SMTP DATA abgelehnt: ${final.code}`);
    try { await smtpCommand(socket, 'QUIT', [221]); } catch {}
  } catch (err) {
    if (timedOut) throw Object.assign(new Error('TOR Mail Versand Zeitüberschreitung.'), { statusCode: 504 });
    throw err;
  } finally {
    clearTimeout(timer);
    try { socket?.destroy(); } catch {}
  }
}

module.exports = {
  MAX_ATTACHMENTS,
  MAX_TOTAL_ATTACHMENT_BYTES,
  normalizeManagedMailPayload,
  smtpConfigFromEnv,
  isManagedMailConfigured,
  buildManagedMime,
  sendManagedMail
};
