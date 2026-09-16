# Einstellungsseite für die KI-Anbindung

Stand: 2026-09-16

## Problem

Am 2026-09-15 stand die KI-Erfassung zweimal still, und beide Male kostete die Diagnose Stunden
statt Minuten:

1. Das konfigurierte Modell `gemini-3.5-flash` war über `/v1beta/interactions` tot — es antwortete
   überhaupt nicht mehr. Von innen sah das wie ein zu knapper Zeitdeckel aus und überstand zwei
   Erhöhungen (15 → 25 → 45 s), bis jemand die Antwortzeit wirklich maß.
2. Das Nachfolgemodell entgleiste in eine Ziffernschleife (`estimate.sugar` mit über 9000 Stellen).

Beide Male lief die Diagnose über SSH auf den Betriebsrechner: `.env` lesen, `curl` von Hand
bauen, Modelle durchprobieren, `docker compose logs` durchsuchen. Und beide Male hieß Umstellen:
Datei ändern, Container neu starten — im schlechteren Fall neu bauen.

Das ist die falsche Werkzeugkiste für einen Wert, dessen richtige Einstellung an Googles Laune
hängt und nicht am eigenen Code. Wer ein Modell wechseln will, soll das tun können, ohne sich an
einem fremden Rechner anzumelden.

Ziel: Modell, Denkstufe und Ausgabedeckel in der Oberfläche ändern, mit einem Knopf gegen den
echten Dienst prüfen, und die letzten Fehlschläge sehen, ohne Logs zu lesen.

## Entscheidungen

1. **Genau ein Administrator, benannt über `Admin:Email` in der Konfiguration.** Kein
   Rollensystem. Identity-Rollen sind zwar registriert, aber nirgends benutzt; sie zu aktivieren
   hieße Rollenvergabe, Seeding beim ersten Start und die Frage, wer der erste Admin wird — viel
   Maschinerie für einen Betreiber. Der Vergleich läuft gegen den `ClaimTypes.Email` des Tokens,
   der schon heute mitreist (`TokenService.cs:37`). Ist `Admin:Email` nicht gesetzt, ist die
   Verwaltung für **niemanden** erreichbar: ein vergessener Eintrag darf die Seite nicht für alle
   öffnen.
2. **Fremde Konten bekommen 404, nicht 403.** Wie bei fremden Mahlzeiten (`MealEndpoints.cs:272`).
   Ein 403 bestätigt die Existenz der Verwaltung; ein 404 sagt nichts. Das Frontend blendet das
   Menü anhand desselben 404 aus — es braucht kein zweites Merkmal dafür.
3. **Die Datenbank schlägt die Umgebung, die Umgebung bleibt die Notbremse.** Gespeicherte Werte
   gewinnen; fehlt einer, gilt weiter `Gemini__Model` und Verwandte aus der `.env`. Damit bleibt
   eine über die Oberfläche verstellte Instanz reparierbar, ohne in der Datenbank zu hantieren:
   Eintrag löschen, fertig.
