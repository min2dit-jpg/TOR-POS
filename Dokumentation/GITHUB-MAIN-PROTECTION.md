# GitHub main Protection Target

Stand: 2026-09-19

Aktueller GitHub-Metadatenstatus: `main.protected = false`.
Die verbundene GitHub-App darf Repository-Dateien/PRs verwalten, besitzt aber
keinen Administration-Zugriff zum Schreiben der Branch-Protection-/Ruleset-Einstellungen.

## Zielkonfiguration für main

In GitHub unter **Settings → Rules → Rulesets** (oder Branch protection) für `main`:

- Pull Request vor Merge erforderlich
- direkte Pushes auf `main` verhindern
- Force Push verhindern
- Branch-Löschung verhindern
- erfolgreiche Status Checks verlangen:
  - `Windows derleme ve mevcut testler`
  - `Cloud sozdizimi ve testler`
- Conversations vor Merge aufgelöst
- Branch vor Merge auf aktuellen `main`-Stand bringen
- sobald ein zweiter qualifizierter Reviewer verfügbar ist: mindestens 1 Approval verlangen
- für echte Production-Release-Tags/Commits Verified/Signatur verwenden

## Warum Approval nicht durch Software ersetzt wird

Automatische Tests, AI-Code-Review und statische Analysen sind zusätzliche Kontrollen.
Für fiskalisch relevante Production-Freigaben ersetzt das nicht die unabhängige
fachliche Prüfung, die in `RELEASE-QUALIFICATION.md` verlangt wird.
