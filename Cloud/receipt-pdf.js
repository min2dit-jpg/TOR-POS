'use strict';

// R145: a small, dependency-free PDF writer for the digital Kassenbon.
//
// AEAO zu § 146a Nr. 2.5.6 asks for a standardised data format that the
// customer can open with free standard software (JPG, PNG and PDF are the
// examples given); TOR offers a PDF download as its standard. A receipt needs
// nothing but monospaced text, so the writer uses the Courier base font every
// PDF viewer carries: no embedded font, no compression, one content stream per
// A4 page, a receipt-wide column in the middle.

const PAGE_WIDTH = 595.28;
const PAGE_HEIGHT = 841.89;
const FONT_SIZE = 10;
const LEADING = 12.5;
const COLUMNS = 48;
// Courier: every glyph is 600/1000 em wide, so a column is a character count.
const LEFT = Number(((PAGE_WIDTH - COLUMNS * FONT_SIZE * 0.6) / 2).toFixed(2));
const TOP = Number((PAGE_HEIGHT - 56).toFixed(2));
const BOTTOM = 64;
const LINES_PER_PAGE = Math.floor((TOP - BOTTOM) / LEADING) + 1;

// WinAnsiEncoding 0x80-0x9F; 0xA0-0xFF are the Latin-1 code points themselves.
const WIN_ANSI = new Map([
  [0x20AC, 0x80], [0x201A, 0x82], [0x201E, 0x84], [0x2026, 0x85], [0x2020, 0x86],
  [0x2021, 0x87], [0x02C6, 0x88], [0x2030, 0x89], [0x0160, 0x8A], [0x2039, 0x8B],
  [0x0152, 0x8C], [0x017D, 0x8E], [0x2018, 0x91], [0x2019, 0x92], [0x201C, 0x93],
  [0x201D, 0x94], [0x2022, 0x95], [0x2013, 0x96], [0x2014, 0x97], [0x02DC, 0x98],
  [0x2122, 0x99], [0x0161, 0x9A], [0x203A, 0x9B], [0x0153, 0x9C], [0x017E, 0x9E],
  [0x0178, 0x9F]
]);
// Turkish letters are not in WinAnsi but are common in product names ("Şiş",
// "Çiğ Köfte"). The base fonts carry the glyphs, so an encoding difference maps
// them onto the unused slots (and florin, which no receipt needs).
const EXTRA_GLYPHS = [
  [0x015E, 0x81, 'Scedilla'], [0x015F, 0x8D, 'scedilla'], [0x011E, 0x8F, 'Gbreve'],
  [0x011F, 0x90, 'gbreve'], [0x0130, 0x9D, 'Idotaccent'], [0x0131, 0x83, 'dotlessi']
];
const EXTRA = new Map(EXTRA_GLYPHS.map(([cp, byte]) => [cp, byte]));

function byteFor(cp) {
  if (cp >= 0x20 && cp < 0x7F) return cp;
  if (cp >= 0xA0 && cp <= 0xFF) return cp;
  return WIN_ANSI.get(cp) ?? EXTRA.get(cp);
}

/** Text to single-byte codes; letters outside the encoding lose their accent, anything else becomes "?". */
function encode(text) {
  const bytes = [];
  for (const ch of String(text ?? '').replace(/\t/g, ' ')) {
    let byte = byteFor(ch.codePointAt(0));
    if (byte === undefined) {
      const base = ch.normalize('NFD').replace(/[\u0300-\u036f]/g, '');
      if (base.length === 1) byte = byteFor(base.codePointAt(0));
    }
    bytes.push(byte === undefined ? 0x3F : byte);
  }
  return bytes;
}

function literal(bytes) {
  let out = '(';
  for (const b of bytes) {
    if (b === 0x28 || b === 0x29 || b === 0x5C) out += '\\' + String.fromCharCode(b);
    else if (b < 0x20 || b > 0x7E) out += '\\' + b.toString(8).padStart(3, '0');
    else out += String.fromCharCode(b);
  }
  return out + ')';
}

