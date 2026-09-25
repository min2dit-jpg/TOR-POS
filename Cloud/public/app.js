'use strict';
const $ = id => document.getElementById(id);
const eur = cents => new Intl.NumberFormat('de-DE',{style:'currency',currency:'EUR'}).format((Number(cents)||0)/100);
const time = iso => { const d=new Date(iso); return Number.isNaN(d.getTime())?'–':d.toLocaleTimeString('de-DE',{timeZone:'Europe/Berlin',hour:'2-digit',minute:'2-digit'}); };
const dateTime = iso => { const d=new Date(iso); return Number.isNaN(d.getTime())?'–':d.toLocaleString('de-DE',{timeZone:'Europe/Berlin'}); };
const esc = value => String(value ?? '').replace(/[&<>'"]/g, c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));

async function api(url, options={}) {
  const res = await fetch(url,{...options,headers:{'Content-Type':'application/json',...(options.headers||{})}});
  let body={}; try{body=await res.json();}catch{}
  if(!res.ok){const err=new Error(body.error||`HTTP ${res.status}`);err.status=res.status;err.code=body.code||'';throw err;}
  return body;
}

if ($('loginForm')) {
  let challenge='';
  api('/api/health').then(h=>{if(h.demo){$('demoCredentials').hidden=false;$('email').value='demo@torpos.local';}}).catch(()=>{});
  $('loginForm').addEventListener('submit', async e => {
    e.preventDefault(); $('loginMsg').textContent='';
    try {
      const result=await api('/api/login',{method:'POST',body:JSON.stringify({email:$('email').value,password:$('password').value})});
      if(result.requires_2fa){challenge=result.challenge;$('loginForm').hidden=true;$('twoFactorForm').hidden=false;$('twoFactorCode').focus();return;}
      location.href='/portal#dashboard';
    } catch(err) { $('loginMsg').textContent=err.message; }
  });
  $('twoFactorForm').addEventListener('submit',async e=>{
    e.preventDefault();$('twoFactorMsg').textContent='';
    try{await api('/api/login/2fa',{method:'POST',body:JSON.stringify({challenge,code:$('twoFactorCode').value})});location.href='/portal#dashboard';}
    catch(err){$('twoFactorMsg').textContent=err.message;}
  });
  $('twoFactorBack').addEventListener('click',()=>{challenge='';$('twoFactorCode').value='';$('twoFactorForm').hidden=true;$('loginForm').hidden=false;$('password').focus();});
}

function paymentName(method){ return method==='CASH'?'Bar':method==='CARD'?'Karte':method==='MIXED'?'Gemischt':'Unbekannt'; }
function bookingName(type){ return type==='STORNO'?'Storno':type==='RETURN'?'Retoure':'Verkauf'; }
function onlineFrom(iso){ return (Date.now()-(Date.parse(iso)||0))<10*60*1000; }
function statusLine(label, value, goodValues=[]){
  const good=goodValues.includes(String(value||'').toUpperCase());
  return `<div class="status-line"><span>${esc(label)}</span><b class="${good?'ok-text':''}">${esc(value||'Unbekannt')}</b></div>`;
}

function qtyText(value){
  const n=Number(value)||0;
  return new Intl.NumberFormat('de-DE',{maximumFractionDigits:3}).format(n);
}

async function openReceiptDetail(saleId){
  const modal=$('receiptModal');
  if(!modal) return;
  $('receiptModalBody').innerHTML='<div class="muted">Bon wird geladen…</div>';
  modal.hidden=false;
  document.body.classList.add('modal-open');
  try{
    const data=await api(`/api/receipts/${encodeURIComponent(saleId)}`);
    const r=data.receipt;
    const items=Array.isArray(r.items)?r.items:[];
    $('receiptModalTitle').textContent=`Bon #${r.receipt_number}`;
    $('receiptModalBody').innerHTML=`
      <div class="receipt-meta">
        <div><small>Datum / Zeit</small><b>${esc(dateTime(r.occurred_at))}</b></div>
        <div><small>Filiale</small><b>${esc(r.branch_name)}</b></div>
        <div><small>Kasse</small><b>${esc(r.register_name)}</b></div>
        <div><small>Bediener</small><b>${esc(r.operator_name||'–')}</b></div>
        <div><small>Vorgang</small><b>${esc(bookingName(r.transaction_type))}${r.original_receipt_number?` · Bezug #${esc(r.original_receipt_number)}`:''}</b></div>
        <div><small>Zahlart</small><b>${esc(paymentName(r.payment_method))}</b></div>
        <div><small>Abholnummer</small><b>${Number(r.pickup_number)>0?esc(String(r.pickup_number).padStart(3,'0')):'–'}</b></div>
        <div><small>Gerät</small><b>${esc(r.device_code||'–')}</b></div>
      </div>
      <div class="tablewrap receipt-items-wrap">
        <table class="table receipt-items"><thead><tr><th>Pos.</th><th>Artikel</th><th>Menge</th><th>Einzelpreis</th><th>MwSt.</th><th>Summe</th></tr></thead>
        <tbody>${items.length?items.map(i=>`<tr><td>${esc(i.position_no)}</td><td><b>${esc(i.name)}</b><div class="muted small">${esc(i.product_key||'')}</div></td><td>${qtyText(i.quantity)}</td><td>${eur(i.unit_price_cents)}</td><td>${qtyText(i.vat_rate)} %</td><td><b>${eur(i.line_total_cents)}</b></td></tr>`).join(''):'<tr><td colspan="6" class="muted">Für diesen Bon wurden noch keine Artikelpositionen synchronisiert.</td></tr>'}</tbody></table>
      </div>
      <div class="receipt-totals">
        <div><span>Zwischensumme</span><b>${eur(r.subtotal_cents)}</b></div>
        <div><span>Rabatt</span><b>${eur(r.discount_cents)}</b></div>
        <div class="receipt-grand"><span>Gesamt</span><strong>${eur(r.total_cents)}</strong></div>
      </div>`;
  }catch(err){
    $('receiptModalBody').innerHTML=`<div class="msg">Bon konnte nicht geladen werden: ${esc(err.message)}</div>`;
  }
}

function closeReceiptDetail(){
  const modal=$('receiptModal');
  if(modal) modal.hidden=true;
  document.body.classList.remove('modal-open');
}

if ($('logoutBtn')) {
  $('logoutBtn').addEventListener('click', async()=>{ try{await api('/api/logout',{method:'POST'});}finally{location.href='/login';} });
  if($('receiptModalClose')) $('receiptModalClose').addEventListener('click',closeReceiptDetail);
  if($('receiptModalBackdrop')) $('receiptModalBackdrop').addEventListener('click',closeReceiptDetail);
  window.addEventListener('keydown',e=>{if(e.key==='Escape') closeReceiptDetail();});

  const validViews = new Set(['dashboard','sales','receipts','reports','products','stock','employees','branches','devices','security','support']);
  function showView(name){
    if(!validViews.has(name)) name='dashboard';
    document.querySelectorAll('.portal-view').forEach(el=>{ el.hidden = el.dataset.page !== name; });
    document.querySelectorAll('#portalNav a[data-view]').forEach(a=>a.classList.toggle('active',a.dataset.view===name));
    if(location.hash !== `#${name}`) history.replaceState(null,'',`#${name}`);
    window.scrollTo({top:0,behavior:'instant'});
  }
  document.querySelectorAll('#portalNav a[data-view]').forEach(a=>a.addEventListener('click',e=>{ e.preventDefault(); showView(a.dataset.view); }));
  window.addEventListener('hashchange',()=>showView(location.hash.slice(1)));

  async function loadSecurity(me){
    const sec=me?.security||{};
    $('twoFactorBadge').textContent=sec.totp_enabled?'2FA aktiv':sec.setup_required?'Einrichtung erforderlich':'2FA nicht aktiv';
    $('twoFactorBadge').classList.toggle('badge-ok',!!sec.totp_enabled);
    $('twoFactorState').innerHTML=`${statusLine('Zwei-Faktor',sec.totp_enabled?'Aktiv':'Nicht aktiv',['AKTIV'])}${statusLine('Inhaber-Richtlinie',sec.setup_required?'Einrichtung erforderlich':'Erfüllt',['ERFÜLLT'])}`;
    // R128: while the one-time password is still active the server refuses
    // 2FA enrolment anyway, so the button would only produce an error.
    $('twoFactorStart').hidden=!!sec.password_change_required;
    // G-5: with 2FA on, the button switches the authenticator and the server
    // wants the password and a current code for that.
    $('twoFactorStart').textContent=sec.totp_enabled?'AUTHENTICATOR WECHSELN':'2FA EINRICHTEN';
    $('twoFactorReauth').hidden=!sec.totp_enabled;
    $('twoFactorDisablePanel').hidden=!sec.totp_enabled;
    $('passwordHint').textContent=sec.password_change_required
      ?'Sie sind mit einem Einmal-Passwort angemeldet. Bitte jetzt ein eigenes Passwort festlegen (mindestens 12 Zeichen) – vorher werden keine Geschäftsdaten angezeigt.'
      :'Mindestens 12 Zeichen. Nach der Änderung werden alle anderen Anmeldungen dieses Kontos beendet.';
    $('passwordHint').classList.toggle('low',!!sec.password_change_required);
  }
  if($('passwordChange')) $('passwordChange').addEventListener('click',async()=>{
    const msg=$('passwordMsg');msg.textContent='';msg.classList.remove('ok-text');
    const current=$('passwordCurrent').value,next=$('passwordNew').value,repeat=$('passwordRepeat').value;
    if(next!==repeat){msg.textContent='Die beiden neuen Passwörter stimmen nicht überein.';return;}
    try{
      const r=await api('/api/password/change',{method:'POST',body:JSON.stringify({current_password:current,new_password:next})});
      $('passwordCurrent').value='';$('passwordNew').value='';$('passwordRepeat').value='';
      msg.textContent='Passwort geändert.'+(r.sessions_ended?` ${r.sessions_ended} andere Anmeldung(en) beendet.`:'');
      msg.classList.add('ok-text');
      await refresh();
    }catch(err){msg.textContent=err.message;}
  });
  if($('twoFactorStart')) $('twoFactorStart').addEventListener('click',async()=>{
    $('twoFactorSetupMsg').textContent='';
    const reauth=!$('twoFactorReauth').hidden?{password:$('twoFactorReauthPassword').value,code:$('twoFactorReauthCode').value}:{};
    try{const r=await api('/api/2fa/setup/start',{method:'POST',body:JSON.stringify(reauth)});$('twoFactorReauthPassword').value='';$('twoFactorReauthCode').value='';$('twoFactorSecret').textContent=r.secret;$('twoFactorSetup').hidden=false;$('twoFactorConfirmCode').focus();}
    catch(err){$('twoFactorSetupMsg').textContent=err.message;$('twoFactorSetup').hidden=false;}
  });
  if($('twoFactorConfirm')) $('twoFactorConfirm').addEventListener('click',async()=>{
    $('twoFactorSetupMsg').textContent='';
    try{const r=await api('/api/2fa/setup/confirm',{method:'POST',body:JSON.stringify({code:$('twoFactorConfirmCode').value})});$('twoFactorSetup').hidden=true;$('recoveryPanel').hidden=false;$('recoveryCodes').textContent=(r.recovery_codes||[]).join('\n');const me=await api('/api/me');await loadSecurity(me);}
    catch(err){$('twoFactorSetupMsg').textContent=err.message;}
  });
  if($('twoFactorDisable')) $('twoFactorDisable').addEventListener('click',async()=>{
    $('twoFactorDisableMsg').textContent='';
    try{await api('/api/2fa/disable',{method:'POST',body:JSON.stringify({password:$('twoFactorDisablePassword').value,code:$('twoFactorDisableCode').value})});$('twoFactorDisablePassword').value='';$('twoFactorDisableCode').value='';const me=await api('/api/me');await loadSecurity(me);}
    catch(err){$('twoFactorDisableMsg').textContent=err.message;}
  });

  let offset=0, loading=false;
  async function refresh(){
    if(loading)return;loading=true;
    try {
      const me=await api('/api/me');
      $('userLabel').textContent=me.user.display_name;
      $('businessName').textContent=me.business.name;
      await loadSecurity(me);
      // R128: the one-time password comes first, then 2FA, then the business data.
      // Only switch views when not already there, so the 30-second refresh does
      // not jump the page while the owner is typing.
      if(me.security?.password_change_required){$('refreshStatus').textContent='Sicherheit: Einmal-Passwort zuerst durch ein eigenes Passwort ersetzen.';if(location.hash!=='#security'){showView('security');$('passwordCurrent').focus();}return;}
      if(me.security?.setup_required){$('refreshStatus').textContent='Sicherheit: Zwei-Faktor-Anmeldung zuerst einrichten.';showView('security');return;}
      const data=await api('/api/portal/data?offset='+offset);
      $('refreshStatus').textContent=(me.demo?'DEMO · Beispieldaten und Testverbindung · ':'')+'Aktualisiert '+new Date().toLocaleTimeString('de-DE')+' · Europe/Berlin';
      $('pageStatus').textContent=`${data.saleTotal?offset+1:0}–${Math.min(offset+100,data.saleTotal)} von ${data.saleTotal} Bons`;
      $('previousPage').disabled=offset===0;
      $('nextPage').disabled=offset+100>=data.saleTotal;
      const totals=data.totals||{};
      const registers=data.registers||[];
      const sales=data.sales||[];
      const stock=data.stock||[];
      const zReports=data.zReports||[];
      const users=data.users||[];
      const operators=data.operators||[];
      const branches=data.branches||[];

      $('kpiTotal').textContent=eur(totals.total_cents);
      $('kpiCount').textContent=String(totals.sale_count||0);
      $('kpiCash').textContent=eur(totals.cash_cents);
      $('kpiCard').textContent=eur(totals.card_cents);
      const latest=registers.map(r=>Date.parse(r.last_seen_at)||0).sort((a,b)=>b-a)[0]||0;
      $('syncText').textContent=latest?`Letzte Kassenmeldung: ${new Date(latest).toLocaleString('de-DE',{timeZone:'Europe/Berlin'})}`:'Noch keine Kassenmeldung';

      const max=Math.max(1,...(data.hourly||[]).map(x=>Number(x.total_cents)||0));
      $('chart').innerHTML=(data.hourly||[]).map(x=>`<div class="chartcol"><div class="chartbar" style="height:${Math.max(8,Math.round((Number(x.total_cents)||0)/max*190))}px" title="${eur(x.total_cents)}"></div>${esc(x.hour)}:00</div>`).join('') || '<div class="muted">Noch keine Daten.</div>';

      $('registers').innerHTML=registers.map(r=>{
        const online=onlineFrom(r.last_seen_at);
        return `<div class="register-mini"><div class="flex-between"><b>${esc(r.name)}</b><span class="status"><i class="dot" style="background:${online?'var(--green)':'#71879a'}"></i>${online?'Online':'Offline'}</span></div><div class="muted small">${esc(r.branch_name)} · ${esc(r.edition)} · ${esc(r.software_version||'Version unbekannt')}</div></div>`;
      }).join('') || '<div class="muted">Keine Kasse vorhanden.</div>';

      const recent=data.recentSales||[];
      $('salesRows').innerHTML=recent.map(s=>`<tr><td>${s.transaction_type&&s.transaction_type!=='SALE'?esc(bookingName(s.transaction_type))+' ':''}#${esc(s.receipt_number)}</td><td>${Number(s.pickup_number)>0?esc(String(s.pickup_number).padStart(3,'0')):'–'}</td><td>${time(s.occurred_at)}</td><td>${paymentName(s.payment_method)}</td><td>${esc(s.register_name)}</td><td>${esc(s.operator_name||'–')}</td><td><b>${eur(s.total_cents)}</b></td></tr>`).join('') || '<tr><td colspan="7">Noch keine Verkäufe.</td></tr>';
      $('stockRows').innerHTML=stock.filter(s=>Number(s.min_stock_quantity)>0&&Number(s.quantity)<=Number(s.min_stock_quantity)).slice(0,10).map(s=>`<tr><td>${esc(s.name)}</td><td class="low"><b>${esc(s.quantity)}</b></td><td>${esc(s.min_stock_quantity)}</td></tr>`).join('') || '<tr><td colspan="3">Keine Warnungen.</td></tr>';

      $('allSalesRows').innerHTML=sales.map(s=>`<tr><td>${s.transaction_type&&s.transaction_type!=='SALE'?esc(bookingName(s.transaction_type))+' ':''}#${esc(s.receipt_number)}</td><td>${Number(s.pickup_number)>0?esc(String(s.pickup_number).padStart(3,'0')):'–'}</td><td>${dateTime(s.occurred_at)}</td><td>${paymentName(s.payment_method)}</td><td>${esc(s.branch_name)}</td><td>${esc(s.register_name)}</td><td>${esc(s.operator_name||'–')}</td><td>${esc(s.item_count||0)}</td><td><b>${eur(s.total_cents)}</b></td></tr>`).join('') || '<tr><td colspan="9">Noch keine Verkäufe.</td></tr>';
      $('receiptRows').innerHTML=sales.map(s=>`<tr class="receipt-row" data-receipt-id="${esc(s.sale_id)}"><td><b>${s.transaction_type&&s.transaction_type!=='SALE'?esc(bookingName(s.transaction_type))+' ':''}#${esc(s.receipt_number)}</b></td><td>${Number(s.pickup_number)>0?esc(String(s.pickup_number).padStart(3,'0')):'–'}</td><td>${dateTime(s.occurred_at)}</td><td>${esc(s.register_name)}</td><td>${paymentName(s.payment_method)}</td><td>${esc(s.operator_name||'–')}</td><td><b>${eur(s.total_cents)}</b></td><td><button type="button" class="btn btn-mini receipt-detail-btn" data-receipt-id="${esc(s.sale_id)}">Details</button></td></tr>`).join('') || '<tr><td colspan="8">Noch keine Bons synchronisiert.</td></tr>';
      document.querySelectorAll('.receipt-detail-btn').forEach(btn=>btn.addEventListener('click',e=>{e.stopPropagation();openReceiptDetail(btn.dataset.receiptId);}));
      document.querySelectorAll('.receipt-row[data-receipt-id]').forEach(row=>row.addEventListener('click',()=>openReceiptDetail(row.dataset.receiptId)));

      $('avgReceipt').textContent=eur(totals.avg_cents);
      const total=Number(totals.total_cents)||0;
      $('cashShare').textContent=total?`${Math.round((Number(totals.cash_cents)||0)/total*100)} %`:'0 %';
      $('cardShare').textContent=total?`${Math.round((Number(totals.card_cents)||0)/total*100)} %`:'0 %';
      $('reportSaleCount').textContent=String(totals.sale_count||0);
      $('zRows').innerHTML=zReports.map(z=>`<tr><td>${esc(z.z_number)}</td><td>${dateTime(z.occurred_at)}</td><td>${esc(z.branch_name)}</td><td>${esc(z.register_name)}</td><td>${esc(z.sale_count)}</td><td><b>${eur(z.gross_cents)}</b></td></tr>`).join('') || '<tr><td colspan="6"><span class="muted">Noch kein Z-Bericht in die Cloud synchronisiert.</span></td></tr>';

      $('productRows').innerHTML=stock.map(s=>`<tr><td>${esc(s.sku||s.product_key)}</td><td>${esc(s.barcode||'–')}</td><td><b>${esc(s.name)}</b><div class="muted small">${esc(s.group_name||'')} ${s.category_name?'› '+esc(s.category_name):''}</div></td><td>${esc(s.category_name||'–')}</td><td>${eur(s.price_cents||0)}</td><td>${eur(s.purchase_price_cents||0)}</td><td>${esc(s.quantity)} ${esc(s.unit||'')}</td><td>${esc(s.min_stock_quantity||0)}</td><td>${esc(s.register_name)}</td><td>${dateTime(s.updated_at)}</td></tr>`).join('') || '<tr><td colspan="10">Noch keine Artikel synchronisiert.</td></tr>';
      $('inventoryRows').innerHTML=stock.map(s=>{const min=Number(s.min_stock_quantity)||0;const low=min>0&&Number(s.quantity)<=min;const value=Math.round((Number(s.quantity)||0)*(Number(s.purchase_price_cents)||0));return `<tr><td>${esc(s.sku||s.product_key)}</td><td>${esc(s.barcode||'–')}</td><td><b>${esc(s.name)}</b></td><td>${esc(s.category_name||'–')}</td><td>${esc(s.branch_name)}</td><td>${esc(s.register_name)}</td><td class="${low?'low':''}"><b>${esc(s.quantity)} ${esc(s.unit||'')}</b></td><td>${esc(min)}</td><td>${eur(value)}</td><td><span class="badge ${low?'badge-warn':''}">${low?'Niedrig':'OK'}</span></td></tr>`;}).join('') || '<tr><td colspan="10">Noch keine Bestandsdaten.</td></tr>';

      $('userCards').innerHTML=users.map(u=>`<div class="register-mini"><div class="flex-between"><b>${esc(u.display_name)}</b><span class="badge">${esc(u.role)}</span></div><div class="muted small">${esc(u.email)}</div></div>`).join('') || '<div class="muted">Keine Portal-Benutzer.</div>';
      $('operatorRows').innerHTML=operators.map(o=>`<tr><td><b>${esc(o.operator_name||'Unbekannt')}</b></td><td>${esc(o.sale_count)}</td><td>${eur(o.total_cents)}</td></tr>`).join('') || '<tr><td colspan="3">Noch keine Bedienerdaten.</td></tr>';

      $('branchCards').innerHTML=branches.map(b=>`<div class="panel"><div class="flex-between"><div><div class="eyebrow">Filiale</div><h3>${esc(b.name)}</h3><div class="muted">${esc(b.city||'')}</div></div><span class="badge">${esc(b.register_count)} Kasse(n)</span></div></div>`).join('') || '<div class="panel muted">Keine Filiale vorhanden.</div>';

      $('deviceCards').innerHTML=registers.map(r=>{const online=onlineFrom(r.last_seen_at); return `<div class="panel"><div class="flex-between"><div><div class="eyebrow">${esc(r.branch_name)}</div><h3>${esc(r.name)}</h3><div class="muted small">${esc(r.device_code)} · ${esc(r.edition)}</div></div><span class="status"><i class="dot" style="background:${online?'var(--green)':'#71879a'}"></i>${online?'Online':'Offline'}</span></div><div class="status-grid">${statusLine('Software',r.software_version)}${statusLine('TSE',r.tse_status,['BEREIT','OK'])}${statusLine('Drucker',r.printer_status,['BEREIT','OK'])}${statusLine('Letzte Meldung',dateTime(r.last_seen_at))}</div></div>`;}).join('') || '<div class="panel muted">Keine Geräte vorhanden.</div>';

      $('supportIdentity').innerHTML=`<div class="support-code"><small>Kundennummer</small><b>${esc(me.business.customer_number)}</b></div>`;
      const health=await api('/api/health');
      $('cloudHealthBadge').textContent=health.ok?'Cloud online':'Cloud nicht erreichbar';
      $('cloudHealthBadge').classList.toggle('badge-ok',!!health.ok);
      $('supportStatus').innerHTML=`${statusLine('TOR Cloud',health.ok?'Online':'Fehler',['ONLINE'])}${statusLine('Cloud-Version',health.version)}${statusLine('Kassen verbunden',String(registers.filter(r=>onlineFrom(r.last_seen_at)).length))}${statusLine('Letzte Prüfung',new Date().toLocaleString('de-DE',{timeZone:'Europe/Berlin'}))}`;

      document.querySelectorAll('.portal-view').forEach(el=>el.setAttribute('aria-busy','false'));
    } catch(err) {
      if(String(err.message).includes('angemeldet')) location.href='/login';
      else { $('refreshStatus').textContent='Aktualisierung fehlgeschlagen · Angezeigte Daten können veraltet sein: '+err.message; document.querySelectorAll('#registers .status, #deviceCards .status').forEach(el=>el.textContent='Nicht aktuell geprüft'); $('cloudHealthBadge').textContent='Verbindung nicht bestätigt';$('cloudHealthBadge').classList.remove('badge-ok'); }
    } finally {loading=false;}
  }
  $('refreshButton').addEventListener('click',refresh);
  $('previousPage').addEventListener('click',()=>{if(!loading){offset=Math.max(0,offset-100);refresh();}});
  $('nextPage').addEventListener('click',()=>{if(!loading){offset+=100;refresh();}});
  showView(location.hash.slice(1)||'dashboard');
  refresh();
  setInterval(()=>{if(!document.hidden)refresh();},30000);
  document.addEventListener('visibilitychange',()=>{if(!document.hidden)refresh();});
}
