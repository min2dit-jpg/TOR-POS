// Optional UI event test: npm install --no-save jsdom, then node tests/portal-dom.cjs.
const {JSDOM}=require('jsdom');
const {spawn}=require('node:child_process');
const fs=require('node:fs'),path=require('node:path'),os=require('node:os');
const assert=require('node:assert/strict');
(async()=>{
 const dir=fs.mkdtempSync(path.join(os.tmpdir(),'tor-dom-'));
 const child=spawn(process.execPath,['server.js'],{cwd:path.join(__dirname,'..'),env:{...process.env,PORT:'0',HOST:'127.0.0.1',TOR_CLOUD_DEMO:'true',TOR_CLOUD_DB:path.join(dir,'db.sqlite')}});
 let dom;
 try{
  const base=await new Promise((resolve,reject)=>{child.stdout.on('data',d=>{const m=String(d).match(/http:\/\/127\.0\.0\.1:\d+/);if(m)resolve(m[0]);});child.on('exit',()=>reject(new Error('startup')));});
  const login=await fetch(base+'/api/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({email:'demo@torpos.local',password:'TorDemo2026!'})});
  const cookie=login.headers.get('set-cookie').split(';')[0];
  dom=new JSDOM(fs.readFileSync(path.join(__dirname,'../public/portal.html'),'utf8'),{url:base+'/portal',runScripts:'outside-only'});
  let offline=false,interval;const win=dom.window;
  win.fetch=(url,opts={})=>{if(offline)return Promise.reject(new Error('Test offline'));return fetch(new URL(url,base),{...opts,headers:{...opts.headers,Cookie:cookie}});};
  win.scrollTo=()=>{};win.setInterval=fn=>{interval=fn;return 1;};
  win.eval(fs.readFileSync(path.join(__dirname,'../public/app.js'),'utf8'));
  async function until(fn){for(let i=0;i<200;i++){if(fn())return;await new Promise(r=>setTimeout(r,10));}throw new Error('DOM timeout');}
  await until(()=>win.document.querySelector('#kpiTotal').textContent!=='–');
  await until(()=>win.document.querySelector('#cloudHealthBadge').textContent==='Cloud online');
  for(const view of ['dashboard','sales','receipts','reports','products','stock','employees','branches','devices','security','support']){
   win.document.querySelector(`[data-view=${view}]`).click();
   assert.equal(win.document.querySelector(`[data-page=${view}]`).hidden,false);
   assert.equal([...win.document.querySelectorAll('.portal-view')].filter(x=>!x.hidden).length,1);
  }
  win.document.querySelector('[data-view=receipts]').click();win.document.querySelector('.receipt-detail-btn').click();
  await until(()=>win.document.querySelectorAll('.receipt-items tbody tr').length>0);
  assert.equal(win.document.querySelector('#receiptModal').hidden,false);
  win.document.querySelector('#receiptModalClose').click();assert.equal(win.document.querySelector('#receiptModal').hidden,true);
  assert.ok(interval,'Automatic refresh registered');
  offline=true;win.document.querySelector('#refreshButton').click();
  await until(()=>win.document.querySelector('#refreshStatus').textContent.includes('fehlgeschlagen'));
  assert.equal(win.document.querySelector('#cloudHealthBadge').textContent,'Verbindung nicht bestätigt');
  assert.equal(win.document.querySelector('#registers .status').textContent,'Nicht aktuell geprüft');
  console.log('PASS: 11 portal menus, receipt open/close, refresh registration and offline status DOM interactions');
 }finally{dom?.window.close();child.kill();await new Promise(r=>child.exitCode!==null?r():child.once('exit',r));fs.rmSync(dir,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
