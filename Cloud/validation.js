'use strict';
const fail = message => { throw Object.assign(new Error(message), {statusCode:400}); };
function text(v, name, max=200, optional=false) {
  if (optional && v == null) return '';
  if (typeof v !== 'string' || (!optional && !v.trim()) || v.length>max) fail(`${name}: ungültiger Text`);
  return v;
}
function number(v,name,min=-1e12,max=1e12,integer=false){
  if(typeof v!=='number'||!Number.isFinite(v)||v<min||v>max||(integer&&!Number.isSafeInteger(v))) fail(`${name}: ungültige Zahl`);
  return v;
}
function cents(v,name,min=-1e12){return number(v,name,min,1e12,true);}
// R149: the till's rule (TorPos.Core.ReceiptTotals) - a discount never makes a
// purchase negative; returned deposit can make the subtotal itself negative.
function receiptTotal(subtotal,discount){return subtotal<0?subtotal-Math.max(0,discount):Math.max(0,subtotal-discount);}
function canonical(value){
  if(Array.isArray(value))return '['+value.map(canonical).join(',')+']';
  if(value && typeof value==='object')return '{'+Object.keys(value).sort().map(k=>JSON.stringify(k)+':'+canonical(value[k])).join(',')+'}';
  return JSON.stringify(value);
}
function berlinParts(value){
  const parts=new Intl.DateTimeFormat('en-CA',{timeZone:'Europe/Berlin',year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',hourCycle:'h23'}).formatToParts(new Date(value));
  const p=Object.fromEntries(parts.map(x=>[x.type,x.value]));
  return {day:`${p.year}-${p.month}-${p.day}`,hour:p.hour};
}
function normalizeEvent(raw){
  if(!raw||typeof raw!=='object'||Array.isArray(raw))fail('Ereignis fehlt');
  const eventId=text(raw.event_id,'event_id',120);
  const type=text(raw.type,'type',80);
  const at=text(raw.occurred_at,'occurred_at',40);
  if(!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/.test(at)||!Number.isFinite(Date.parse(at)))fail('Zeitstempel mit Zeitzone erforderlich');
  const occurredAt=new Date(at).toISOString();
  const p=raw.payload;
  if(!p||typeof p!=='object'||Array.isArray(p))fail('payload fehlt');
  if(type==='sale.completed'){
    cents(p.receipt_number,'receipt_number',1);
    if(p.pickup_number!=null)number(p.pickup_number,'pickup_number',0,999999,true);
    // R179: SALE/STORNO/RETURN share one idempotent event contract. The till
    // keeps reversal amounts positive on the wire; Cloud applies the sign when
    // projecting them so validation remains identical to the local receipt.
    p.transaction_type=String(p.transaction_type||'SALE').toUpperCase();
    if(!['SALE','STORNO','RETURN'].includes(p.transaction_type))fail('Buchungstyp muss SALE, STORNO oder RETURN sein');
    if(p.transaction_type!=='SALE')cents(p.original_receipt_number,'original_receipt_number',1);
    // R149: MIXED is the R101 cash/card split the till has always sent, and the
    // discount arrives as discount_cents (older tills: manual_discount_cents only).
    if(!['CASH','CARD','MIXED'].includes(p.payment_method))fail('Zahlart muss CASH, CARD oder MIXED sein');
    if(p.discount_cents==null&&p.manual_discount_cents!=null)p.discount_cents=p.manual_discount_cents;
    // R149: returned deposit (Leergut) can make a receipt negative - money paid out.
    cents(p.subtotal_cents,'subtotal_cents');cents(p.discount_cents,'discount_cents',0);cents(p.total_cents,'total_cents');
    if(receiptTotal(p.subtotal_cents,p.discount_cents)!==p.total_cents)fail('Zwischensumme, Rabatt und Gesamt stimmen nicht überein');
    if(p.cash_portion_cents!=null)cents(p.cash_portion_cents,'cash_portion_cents');
    if(p.card_portion_cents!=null)cents(p.card_portion_cents,'card_portion_cents');
    if(p.transaction_type!=='SALE' &&
       ((p.cash_portion_cents??0)+(p.card_portion_cents??0)!==p.total_cents))
      fail('Bar-/Kartenanteil stimmt nicht mit Gesamt überein');
    if(p.total_cents<0&&p.payment_method!=='CASH')fail('Eine Pfand-Auszahlung ist nur bar möglich');
    text(p.operator_name,'operator_name',200,true);
    if(!Array.isArray(p.items)||p.items.length<1||p.items.length>5000)fail('1 bis 5000 Bonpositionen erforderlich');
    const positions=new Set();let sum=0;
    for(const i of p.items){
      if(!i||typeof i!=='object')fail('Bonposition ungültig');
      number(i.position_no,'position_no',1,5000,true);
      if(positions.has(i.position_no))fail('Doppelte Positionsnummer');positions.add(i.position_no);
      text(i.product_key,'product_key',120,true);text(i.name,'name',500);
      number(i.quantity,'quantity',-1e6,1e6);cents(i.unit_price_cents,'unit_price_cents');cents(i.line_total_cents,'line_total_cents');
      number(i.vat_rate,'vat_rate',0,100);
      const promoted=Number(i.promotion_percent||0)>0 && i.list_unit_price_cents!=null;
      if(promoted){
        cents(i.list_unit_price_cents,'list_unit_price_cents');
        cents(i.promotion_discount_cents,'promotion_discount_cents',0);
        number(i.promotion_percent,'promotion_percent',1,100,true);
        const listProduct=i.quantity*i.list_unit_price_cents;
        const listRounded=Math.sign(listProduct)*Math.floor(Math.abs(listProduct)+0.5+1e-7);
        if(listRounded-i.promotion_discount_cents!==i.line_total_cents)fail('Aktionspreis und Positionsbetrag stimmen nicht überein');
      }else{
        const product=i.quantity*i.unit_price_cents;
        const rounded=Math.sign(product)*Math.floor(Math.abs(product)+0.5+1e-7);
        if(rounded!==i.line_total_cents)fail('Menge und Positionsbetrag stimmen nicht überein');
      }
      sum+=i.line_total_cents;
    }
    if(sum!==p.subtotal_cents)fail('Summe der Bonpositionen stimmt nicht überein');
    if(p.item_count!=null && p.item_count!==p.items.length)fail('Positionsanzahl stimmt nicht überein');
    if(p.stock_consumption!=null){
      if(!Array.isArray(p.stock_consumption)||p.stock_consumption.length>5000)fail('stock_consumption ungültig');
      const consumptionKeys=new Set();
      for(const c of p.stock_consumption){
        if(!c||typeof c!=='object')fail('stock_consumption Eintrag ungültig');
        text(c.product_key,'stock_consumption.product_key',120);
        number(c.quantity,'stock_consumption.quantity',0,1e9);
        if(consumptionKeys.has(c.product_key))fail('Doppelter Lagerverbrauch'); consumptionKeys.add(c.product_key);
      }
    }
  }else if(type==='heartbeat'){
    for(const key of ['software_version','tse_status','printer_status'])text(p[key],key,100,true);
  }else if(type==='stock.snapshot'){
    if(!Array.isArray(p.items)||p.items.length>5000)fail('Bestand: maximal 5000 Artikel pro vollständigem Snapshot');
    const seen=new Set();
    for(const i of p.items){
      if(!i||typeof i!=='object')fail('Artikel ungültig');
      text(i.product_key,'product_key',120);text(i.name,'name',500);text(i.sku,'sku',120,true);text(i.barcode,'barcode',120,true);text(i.group_name,'group_name',200,true);text(i.category_name,'category_name',200,true);text(i.unit,'unit',80,true);if(i.price_cents!=null)cents(i.price_cents,'price_cents',0);if(i.purchase_price_cents!=null)cents(i.purchase_price_cents,'purchase_price_cents',0);if(i.min_stock_quantity!=null)number(i.min_stock_quantity,'min_stock_quantity',0,1e9);number(i.quantity,'quantity',-1e9,1e9);
      if(seen.has(i.product_key))fail('Doppelter Artikel');seen.add(i.product_key);
    }
  }else if(type==='cash.movement'){
    if(!['DEPOSIT','WITHDRAWAL'].includes(p.movement_type))fail('Ungültige Bargeldbewegung');
    cents(p.amount_cents,'amount_cents',0);text(p.reason,'reason',500,true);text(p.actor,'actor',200,true);
  }else if(type==='z.closed'){
    text(p.z_number,'z_number',120);cents(p.gross_cents,'gross_cents',0);number(p.sale_count,'sale_count',0,1e9,true);
  }else fail('Unbekannter Ereignistyp');
  return {eventId,type,occurredAt,payload:p};
}
module.exports={normalizeEvent,canonical,berlinParts,receiptTotal};
