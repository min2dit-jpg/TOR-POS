# TOR POS R96 – Im Haus/Außer Haus überlebt jetzt Parken und Absturz-Wiederherstellung

## Kontext
Gefunden bei einer Hoch-Effort-Codeüberprüfung von R85-R95 (auf Nutzerwunsch).
Die Im-Haus/Außer-Haus-Auswahl (`_imHaus`) wurde nirgends über zwei reale
Wege hinweg mitgeführt:
1. **Parken (IMBISS ORDER-Modus)**: `ParkAsync`/`UpdateAsync` erhielten
   den rohen Warenkorb ohne jede Kenntnis von `_imHaus`, und
   `PrepareNextCustomer()` setzte den Umschalter direkt nach dem Parken
   auf Außer Haus zurück.
2. **Absturz-Wiederherstellung**: `OpenCartRecoverySnapshot` (die JSON-Datei,
   die den offenen Warenkorb bei einem Programmabsturz rettet) kannte kein
   `ImHaus`-Feld.

## Konkretes Fehlerbild
Kasiyer aktiviert "IM HAUS", fügt Artikel hinzu, parkt die Bestellung
(z. B. um sie später zu kassieren). Bei der späteren Wiederaufnahme zum
Kassieren steht der Umschalter wieder stillschweigend auf "AUSSER HAUS" -
wird er nicht erneut gesetzt, wird eine vor Ort verzehrte Speise
fälschlich mit 7 % statt 19 % abgerechnet. Derselbe Verlustträte ein,
wenn das Programm zwischen Umschalten und Kassieren abstürzt.

## Änderung
Bewusst **nicht** durch Einbrennen des MwSt.-Satzes in die geparkten
Zeilen gelöst (das hätte bei einem später zum wiedereröffneten Bon
hinzugefügten Artikel zu einem gemischten, inkonsistenten Warenkorb
geführt), sondern durch Persistieren der Auswahl selbst:
- Neue Spalte `parked_receipts.im_haus` (über dasselbe leichte
  `EnsureColumnAsync`-Muster wie `preparation_state`/`order_note`, keine
  vollständige versionierte Migration nötig).
- `IParkedReceiptRepository.ParkAsync`/`UpdateAsync` erhalten einen neuen
  `imHaus`-Parameter (Default `false`, bestehende Aufrufer unverändert
  kompilierbar); `ParkedReceipt.ImHaus` wird bei `GetOpenAsync`/
  `GetOpenByIdAsync` korrekt zurückgelesen (Spalte ans Ende der
  SELECT-Liste angehängt, um bestehende Spaltenindizes nicht zu
  verschieben).
- `MainWindow`: beide Park-Aufrufe übergeben jetzt `_imHaus`; beide
  Wiedereröffnungsstellen (Bestellübersicht, Geparkte-Bons-Dialog) setzen
  `_imHaus = parked.ImHaus` und aktualisieren den Umschalter, bevor der
  Warenkorb angezeigt wird.
- `OpenCartRecoverySnapshot` bekommt ein `ImHaus`-Feld; beide
  Wiederherstellungspfade (offene ZVT-Zahlung aus dem Checkout-Journal -
  dort war `CheckoutSnapshot.ImHaus` aus R95 bereits vorhanden - und die
  JSON-Datei-Wiederherstellung) setzen `_imHaus` vor dem erneuten Anzeigen
  des Warenkorbs zurück.

Die eigentliche MwSt.-Anhebung passiert weiterhin ausschließlich in
`CaptureCheckout`s bestehendem `CopyLines(_imHaus)`-Aufruf beim
tatsächlichen Kassiervorgang (unverändert seit R95) - dieser Fix stellt
nur sicher, dass `_imHaus` zu diesem Zeitpunkt den richtigen Wert trägt.

## Ergebnis
- 6 neue Prüfungen in `R96ReviewTests.cs`: `ParkAsync` gibt die Auswahl im
  Rückgabewert korrekt zurück, `GetOpenByIdAsync` und `GetOpenAsync`
  lesen sie nach einem frischen Reload korrekt zurück, ein Aufrufer ohne
  explizite Angabe bleibt beim bisherigen Außer-Haus-Standard, und
  `UpdateAsync` kann die Auswahl an einer bereits geparkten Bestellung in
  beide Richtungen ändern. Sicherheits-Testsuite: **485/485 PASS**.
- `dotnet build -c Release` für `TorPos.App`: 0 Warnung(en), 0 Fehler.

## Einordnung
Schließt die in der Hoch-Effort-Überprüfung gemeldete Lücke vollständig.
Reine Vervollständigung der R95-Funktion, keine neue fiskalische
Buchungslogik. Beide Fiskal-Sperren unverändert `false`.
