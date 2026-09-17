'use strict';
// R145: TOR Digital Receipt Cloud end to end - the till publishes through the
// authenticated device API, the customer opens bon.<domain>/r/<token>.
//
// The server runs with TOR_CLOUD_PUBLIC_URL on 127.0.0.1 and the receipt domain
// on "localhost", so both security domains are reachable on one port and are
// told apart by the Host header, exactly as behind the proxy.
const {test,before,after}=require('node:test');
const assert=require('node:assert/strict');
const {spawn}=require('node:child_process');
const http=require('node:http');
const {mkdtempSync,rmSync}=require('node:fs');
const {tmpdir}=require('node:os');
const path=require('node:path');
const crypto=require('node:crypto');
const {DatabaseSync}=require('node:sqlite');

const root=mkdtempSync(path.join(tmpdir(),'tor-cloud-receipts-'));
const dbPath=path.join(root,'db.sqlite');
const device={'X-Device-Code':'DEMO-KASSE-01','X-Device-Token':'tor-demo-device-token-2026'};
const otherDevice={'X-Device-Code':'OTHER-01','X-Device-Token':'other-device-secret'};
let child,base,port;

function receipt(overrides={}){
 return {
  format:'TOR-DIGITALBON-1',test_receipt:false,
  business:{name:'Imbiss am Markt',address:'Hauptstraße 1, 10115 Berlin',tax_number:'27/123/45678',vat_id:''},
  receipt_number:'000145',issued_at:'17.09.2026 12:00:05',pickup_number:null,
  lines:[{name:'Döner',quantity:'1',unit_price_cents:700,line_total_cents:700,vat_rate:'7'}],
  subtotal_cents:700,discount_cents:0,total_cents:700,
  vat:[{rate:'7',net_cents:654,tax_cents:46,gross_cents:700}],
  payments:[{label:'Bar',amount_cents:700}],
  tse:[{label:'Transaktionsnummer',value:'145'},{label:'Vorgangsende',value:'2026-09-17T10:00:05.123Z'}],
  notes:[],
  ...overrides
 };
}
async function publish(ref,body=receipt(),auth=device){
 const r=await fetch(base+'/api/v1/devices/receipts',{method:'POST',headers:{'Content-Type':'application/json',...auth},body:JSON.stringify({receipt_ref:ref,receipt:body})});
 return {status:r.status,body:await r.json()};
}
// A request with a chosen Host header - fetch() does not allow setting one.
function hostRequest(route,{host='localhost',method='GET'}={}){
 return new Promise((resolve,reject)=>{
  const req=http.request({host:'127.0.0.1',port,path:route,method,headers:{Host:host}},res=>{
   const chunks=[];res.on('data',c=>chunks.push(c));
   res.on('end',()=>resolve({status:res.statusCode,headers:res.headers,body:Buffer.concat(chunks)}));
  });
  req.on('error',reject);req.end();
 });
}
function openDb(){const db=new DatabaseSync(dbPath);db.exec('PRAGMA busy_timeout=5000;');return db;}

before(async()=>{
 child=spawn(process.execPath,['server.js'],{cwd:path.join(__dirname,'..'),env:{...process.env,PORT:'0',HOST:'127.0.0.1',TOR_CLOUD_DB:dbPath,TOR_CLOUD_UPDATES:path.join(root,'updates'),TOR_CLOUD_CLEANUP_INTERVAL_MS:'300',TOR_CLOUD_DEMO:'true',TOR_CLOUD_PUBLIC_URL:'http://127.0.0.1:9999',TOR_CLOUD_RECEIPT_URL:'http://localhost:9998',TOR_CLOUD_IMPRINT_URL:'https://tor-pos.de/impressum',TOR_CLOUD_PRIVACY_URL:'https://tor-pos.de/datenschutz',TOR_CLOUD_TRUST_PROXY:'',TOR_CLOUD_BACKUP_DIR:''}});
 base=await new Promise((resolve,reject)=>{let log='';child.stdout.on('data',d=>{log+=d;const m=log.match(/http:\/\/127\.0\.0\.1:(\d+)/);if(m)resolve(m[0]);});child.once('exit',()=>reject(new Error('server exited')));setTimeout(()=>reject(new Error('startup timeout')),10000).unref();});
 port=Number(new URL(base).port);
 const db=openDb();
 db.exec("INSERT INTO businesses(id,name,customer_number,created_at) VALUES(2,'Other','OTHER','2026-09-17'); INSERT INTO branches(id,business_id,name,created_at) VALUES(2,2,'Other','2026-09-17'); INSERT INTO registers(id,branch_id,device_code,name,created_at) VALUES(2,2,'OTHER-01','Other','2026-09-17');");
 db.prepare('INSERT INTO device_tokens(register_id,token_hash,created_at) VALUES(2,?,?)').run(crypto.createHash('sha256').update('other-device-secret').digest('hex'),'2026-09-17');
 db.close();
});
after(async()=>{child?.kill();await new Promise(r=>child?.exitCode!==null?r():child.once('exit',r));rmSync(root,{recursive:true,force:true});});

