# Test checklist

## Performans
- [ ] 10.000 ürün dataset
- [ ] 500.000 satış geçmişi
- [ ] 1000 ardışık barcode lookup
- [ ] 4 GB RAM POS PC
- [ ] 1366x768 touch screen
- [ ] 8 saat kesintisiz kullanım

## Scanner
- [ ] EAN-8
- [ ] EAN-13
- [ ] UPC-A numeric
- [ ] hızlı ardışık scan
- [ ] bilinmeyen barcode
- [ ] aynı ürün peş peşe

## Satış
- [ ] BAR
- [ ] KARTE test kaydı
- [ ] Pfand
- [ ] PFAND ana panel butonu 8 / 15 / 25 Cent seçeneklerini açıyor
- [ ] PFAND penceresinde Kiste leer 1,50 € ve Kiste voll 3,30 € görünüyor
- [ ] KIOSK kurulumunda PFAND, IMBISS kurulumunda aynı yerde EXTRA görünüyor
- [ ] KIOSK lisansı IMBISS’te ve IMBISS lisansı KIOSK’ta reddediliyor
- [ ] IMBISS Stammdaten içinde EXTRAS sekmesi ve Käse/Ketchup/Fleisch örnekleri görünüyor
- [ ] Admin dışında üç çalışan hesabı Personal & Rechte bölümünden düzenlenebiliyor
- [ ] Yetkisi kapatılan işlem ilgili çalışan için devre dışı kalıyor
- [ ] TRAINING girişinde terminal çağrılmıyor, bon numarası oluşmuyor ve satış tablosuna kayıt yapılmıyor
- [ ] BON-HISTORIE yalnız günün/son Z’den sonraki bonlarını gösteriyor ve seçilen bon kopya basılabiliyor
- [ ] Z sonrasında eski satışların silinmediği doğrulanıyor
- [ ] 1180x760 çözünürlükte Aktueller Verkauf ve tüm işlem butonları çakışmadan görünüyor
- [ ] BAR ve KARTE aynı boyutta, diğer işlem tuşlarından daha büyük görünüyor
- [ ] Tüm ikincil işlem butonları kendi satırında eşit genişlik ve yükseklikte görünüyor
- [ ] Paneldeki 16 buton 4 x 4 olarak aynı ölçüde ve aralıksız görünüyor
- [ ] Temiz ilk kurulumda KIOSK / IMBISS seçim sayfası etkin görünüyor
- [ ] Paket açılarak doğrudan ilk çalıştırmada KIOSK / IMBISS uygulama içi seçim ekranı açılıyor
- [ ] Mevcut kurulum güncellemesinde kayıtlı edition değiştirilmeden korunuyor
- [ ] Farklı ekran yüksekliklerinde UniformGrid kalan kontrol alanını tamamen dolduruyor
- [ ] 16 butonun ActualWidth ve ActualHeight değerleri birbirine eşit görünüyor
- [ ] variant
- [ ] quantity
- [ ] discount
- [ ] restart
- [ ] güç kesintisi simülasyonu

TSE bağlanmadan TEST / TSE NICHT VERBUNDEN kalmalıdır.

## v0.7.32 – Menü / Warenverwaltung / Berichte

- [ ] Hauptmenü zeigt WARENVERWALTUNG / EINSTELLUNGEN / BERICHTE.
- [ ] WARENVERWALTUNG → Artikelverwaltung öffnet Stammdaten.
- [ ] Duplikate anzeigen listet doppelte EAN/Artikelnummer/Namen.
- [ ] Artikel exportieren erzeugt CSV; erneuter Import aktualisiert Artikel ohne Bon-/Umsatzdaten zu verändern.
- [ ] Import aus Datenbank übernimmt nur Stammdaten, keine Verkäufe/TSE-/Z-Daten.
- [ ] Inventur speichert gezählten Bestand; reale Verkäufe reduzieren stock_quantity.
- [ ] Etiketten drucken erzeugt PDF; gültige EAN-13 werden als Barcode gezeichnet.
- [ ] Programm beenden löst den vorhandenen Exit-Backup aus.
- [ ] Einstellungen-Menü öffnet die angeforderte Zielseite direkt.
- [ ] Pfand / Leergut speichert 8/15/25 Cent sowie Kiste leer/voll und direkte KIOSK-Pfandtasten übernehmen die Werte.
- [ ] X-Bericht zeigt aktuellen Zeitraum seit letztem Z und erzeugt KEINEN daily_closing/Z-Zähler.
- [ ] Kassensturz zeigt Soll / Ist / Differenz und protokolliert die Zählung.
- [ ] Z-Abschluss-Journal ist append-only; archivierter Z kann als PDF nachgedruckt werden.
- [ ] Produktiver Z bleibt vollständig durch FiscalCompliance ProductionAllowed + offene-Parken-Sperre geschützt.
- [ ] Programmierungsprotokoll-PDF enthält Firma, eAS, Software, Edition, TSE, Lizenz, Steuern und Geräte.
- [ ] Fiskal-Prüfung zeigt alle Blocking Items.
- [ ] GDPdU-Tools erzeugt TOR-interne CSV-Prüfunterlagen mit deutlichem Hinweis, dass dies kein DSFinV-K-Ersatz ist.
- [ ] DSFinV-K bleibt gesperrt, solange offizieller Descriptor und vollständige sale_fiscal_data fehlen.
- [ ] TSE Export ist nur aktiv, wenn der Swissbit Provider ExportAvailable meldet.