4. **Das Fehlerprotokoll speichert KEINEN Esstext.** Zeitpunkt, Fehlerart, Modell, Denkstufe,
   Dauer, HTTP-Status und ein kurzer Grund — mehr nicht. Der Bestand hält diese Linie bereits
   bewusst (`GeminiService.DescribeRoot`: „im Fehlerfall landet das im Log, und der Text der
   Mahlzeit gehoert dort nicht hinein"). Ein Protokoll, das Mahlzeiten mitschreibt, wäre ein
   Rückschritt hinter das, was `.env.example` dem Betreiber zusichert.
5. **Der API-Schlüssel bleibt draußen.** Er steht in der `.env` und gehört nicht in eine
   Web-Oberfläche — weder zum Lesen noch zum Ändern. Ein Feld, das ein Geheimnis anzeigt, ist ein
   Geheimnis weniger.
6. **Der Zeitdeckel bleibt Umgebungsvariable.** `Gemini:TimeoutSeconds` wirkt beim Aufbau des
   `HttpClient` und damit nur beim Start; ihn zur Laufzeit verstellbar zu machen, hieße den
   Client-Aufbau umbauen. Der Nutzen steht dazu in keinem Verhältnis: er war bei beiden Vorfällen
   nie die richtige Stellschraube.

## Architektur

```
Browser ──GET/PUT /api/admin/settings──┐
        ──POST   /api/admin/settings/probe──┤
        ──GET    /api/admin/failures────────┤
                                            ▼
                                     AdminEndpoints
                                   (Admin:Email-Pruefung,
                                    sonst 404)
                                            │
                        ┌───────────────────┴───────────────────┐
                        ▼                                       ▼
                 AiSettingsProvider                       AppDbContext
             (Singleton, Cache, Fallback                (AiSettings: eine Zeile,
              auf IConfiguration)                        AiFailures: Ringpuffer 50)
                        │
                        ▼
                  GeminiService  ── liest Modell, Denkstufe, Ausgabedeckel
                                    NICHT mehr direkt aus IConfiguration
```

### AiSettingsProvider

Ein Singleton mit drei Werten und einem Cache. `GeminiService` fragt ihn statt
`configuration["Gemini:Model"]`; jeder Wert, den die Datenbank nicht hat, kommt weiter aus
`IConfiguration`.

Der Cache ist nötig, weil `GeminiService` pro Anfrage gebaut wird und ein Datenbankzugriff je
KI-Aufruf reine Verschwendung wäre. Er wird beim Speichern verworfen, nicht nach Zeit: es gibt
genau einen Schreiber, und der sitzt im selben Prozess.

Gelesen wird über einen eigenen `IServiceScopeFactory`-Scope, weil das Singleton den scoped
`AppDbContext` nicht halten darf. Schlägt der Lesezugriff fehl, gelten die Werte aus
`IConfiguration` und es entsteht eine Logzeile — dieselbe Haltung wie bei `LoadHistoryAsync`
(`AiMealAssistant.cs`): eine kaputte Nebensache darf die Haupterfassung nicht mitreißen.

### Endpunkte

| Endpunkt | Zweck |
|---|---|
| `GET /api/admin/settings` | Aktuelle Werte samt Herkunft je Feld („aus der Datenbank" / „aus der Umgebung"). Die Herkunft ist der Grund, warum man nach einem Fehlgriff nicht rätselt, warum ein Wert nicht wirkt. |
| `PUT /api/admin/settings` | Speichert Modell, Denkstufe, Ausgabedeckel. Leeres Feld heißt „zurück zur Umgebungsvariable", nicht „leerer Wert". |
| `POST /api/admin/settings/probe` | EIN echter Aufruf mit den gespeicherten Werten und einer festen Beispieleingabe. Liefert Dauer in Millisekunden, HTTP-Status und die auf 2000 Zeichen gekürzte Rohantwort. |
| `GET /api/admin/failures` | Die letzten 50 Fehlschläge, jüngster zuerst. |

Die Probe nutzt eine **feste** Beispieleingabe („zwei Broetchen mit Gouda"), keine des Nutzers:
so ist das Ergebnis zwischen zwei Läufen vergleichbar, und die Probe kann nicht versehentlich zum
Weg werden, auf dem Mahlzeitentexte zusätzlich an Google gehen.

Die Probe zählt gegen dasselbe Minutenkontingent wie die normale Erfassung (20 Anfragen). Sie
unterliegt deshalb demselben `AiRateLimiter` wie `/api/ai/parse-meal` — ein Admin, der den Knopf
zwanzigmal drückt, legt sonst seine eigene Erfassung lahm. Genau das ist am 2026-09-15 passiert,
als die Diagnose das Kontingent aufbrauchte.

### Fehlerprotokoll

`AiMealAssistant` und `GeminiService` schreiben an den Stellen, die heute schon `LogWarning`
rufen. Ein Eintrag trägt: Zeitpunkt (UTC), Art (`Timeout`, `Schema`, `Quota`, `Unavailable`),
Modell, Denkstufe, Dauer in Millisekunden, HTTP-Status (sofern vorhanden) und einen Kurzgrund aus
höchstens 200 Zeichen.

Der Kurzgrund ist der Ausnahmetext, nicht der Prompt. Bei einem Schemafehler ist das etwa
`The JSON value could not be converted ... Path: $.items[0].estimate.sugar` — der Pfad, nicht der
Inhalt. Diese Grenze ist der Kern von Entscheidung 4 und gehört als Test abgesichert.

Beim Schreiben löscht derselbe Vorgang alles jenseits der jüngsten 50. Kein Hintergrundjob, keine
Aufbewahrungsfrist in der Konfiguration: eine Tabelle, die nie wächst, braucht keine Pflege.

### Frontend

Neue Seite `AdminPage.tsx` unter `/admin`: ein Formular mit drei Feldern, ein Speichern-Knopf, ein
Test-Knopf mit Ergebnisanzeige (Dauer, Status, Rohantwort in einem `<pre>`), darunter die
Fehlerliste als Tabelle.

Der Menüpunkt erscheint nur, wenn `GET /api/admin/settings` beim Laden nicht 404 liefert. Kein
zusätzliches Merkmal im Token, keine zweite Wahrheit darüber, wer Admin ist.

Jedes Feld zeigt neben sich, woher sein Wert stammt. Ein leeres Feld ist nicht „kein Wert",
sondern „nimm die Umgebungsvariable" — das steht als Hinweis daneben, weil es sonst niemand errät.

## Tests

| Fall | Erwartung |
|---|---|
| Nicht-Admin ruft einen der vier Endpunkte | 404 auf allen vieren |
| `Admin:Email` nicht gesetzt | 404 auch für den, der sonst Admin wäre |
| Gespeicherter Modellwert | landet im Gemini-Anfragerumpf statt des Werts aus der Umgebung |
| Feld leer gespeichert | Rückfall auf die Umgebungsvariable, nicht leerer Modellname |
| Datenbank beim Lesen kaputt | Werte aus der Umgebung, Logzeile, KI-Erfassung läuft weiter |
| Fehlschlag tritt auf | Protokolleintrag mit Art und Modell — und **ohne** den Esstext |
| 51. Fehlschlag | Tabelle enthält 50 Einträge, der älteste ist fort |
| Probe | ruft Gemini genau einmal, liefert Dauer und Status, zählt gegen den Rate-Limiter |

Der Test „ohne den Esstext" ist der wichtigste: er schreibt eine Mahlzeit mit einem
wiedererkennbaren Wort, provoziert einen Fehlschlag und prüft, dass dieses Wort in keiner Spalte
des Protokolls vorkommt.

## Nicht in diesem Umfang

- Rollen, Nutzerverwaltung, mehrere Administratoren.
- Der API-Schlüssel — weder anzeigen noch ändern.
- `Gemini:TimeoutSeconds` zur Laufzeit (siehe Entscheidung 6).
- Eine Modellauswahl aus `GET /v1beta/models`. Verlockend, aber die Liste ist kein Versprechen:
  `gemini-3.5-flash` stand am 2026-09-15 darin und antwortete trotzdem nicht. Ein Freitextfeld
  plus Probe-Knopf sagt die Wahrheit, eine Auswahlliste gaukelt sie vor.
- Auswertungen über das Fehlerprotokoll (Häufigkeiten, Diagramme).