test('R145 the till publishes a receipt; the customer reads it on the bon domain without login, never indexed or cached',async()=>{
 const anonymous=await fetch(base+'/api/v1/devices/receipts',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({receipt_ref:'sale-145-0001',receipt:receipt()})});
 assert.equal(anonymous.status,401,'publishing needs the device authentication of api.<domain>');

 const published=await publish('sale-145-0001',{...receipt(),operator_name:'kassierer1'});
 assert.equal(published.status,201);
 const link=new URL(published.body.url);
 assert.equal(link.origin,'http://localhost:9998','the link points at the receipt domain');
 const token=link.pathname.slice('/r/'.length);
 assert.match(token,/^[A-Za-z0-9_-]{43}$/,'a 256-bit token, base64url');
 assert.equal(Date.parse(published.body.expires_at)-Date.parse(published.body.created_at),90*24*60*60*1000,'90 days by default');

 const page=await hostRequest(link.pathname);
 assert.equal(page.status,200);
 const html=page.body.toString('utf8');
 for(const part of ['Ihr digitaler Kassenbon','PDF herunterladen','Teilen','Drucken','Imbiss am Markt','Döner','7,00 €','Transaktionsnummer','2026-09-17T10:00:05.123Z'])assert.ok(html.includes(part),part);
 assert.equal(page.headers['cache-control'],'private, no-store');
 assert.equal(page.headers['x-robots-tag'],'noindex, nofollow, noarchive, nosnippet');
 assert.equal(page.headers['referrer-policy'],'no-referrer');
 assert.equal(page.headers['x-frame-options'],'DENY');
 assert.match(page.headers['content-security-policy'],/default-src 'none'; script-src 'self'/);
 assert.equal(page.headers['set-cookie'],undefined,'no cookies on the receipt domain');
 assert.ok(html.includes('<a href="https://tor-pos.de/impressum" rel="noopener noreferrer">Impressum</a>')&&html.includes('Datenschutz</a>'),'Impressum and privacy notice of the operator are linked');

 const pdf=await hostRequest(link.pathname+'/pdf');
 assert.equal(pdf.status,200);
 assert.equal(pdf.headers['content-type'],'application/pdf');
 assert.equal(pdf.headers['content-disposition'],'attachment; filename="Kassenbon-000145.pdf"');
 assert.equal(pdf.headers['x-robots-tag'],'noindex, nofollow, noarchive, nosnippet');
 assert.equal(pdf.headers['cache-control'],'private, no-store');
 assert.equal(pdf.body.subarray(0,8).toString('latin1'),'%PDF-1.4');
 assert.ok(pdf.body.toString('latin1').includes('(D\\366ner) Tj'));

 const head=await hostRequest(link.pathname,{method:'HEAD'});
 assert.equal(head.status,200);
 assert.equal(head.body.length,0);

 const db=openDb();
 const row=db.prepare('SELECT business_id,register_id,token_hash,document_json,created_at,expires_at FROM public_receipts WHERE receipt_ref=?').get('sale-145-0001');
 db.close();
 assert.equal(row.business_id,1);
 assert.equal(row.token_hash,crypto.createHash('sha256').update(token).digest('hex'),'only the hash of the token is stored');
 assert.ok(!JSON.stringify(row).includes(token),'the token itself is nowhere in the database');
 assert.ok(!row.document_json.includes('kassierer1'),'fields a receipt does not show are not stored');
});