## v0.7.32 – Build-Fix Test
- [ ] `1-SETUP-ERSTELLEN.bat` ohne CS0176
- [ ] Warenverwaltung öffnet
- [ ] Berichte / X-Bericht öffnet
- [ ] Z-Abschluss-Journal öffnet
- [ ] PDF-Berichte können geöffnet werden
- [ ] Programmierungsprotokoll PDF kann geöffnet werden


## v0.7.32 – Touch-Layout
- [ ] Programmeinstellungen zeigt Waren-/Artikeltasten-Layout
- [ ] Standard 4×3 Warengruppen = 12 sichtbare Tasten
- [ ] Standard 4×10 Artikel = 40 sichtbare Tasten je Seite
- [ ] Warengruppe wählen lädt nur deren Artikel
- [ ] ◀ / ▶ Warengruppen-Paging funktioniert
- [ ] ◀ / ▶ Artikel-Paging funktioniert
- [ ] Änderung der Spalten/Zeilen wird nach Schließen sofort übernommen
- [ ] Kleine Artikelraster können Bilder zeigen; hohe Raster werden kompakt


## v0.7.32 – Startbildschirm
- [ ] TOR POS über Desktop-Verknüpfung starten
- [ ] kein schwarzes Konsolenfenster sichtbar
- [ ] helles TOR-Kassensysteme Splash-Fenster sichtbar
- [ ] TOR-Logo wird angezeigt
- [ ] humorvoller Starttext vollständig lesbar
- [ ] Fortschrittsbalken läuft
- [ ] Login-Fenster erscheint nach Initialisierung automatisch


## v0.7.32 – Startup Build Test
- [ ] `1-SETUP-ERSTELLEN.bat` CS0176 vermeden tamamlanıyor
- [ ] Inno Setup otomatik başlıyor
- [ ] TOR POS Pro masaüstü kısayolundan başlatılıyor
- [ ] müşteri uygulamasında siyah konsol yok
- [ ] TOR Kassensysteme logo splash görünüyor
- [ ] splash sonrası Login açılıyor


## v0.7.32 – Warengruppen-Navigation
- [ ] Hauptkasse startet mit WARENGRUPPEN-Übersicht
- [ ] keine Artikel werden vor Auswahl einer Warengruppe angezeigt
- [ ] Warengruppe antippen öffnet deren Artikel auf derselben Fläche
- [ ] `◀ WARENGRUPPEN` kehrt zur Gruppenübersicht zurück
- [ ] Warengruppen-Seiten ◀/▶ funktionieren
- [ ] Artikel-Seiten ◀/▶ funktionieren
- [ ] Programmeinstellungen 1–10 Warengruppen-Zeilen speichern
- [ ] Artikelverkauf / Scanner / Warenkorb unverändert funktionsfähig


## v0.7.32 – Warengruppen-Kacheltest
- [ ] Warengruppenname sitzt innerhalb der großen Kachel
- [ ] keine kleinen schwebenden Warengruppenbuttons mehr
- [ ] jede sichtbare Warengruppe füllt ihre Rasterzelle
- [ ] lange Namen werden höchstens zweizeilig dargestellt
- [ ] Kachel reagiert sichtbar auf Mouse/Touch
- [ ] Warengruppe öffnet weiterhin nur deren Artikel
- [ ] ◀ WARENGRUPPEN funktioniert
- [ ] Warengruppen-Spalten/Zeilen aus Programmeinstellungen funktionieren weiter
