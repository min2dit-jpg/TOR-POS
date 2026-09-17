'use strict';

// R145: TOR Digital Receipt Cloud - the customer's copy of a Kassenbon behind
// a QR code.
//
// This is a public presentation service, not a fiscal archive. The records
// that tax law requires (Kassen-DB, TSE, DSFinV-K) stay in TOR POS on the till
// and are kept there under § 147 AO; nothing here replaces or feeds them. The
// till sends only what the receipt shows, the Cloud keeps it for a limited
// time (TOR_CLOUD_RECEIPT_TTL_DAYS) and then deletes token, PDF and content.
//
// The legal content of the receipt (§ 6 KassenSichV, AEAO zu § 146a Nr. 2.4.4)
// is decided on the till, the one place that knows the sale; this module checks
// the data are well-formed and consistent and lays them out as page and PDF.

const { buildPdf, wrap, width, COLUMNS } = require('./receipt-pdf');
// R149: the till's total rule (returned deposit can make a receipt negative).
const { receiptTotal } = require('./validation');

const FORMAT = 'TOR-DIGITALBON-1';
const MAX_CENTS = 1_000_000_000;

function fail(message) {
  throw Object.assign(new Error(message), { statusCode: 400 });
}

function text(value, field, max, { required = false } = {}) {
  if (value == null) value = '';
  if (typeof value !== 'string') fail(`${field} muss Text sein.`);
  const clean = value.replace(/[\x00-\x1F\x7F]/g, ' ').replace(/\s+/g, ' ').trim();
  if (required && !clean) fail(`${field} fehlt.`);
  if (clean.length > max) fail(`${field} ist zu lang.`);
  return clean;
}

function cents(value, field) {
  if (!Number.isSafeInteger(value) || Math.abs(value) > MAX_CENTS) fail(`${field} muss ein ganzzahliger Centbetrag sein.`);
  return value;
}

function list(value, field, max, min = 0) {
  if (value == null) value = [];
  if (!Array.isArray(value)) fail(`${field} muss eine Liste sein.`);
  if (value.length < min) fail(`${field} fehlt.`);
  if (value.length > max) fail(`${field} hat zu viele Einträge.`);
  return value;
}

function object(value, field) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) fail(`${field} fehlt.`);
  return value;
}

/**
 * Accepts exactly the fields a receipt shows and nothing else (DSGVO Art. 5
 * Abs. 1 lit. c): unknown fields are dropped, so a till can never park an
 * operator name, a customer or anything else on the public side by accident.
 * Returns the normalised document with a fixed key order, which is also what
 * the immutability check hashes.
 */
