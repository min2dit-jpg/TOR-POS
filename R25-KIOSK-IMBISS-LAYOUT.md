# R25 KIOSK / IMBISS Layout

Mode-aware defaults were added without changing the stable XAML structure.

Default layout when the old generic defaults are still in use:
- KIOSK categories: 5 x 6
- KIOSK products: 5 x 8
  - compact, scanner-first quick selection
  - product images stay suppressed in compact mode
- IMBISS categories: 4 x 4
- IMBISS products: 4 x 5
  - larger touch targets
  - product images can be shown when enabled

Important:
If the customer already changed row/column settings away from TOR's former
defaults, those explicit values are preserved.

No changes to:
- payment commit
- TSE
- ZVT
- DSFinV-K
- database schema
- receipt numbering
- fiscal logic
