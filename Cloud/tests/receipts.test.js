'use strict';
// R145: the digital receipt document, its page and its PDF, without a server.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const {validateReceipt,renderReceiptPage,renderReceiptPdf,pdfLines,euro,FORMAT}=require('../receipts');
const {buildPdf,wrap,encode,COLUMNS,LINES_PER_PAGE}=require('../receipt-pdf');
const {readFileSync}=require('node:fs');
const path=require('node:path');

function receipt(overrides={}){
 return {
  format:FORMAT,test_receipt:false,
  business:{name:'Imbiss am Markt',address:'Hauptstraße 1, 10115 Berlin',tax_number:'27/123/45678',vat_id:''},
  receipt_number:'000145',issued_at:'17.09.2026 12:00:05',pickup_number:23,
  lines:[{name:'Çiğ Köfte Dürüm',quantity:'2',unit_price_cents:650,line_total_cents:1300,vat_rate:'7'},
         {name:'Ayran',quantity:'1',unit_price_cents:250,line_total_cents:250,vat_rate:'19',note:'Angebot -10 %'}],
  subtotal_cents:1550,discount_cents:50,total_cents:1500,
  vat:[{rate:'19',net_cents:202,tax_cents:38,gross_cents:240},{rate:'7',net_cents:1178,tax_cents:82,gross_cents:1260}],
  payments:[{label:'Bar',amount_cents:1000},{label:'Karte',amount_cents:500}],
  tse:[{label:'Kassen-Seriennummer',value:'TORPOS-538AE94ABA78A2058181215'},{label:'Prüfwert',value:'M'.repeat(120)}],
  notes:['Beleg nach § 146a Abs. 2 AO.'],
  ...overrides
 };
}

// A PDF reader starts at startxref and trusts every offset in the table.
function assertPdfStructure(pdf){
 const text=pdf.toString('latin1');
 assert.ok(text.startsWith('%PDF-1.4\n'));
 assert.ok(text.endsWith('%%EOF\n'));
 const xref=Number(/startxref\n(\d+)\n%%EOF\n$/.exec(text)[1]);
 assert.equal(text.slice(xref,xref+5),'xref\n');
 const [,count]=/^xref\n0 (\d+)\n/.exec(text.slice(xref));
 const entries=text.slice(xref).split('\n').slice(3,3+Number(count)-1);
 entries.forEach((entry,i)=>{
  const offset=Number(entry.slice(0,10));
  assert.equal(text.slice(offset,offset+`${i+1} 0 obj`.length),`${i+1} 0 obj`,`object ${i+1} at its xref offset`);
 });
 for(const m of text.matchAll(/<< \/Length (\d+) >>\nstream\n/g)){
  const start=m.index+m[0].length;
  assert.equal(text.slice(start+Number(m[1]),start+Number(m[1])+9),'endstream','stream length matches');
 }
 return text;
}

test('R145 the document keeps exactly the receipt fields and drops everything else',()=>{
 const doc=validateReceipt(receipt({operator_name:'kassierer1',customer_email:'a@b.de',business:{name:'  Imbiss   am Markt ',address:'Hauptstraße 1, 10115 Berlin',owner:'x'}}));
 assert.equal(doc.business.name,'Imbiss am Markt');
 assert.equal(doc.operator_name,undefined);
 assert.equal(doc.customer_email,undefined);
 assert.equal(doc.business.owner,undefined);
 assert.deepEqual(Object.keys(doc),['format','test_receipt','business','receipt_number','issued_at','pickup_number','lines','subtotal_cents','discount_cents','total_cents','vat','payments','tse','notes']);
});