function utf16Text(text) {
  return '<FEFF' + Buffer.from(String(text), 'utf16le').swap16().toString('hex').toUpperCase() + '>';
}

/** Number of columns a string takes in the monospaced layout. */
function width(text) {
  return Array.from(String(text ?? '')).length;
}

/** Word wrap to `columns`; a word longer than a line (a signature) is cut. */
function wrap(text, columns = COLUMNS) {
  const lines = [];
  let current = '';
  for (const word of String(text ?? '').replace(/\s+/g, ' ').trim().split(' ')) {
    if (!word) continue;
    let rest = Array.from(word);
    if (current && width(current) + 1 + rest.length <= columns) {
      current += ' ' + word;
      continue;
    }
    if (current) lines.push(current);
    current = '';
    while (rest.length > columns) {
      lines.push(rest.slice(0, columns).join(''));
      rest = rest.slice(columns);
    }
    current = rest.join('');
  }
  if (current) lines.push(current);
  return lines;
}

/**
 * Builds the PDF. `lines` are { text, bold } objects already laid out to at
 * most COLUMNS characters; they flow onto as many A4 pages as needed.
 */
function buildPdf({ title, lines }) {
  const pages = [];
  for (let i = 0; i < lines.length; i += LINES_PER_PAGE) pages.push(lines.slice(i, i + LINES_PER_PAGE));
  if (pages.length === 0) pages.push([]);

  const objects = [];
  objects[0] = '<< /Type /Catalog /Pages 2 0 R >>';
  objects[2] = '<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding 5 0 R >>';
  objects[3] = '<< /Type /Font /Subtype /Type1 /BaseFont /Courier-Bold /Encoding 5 0 R >>';
  objects[4] = `<< /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [${EXTRA_GLYPHS.map(([, byte, name]) => `${byte} /${name}`).join(' ')}] >>`;
  objects[5] = `<< /Title ${utf16Text(title)} /Creator (TOR POS) /Producer (TOR POS Cloud) >>`;

  const kids = [];
  pages.forEach((pageLines, index) => {
    const pageNo = 7 + index * 2;
    const contentNo = pageNo + 1;
    kids.push(`${pageNo} 0 R`);
    let stream = `BT\n/F1 ${FONT_SIZE} Tf\n${LEADING} TL\n${LEFT} ${TOP} Td\n`;
    let bold = false;
    for (const line of pageLines) {
      if (!!line.bold !== bold) {
        bold = !!line.bold;
        stream += `/${bold ? 'F2' : 'F1'} ${FONT_SIZE} Tf\n`;
      }
      stream += `${literal(encode(line.text))} Tj T*\n`;
    }
    stream += 'ET\n';
    if (pages.length > 1) {
      const label = `Seite ${index + 1} von ${pages.length}`;
      const x = Number((LEFT + (COLUMNS - width(label)) * FONT_SIZE * 0.6).toFixed(2));
      stream += `BT\n/F1 ${FONT_SIZE} Tf\n${x} 36 Td\n${literal(encode(label))} Tj\nET\n`;
    }
    objects[pageNo - 1] = `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${PAGE_WIDTH} ${PAGE_HEIGHT}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents ${contentNo} 0 R >>`;
    // The stream is pure ASCII (literal() escapes every other byte), so its
    // character count is its byte length.
    objects[contentNo - 1] = `<< /Length ${stream.length} >>\nstream\n${stream}endstream`;
  });
  objects[1] = `<< /Type /Pages /Kids [${kids.join(' ')}] /Count ${pages.length} >>`;

  let out = '%PDF-1.4\n%\xE2\xE3\xCF\xD3\n';
  const offsets = [];
  objects.forEach((body, index) => {
    offsets.push(Buffer.byteLength(out, 'latin1'));
    out += `${index + 1} 0 obj\n${body}\nendobj\n`;
  });
  const xref = Buffer.byteLength(out, 'latin1');
  out += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  out += offsets.map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('');
  out += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R /Info 6 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(out, 'latin1');
}

module.exports = { buildPdf, wrap, width, encode, COLUMNS, LINES_PER_PAGE };