function validateReceipt(raw) {
  const r = object(raw, 'receipt');
  if (r.format !== FORMAT) fail(`Unbekanntes Belegformat (erwartet ${FORMAT}).`);
  const business = object(r.business, 'receipt.business');
  let pickup = null;
  if (r.pickup_number != null) {
    if (!Number.isSafeInteger(r.pickup_number) || r.pickup_number < 1 || r.pickup_number > 99999) fail('pickup_number ist ungültig.');
    pickup = r.pickup_number;
  }

  const doc = {
    format: FORMAT,
    test_receipt: r.test_receipt === true,
    business: {
      name: text(business.name, 'business.name', 200, { required: true }),
      address: text(business.address, 'business.address', 300),
      tax_number: text(business.tax_number, 'business.tax_number', 40),
      vat_id: text(business.vat_id, 'business.vat_id', 40)
    },
    receipt_number: text(r.receipt_number, 'receipt_number', 40, { required: true }),
    issued_at: text(r.issued_at, 'issued_at', 40, { required: true }),
    pickup_number: pickup,
    lines: list(r.lines, 'lines', 500, 1).map((raw, i) => {
      const line = object(raw, `lines[${i}]`);
      return {
        name: text(line.name, `lines[${i}].name`, 200, { required: true }),
        quantity: text(line.quantity, `lines[${i}].quantity`, 20, { required: true }),
        unit_price_cents: cents(line.unit_price_cents, `lines[${i}].unit_price_cents`),
        line_total_cents: cents(line.line_total_cents, `lines[${i}].line_total_cents`),
        vat_rate: text(line.vat_rate, `lines[${i}].vat_rate`, 10, { required: true }),
        note: text(line.note, `lines[${i}].note`, 200)
      };
    }),
    subtotal_cents: cents(r.subtotal_cents, 'subtotal_cents'),
    discount_cents: cents(r.discount_cents, 'discount_cents'),
    total_cents: cents(r.total_cents, 'total_cents'),
    vat: list(r.vat, 'vat', 10, 1).map((raw, i) => {
      const group = object(raw, `vat[${i}]`);
      return {
        rate: text(group.rate, `vat[${i}].rate`, 10, { required: true }),
        net_cents: cents(group.net_cents, `vat[${i}].net_cents`),
        tax_cents: cents(group.tax_cents, `vat[${i}].tax_cents`),
        gross_cents: cents(group.gross_cents, `vat[${i}].gross_cents`)
      };
    }),
    payments: list(r.payments, 'payments', 10, 1).map((raw, i) => {
      const payment = object(raw, `payments[${i}]`);
      return {
        label: text(payment.label, `payments[${i}].label`, 40, { required: true }),
        amount_cents: cents(payment.amount_cents, `payments[${i}].amount_cents`)
      };
    }),
    tse: list(r.tse, 'tse', 20).map((raw, i) => {
      const field = object(raw, `tse[${i}]`);
      return {
        label: text(field.label, `tse[${i}].label`, 60, { required: true }),
        value: text(field.value, `tse[${i}].value`, 1200, { required: true })
      };
    }),
    notes: list(r.notes, 'notes', 10).map((note, i) => text(note, `notes[${i}]`, 300, { required: true }))
  };

  // A receipt is shown as the till computed it, but it has to add up: a till
  // that sends figures which contradict each other gets no public receipt and
  // falls back to paper.
  if (doc.lines.reduce((sum, line) => sum + line.line_total_cents, 0) !== doc.subtotal_cents) fail('Die Positionen ergeben nicht die Zwischensumme.');
  if (doc.discount_cents < 0) fail('discount_cents darf nicht negativ sein.');
  if (doc.total_cents !== receiptTotal(doc.subtotal_cents, doc.discount_cents)) fail('Gesamtbetrag passt nicht zu Zwischensumme und Rabatt.');
  for (const group of doc.vat) {
    if (group.net_cents + group.tax_cents !== group.gross_cents) fail(`MwSt. ${group.rate}%: Netto und Steuer ergeben nicht den Bruttobetrag.`);
  }
  if (doc.vat.reduce((sum, group) => sum + group.gross_cents, 0) !== doc.total_cents) fail('Die MwSt.-Gruppen ergeben nicht den Gesamtbetrag.');
  if (doc.payments.reduce((sum, payment) => sum + payment.amount_cents, 0) !== doc.total_cents) fail('Die Zahlungen ergeben nicht den Gesamtbetrag.');
  return doc;
}

function euro(value) {
  const negative = value < 0;
  const digits = String(Math.abs(value)).padStart(3, '0');
  const whole = digits.slice(0, -2).replace(/\B(?=(\d{3})+(?!\d))/g, '.');
  return `${negative ? '-' : ''}${whole},${digits.slice(-2)} €`;
}

function berlinDate(iso) {
  return new Intl.DateTimeFormat('de-DE', { timeZone: 'Europe/Berlin', day: '2-digit', month: '2-digit', year: 'numeric' }).format(new Date(iso));
}

function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]));
}

function pickupText(doc) {
  return String(doc.pickup_number).padStart(3, '0');
}