test('R145 receipts and the rest of the Cloud are separate security domains',async()=>{
 const {body}=await publish('sale-145-0002');
 const route=new URL(body.url).pathname;
 const onApi=await hostRequest(route,{host:`127.0.0.1:${port}`});
 assert.equal(onApi.status,404,'no receipt on the API/portal domain');
 assert.ok(!onApi.body.toString('utf8').includes('Imbiss am Markt'));
 for(const other of ['/api/health','/portal','/login','/index.html','/updates/manifest.json','/api/v1/devices/ping']){
  const r=await hostRequest(other);
  assert.equal(r.status,404,`${other} does not exist on the receipt domain`);
  assert.equal(r.headers['x-robots-tag'],'noindex, nofollow, noarchive, nosnippet');
 }
 assert.equal((await hostRequest('/api/v1/devices/receipts',{method:'POST'})).status,405,'the receipt domain takes no uploads');
 const robots=await hostRequest('/robots.txt');
 assert.equal(robots.status,200);
 assert.ok(!robots.body.toString('utf8').includes('Disallow'),'robots.txt does not block the receipts - a blocked page never shows its noindex to a search engine');
 assert.equal(robots.headers['x-robots-tag'],'noindex, nofollow, noarchive, nosnippet');
 const home=await hostRequest('/');
 assert.equal(home.status,200);
 assert.ok(home.body.toString('utf8').includes('persönlichen Link'));
 const script=await hostRequest('/assets/bon.js');
 assert.equal(script.status,200);
 assert.match(script.headers['content-type'],/^application\/javascript/);
 assert.equal((await hostRequest('/assets/constructor')).status,404);
});

test('R145 a receipt is immutable; asking again for the same receipt only replaces the link',async()=>{
 const first=await publish('sale-145-0003');
 assert.equal(first.status,201);
 const again=await publish('sale-145-0003');
 assert.equal(again.status,200);
 assert.equal(again.body.reissued,true);
 assert.equal(again.body.receipt_id,first.body.receipt_id);
 assert.equal(again.body.expires_at,first.body.expires_at,'asking again does not extend the lifetime');
 assert.notEqual(again.body.url,first.body.url);
 assert.equal((await hostRequest(new URL(first.body.url).pathname)).status,404,'the link the till never received stops working');
 assert.equal((await hostRequest(new URL(again.body.url).pathname)).status,200);

 const changed=receipt({lines:[{name:'Döner',quantity:'2',unit_price_cents:350,line_total_cents:700,vat_rate:'7'}]});
 const conflict=await publish('sale-145-0003',changed);
 assert.equal(conflict.status,409);
 assert.match(conflict.body.error,/unveränderlich/);

 const wrongTotal=await publish('sale-145-0004',receipt({total_cents:699}));
 assert.equal(wrongTotal.status,400);
 assert.match(wrongTotal.body.error,/Gesamtbetrag/);
 assert.equal((await publish('x',receipt())).status,400);
});

test('R145 a receipt belongs to the business of the till that published it',async()=>{
 const own=await publish('sale-145-0005',receipt(),otherDevice);
 assert.equal(own.status,201,'the same reference is independent per till');
 const db=openDb();
 const row=db.prepare('SELECT business_id,register_id FROM public_receipts WHERE id=?').get(own.body.receipt_id);
 db.close();
 assert.deepEqual({...row},{business_id:2,register_id:2});
 assert.equal((await publish('sale-145-0006',receipt(),{'X-Device-Code':'OTHER-01','X-Device-Token':'wrong'})).status,403);
});

test('R145 an expired receipt is gone at once and then deleted entirely',async()=>{
 const {body}=await publish('sale-145-0007');
 const route=new URL(body.url).pathname;
 assert.equal((await hostRequest(route)).status,200);
 let db=openDb();
 db.prepare('UPDATE public_receipts SET expires_at=? WHERE id=?').run('2000-01-01T00:00:00.000Z',body.receipt_id);
 db.close();
 assert.equal((await hostRequest(route)).status,404,'not served once expired, even before housekeeping runs');
 assert.equal((await hostRequest(route+'/pdf')).status,404);
 let remaining=1;
 for(let i=0;i<50&&remaining;i++){
  await new Promise(r=>setTimeout(r,100));
  db=openDb();
  remaining=db.prepare('SELECT COUNT(*) n FROM public_receipts WHERE id=?').get(body.receipt_id).n;
  db.close();
 }
 assert.equal(remaining,0,'token hash, PDF and content are deleted by housekeeping');
});

test('R145 an address asking for many unknown links is slowed down; a valid link still works',async()=>{
 const {body}=await publish('sale-145-0008');
 let last;
 for(let i=0;i<61;i++)last=await hostRequest('/r/'+'x'.repeat(43));
 assert.equal(last.status,429);
 assert.equal((await hostRequest(new URL(body.url).pathname)).status,200);
});
