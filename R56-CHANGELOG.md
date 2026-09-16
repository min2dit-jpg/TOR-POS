# R56 – Tastatur / Sofortdruck / Aktionszeile

- Deutsche In-App-Tastatur mit Touch-Automatik (abschaltbar), manueller Taste,
  Zahlen-/Sonderzeichenbelegung, Passwortmaskierung, Auswahlersetzung und Unicode-Löschung.
  Kein Öffnen durch HID-Tastendrücke. Bei geöffneter Tastatur bleibt das Formular scrollbar.
- Auftragserstellung/-änderung/-storno signalisiert den Druck-Dispatcher erst nach Commit.
  Semaphore bündelt Wecksignale; persistente Auftrags-IDs und Druckjournal bleiben unverändert.
- Untere Aktionszeile: 72 DIP; Bestellung annehmen zweizeilig. Bar/Karte kleinere umbrechende Schrift.
- Einheitliche Versionsnummer 0.7.33.560.

Prüfgrenzen: 203 Checks im Offline-C#-Harness. XAML-Namensfelder im Harness generiert,
keine XAML-Kompilierung. NuGet/normaler Build blockiert. Echte Windows-Touch- und
Druckerabnahme ausstehend. Cloud/SumUp unverändert. Kein Produktionsfreigabewechsel.