function receiptArticle(doc) {
  const parts = [];
  parts.push('<article class="bon" aria-label="Kassenbon">');
  if (doc.test_receipt) parts.push('<p class="banner">TESTBON · KEIN FISKALBELEG</p>');
  parts.push(`<h2>${esc(doc.business.name)}</h2>`);
  if (doc.business.address) parts.push(`<p class="muted">${esc(doc.business.address)}</p>`);
  if (doc.business.tax_number) parts.push(`<p class="muted">Steuernr. ${esc(doc.business.tax_number)}</p>`);
  if (doc.business.vat_id) parts.push(`<p class="muted">USt-IdNr. ${esc(doc.business.vat_id)}</p>`);

  parts.push('<dl class="meta">');
  parts.push(`<div><dt>Bon-Nr.</dt><dd>${esc(doc.receipt_number)}</dd></div>`);
  parts.push(`<div><dt>Datum</dt><dd>${esc(doc.issued_at)}</dd></div>`);
  if (doc.pickup_number) parts.push(`<div><dt>Abholnummer</dt><dd>${pickupText(doc)}</dd></div>`);
  parts.push('</dl>');

  parts.push('<table class="items"><thead><tr><th scope="col">Artikel</th><th scope="col" class="num">Betrag</th></tr></thead><tbody>');
  for (const line of doc.lines) {
    parts.push(`<tr><td>${esc(line.name)}<span class="sub">${esc(line.quantity)} × ${euro(line.unit_price_cents)} · ${esc(line.vat_rate)} % MwSt.</span>${line.note ? `<span class="sub">${esc(line.note)}</span>` : ''}</td><td class="num">${euro(line.line_total_cents)}</td></tr>`);
  }
  parts.push('</tbody></table>');

  parts.push('<table class="sums"><tbody>');
  if (doc.discount_cents) {
    parts.push(`<tr><td>Zwischensumme</td><td class="num">${euro(doc.subtotal_cents)}</td></tr>`);
    parts.push(`<tr><td>Rabatt</td><td class="num">-${euro(doc.discount_cents)}</td></tr>`);
  }
  parts.push(`<tr class="total"><td>Gesamt</td><td class="num">${euro(doc.total_cents)}</td></tr>`);
  parts.push('</tbody></table>');

  parts.push('<table class="vat"><thead><tr><th scope="col">MwSt.</th><th scope="col" class="num">Netto</th><th scope="col" class="num">Steuer</th><th scope="col" class="num">Brutto</th></tr></thead><tbody>');
  for (const group of doc.vat) {
    parts.push(`<tr><td>${esc(group.rate)} %</td><td class="num">${euro(group.net_cents)}</td><td class="num">${euro(group.tax_cents)}</td><td class="num">${euro(group.gross_cents)}</td></tr>`);
  }
  parts.push('</tbody></table>');

  parts.push('<table class="pay"><caption>Zahlung</caption><tbody>');
  for (const payment of doc.payments) parts.push(`<tr><td>${esc(payment.label)}</td><td class="num">${euro(payment.amount_cents)}</td></tr>`);
  parts.push('</tbody></table>');

  if (doc.tse.length) {
    parts.push('<section class="tse"><h3>Technische Sicherheitseinrichtung</h3><dl>');
    for (const field of doc.tse) parts.push(`<div><dt>${esc(field.label)}</dt><dd>${esc(field.value)}</dd></div>`);
    parts.push('</dl></section>');
  }
  for (const note of doc.notes) parts.push(`<p class="note">${esc(note)}</p>`);
  parts.push('</article>');
  return parts.join('');
}

// Impressum and privacy notice of whoever runs the receipt domain (§ 5 DDG,
// Art. 13 DSGVO); configured per deployment, opened without a referrer.
function legalLinks(links = {}) {
  const parts = [];
  if (links.imprint) parts.push(`<a href="${esc(links.imprint)}" rel="noopener noreferrer">Impressum</a>`);
  if (links.privacy) parts.push(`<a href="${esc(links.privacy)}" rel="noopener noreferrer">Datenschutz</a>`);
  return parts.length ? `<p class="legal">${parts.join(' · ')}</p>` : '';
}

function page(title, body) {
  return '<!doctype html><html lang="de"><head><meta charset="utf-8">' +
    '<meta name="viewport" content="width=device-width, initial-scale=1">' +
    '<meta name="robots" content="noindex, nofollow, noarchive">' +
    '<meta name="referrer" content="no-referrer">' +
    `<title>${esc(title)}</title>` +
    '<link rel="stylesheet" href="/assets/bon.css">' +
    '<script src="/assets/bon.js" defer></script>' +
    `</head><body><main class="page">${body}</main></body></html>`;
}

/** The public page: the receipt readable on screen, PDF herunterladen, Teilen, Drucken. */
function renderReceiptPage(doc, { token, expiresAt, links }) {
  return page('Ihr digitaler Kassenbon',
    '<header class="head">' +
      '<h1>Ihr digitaler Kassenbon</h1>' +
      `<p class="from">${esc(doc.business.name)}</p>` +
    '</header>' +
    '<nav class="actions" aria-label="Beleg">' +
      `<a class="btn primary" href="/r/${esc(token)}/pdf" download>PDF herunterladen</a>` +
      '<button class="btn" type="button" data-action="share" hidden>Teilen</button>' +
      '<button class="btn" type="button" data-action="print" hidden>Drucken</button>' +
    '</nav>' +
    '<p class="status" role="status" aria-live="polite"></p>' +
    receiptArticle(doc) +
    '<footer class="info">' +
      `<p>Dieser Link ist bis zum ${berlinDate(expiresAt)} abrufbar und wird danach automatisch gelöscht. Laden Sie den Beleg als PDF herunter, wenn Sie ihn länger benötigen.</p>` +
      '<p>Wer den Link kennt, kann den Beleg sehen. Teilen Sie ihn nur gezielt.</p>' +
      '<p>Keine Anmeldung, keine Cookies, kein Tracking.</p>' +
      legalLinks(links) +
    '</footer>');
}

