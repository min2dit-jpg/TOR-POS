# R19 – Ersteinrichtungs-Assistent

Erster erfolgreicher Login öffnet einmalig einen 6-Schritt-Assistenten:
Firma/Kassenart → Bondrucker → TSE-Prüfung → optional ZVT → Kontrolle → Fertig.

Sicherheitsprinzipien:
- keine automatische TSE-Aktivierung / kein TSE-Setup
- keine Test-Kartenzahlung, nur ZVT-Verbindungsprobe
- Assistent gilt erst nach dem letzten Schritt als abgeschlossen
- Abbruch lässt `installation.first_run_completed=false`; beim nächsten Login erscheint er erneut
- bestehende R18-Einstellungen und Techniker-Schutz bleiben unverändert
