# TOR POS v0.7.33 R10 – Payment Flow

## Barzahlung
- BAR öffnet einen touch-optimierten Zahlungsdialog.
- Gesamtbetrag groß sichtbar.
- PASSEND kassiert mit einem Tipp.
- Vier dynamische Schnellbeträge werden passend zum Bon berechnet.
- Freie Eingabe per Touch-Ziffernblock oder Tastatur.
- Rückgeld wird live berechnet; Unterzahlung kann nicht abgeschlossen werden.
- Gegeben/Rückgeld werden nach erfolgreichem Verkauf im Audit protokolliert.
- Auf dem unmittelbar gedruckten Barbon werden Gegeben und Rückgeld ausgegeben.

## Kartenzahlung / ZVT
- Klarere Zustände: Terminal wartet / Karte OK / nicht gebucht.
- UNGEKLAERT_TIMEOUT wird als großer Warnhinweis angezeigt.
- Bei ungeklärtem Timeout bleibt der Bon offen; kein automatischer zweiter Zahlversuch.
- Wenn das Terminal Zahlung bestätigt, aber der lokale Sale-Commit fehlschlägt, wird der Fehler jetzt wirklich bis zum Sicherheits-Handler weitergereicht und als kritischer Audit-Fall erfasst.

## Nächster Kunde
Nach erfolgreicher Buchung werden Scanner-Puffer, numerische Mengeneingabe, Auswahl und Produktseite zurückgesetzt. Die Kasse ist sofort für den nächsten Vorgang bereit.

## Testpunkte auf Windows
1. BAR, PASSEND.
2. BAR, Schnellbetrag größer als Bon -> Rückgeld.
3. BAR, Betrag kleiner als Bon -> KASSIEREN deaktiviert.
4. BAR, freie Touch-Eingabe mit Komma.
5. KARTE ohne Terminal -> Bon bleibt offen.
6. ZVT Erfolg -> Sale wird einmal gespeichert.
7. ZVT Timeout nach Zahlungsstart -> Warnfenster, Bon bleibt offen.
8. Drucker aktiv -> Barbon enthält Gegeben/Rückgeld.