function renderNotFoundPage(links) {
  return page('Kassenbon nicht verfügbar',
    '<header class="head"><h1>Kassenbon nicht verfügbar</h1></header>' +
    '<section class="bon plain">' +
      '<p>Dieser Link ist ungültig oder abgelaufen. Digitale Kassenbons sind nur für einen begrenzten Zeitraum abrufbar und werden danach automatisch gelöscht.</p>' +
      '<p>Bei Fragen zu Ihrem Einkauf wenden Sie sich bitte an das Geschäft.</p>' +
    '</section>' +
    `<footer class="info">${legalLinks(links)}</footer>`);
}

function renderHomePage(links) {
  return page('TOR Digitaler Kassenbon',
    '<header class="head"><h1>TOR Digitaler Kassenbon</h1></header>' +
    '<section class="bon plain">' +
      '<p>Digitale Kassenbons sind nur über den persönlichen Link aus dem QR-Code an der Kasse abrufbar.</p>' +
    '</section>' +
    `<footer class="info">${legalLinks(links)}</footer>`);
}

/** The same receipt as monospaced lines for the PDF. */
function pdfLines(doc) {
  const out = [];
  const push = (value, bold = false) => out.push({ text: value, bold });
  const rule = () => push('-'.repeat(COLUMNS));
  const center = (value, bold = false) => {
    for (const line of wrap(value)) push(' '.repeat(Math.floor((COLUMNS - width(line)) / 2)) + line, bold);
  };
  const para = (value, indent = 0, bold = false) => {
    for (const line of wrap(value, COLUMNS - indent)) push(' '.repeat(indent) + line, bold);
  };
  const pair = (left, right, bold = false) => {
    const lines = wrap(left);
    const last = lines.pop() ?? '';
    for (const line of lines) push(line, bold);
    if (width(last) + 1 + width(right) <= COLUMNS) {
      push(last + ' '.repeat(COLUMNS - width(last) - width(right)) + right, bold);
    } else {
      if (last) push(last, bold);
      push(' '.repeat(Math.max(0, COLUMNS - width(right))) + right, bold);
    }
  };

  center('Ihr digitaler Kassenbon', true);
  if (doc.test_receipt) {
    push('');
    center('TESTBON - KEIN FISKALBELEG', true);
  }
  push('');
  center(doc.business.name, true);
  if (doc.business.address) center(doc.business.address);
  if (doc.business.tax_number) center(`Steuernr. ${doc.business.tax_number}`);
  if (doc.business.vat_id) center(`USt-IdNr. ${doc.business.vat_id}`);
  rule();
  pair(`Bon-Nr. ${doc.receipt_number}`, doc.issued_at);
  if (doc.pickup_number) push(`Abholnummer ${pickupText(doc)}`, true);
  rule();
  for (const line of doc.lines) {
    para(line.name);
    pair(`  ${line.quantity} × ${euro(line.unit_price_cents)} (${line.vat_rate} % MwSt.)`, euro(line.line_total_cents));
    if (line.note) para(line.note, 2);
  }
  rule();
  if (doc.discount_cents) {
    pair('Zwischensumme', euro(doc.subtotal_cents));
    pair('Rabatt', `-${euro(doc.discount_cents)}`);
  }
  pair('GESAMT', euro(doc.total_cents), true);
  rule();
  push('MwSt.'.padEnd(7) + 'Netto'.padStart(13) + 'Steuer'.padStart(14) + 'Brutto'.padStart(14));
  for (const group of doc.vat) {
    push(`${group.rate} %`.padEnd(7) + euro(group.net_cents).padStart(13) + euro(group.tax_cents).padStart(14) + euro(group.gross_cents).padStart(14));
  }
  rule();
  for (const payment of doc.payments) pair(payment.label, euro(payment.amount_cents));
  if (doc.tse.length) {
    rule();
    push('Technische Sicherheitseinrichtung', true);
    for (const field of doc.tse) {
      if (width(field.label) + 2 + width(field.value) <= COLUMNS) {
        push(`${field.label}: ${field.value}`);
      } else {
        push(`${field.label}:`);
        para(field.value, 2);
      }
    }
  }
  if (doc.notes.length) {
    rule();
    for (const note of doc.notes) para(note);
  }
  rule();
  center('Digitaler Kassenbon · TOR POS');
  return out;
}

function renderReceiptPdf(doc) {
  return buildPdf({ title: `Kassenbon ${doc.receipt_number} · ${doc.business.name}`, lines: pdfLines(doc) });
}

