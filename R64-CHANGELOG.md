# TOR POS R64 – Menü-Aufräumen & Sortierung

Version: **0.7.33.640** (Vorschlag – in `Directory.Build.props` / `.csproj` noch anzupassen)
Basis: **R63-Core-Stability-Cleanup**

## Ziel
Aufräumen des Warengruppen-/Artikel-Menüs (Kassenbildschirm) sowie Reduzierung
von dupliziertem Code rund um dieses Menü. Keine fiskalischen, keine
Bezahl-/TSE-relevanten Änderungen.

## 1. Zentrale Paginierungs-Formel
`MainWindow.axaml.cs` hat die Spalten/Zeilen/Seiten-Berechnung für die
Warengruppen- und Artikel-Kacheln vorher an **5 Stellen** dupliziert gehabt
(`BuildCategories`, `OnCategoryNextClick`, `EnsureSelectedCategoryVisible`,
`BuildProducts`, `OnProductNextClick`). Jetzt gibt es einen einzigen
`GridPaging`-Wert, erzeugt über `CategoryPaging()` / `ProductPaging()`.
Verhalten unverändert, nur die Fehlerquelle "eine Stelle vergessen"
beseitigt.

## 2. MainWindow.axaml.cs aufgeteilt
Reiner Menü-/Kachel-Code (Kategorie- und Artikel-Grid, Paginierung,
Bild-Cache, Farb-Hilfsfunktionen) wurde aus `MainWindow.axaml.cs`
herausgelöst in eine neue Datei `MainWindow.Menu.cs` (gleiches Muster wie
das bereits bestehende `MainWindow.Safety.cs`). Reines Verschieben von Code,
keine Logikänderung.

## 3. Warengruppen direkt verschiebbar
`ProductEditorWindow.cs`, Reiter „Warengruppen“: neue Buttons
**„▲ NACH OBEN“** / **„▼ NACH UNTEN“**.

- Verschiebt die gewählte Warengruppe in genau der Reihenfolge, in der sie
  auch auf dem Kassenbildschirm erscheint.
- Nummeriert anschließend **alle** Warengruppen lückenlos neu (0,1,2,…) –
  das repariert nebenbei alte, doppelte oder nie gepflegte Sortierwerte.
- Das Textfeld „Sortierung“ bleibt für die manuelle Feineingabe erhalten,
  ist für die normale Nutzung aber nicht mehr nötig.

## Nicht enthalten (bewusst offen gelassen)
- Gruppen (`ProductGroup`) und Artikel (`Product`) haben weiterhin nur die
  manuelle Sortier-Nummer, keine Auf/Ab-Buttons. Gleiches Muster könnte bei
  Bedarf 1:1 übernommen werden.
- Keine Windows-Build-/Testausführung möglich in der Generierungsumgebung
  (kein .NET SDK, kein Netzwerkzugriff). **Vor Rollout zwingend:**
  `Desktop\1-SETUP-ERSTELLEN.bat` bauen und
  `Desktop\7-SICHERHEITSTESTS.bat` bis **ALL 242 CHECKS PASSED** laufen
  lassen, plus manueller Test des Verschiebens im Warengruppen-Editor und
  Kontrolle der resultierenden Reihenfolge auf dem Kassenbildschirm.

## Geänderte Dateien
- `Desktop/src/TorPos.App/MainWindow.axaml.cs` (bereinigt)
- `Desktop/src/TorPos.App/MainWindow.Menu.cs` (neu)
- `Desktop/src/TorPos.App/ProductEditorWindow.cs` (Verschieben-Buttons)


## FIX1 – İnceleme sonrası düzeltmeler
- Gerçek ürün/EXE/Setup sürümü `0.7.33.640 / R64` olarak güncellendi.
- Warengruppe ▲/▼ sıralaması artık tek SQLite transaction içinde yalnızca
  `sort_order` alanlarını değiştirir. `SaveCategoryAsync` üzerinden MwSt.,
  renk ve metadata tekrar yazılmaz.
- Geçersiz bir ID çıkarsa transaction tamamen rollback olur; yarım sıralama kalmaz.
- Warengruppen editöründeki liste kasa ekranındaki `SortOrder -> Name`
  sırasıyla aynı hale getirildi.
- 5 geniş buton tek satır yerine iki satıra ayrıldı; 1180 px pencerede taşma riski azaltıldı.
- Boş Warengruppe hücreleri artık sahte/deaktif kutu çizmez.
- 2 yeni güvenlik testi eklendi. Windows hedefi: `ALL 244 CHECKS PASSED`.
