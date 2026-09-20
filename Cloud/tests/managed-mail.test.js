'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
  normalizeManagedMailPayload,
  smtpConfigFromEnv,
  isManagedMailConfigured,
  buildManagedMime
} = require('../managed-mail');

test('TOR Mail accepts one recipient and PDF/CSV attachments only', () => {
  const mail = normalizeManagedMailPayload({
    recipient: 'steuerberater@example.com',
    subject: 'TOR POS · DATEV Z 000155',
    body: 'DATEV-Datei im Anhang.',
    attachments: [
      { filename: 'datev.csv', data_base64: Buffer.from('a;b\r\n1;2\r\n').toString('base64') },
      { filename: 'z.pdf', data_base64: Buffer.from('%PDF-test').toString('base64') }
    ]
  });
  assert.equal(mail.recipient, 'steuerberater@example.com');
  assert.equal(mail.attachments.length, 2);
  assert.equal(mail.attachments[0].contentType, 'text/csv; charset=utf-8');
  assert.equal(mail.attachments[1].contentType, 'application/pdf');
});

test('TOR Mail rejects header injection and non-report attachments', () => {
  assert.throws(() => normalizeManagedMailPayload({
    recipient: 'ok@example.com\r\nBcc: attacker@example.com',
    subject: 'x',
    body: 'x'
  }), /ungültig/i);
  assert.throws(() => normalizeManagedMailPayload({
    recipient: 'ok@example.com',
    subject: 'x',
    body: 'x',
    attachments: [{ filename: 'payload.exe', data_base64: Buffer.from('x').toString('base64') }]
  }), /PDF- und CSV/i);
});

test('TOR Mail SMTP configuration is server-only and complete', () => {
  const config = smtpConfigFromEnv({
    TOR_MAIL_SMTP_HOST: 'smtp.example.com',
    TOR_MAIL_SMTP_PORT: '587',
    TOR_MAIL_SMTP_USER: 'reports@example.com',
    TOR_MAIL_SMTP_PASSWORD: 'secret',
    TOR_MAIL_FROM: 'reports@example.com',
    TOR_MAIL_FROM_NAME: 'TOR POS',
    TOR_MAIL_SMTP_MODE: 'starttls'
  });
  assert.equal(isManagedMailConfigured(config), true);
  assert.equal(config.password, 'secret');
  assert.equal(isManagedMailConfigured({ ...config, password: '' }), false);
});

test('TOR Mail MIME uses fixed sender and does not expose SMTP credentials', () => {
  const config = {
    host: 'smtp.example.com', port: 587, username: 'reports@example.com',
    password: 'super-secret', from: 'reports@example.com', fromName: 'TOR POS', mode: 'starttls'
  };
  const mail = normalizeManagedMailPayload({
    recipient: 'kanzlei@example.com',
    subject: 'Monatsberichte September',
    body: 'Anbei die Berichte.',
    attachments: []
  });
  const mime = buildManagedMime(config, mail);
  assert.match(mime, /From: TOR POS <reports@example\.com>/);
  assert.match(mime, /To: <kanzlei@example\.com>/);
  assert.equal(mime.includes('super-secret'), false);
  assert.equal(mime.includes('smtp.example.com'), false);
});
