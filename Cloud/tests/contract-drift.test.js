'use strict';
// Drift guard between the three places that describe the same Cloud data:
// Shared/simulator-contract.json (website simulator's portal preview), the real
// portal (Cloud/public) and the till that sends the heartbeat
// (Desktop TorCloudSyncService). The Windows CI checks the labels too
// (Verify-Simulator-Contract.ps1); this runs on the Cloud job as well.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const {readFileSync,readdirSync,existsSync}=require('node:fs');
const path=require('node:path');

const repo=path.join(__dirname,'..','..');
const contractFile=path.join(repo,'Shared','simulator-contract.json');
const skip=!existsSync(contractFile);
const publicDir=path.join(__dirname,'..','public');
const portal=readdirSync(publicDir).filter(n=>/\.(html|js)$/.test(n)).map(n=>readFileSync(path.join(publicDir,n),'utf8')).join('\n');
const server=readFileSync(path.join(__dirname,'..','server.js'),'utf8');

test('every portal label of the simulator contract exists in the real portal',{skip},()=>{
  const {cloudPortal}=JSON.parse(readFileSync(contractFile,'utf8'));
  const labels=[...cloudPortal.menu,...Object.values(cloudPortal.reports),...Object.values(cloudPortal.deviceStatus)];
  const missing=labels.filter(l=>!portal.toLowerCase().includes(String(l).toLowerCase()));
  assert.deepEqual(missing,[],'simulator shows portal labels the real portal does not have');
  assert.ok(String(cloudPortal.previewNote||'').includes('Keine Verbindung zu TOR Cloud'),'the preview says it is not connected');
});

test('each Gerätestatus line reads a heartbeat field the Cloud stores and the till sends',{skip},()=>{
  const {cloudPortal}=JSON.parse(readFileSync(contractFile,'utf8'));
  const app=readFileSync(path.join(publicDir,'app.js'),'utf8');
  const tillFile=path.join(repo,'Desktop','src','TorPos.Infrastructure','TorCloudSyncService.cs');
  const till=existsSync(tillFile)?readFileSync(tillFile,'utf8'):null;
  for(const label of Object.values(cloudPortal.deviceStatus)){
    const line=app.split('\n').find(l=>l.includes(`'${label}'`));
    assert.ok(line,`portal line for "${label}"`);
    const fields=[...new Set([...line.matchAll(/\br\.([a-z_]+)/g)].map(m=>m[1]))];
    assert.ok(fields.length>0,`"${label}" reads a field`);
    for(const field of fields){
      assert.ok(server.includes(field),`server.js stores/returns ${field} (for "${label}")`);
      if(till)assert.ok(till.includes(field),`the till heartbeat sends ${field} (for "${label}")`);
    }
  }
});
