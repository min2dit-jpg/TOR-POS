# R45 HTTP contract

Device calls require `X-Device-Code` and `X-Device-Token`. Redirects are not supported.
GET `/api/v1/devices/ping` confirms device authentication only.
POST `/api/v1/devices/sync`: JSON `{ "events": [...] }`, 1–250 events and at most 1 MiB.
Batch is atomic. Never acknowledge a failed batch locally.
Each event has a stable `event_id`, supported `type`, zoned ISO `occurred_at`, object `payload`.
On 200 require `ok:true` and exactly matching `results` containing `accepted` or `duplicate`
for every event ID. Same ID with changed contents yields 409. Retry must keep ID and content.

Example sale payload:
```json
{"receipt_number":101,"payment_method":"CASH","subtotal_cents":500,"discount_cents":0,"total_cents":500,"operator_name":"Demo","item_count":1,"items":[{"position_no":1,"product_key":"1","name":"Artikel","quantity":2,"unit_price_cents":250,"line_total_cents":500,"vat_rate":19}]}
```
Money is integer cents, quantities numeric, VAT numeric percent. Totals and positions must balance.
`item_count` is number of positions, not sum of quantities. CASH and CARD are supported.
`sale.completed` requires 1–5000 items, not a truncated list.
`stock.snapshot`: `{ "items": [{"product_key":"1","name":"Artikel","sku":"A-100","barcode":"4000000000011","group_name":"Getränke","category_name":"Softdrinks","unit":"Stück","price_cents":250,"quantity":12}] }`.
Complete active inventory replaces previous stock list, maximum 5000 items, no silent truncation.
`heartbeat`: `{ "software_version":"0.7.33-R45", "tse_status":"NICHT GEPRÜFT", "printer_status":"NICHT GEPRÜFT" }`.
`cash.movement`: movement_type DEPOSIT/WITHDRAWAL, nonnegative amount_cents, reason, actor.
`z.closed`: z_number, gross_cents, sale_count. These two events are not yet emitted by Desktop R45.

OWNER portal session: GET `/api/portal/data?offset=0` (100 sales/page), `/api/receipts/:saleId`.
Queries scope to authenticated business. Public health response includes demo-mode flag.