const ASSETS = {
  '/assets/bon.css': {
    type: 'text/css; charset=utf-8',
    body: `:root{--ink:#172026;--muted:#5d6b75;--line:#e3e8ec;--paper:#fff;--bg:#eef2f5;--accent:#0b6b5a;--accent-ink:#fff;color-scheme:light}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.45 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Arial,sans-serif}
.page{max-width:480px;margin:0 auto;padding:20px 16px 32px}
.head h1{margin:0;font-size:24px;line-height:1.2}
.head .from{margin:4px 0 0;color:var(--muted)}
.actions{display:flex;flex-wrap:wrap;gap:8px;margin:16px 0 4px}
.btn{flex:1 1 auto;min-height:48px;padding:12px 16px;border:1px solid #c5ced5;border-radius:10px;background:var(--paper);color:var(--ink);font:inherit;font-weight:600;text-align:center;text-decoration:none;cursor:pointer}
.btn.primary{flex-basis:100%;background:var(--accent);border-color:var(--accent);color:var(--accent-ink)}
.btn:focus-visible{outline:3px solid #f2b705;outline-offset:2px}
[hidden]{display:none!important}
.status{min-height:1.2em;margin:4px 0 8px;color:var(--accent);font-size:14px}
.bon{background:var(--paper);border-radius:14px;padding:20px 16px;box-shadow:0 1px 3px rgba(0,0,0,.08)}
.bon h2{margin:0;font-size:19px}
.bon h3{margin:0 0 6px;font-size:14px}
.muted{margin:2px 0;color:var(--muted);font-size:14px}
.banner{margin:0 0 12px;padding:8px 10px;border-radius:8px;background:#fff3cd;color:#6b4e00;font-weight:700;font-size:14px}
.meta{display:flex;flex-wrap:wrap;gap:4px 20px;margin:14px 0;padding:10px 0;border-top:1px solid var(--line);border-bottom:1px solid var(--line)}
.meta dt{color:var(--muted);font-size:12px}.meta dd{margin:0;font-weight:600}
table{width:100%;border-collapse:collapse;margin:10px 0;font-size:15px}
th{text-align:left;color:var(--muted);font-size:12px;font-weight:600;padding:4px 0}
td{padding:6px 0;vertical-align:top}
.items td{border-bottom:1px solid var(--line)}
.num{text-align:right;white-space:nowrap;padding-left:12px}
.sub{display:block;color:var(--muted);font-size:13px}
.sums .total td{font-size:20px;font-weight:700;padding-top:10px}
.vat{font-size:13px}
.pay caption{text-align:left;color:var(--muted);font-size:12px;font-weight:600}
.tse{margin-top:14px;padding-top:12px;border-top:1px solid var(--line);font-size:12px}
.tse dl{margin:0}.tse div{margin:0 0 4px}.tse dt{color:var(--muted)}.tse dd{margin:0;font-family:ui-monospace,Consolas,monospace;overflow-wrap:anywhere}
.note{margin:10px 0 0;font-size:13px;color:var(--muted)}
.plain p{margin:0 0 10px}
.info{margin-top:16px;color:var(--muted);font-size:13px}
.info p{margin:0 0 6px}
.legal a{color:var(--muted)}
@media print{body{background:#fff}.page{max-width:none;padding:0}.actions,.status,.info,.head{display:none}.bon{box-shadow:none;padding:0}}
`
  },
  '/assets/bon.js': {
    type: 'application/javascript; charset=utf-8',
    body: `'use strict';
(function () {
  var status = document.querySelector('.status');
  function say(message) { if (status) status.textContent = message; }
  var print = document.querySelector('[data-action="print"]');
  if (print && typeof window.print === 'function') {
    print.hidden = false;
    print.addEventListener('click', function () { window.print(); });
  }
  var share = document.querySelector('[data-action="share"]');
  var canCopy = !!(navigator.clipboard && window.isSecureContext);
  if (share && (navigator.share || canCopy)) {
    share.hidden = false;
    share.addEventListener('click', function () {
      var url = location.href.split('#')[0];
      if (navigator.share) {
        navigator.share({ title: document.title, url: url }).catch(function () {});
        return;
      }
      navigator.clipboard.writeText(url).then(
        function () { say('Link kopiert.'); },
        function () { say('Link konnte nicht kopiert werden.'); });
    });
  }
})();
`
  }
};

module.exports = {
  FORMAT,
  validateReceipt,
  renderReceiptPage,
  renderNotFoundPage,
  renderHomePage,
  renderReceiptPdf,
  pdfLines,
  euro,
  ASSETS
};
