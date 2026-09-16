# TOR POS R98 – CSV-/Datenbank-Import vererbte Im-Haus-Einstellung nicht

## Kontext
Gefunden bei einer Vollständigkeitsprüfung von R97 ("System muss
vollständig einsatzbereit sein"): `ImportArticlesCsvAsync` und
`ImportArticlesFromDatabaseAsync` schreiben Artikel über einen komplett
separaten Codepfad (`EnsureCategoryAsync`/`UpsertProductAsync`) als der
normale Warengruppen-/Artikel-Editor (`SaveWithStockAsync`). Dieser
separate Pfad kannte `im_haus_applicable` überhaupt nicht - ein per CSV
importierter oder aktualisierter Artikel bekam immer den rohen
SQLite-Spaltenstandard (an), unabhängig davon, ob ein Admin die
Warengruppe zuvor bewusst über den R97-Schalter ausgeschlossen hatte.

## Konkretes Fehlerbild
Admin schaltet "Im Haus/Außer Haus" für die Warengruppe "Süßwaren"
(7 % MwSt.) bewusst ab. Später importiert er sein Sortiment per CSV neu
(z. B. Preisaktualisierung) - jeder Süßwaren-Artikel bekommt dabei
stillschweigend wieder `im_haus_applicable=1`, die bewusste Abwahl ist
weg, ohne dass der Import das meldet.

## Änderung
- `EnsureCategoryAsync` liest nach dem Anlegen/Aktualisieren der
  Warengruppen-Stammdaten deren aktuelles `im_haus_applicable` zurück
  (Rückgabewert jetzt ein Tupel `(CategoryId, ImHausApplicable)`). Die
  UPSERT-Anweisung selbst setzt `im_haus_applicable` bewusst NICHT auf
  einer bereits bestehenden Warengruppe zurück - ein Preis-Import darf
  eine bewusste Admin-Entscheidung nie stillschweigend überschreiben.
- `UpsertProductAsync` bekommt einen neuen `imHausApplicable`-Parameter
  und setzt ihn sowohl beim INSERT (neuer Artikel) als auch beim UPDATE
  (bestehender Artikel, per EAN/Artikel-Nr. erkannt).
- Beide Importpfade (CSV und Datenbank-zu-Datenbank) nutzen dieselben
  zwei Hilfsfunktionen und sind damit automatisch beide korrigiert.

## Ergebnis
- 4 neue Prüfungen in `R98ReviewTests.cs`: ein CSV-Import in eine bereits
  abgeschaltete Warengruppe vererbt korrekt `false`, eine beim Import neu
  angelegte Warengruppe bekommt den normalen Standard (`true`), und ein
  erneuter Import (UPDATE-Zweig, nicht nur INSERT) behält die
  abgeschaltete Einstellung bei. Sicherheits-Testsuite: **496/496 PASS**.
- `dotnet build -c Release` für `TorPos.App`: 0 Warnung(en), 0 Fehler.

## Einordnung
Reine Korrektur der Datenvererbung, keine neue fiskalische Regel - macht
nur den Import-Pfad konsistent mit dem bereits korrekten Editor-Pfad.
Beide Fiskal-Sperren unverändert `false`.
