'use strict';
const {test,before,after}=require('node:test');
const assert=require('node:assert/strict');
const {spawn}=require('node:child_process');
const {mkdtempSync,rmSync,mkdirSync,writeFileSync,readFileSync,statSync,utimesSync}=require('node:fs');
const {tmpdir}=require('node:os');
const path=require('node:path');
const {DatabaseSync}=require('node:sqlite');
const crypto=require('node:crypto');
const {normalizeEvent,berlinParts}=require('../validation');
const root=mkdtempSync(path.join(tmpdir(),'tor-cloud-test-'));
let child,base,cookie,otherCookie,viewerCookie;
const backups=path.join(root,'backups');
const headers={'X-Device-Code':'DEMO-KASSE-01','X-Device-Token':'tor-demo-device-token-2026'};
const BASE32='ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
function base32Decode(text){const clean=String(text||'').toUpperCase().replace(/[^A-Z2-7]/g,'');let bits=0,value=0;const out=[];for(const ch of clean){const idx=BASE32.indexOf(ch);value=(value<<5)|idx;bits+=5;if(bits>=8){out.push((value>>>(bits-8))&255);bits-=8;}}return Buffer.from(out);}
function totp(secret){const counter=BigInt(Math.floor(Date.now()/30000)),msg=Buffer.alloc(8);msg.writeBigUInt64BE(counter);const h=crypto.createHmac('sha1',base32Decode(secret)).update(msg).digest(),off=h[h.length-1]&15,bin=((h[off]&127)<<24)|(h[off+1]<<16)|(h[off+2]<<8)|h[off+3];return String(bin%1000000).padStart(6,'0');}
async function request(route,body,extra={}){
 const r=await fetch(base+route,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json',...extra},body:body===undefined?undefined:JSON.stringify(body)});
 return {status:r.status,body:await r.json(),cookie:r.headers.get('set-cookie')?.split(';')[0],headers:r.headers};
}
function event(id='test-sale',receipt=900){return {event_id:id,type:'sale.completed',occurred_at:'2026-09-07T00:30:00+02:00',payload:{receipt_number:receipt,payment_method:'CASH',subtotal_cents:900,discount_cents:100,total_cents:800,operator_name:'test',item_count:1,items:[{position_no:1,product_key:'1',name:'A',quantity:3,unit_price_cents:300,line_total_cents:900,vat_rate:19}]}};}
async function sync(events,auth=headers){return request('/api/v1/devices/sync',{events},auth);}
before(async()=>{
 const updates=path.join(root,'updates');mkdirSync(updates,{recursive:true});const setup=Buffer.from('TOR POS fake setup for updater test');writeFileSync(path.join(updates,'TOR-POS-Pro-Setup.exe'),setup);const sha=crypto.createHash('sha256').update(setup).digest('hex').toUpperCase();
 writeFileSync(path.join(updates,'manifest.json'),JSON.stringify({enabled:true,version:'0.7.33.48',revision:'R48-Test',published_at:'2026-09-08T00:00:00Z',mandatory:false,editions:['KIOSK','IMBISS'],filename:'TOR-POS-Pro-Setup.exe',sha256:sha,signer_thumbprint:'',release_notes:'Updater test'}));
 const demoSetup=Buffer.from('TOR POS fake seven day demo setup');const demoFile='TOR-POS-Demo-Setup.exe';writeFileSync(path.join(updates,demoFile),demoSetup);const demoSha=crypto.createHash('sha256').update(demoSetup).digest('hex').toUpperCase();
 writeFileSync(path.join(updates,'trial-manifest.json'),JSON.stringify({enabled:true,version:'0.7.33.849',revision:'R149-Demo-Test',published_at:'2026-09-19T00:00:00Z',editions:['KIOSK','IMBISS'],filename:demoFile,sha256:demoSha,signer_thumbprint:'A'.repeat(40),trial_days:7,release_notes:'7-day demo test'}));
 // R125: four stale backups from 2020 - the startup backup must be written
 // (none is recent) and retention must cut the set down to TOR_CLOUD_BACKUP_KEEP=3.
 mkdirSync(backups,{recursive:true});for(const day of ['01','02','03','04'])writeFileSync(path.join(backups,`tor-cloud-202001${day}T000000Z.db`),'stale');
 child=spawn(process.execPath,['server.js'],{cwd:path.join(__dirname,'..'),env:{...process.env,PORT:'0',HOST:'127.0.0.1',TOR_CLOUD_DB:path.join(root,'db.sqlite'),TOR_CLOUD_UPDATES:updates,TOR_CLOUD_CLEANUP_INTERVAL_MS:'500',TOR_CLOUD_BACKUP_DIR:backups,TOR_CLOUD_BACKUP_KEEP:'3',TOR_CLOUD_DEMO:'true',TOR_CLOUD_PUBLIC_URL:'http://127.0.0.1:9999',TOR_GOOGLE_OAUTH_CLIENT_ID:'test-client.apps.googleusercontent.com',TOR_GOOGLE_OAUTH_CLIENT_SECRET:'test-secret',TOR_CLOUD_GOOGLE_TOKEN_KEY:'test-google-token-key-material-1234567890'}});
 // Port 0 is reported by the startup message using server.address().port.
 base=await new Promise((resolve,reject)=>{let log='';child.stdout.on('data',d=>{log+=d;const m=log.match(/http:\/\/127\.0\.0\.1:(\d+)/);if(m)resolve(m[0]);});child.once('exit',()=>reject(new Error('server exited')));setTimeout(()=>reject(new Error('startup timeout')),10000).unref();});
 cookie=(await request('/api/login',{email:'demo@torpos.local',password:'TorDemo2026!'})).cookie;
 const db=new DatabaseSync(path.join(root,'db.sqlite'));db.exec('PRAGMA busy_timeout=5000;');
 db.exec("INSERT INTO businesses(id,name,customer_number,created_at) VALUES(2,'Other','OTHER','2026-09-07'); INSERT INTO branches(id,business_id,name,created_at) VALUES(2,2,'Other','2026-09-07'); INSERT INTO registers(id,branch_id,device_code,name,created_at) VALUES(2,2,'OTHER-01','Other','2026-09-07');");
 const salt='review-salt',hash=crypto.scryptSync('test-password',salt,64).toString('hex');
 for(const [email,business,role] of [['other@test.local',2,'OWNER'],['viewer@test.local',1,'EMPLOYEE']]) db.prepare('INSERT INTO users(business_id,email,display_name,role,password_salt,password_hash,created_at) VALUES(?,?,?,?,?,?,?)').run(business,email,email,role,salt,hash,'2026-09-07');
 db.prepare('INSERT INTO device_tokens(register_id,token_hash,created_at) VALUES(2,?,?)').run(crypto.createHash('sha256').update('other-device-secret').digest('hex'),'2026-09-07');db.close();
 otherCookie=(await request('/api/login',{email:'other@test.local',password:'test-password'})).cookie;
 viewerCookie=(await request('/api/login',{email:'viewer@test.local',password:'test-password'})).cookie;
});
after(async()=>{child?.kill();await new Promise(r=>child?.exitCode!==null?r():child.once('exit',r));rmSync(root,{recursive:true,force:true});});
test('login, demo data, receipt detail and tenant isolation',async()=>{
 const p=await request('/api/portal/data',undefined,{Cookie:cookie});assert.equal(p.status,200);assert.equal(p.body.saleTotal,8);
 const id=p.body.sales[0].sale_id;
 assert.ok((await request('/api/receipts/'+id,undefined,{Cookie:cookie})).body.receipt.items.length);
 assert.equal((await request('/api/receipts/'+id)).status,401);
 assert.equal((await request('/api/receipts/'+id,undefined,{Cookie:otherCookie})).status,404);
 assert.equal((await request('/api/portal/data',undefined,{Cookie:viewerCookie})).status,403);
});
test('consistent sale persists once; changed repeat is conflict',async()=>{
 assert.equal((await sync([event()])).body.accepted,1);
 assert.equal((await sync([event()])).body.duplicates,1);
 const changed=event();changed.payload.operator_name='changed';assert.equal((await sync([changed])).status,409);
 const p=await request('/api/portal/data',undefined,{Cookie:cookie});assert.equal(p.body.saleTotal,9);
});
test('R48 IMBISS pickup number is persisted and returned in receipt detail',async()=>{
 const sale=event('r48-pickup',948);sale.payload.pickup_number=23;
 assert.equal((await sync([sale])).body.accepted,1);
 const p=await request('/api/portal/data',undefined,{Cookie:cookie});
 const row=p.body.sales.find(x=>x.receipt_number===948);assert.ok(row);assert.equal(row.pickup_number,23);
 const detail=await request('/api/receipts/'+row.sale_id,undefined,{Cookie:cookie});
 assert.equal(detail.status,200);assert.equal(detail.body.receipt.pickup_number,23);
});

test('same event ID on a different register does not conflict',async()=>{
 assert.equal((await sync([event()],{'X-Device-Code':'OTHER-01','X-Device-Token':'other-device-secret'})).body.accepted,1);
 assert.equal((await request('/api/portal/data',undefined,{Cookie:otherCookie})).body.saleTotal,1);
});
test('invalid values and HTML payment types rejected; whole batch rolls back',async()=>{
 const invalid=event('invalid');invalid.payload.total_cents=1;
 assert.equal((await sync([event('must-rollback'),invalid])).status,400);
 assert.equal((await sync([event('must-rollback')])).body.accepted,1);
 const html=event('html');html.payload.payment_method='<img src=x onerror=alert(1)>';assert.equal((await sync([html])).status,400);
 const badLine=event('badline');badLine.payload.items[0].quantity=4;assert.equal((await sync([badLine])).status,400);
 const unknown={...event('unknown'),type:'sale.completedd'};assert.equal((await sync([unknown])).status,400);
 assert.equal((await request('/api/v1/devices/sync',{},headers)).status,400);
});
test('snapshots replace removed products and older snapshots cannot reverse stock',async()=>{
 const stock=(id,at,items)=>({event_id:id,type:'stock.snapshot',occurred_at:at,payload:{items}});
 assert.equal((await sync([stock('new-stock','2026-09-07T15:00:00Z',[{product_key:'r41',name:'Local product',quantity:12}])])).status,200);
 await sync([stock('old-stock','2026-09-07T14:00:00Z',[{product_key:'old',name:'Old',quantity:1}])]);
 let p=await request('/api/portal/data',undefined,{Cookie:cookie});assert.equal(p.body.stock.length,1);assert.equal(p.body.stock[0].product_key,'r41');
 await sync([stock('empty-stock','2026-09-07T16:00:00Z',[])]);
 p=await request('/api/portal/data',undefined,{Cookie:cookie});assert.equal(p.body.stock.length,0);
});
test('R42 stock snapshot keeps barcode, SKU, category, price and unit',async()=>{
 const stock={event_id:'r42-product-info',type:'stock.snapshot',occurred_at:'2026-09-07T17:00:00Z',payload:{items:[{
  product_key:'42',name:'R42 Cola',sku:'COLA42',barcode:'4000000000042',group_name:'Getränke',category_name:'Softdrinks',unit:'Stück',price_cents:280,quantity:9
 }]}};
 assert.equal((await sync([stock])).status,200);
 const p=await request('/api/portal/data',undefined,{Cookie:cookie});
 assert.equal(p.body.stock.length,1);const item=p.body.stock[0];
 assert.equal(item.sku,'COLA42');assert.equal(item.barcode,'4000000000042');assert.equal(item.category_name,'Softdrinks');assert.equal(item.group_name,'Getränke');assert.equal(item.price_cents,280);assert.equal(item.unit,'Stück');
});
test('R47 stock snapshot keeps Mindestbestand and Einkaufspreis',async()=>{
 const stock={event_id:'r47-product-info',type:'stock.snapshot',occurred_at:'2026-09-07T17:30:00Z',payload:{items:[{
  product_key:'47',name:'R47 Cola',sku:'COLA47',barcode:'4000000000047',group_name:'Getränke',category_name:'Softdrinks',unit:'Stück',price_cents:290,purchase_price_cents:110,min_stock_quantity:6,quantity:5
 }]}};
 assert.equal((await sync([stock])).status,200);
 const p=await request('/api/portal/data',undefined,{Cookie:cookie});
 const item=p.body.stock.find(x=>x.product_key==='47');assert.ok(item);
 assert.equal(item.purchase_price_cents,110);assert.equal(item.min_stock_quantity,6);assert.equal(item.quantity,5);
});

test('R46 completed sale updates cloud stock once and duplicate does not double-decrement',async()=>{
 const snapshot={event_id:'r46-stock-base',type:'stock.snapshot',occurred_at:'2026-09-07T18:00:00Z',payload:{items:[{
  product_key:'1',name:'R46 Artikel',sku:'R46-1',barcode:'4000000000046',group_name:'Kiosk',category_name:'Test',unit:'Stück',price_cents:300,quantity:10
 }]}};
 assert.equal((await sync([snapshot])).status,200);
 const sale=event('r46-stock-sale',946);sale.occurred_at='2026-09-07T18:01:00Z';
 assert.equal((await sync([sale])).body.accepted,1);
 let p=await request('/api/portal/data',undefined,{Cookie:cookie});
 assert.equal(p.body.stock.find(x=>x.product_key==='1').quantity,7);
 assert.equal((await sync([sale])).body.duplicates,1);
 p=await request('/api/portal/data',undefined,{Cookie:cookie});
 assert.equal(p.body.stock.find(x=>x.product_key==='1').quantity,7);
});
test('R49 combo stock consumption decrements components instead of visible menu article',async()=>{
 const snapshot={event_id:'r49-combo-base',type:'stock.snapshot',occurred_at:'2026-09-07T18:10:00Z',payload:{items:[
  {product_key:'101',name:'Döner',quantity:10},{product_key:'102',name:'Pommes',quantity:20},{product_key:'103',name:'Getränk',quantity:30},{product_key:'900',name:'Döner Menü',quantity:0}
 ]}};
 assert.equal((await sync([snapshot])).status,200);
 const sale=event('r49-combo-sale',949);sale.occurred_at='2026-09-07T18:11:00Z';
 sale.payload.items=[{position_no:1,product_key:'900',name:'Döner Menü',quantity:2,unit_price_cents:450,line_total_cents:900,vat_rate:19}];
 sale.payload.item_count=1;sale.payload.subtotal_cents=900;sale.payload.discount_cents=100;sale.payload.total_cents=800;
 sale.payload.stock_consumption=[{product_key:'101',quantity:2},{product_key:'102',quantity:2},{product_key:'103',quantity:2}];
 assert.equal((await sync([sale])).body.accepted,1);
 const p=await request('/api/portal/data',undefined,{Cookie:cookie});
 assert.equal(p.body.stock.find(x=>x.product_key==='101').quantity,8);
 assert.equal(p.body.stock.find(x=>x.product_key==='102').quantity,18);
 assert.equal(p.body.stock.find(x=>x.product_key==='103').quantity,28);
 assert.equal(p.body.stock.find(x=>x.product_key==='900').quantity,0);
});
test('R46 delayed sale older than latest stock snapshot cannot decrement it again',async()=>{
 const snapshot={event_id:'r46-newer-stock',type:'stock.snapshot',occurred_at:'2026-09-07T19:00:00Z',payload:{items:[{
  product_key:'1',name:'R46 Artikel',quantity:6
 }]}};
 assert.equal((await sync([snapshot])).status,200);
 const delayed=event('r46-delayed-sale',947);delayed.occurred_at='2026-09-07T18:30:00Z';
 assert.equal((await sync([delayed])).body.accepted,1);
 const p=await request('/api/portal/data',undefined,{Cookie:cookie});
 assert.equal(p.body.stock.find(x=>x.product_key==='1').quantity,6);
});

test('Berlin calendar and DST boundaries',()=>{
 assert.deepEqual(berlinParts('2026-09-06T22:30:00Z'),{day:'2026-09-07',hour:'00'});
 assert.deepEqual(berlinParts('2026-03-29T01:30:00Z'),{day:'2026-03-29',hour:'03'});
 assert.deepEqual(berlinParts('2026-10-25T01:30:00Z'),{day:'2026-10-25',hour:'02'});
});
test('pagination reaches older receipts',async()=>{
 const batch=Array.from({length:105},(_,i)=>event('page-'+i,1000+i));assert.equal((await sync(batch)).status,200);
 const p1=await request('/api/portal/data',undefined,{Cookie:cookie});
 const p2=await request('/api/portal/data?offset=100',undefined,{Cookie:cookie});
 assert.equal(p1.body.sales.length,100);assert.ok(p2.body.sales.length>0);
 assert.ok(!p2.body.sales.some(x=>p1.body.sales.some(y=>x.sale_id===y.sale_id)));
});
test('R62 Google QR pairing is device-authenticated and returns a scannable matrix',async()=>{
 const started=await request('/api/v1/devices/google-oauth/pair/start',{},headers);assert.equal(started.status,200);assert.equal(started.body.ok,true);
 assert.match(started.body.pair_id,/^[A-Za-z0-9_-]+$/);assert.ok(started.body.pair_secret.length>20);assert.equal(started.body.qr_matrix.length,41);
 assert.ok(started.body.qr_matrix.every(x=>/^[01]{41}$/.test(x)));assert.match(started.body.display_url,/\/g\//);
 const pending=await request('/api/v1/devices/google-oauth/pair/status',{pair_id:started.body.pair_id,pair_secret:started.body.pair_secret},headers);
 assert.equal(pending.status,200);assert.equal(pending.body.status,'PENDING');
 assert.equal((await request('/api/v1/devices/google-oauth/pair/status',{pair_id:started.body.pair_id,pair_secret:'wrong'},headers)).status,404);
 assert.equal((await request('/api/v1/devices/google-oauth/pair/start',{}, {'X-Device-Code':'DEMO-KASSE-01','X-Device-Token':'wrong'})).status,403);
 const u=new URL(started.body.display_url);const page=await fetch(base+u.pathname+u.search);assert.equal(page.status,200);assert.match(await page.text(),/MIT GOOGLE ANMELDEN/);
});

test('7-day trial is idempotent per persistent random Trial-ID and reinstall does not reset it',async()=>{
 const trialId='A'.repeat(64);
 const first=await request('/api/v1/trial/activate',{trial_id:trialId,version:'0.7.33.849',revision:'R149'});
 assert.equal(first.status,200);assert.equal(first.body.state,'ACTIVE');assert.equal(first.body.reused,false);
 const duration=Date.parse(first.body.expires_at)-Date.parse(first.body.started_at);
 assert.equal(duration,7*24*60*60*1000,'first activation is exactly seven days');

 const again=await request('/api/v1/trial/activate',{trial_id:trialId,version:'0.7.33.999',revision:'REINSTALL'});
 assert.equal(again.status,200);assert.equal(again.body.reused,true);
 assert.equal(again.body.started_at,first.body.started_at,'reinstall keeps original start');
 assert.equal(again.body.expires_at,first.body.expires_at,'reinstall keeps original expiry');

 const db=new DatabaseSync(path.join(root,'db.sqlite'));db.exec('PRAGMA busy_timeout=5000;');
 try{db.prepare('UPDATE trial_ids SET expires_at=? WHERE trial_id=?').run('2000-01-08T00:00:00.000Z',trialId);}finally{db.close();}
 const expired=await request('/api/v1/trial/activate',{trial_id:trialId,version:'0.7.33.849',revision:'AFTER-EXPIRY'});
 assert.equal(expired.status,200);assert.equal(expired.body.state,'EXPIRED');assert.equal(expired.body.reused,true);
 assert.equal(expired.body.started_at,first.body.started_at,'expired Trial-ID never gets a new seven-day start');

 assert.equal((await request('/api/v1/trial/activate',{trial_id:'not-a-trial-id'})).status,400);
});

test('public demo download uses a separate hash-verified manifest',async()=>{
 const redirect=await fetch(base+'/api/v1/trial/download',{redirect:'manual'});
 assert.equal(redirect.status,302);
 const location=new URL(redirect.headers.get('location'));
 const download=await fetch(base+location.pathname);
 assert.equal(download.status,200);
 assert.equal(Buffer.from(await download.arrayBuffer()).toString(),'TOR POS fake seven day demo setup');

 const target=path.join(root,'updates','TOR-POS-Demo-Setup.exe');
 const original=readFileSync(target);
 try{
  writeFileSync(target,Buffer.from('tampered demo'));
  assert.equal((await fetch(base+location.pathname)).status,409,'tampered demo setup is never served');
 }finally{writeFileSync(target,original);}
});

test('R48 update manifest and download endpoint',async()=>{
 const check=await request('/api/v1/updates/check?version=0.7.33.46&edition=KIOSK');assert.equal(check.status,200);assert.equal(check.body.update_available,true);assert.equal(check.body.manifest.revision,'R48-Test');
 const current=await request('/api/v1/updates/check?version=0.7.33.48&edition=KIOSK');assert.equal(current.body.update_available,false);
 const du=new URL(check.body.manifest.download_url);const r=await fetch(base+du.pathname);assert.equal(r.status,200);assert.equal(Buffer.from(await r.arrayBuffer()).toString(),'TOR POS fake setup for updater test');
});
// R120: the publishing script checks Authenticode, but nothing re-checked the
// bytes at serve time - so anything able to write into the updates directory
// bypassed that gate. The download now verifies the file against the manifest
// hash on every request.
test('R120 a tampered installer is refused at download time',async()=>{
 const updates=path.join(root,'updates');const target=path.join(updates,'TOR-POS-Pro-Setup.exe');
 const original=readFileSync(target);
 try{
  writeFileSync(target,Buffer.from('TOR POS tampered setup'));
  const r=await fetch(base+'/updates/TOR-POS-Pro-Setup.exe');
  assert.equal(r.status,409);
 } finally {
  writeFileSync(target,original);
 }
 const restored=await fetch(base+'/updates/TOR-POS-Pro-Setup.exe');
 assert.equal(restored.status,200);
});
// C-3: the hash is cached per file identity. A same-size rewrite that even puts
// the old mtime back must still miss the cache (ctime/inode change).
test('C-3 cached installer hash still catches a same-size tamper with restored mtime',async()=>{
 const target=path.join(root,'updates','TOR-POS-Pro-Setup.exe');
 const original=readFileSync(target),{atime,mtime}=statSync(target);
 assert.equal((await fetch(base+'/updates/TOR-POS-Pro-Setup.exe')).status,200);
 try{
  const forged=Buffer.from(original);forged[0]^=1;writeFileSync(target,forged);utimesSync(target,atime,mtime);
  assert.equal((await fetch(base+'/updates/TOR-POS-Pro-Setup.exe')).status,409);
 } finally { writeFileSync(target,original); }
 assert.equal((await fetch(base+'/updates/TOR-POS-Pro-Setup.exe')).status,200);
});
test('C-3 parallel downloads of a large installer share one hash and all succeed',async()=>{
 const updates=path.join(root,'updates'),target=path.join(updates,'TOR-POS-Pro-Setup.exe'),manifestPath=path.join(updates,'manifest.json');
 const original=readFileSync(target),manifest=readFileSync(manifestPath);
 const big=crypto.randomBytes(24*1024*1024);
 try{
  writeFileSync(target,big);
  writeFileSync(manifestPath,JSON.stringify({...JSON.parse(manifest),sha256:crypto.createHash('sha256').update(big).digest('hex').toUpperCase()}));
  const downloads=Array.from({length:6},()=>fetch(base+'/updates/TOR-POS-Pro-Setup.exe').then(async r=>({status:r.status,body:Buffer.from(await r.arrayBuffer())})));
  assert.equal((await fetch(base+'/api/health')).status,200);
  for(const d of await Promise.all(downloads)){assert.equal(d.status,200);assert.ok(d.body.equals(big));}
 } finally { writeFileSync(target,original);writeFileSync(manifestPath,manifest); }
});
test('R45 TOTP enrollment and challenge login',async()=>{
 const start=await request('/api/2fa/setup/start',{}, {Cookie:otherCookie});assert.equal(start.status,200);assert.match(start.body.secret,/^[A-Z2-7]+$/);
 const confirm=await request('/api/2fa/setup/confirm',{code:totp(start.body.secret)},{Cookie:otherCookie});assert.equal(confirm.status,200);assert.equal(confirm.body.recovery_codes.length,8);
 await request('/api/logout',{}, {Cookie:otherCookie});
 const login=await request('/api/login',{email:'other@test.local',password:'test-password'});assert.equal(login.status,200);assert.equal(login.body.requires_2fa,true);assert.ok(login.body.challenge);
 assert.equal((await request('/api/login/2fa',{challenge:login.body.challenge,code:'000000'})).status,401);
 const ok=await request('/api/login/2fa',{challenge:login.body.challenge,code:totp(start.body.secret)});assert.equal(ok.status,200);assert.ok(ok.cookie);
 otherCookie=ok.cookie;
});
test('credential and browser-origin protection',async()=>{
 assert.equal((await sync([event('forbidden')],{'X-Device-Code':'OTHER-01','X-Device-Token':'tor-demo-device-token-2026'})).status,403);
 assert.equal((await request('/api/logout',{}, {Cookie:cookie,Origin:'https://foreign.example'})).status,403);
 const health=await request('/api/health');assert.match(health.headers.get('content-security-policy'),/script-src 'self'/);
 for(let i=0;i<10;i++)await request('/api/login',{email:'limited@test.local',password:'wrong'});
 assert.equal((await request('/api/login',{email:'limited@test.local',password:'wrong'})).status,429);
});

// ---------------------------------------------------------------- R125

async function until(check,what,ms=8000){
 const end=Date.now()+ms;
 while(Date.now()<end){const value=await check();if(value)return value;await new Promise(r=>setTimeout(r,100));}
 throw new Error('Timeout: '+what);
}

test('R125 expired sessions and 2FA challenges are swept without anyone touching them',async()=>{
 const db=new DatabaseSync(path.join(root,'db.sqlite'));db.exec('PRAGMA busy_timeout=5000;');
 try{
  const user=db.prepare("SELECT id FROM users WHERE email='demo@torpos.local'").get();
  const future=new Date(Date.now()+60*60*1000).toISOString();
  db.prepare('INSERT INTO sessions(id,user_id,created_at,expires_at) VALUES(?,?,?,?)').run('r125-expired',user.id,'2000-01-01T00:00:00.000Z','2000-01-01T12:00:00.000Z');
  db.prepare('INSERT INTO sessions(id,user_id,created_at,expires_at) VALUES(?,?,?,?)').run('r125-live',user.id,new Date().toISOString(),future);
  db.prepare('INSERT INTO login_challenges(id,user_id,created_at,expires_at) VALUES(?,?,?,?)').run('r125-challenge',user.id,'2000-01-01T00:00:00.000Z','2000-01-01T00:05:00.000Z');
  await until(()=>!db.prepare("SELECT 1 FROM sessions WHERE id='r125-expired'").get() && !db.prepare("SELECT 1 FROM login_challenges WHERE id='r125-challenge'").get(),'expired rows removed');
  assert.ok(db.prepare("SELECT 1 FROM sessions WHERE id='r125-live'").get(),'a valid session is left alone');
 }finally{db.close();}
 assert.equal((await request('/api/portal/data',undefined,{Cookie:cookie})).status,200,'the logged-in demo owner stays logged in');
});

test('R125 the database is backed up while running and old backups are pruned',async()=>{
 const fs=require('node:fs');
 const fresh=await until(()=>fs.readdirSync(backups).find(name=>/^tor-cloud-\d{8}T\d{6}Z\.db$/.test(name)&&!name.startsWith('tor-cloud-2020')),'startup backup');
 const copy=new DatabaseSync(path.join(backups,fresh));
 try{assert.equal(copy.prepare("SELECT customer_number FROM businesses WHERE customer_number='TOR-DEMO-001'").get()?.customer_number,'TOR-DEMO-001','the backup is a real, readable copy of the live database');}
 finally{copy.close();}
 const all=await until(()=>{const names=fs.readdirSync(backups).filter(n=>n.endsWith('.db'));return names.length===3?names:null;},'retention');
 assert.deepEqual(all.filter(n=>n.startsWith('tor-cloud-2020')).sort(),['tor-cloud-20200103T000000Z.db','tor-cloud-20200104T000000Z.db'],'retention removes the oldest backups first');
});

test('R125 provisioning creates a customer whose owner and till work against the running server',async()=>{
 const provision=require('../tools/provision');
 const dbPath=path.join(root,'db.sqlite');
 const db=provision.openDatabase(dbPath);
 try{
  const customer=provision.createCustomer(db,{name:'Imbiss R125',customerNumber:'tor-r125-001',ownerEmail:'Inhaber.R125@Example.de',ownerName:'R125 Inhaber'});
  assert.equal(customer.customerNumber,'TOR-R125-001');
  assert.equal(customer.ownerEmail,'inhaber.r125@example.de');

  // The one-time password is never stored in clear text.
  const stored=db.prepare('SELECT password_hash,role FROM users WHERE email=?').get(customer.ownerEmail);
  assert.equal(stored.role,'OWNER');
  assert.notEqual(stored.password_hash,customer.oneTimePassword);

  // The server verifies what the tool wrote - same hashing, one module.
  const login=await request('/api/login',{email:customer.ownerEmail,password:customer.oneTimePassword});
  assert.equal(login.status,200,'the new owner can sign in with the one-time password');
  // R128: ...but sees nothing until that one-time password has been replaced.
  const blocked=await request('/api/portal/data',undefined,{Cookie:login.cookie});
  assert.equal(blocked.status,428);
  assert.equal(blocked.body.code,'PASSWORD_CHANGE_REQUIRED');
  const own='R125-Eigenes-Passwort!';
  assert.equal((await request('/api/password/change',{current_password:customer.oneTimePassword,new_password:own},{Cookie:login.cookie})).status,200);
  const portal=await request('/api/portal/data',undefined,{Cookie:login.cookie});
  assert.equal(portal.status,200);
  assert.equal(portal.body.saleTotal,0,'a new customer sees none of the sales of another tenant');

  const branch=provision.addBranch(db,{customerNumber:customer.customerNumber,name:'Mitte',city:'Berlin'});
  provision.addRegister(db,{branchId:branch.branchId,deviceCode:'R125-KASSE-01',name:'Kasse 1',edition:'imbiss'});
  const first=provision.issueDeviceToken(db,{deviceCode:'R125-KASSE-01',label:'Kasse 1'});
  const auth=token=>({'X-Device-Code':'R125-KASSE-01','X-Device-Token':token});
  assert.equal((await request('/api/v1/devices/ping',undefined,auth(first.token))).status,200,'the till authenticates with the issued token');
  assert.equal(db.prepare('SELECT COUNT(*) AS n FROM device_tokens WHERE token_hash=?').get(first.token).n,0,'the token itself is not stored');

  const rotated=provision.issueDeviceToken(db,{deviceCode:'R125-KASSE-01',revokeExisting:true});
  assert.equal(rotated.revoked,1);
  assert.equal((await request('/api/v1/devices/ping',undefined,auth(first.token))).status,403,'rotation locks out the old token (403 Gerät nicht autorisiert)');
  assert.equal((await request('/api/v1/devices/ping',undefined,auth(rotated.token))).status,200,'and the new one works');

  assert.equal(provision.revokeDeviceTokens(db,{deviceCode:'R125-KASSE-01'}).revoked,1);
  assert.equal((await request('/api/v1/devices/ping',undefined,auth(rotated.token))).status,403,'a revoked till cannot sync');

  const reset=provision.resetOwnerPassword(db,{email:customer.ownerEmail});
  assert.equal(reset.sessionsEnded,1);
  assert.equal((await request('/api/portal/data',undefined,{Cookie:login.cookie})).status,401,'a password reset ends existing sessions');
  assert.equal((await request('/api/login',{email:customer.ownerEmail,password:customer.oneTimePassword})).status,401,'the old password no longer works');
  assert.equal((await request('/api/login',{email:customer.ownerEmail,password:reset.oneTimePassword})).status,200);

  // Mistakes are refused with a reason instead of half-created rows.
  assert.throws(()=>provision.createCustomer(db,{name:'X',customerNumber:'TOR-DEMO-001',ownerEmail:'x@example.de',ownerName:'X'}),/Demomodus/);
  assert.throws(()=>provision.createCustomer(db,{name:'X',customerNumber:'TOR-R125-002',ownerEmail:customer.ownerEmail,ownerName:'X'}),/bereits/);
  assert.equal(db.prepare("SELECT COUNT(*) AS n FROM businesses WHERE customer_number='TOR-R125-002'").get().n,0,'a refused customer leaves no business row behind');
  assert.throws(()=>provision.addRegister(db,{branchId:branch.branchId,deviceCode:'Kasse 1',name:'X'}),/Gerätecode/,'a code the till would reject is refused here too');
  assert.throws(()=>provision.addRegister(db,{branchId:branch.branchId,deviceCode:'R125-KASSE-01',name:'X'}),/bereits vergeben/);
  assert.throws(()=>provision.addRegister(db,{branchId:branch.branchId,deviceCode:'R125-KASSE-02',name:'X',edition:'TISCH'}),/KIOSK oder IMBISS/);
 }finally{db.close();}

 const lines=[];
 assert.equal(provision.run(['list','--db',dbPath,'--customer','TOR-R125-001'],{out:l=>lines.push(l)}),0);
 const listed=JSON.parse(lines.at(-1));
 assert.equal(listed[0].branches[0].registers[0].deviceCode,'R125-KASSE-01');
 assert.equal(listed[0].branches[0].registers[0].activeTokens,0);
 assert.throws(()=>provision.run(['add-branch','--db',dbPath,'--customer'],{out:()=>{}}),/braucht einen Wert/);
});

// ---------------------------------------------------------------- R128

test('R128 an owner on a one-time password must choose an own password before anything else',async()=>{
 const provision=require('../tools/provision');
 const dbPath=path.join(root,'db.sqlite');
 const db=provision.openDatabase(dbPath);
 let customer;
 try{customer=provision.createCustomer(db,{name:'Imbiss R128',customerNumber:'TOR-R128-001',ownerEmail:'inhaber.r128@example.de',ownerName:'R128 Inhaber'});}
 finally{db.close();}

 const first=await request('/api/login',{email:customer.ownerEmail,password:customer.oneTimePassword});
 const second=await request('/api/login',{email:customer.ownerEmail,password:customer.oneTimePassword});
 assert.equal(first.status,200);
 const cookieA=first.cookie, cookieB=second.cookie;

 const me=await request('/api/me',undefined,{Cookie:cookieA});
 assert.equal(me.status,200,'the portal can still ask who is logged in');
 assert.equal(me.body.security.password_change_required,true,'and learns that the password must be changed');
 assert.equal((await request('/api/portal/data',undefined,{Cookie:cookieA})).body.code,'PASSWORD_CHANGE_REQUIRED','business data stays closed');
 assert.equal((await request('/api/2fa/setup/start',{},{Cookie:cookieA})).body.code,'PASSWORD_CHANGE_REQUIRED','2FA is enrolled only after the handed-over password is gone');

 const change=body=>request('/api/password/change',body,{Cookie:cookieA});
 assert.equal((await change({current_password:'falsch-falsch-falsch',new_password:'Ganz-Neues-Passwort-1'})).status,401,'the current password is required');
 const short=await change({current_password:customer.oneTimePassword,new_password:'kurz'});
 assert.equal(short.status,400);assert.match(short.body.error,/mindestens 12 Zeichen/);
 assert.equal((await change({current_password:customer.oneTimePassword,new_password:customer.oneTimePassword})).status,400,'the same password is not accepted as a new one');
 assert.equal((await change({current_password:customer.oneTimePassword,new_password:customer.ownerEmail})).status,400,'the e-mail address is not accepted as a password');
 assert.equal((await request('/api/password/change',{current_password:customer.oneTimePassword,new_password:'Ganz-Neues-Passwort-1'})).status,401,'not without a session');

 const ok=await change({current_password:customer.oneTimePassword,new_password:'Ganz-Neues-Passwort-1'});
 assert.equal(ok.status,200);
 assert.equal(ok.body.sessions_ended,1,'the other session opened with the one-time password is ended');
 assert.equal((await request('/api/portal/data',undefined,{Cookie:cookieB})).status,401);
 assert.equal((await request('/api/portal/data',undefined,{Cookie:cookieA})).status,200,'the session that changed the password continues into the portal');
 assert.equal((await request('/api/me',undefined,{Cookie:cookieA})).body.security.password_change_required,false);

 assert.equal((await request('/api/login',{email:customer.ownerEmail,password:customer.oneTimePassword})).status,401,'the one-time password no longer works');
 assert.equal((await request('/api/login',{email:customer.ownerEmail,password:'Ganz-Neues-Passwort-1'})).status,200,'the own password does');

 // A reset hands out a new one-time password - and with it the same obligation.
 const db2=provision.openDatabase(dbPath);
 let reset;
 try{reset=provision.resetOwnerPassword(db2,{email:customer.ownerEmail});}finally{db2.close();}
 const again=await request('/api/login',{email:customer.ownerEmail,password:reset.oneTimePassword});
 assert.equal((await request('/api/portal/data',undefined,{Cookie:again.cookie})).body.code,'PASSWORD_CHANGE_REQUIRED','a password reset requires a new own password again');

 // The demo owner was never on a one-time password and is not affected.
 assert.equal((await request('/api/portal/data',undefined,{Cookie:cookie})).status,200);
});


test('R179 reversal sync restores stock and is stored as a signed counter-booking',async()=>{
 const snapshot={event_id:'r179-stock-base',type:'stock.snapshot',occurred_at:'2026-09-21T12:00:00Z',payload:{items:[{
  product_key:'179',name:'R179 Artikel',sku:'R179',barcode:'4017900000000',group_name:'Test',category_name:'Test',unit:'Stück',price_cents:300,quantity:10
 }]}};
 assert.equal((await sync([snapshot])).status,200);
 const sale={event_id:'r179-sale',type:'sale.completed',occurred_at:'2026-09-21T12:01:00Z',payload:{
  receipt_number:179001,transaction_type:'SALE',payment_method:'CASH',cash_portion_cents:900,card_portion_cents:0,
  subtotal_cents:900,discount_cents:0,total_cents:900,operator_name:'tester',item_count:1,
  items:[{position_no:1,product_key:'179',name:'R179 Artikel',quantity:3,unit_price_cents:300,line_total_cents:900,vat_rate:19}],
  stock_consumption:[{product_key:'179',quantity:3}]
 }};
 assert.equal((await sync([sale])).body.accepted,1);
 const ret={event_id:'r179-return',type:'sale.completed',occurred_at:'2026-09-21T12:02:00Z',payload:{
  receipt_number:179002,original_receipt_number:179001,transaction_type:'RETURN',payment_method:'CASH',cash_portion_cents:300,card_portion_cents:0,
  subtotal_cents:300,discount_cents:0,total_cents:300,operator_name:'tester',item_count:1,
  items:[{position_no:1,product_key:'179',name:'R179 Artikel',quantity:1,unit_price_cents:300,line_total_cents:300,vat_rate:19}],
  stock_consumption:[{product_key:'179',quantity:1}]
 }};
 assert.equal((await sync([ret])).body.accepted,1);
 const p=await request('/api/portal/data',undefined,{Cookie:cookie});
 assert.equal(p.body.stock.find(x=>x.product_key==='179').quantity,8);
 const row=p.body.sales.find(x=>x.receipt_number===179002);assert.ok(row);
 assert.equal(row.transaction_type,'RETURN');assert.equal(row.original_receipt_number,179001);assert.equal(row.total_cents,-300);
});

test('R179 portal labels mixed payment as Gemischt',()=>{
 const app=readFileSync(path.join(__dirname,'../public/app.js'),'utf8');
 assert.match(app,/method==='MIXED'\?'Gemischt'/);
});
