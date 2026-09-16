# TOR POS R87 – Artikelbild-Thumbnails statt Volldekodierung

## Kontext
`MainWindow.Menu.cs` (`LoadImage`) dekodierte jedes Artikelbild mit
`new Bitmap(stream)` bei jedem ersten Rendern einer Artikelkachel in voller
Auflösung - unabhängig davon, dass die Kachel selbst nur wenige hundert
Pixel breit ist. Ein von einem Kunden hochgeladenes Handyfoto kann leicht
3000+ Pixel breit sein; bei größeren Sortimenten (siehe R84s
10.000-Artikel-Benchmark) summiert sich das unnötig in Arbeitsspeicher und
Dekodierzeit bei jedem Kategoriewechsel. Roadmap-Punkt "thumbnail
generation" (Core v0.2).

## Änderung
`LoadImage` dekodiert jetzt über Avalonias eigenes
`Bitmap.DecodeToWidth(stream, 480, BitmapInterpolationMode.HighQuality)`
statt `new Bitmap(stream)` - der Codec skaliert direkt beim Dekodieren auf
eine begrenzte Breite herunter, ohne den vollen Originalframe erst in den
Speicher zu laden. Kein zusätzliches Paket, keine separate
Thumbnail-Datei auf der Festplatte, die mit dem Originalbild
synchronisiert werden müsste - reine Laufzeitoptimierung an einer einzigen
Stelle. Der bestehende `_imageCache`-Mechanismus (ein dekodiertes Bitmap
pro Bildpfad, wiederverwendet über alle Kachel-Renderings) bleibt
unverändert.

Der Artikel-Editor (`ProductEditorWindow`, Bildvorschau beim Bearbeiten)
bleibt bewusst unverändert - dort wird ein einzelnes Bild in einem
größeren Vorschaubereich gezeigt, kein wiederholt gerendertes Kachelraster.

## Ergebnis
- 3 neue Prüfungen in `R87ReviewTests.cs` - die erste Prüfung in der
  Sicherheits-Suite, die tatsächlich Avalonias Skia-Renderpfad anstößt
  (`AppBuilder.SetupWithoutStarting()` über `TorPos.App.Program`, keine
  Fenster, keine App-DI): ein 1600×900-Testbild dekodiert auf die
  begrenzte Breite herunter, das Seitenverhältnis bleibt proportional
  erhalten, und ein bereits kleineres Bild wird nicht künstlich
  hochskaliert. Sicherheits-Testsuite: **449/449 PASS** (vorher 446, keine
  Regression).
- `dotnet build -c Release` für `TorPos.App`: 0 Warnung(en), 0 Fehler.

## Einordnung
Reine Rendering-/Performance-Änderung ohne Bezug zu Fiskalisierung, TSE
oder Zahlungsabwicklung. Beide Fiskal-Sperren unverändert `false`.

## Nachtrag (noch vor R88)
`R87ReviewTests.cs` initialisierte testweise Avalonias echte Win32-Plattform
(`AppBuilder.SetupWithoutStarting()`), um `Bitmap.DecodeToWidth` mit einem
echten Bild zu verifizieren. Das lief einmal erfolgreich durch, schlug dann
aber wiederholt mit `InvalidOperationException: The calling thread cannot
access this object because a different thread owns it` fehl - auch nachdem
der komplette Test auf einen dedizierten STA-Thread verlegt wurde. Avalonias
Win32-Dispatcher ist offenbar ein prozessweites Singleton, das in diesem
Konsolen-Testhost nicht zuverlässig neu gebunden werden kann. Der Test wurde
wieder entfernt (`ExpectedSafetyChecks` 449→446), um die gesamte
Sicherheits-Suite nicht instabil zu machen - die eigentliche R87-Änderung
(Debug/Release-Build, Code-Review) bleibt davon unberührt und korrekt.