test('R145 figures that contradict each other are refused',()=>{
 const refused=(overrides,pattern)=>assert.throws(()=>validateReceipt(receipt(overrides)),e=>e.statusCode===400&&pattern.test(e.message));
 refused({format:'OTHER'},/Belegformat/);
 refused({subtotal_cents:1551},/Zwischensumme/);
 refused({total_cents:1499},/Gesamtbetrag/);
 refused({vat:[{rate:'19',net_cents:202,tax_cents:39,gross_cents:240},{rate:'7',net_cents:1178,tax_cents:82,gross_cents:1260}]},/Netto und Steuer/);
 refused({vat:[{rate:'7',net_cents:1178,tax_cents:82,gross_cents:1260}]},/MwSt.-Gruppen/);
 refused({payments:[{label:'Bar',amount_cents:1000}]},/Zahlungen/);
 refused({lines:[]},/lines fehlt/);
 refused({business:{name:''}},/business.name fehlt/);
 refused({lines:[{name:'A',quantity:'1',unit_price_cents:1.5,line_total_cents:1550,vat_rate:'7'}],subtotal_cents:1550},/Centbetrag/);
 refused({receipt_number:'1'.repeat(41)},/zu lang/);
});

test('R145 the page is the readable receipt with PDF herunterladen, Teilen and Drucken, and escapes everything',()=>{
 const doc=validateReceipt(receipt({business:{name:'<script>alert(1)</script>',address:'Str. 1'}}));
 const token='A'.repeat(43);
 const html=renderReceiptPage(doc,{token,expiresAt:'2026-12-16T11:00:05.000Z'});
 assert.match(html,/<h1>Ihr digitaler Kassenbon<\/h1>/);
 assert.match(html,new RegExp(`href="/r/${token}/pdf" download>PDF herunterladen<`));
 assert.match(html,/data-action="share" hidden>Teilen</);
 assert.match(html,/data-action="print" hidden>Drucken</);
 assert.match(html,/<meta name="robots" content="noindex, nofollow, noarchive">/);
 assert.ok(!html.includes('<script>alert'));
 assert.ok(html.includes('&lt;script&gt;alert(1)&lt;/script&gt;'));
 assert.ok(html.includes('Çiğ Köfte Dürüm')&&html.includes('15,00 €')&&html.includes('Abholnummer')&&html.includes('023'));
 assert.ok(html.includes('bis zum 16.12.2026'));
 assert.ok(!html.includes('TESTBON'));
 assert.ok(renderReceiptPage(validateReceipt(receipt({test_receipt:true})),{token,expiresAt:'2026-12-16T11:00:05.000Z'}).includes('TESTBON · KEIN FISKALBELEG'));
});

test('R145 money is formatted the German way',()=>{
 assert.equal(euro(0),'0,00 €');
 assert.equal(euro(5),'0,05 €');
 assert.equal(euro(123456789),'1.234.567,89 €');
 assert.equal(euro(-1500),'-15,00 €');
});

test('R145 the PDF is a well-formed document with the receipt in one column',()=>{
 const doc=validateReceipt(receipt());
 const lines=pdfLines(doc);
 assert.ok(lines.every(line=>Array.from(line.text).length<=COLUMNS),'no line wider than the column');
 assert.ok(lines.some(line=>line.bold&&/GESAMT +15,00 €$/.test(line.text)));
 // label and value do not fit one line: the value follows indented
 const serial=lines.findIndex(line=>line.text==='Kassen-Seriennummer:');
 assert.ok(serial>0&&lines[serial+1].text==='  TORPOS-538AE94ABA78A2058181215');
 assert.ok(lines.some(line=>line.text==='Technische Sicherheitseinrichtung'&&line.bold));
 const text=assertPdfStructure(renderReceiptPdf(doc));
 assert.match(text,/\/Count 1 >>/);
 assert.match(text,/\/BaseFont \/Courier /);
 // € is 0x80 (octal 200), Ç 0xC7 (307), ğ the encoding difference 0x90 (220)
 assert.ok(text.includes('\\200'));
 assert.ok(text.includes('(\\307i\\220 K\\366fte D\\374r\\374m) Tj'));
});

