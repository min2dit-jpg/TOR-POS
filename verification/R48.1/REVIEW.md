# R48 review / R48.1 corrections

Reviewed the supplied R48 archive as the base; no rollback to R41/R42.
Desktop compilation succeeded (8 existing nullable/obsolete/XAML-constructor warnings). Safety suite now compiles; R48 had three missing pickup-number arguments in CloudTests. Added repository rollback, article numbering, migration retention, training persistence and Berlin-day tests.

Corrected simulation checkout leaving a parked record open; persistent test/training pickup sequences; atomic product/count/audit save and atomic inventory/audit changes. SumUp source files match the uploaded archive byte for byte. Cloud's 15 tests passed; Cloud implementation unchanged.

Scope limits: hardware acceptance has not been performed here. Production fiscal release remains closed. Menu/combo, kitchen printer routing, localization, delivery integration, fiscal returns and scale/performance acceptance remain follow-up work. Product variants still use their existing separate repository transaction; full article-plus-variant transactional editing is a follow-up, not claimed complete. Training/test pickup numbers allocate at completion and may have gaps after interruptions; they are not fiscal receipt numbers.
