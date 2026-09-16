// R128 optional UI test: the portal's one-time-password flow, driven through the
// real portal.html + app.js against a real server. Needs jsdom, like
// portal-dom.cjs:  npm install --no-save jsdom && npm run test:dom
'use strict';
const {JSDOM}=require('jsdom');
const {spawn}=require('node:child_process');
const fs=require('node:fs'),path=require('node:path'),os=require('node:os');
const assert=require('node:assert/strict');

(async()=>{
 const dir=fs.mkdtempSync(path.join(os.tmpdir(),'tor-pw-dom-'));
 const dbPath=path.join(dir,'db.sqlite');
 const child=spawn(process.execPath,['server.js'],{cwd:path.join(__dirname,'..'),env:{...process.env,PORT:'0',HOST:'127.0.0.1',TOR_CLOUD_DEMO:'true',TOR_CLOUD_DB:dbPath,TOR_CLOUD_UPDATES:path.join(dir,'updates')}});
 let dom;
 try{
  const base=await new Promise((resolve,reject)=>{let log='';child.stdout.on('data',d=>{log+=d;const m=log.match(/http:\/\/127\.0\.0\.1:\d+/);if(m)resolve(m[0]);});child.once('exit',()=>reject(new Error('startup')));});

  const provision=require('../tools/provision');
  const db=provision.openDatabase(dbPath);
  let customer;
  try{customer=provision.createCustomer(db,{name:'DOM Imbiss',customerNumber:'TOR-DOM-001',ownerEmail:'dom@example.de',ownerName:'DOM Inhaber'});}
  finally{db.close();}

  const login=await fetch(base+'/api/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({email:customer.ownerEmail,password:customer.oneTimePassword})});
  assert.equal(login.status,200);
  const cookie=login.headers.get('set-cookie').split(';')[0];

  dom=new JSDOM(fs.readFileSync(path.join(__dirname,'../public/portal.html'),'utf8'),{url:base+'/portal#dashboard',runScripts:'outside-only'});
  const win=dom.window,doc=win.document;
  win.fetch=(url,opts={})=>fetch(new URL(url,base),{...opts,headers:{...opts.headers,Cookie:cookie}});
  win.scrollTo=()=>{};win.setInterval=()=>1;
  win.eval(fs.readFileSync(path.join(__dirname,'../public/app.js'),'utf8'));
  const $=id=>doc.getElementById(id);
  async function until(fn,what){for(let i=0;i<300;i++){if(fn())return;await new Promise(r=>setTimeout(r,10));}throw new Error('DOM timeout: '+what);}

  // Logged in on the one-time password: the portal goes straight to Sicherheit.
  await until(()=>!doc.querySelector('[data-page=security]').hidden,'security view shown');
  assert.equal(doc.querySelector('[data-page=dashboard]').hidden,true,'the dashboard stays closed');
  assert.match($('passwordHint').textContent,/Einmal-Passwort/);
  assert.equal($('twoFactorStart').hidden,true,'2FA enrolment is not offered before the password is replaced');
  assert.equal($('kpiTotal').textContent,'–','no business data was loaded');

  // Typo in the repetition is caught before anything is sent.
  $('passwordCurrent').value=customer.oneTimePassword;
  $('passwordNew').value='Mein-Eigenes-Passwort-1';
  $('passwordRepeat').value='Mein-Eigenes-Passwort-2';
  $('passwordChange').click();
  await until(()=>/stimmen nicht überein/.test($('passwordMsg').textContent),'mismatch message');

  // Server-side rule is shown as returned.
  $('passwordNew').value='kurz';$('passwordRepeat').value='kurz';
  $('passwordChange').click();
  await until(()=>/mindestens 12 Zeichen/.test($('passwordMsg').textContent),'too-short message');

  $('passwordNew').value='Mein-Eigenes-Passwort-1';$('passwordRepeat').value='Mein-Eigenes-Passwort-1';
  $('passwordChange').click();
  await until(()=>/Passwort geändert/.test($('passwordMsg').textContent),'success message');
  assert.equal($('passwordCurrent').value,'','the fields are cleared');

  // The refresh after the change passes the gate and loads the business data.
  await until(()=>$('kpiTotal').textContent!=='–','business data loaded after the change');
  assert.doesNotMatch($('passwordHint').textContent,/Einmal-Passwort/);
  console.log('PASS: one-time password forces Sicherheit, mismatch and length errors shown, change unlocks the portal');
 }finally{dom?.window.close();child.kill();await new Promise(r=>child.exitCode!==null?r():child.once('exit',r));fs.rmSync(dir,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