test('R145 letters outside the font encoding keep their base letter; a long receipt flows onto more pages',()=>{
 assert.deepEqual(encode('Şş Ğğ İı'),[0x81,0x8D,0x20,0x8F,0x90,0x20,0x9D,0x83]);
 assert.deepEqual(encode('Łódź'),[0x3F,0xF3,0x64,0x7A]);
 assert.deepEqual(encode('ą€'),[0x61,0x80]);
 assert.deepEqual(wrap('x'.repeat(100)).map(l=>l.length),[48,48,4]);
 const many=Array.from({length:120},(_,i)=>({text:`Zeile ${i+1}`}));
 const text=assertPdfStructure(buildPdf({title:'Kassenbon',lines:many}));
 const pages=Math.ceil(120/LINES_PER_PAGE);
 assert.ok(pages>1);
 assert.match(text,new RegExp(`/Count ${pages} >>`));
 assert.ok(text.includes(`(Seite ${pages} von ${pages}) Tj`));
 assert.ok(text.includes('(Zeile 120) Tj'));
});

test('R145 the document a till sends is accepted unchanged (the same file Desktop R145ReviewTests builds)',()=>{
 const fixture=JSON.parse(readFileSync(path.join(__dirname,'fixtures','digitalbon-kasse.json'),'utf8'));
 assert.match(fixture.receipt_ref,/^[A-Za-z0-9._:-]{8,120}$/);
 assert.deepEqual(validateReceipt(fixture.receipt),fixture.receipt);
 assertPdfStructure(renderReceiptPdf(validateReceipt(fixture.receipt)));
});

// R149: the sale event of a real till. Until R149 TOR Cloud required discount_cents
// (the till sent manual_discount_cents) and refused MIXED - every sale of a till in
// real operation was rejected and the till's outbox stuck at its first sale.
const {normalizeEvent}=require('../validation');
function saleEvent(payload){return {event_id:'r149-sale',type:'sale.completed',occurred_at:'2026-09-17T12:00:05+02:00',payload};}

test('R149 the sale event a till queues is accepted - including returned deposit paid out in cash',()=>{
 const fixture=JSON.parse(readFileSync(path.join(__dirname,'fixtures','sale-completed-kasse.json'),'utf8'));
 const accepted=normalizeEvent(saleEvent(structuredClone(fixture.payload)));
 assert.equal(accepted.payload.total_cents,-50);
 assert.equal(accepted.payload.items[1].line_total_cents,-300);
});

test('R149 MIXED and an older till without discount_cents are accepted; a payout on card is not',()=>{
 const fixture=JSON.parse(readFileSync(path.join(__dirname,'fixtures','sale-completed-kasse.json'),'utf8'));
 const purchase=p=>({...structuredClone(fixture.payload),...p});
 const items=[{position_no:1,product_key:'1',name:'Cola 0,5l',quantity:2,unit_price_cents:250,line_total_cents:500,vat_rate:19}];
 assert.doesNotThrow(()=>normalizeEvent(saleEvent(purchase({payment_method:'MIXED',items,item_count:1,subtotal_cents:500,total_cents:500,cash_portion_cents:200,card_portion_cents:300,stock_consumption:[]}))));
 const older=purchase({items,item_count:1,subtotal_cents:500,total_cents:450,discount_cents:undefined,manual_discount_cents:50,stock_consumption:[]});
 delete older.discount_cents;
 assert.equal(normalizeEvent(saleEvent(older)).payload.discount_cents,50);
 assert.throws(()=>normalizeEvent(saleEvent(purchase({payment_method:'CARD'}))),/nur bar/);
 assert.throws(()=>normalizeEvent(saleEvent(purchase({total_cents:0}))),/Gesamt/);
});

test('R149 a digital receipt of returned deposit paid out is accepted as it adds up',()=>{
 const payout=validateReceipt(receipt({
  lines:[{name:'Cola 0,5l',quantity:'1',unit_price_cents:250,line_total_cents:250,vat_rate:'19'},
         {name:'PFAND-RÜCKGABE · 25 CENT',quantity:'12',unit_price_cents:-25,line_total_cents:-300,vat_rate:'19'}],
  subtotal_cents:-50,discount_cents:0,total_cents:-50,
  vat:[{rate:'19',net_cents:-42,tax_cents:-8,gross_cents:-50}],
  payments:[{label:'Bar',amount_cents:-50}]}));
 assert.equal(payout.total_cents,-50);
 assert.ok(renderReceiptPage(payout,{token:'A'.repeat(43),expiresAt:'2026-12-16T11:00:05.000Z'}).includes('-0,50 €'));
});
