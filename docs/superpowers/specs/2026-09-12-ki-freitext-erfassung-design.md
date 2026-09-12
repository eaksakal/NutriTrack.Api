# KI-gestützte Freitext-Erfassung von Mahlzeiten

Stand: 2026-09-12

## Problem

Eine Mahlzeit einzutragen kostet heute mehrere Schritte: suchen, Treffer wählen, Menge tippen,
Mahlzeitentyp wählen — und das pro Bestandteil. Wer mittags „zwei Brötchen mit Gouda und einen
Kaffee" gegessen hat, klickt dreimal durch die Suche. Das ist der Grund, warum
Ernährungstagebücher nach zwei Wochen leer bleiben.

Ziel: ein Satz Freitext, und die Posten stehen zur Bestätigung bereit.

## Entscheidungen

Vier Festlegungen, aus denen sich der Rest ergibt:

1. **Nährwerte kommen aus OpenFoodFacts, die KI schätzt nur als Rückfall.** Findet die Suche
   nichts (Selbstgekochtes, Hausmannskost), nutzt der Posten die von der KI geschätzten Werte und
   ist sichtbar als Schätzung markiert. Geraten wird also nur dort, wo es sonst gar keinen Eintrag
   gäbe.
2. **Der Gesprächsfaden lebt im Browser, der Server bleibt zustandslos.** Jede Anfrage trägt den
   bisherigen Verlauf mit sich. Keine Tabelle, keine Sitzungsverwaltung, kein Aufräumen. Preis:
   der Faden ist weg, wenn der Tab zugeht — für eine Eingabe, die unter einer Minute dauert,
   ist das kein Verlust.
3. **Die KI schreibt nicht.** Sie liefert Vorschläge, du bestätigst. Erst die Bestätigung ruft den
   bestehenden Schreibweg.
4. **Freikontingent bei Google, dafür minimaler Prompt.** Googles Bedingungen für die unbezahlte
   Nutzung sind ausdrücklich: „Google uses the content you submit to the Services and any generated
   responses to provide, improve, and develop Google products and services", und „human reviewers
   may read, annotate, and process your API input and output". Deshalb enthält der Prompt
   ausschließlich den Esstext und den Gesprächsverlauf — keine E-Mail, keine Nutzer-ID, keine
   Tagesbilanz, keine Ziele. Google sieht „2 Brötchen mit Gouda", aber nie, zu wem das gehört.

## Architektur

```
Browser  ──POST /api/ai/parse-meal──>  AiEndpoints
                                          │
                                          ▼
                                    AiMealAssistant
                                     │           │
                          GeminiService      OpenFoodFactsService
                                     │           │
                          generativelanguage  world.openfoodfacts.org
                            .googleapis.com

Bestätigung ──POST /api/meals (bestehend, pro Posten)──> Tagebuch
```

Der KI-Pfad legt **keinen** zweiten Schreibweg an. Beim Übernehmen ruft das Frontend für jeden
bestätigten Posten das vorhandene `POST /api/meals` — mit derselben Validierung, derselben
Besitzprüfung und derselben Nährwertrechnung, die die bestehenden Integrationstests absichern.
Ein eigener Schreibpfad für die KI würde über kurz oder lang von diesem abweichen.

### Neue Dateien

| Datei | Zweck |
|---|---|
| `src/NutriTrack.Api/Services/GeminiService.cs` | Reiner HTTP-Client gegen Gemini. Kennt keine Lebensmittel, nur Nachrichten rein, strukturierte Antwort raus. |
| `src/NutriTrack.Api/Services/AiMealAssistant.cs` | Orchestrierung: Gemini rufen, pro Posten OpenFoodFacts suchen, Antwort zusammenbauen. |
| `src/NutriTrack.Api/Services/AiRateLimiter.cs` | Zeitfenster je Nutzer im Speicher. |
| `src/NutriTrack.Api/Endpoints/AiEndpoints.cs` | `POST /api/ai/parse-meal` |
| `src/NutriTrack.Api/Contracts/Ai/*` | Anfrage- und Antwortverträge |
| `tests/NutriTrack.Api.Tests/AiEndpointTests.cs` | Abdeckung siehe unten |
| `tests/NutriTrack.Api.Tests/Infrastructure/StubGeminiHandler.cs` | Gemini im Test, ohne Netz |
| `NutriTrack.Web/src/api/ai.ts` | Client |
| `NutriTrack.Web/src/pages/AiEntryPage.tsx` | Seite `/ai` |

Keine Änderungen an Entities, keine EF-Migration. Die KI-Funktion fügt der Datenbank nichts hinzu.

## Verträge

### Anfrage

```jsonc
POST /api/ai/parse-meal          // RequireAuthorization
{
  "messages": [
    { "role": "user",      "text": "2 Brötchen mit Gouda, dazu ein Kaffee" },
    { "role": "assistant", "text": "Wie groß waren die Brötchen ungefähr?" },
    { "role": "user",      "text": "normale Weizenbrötchen" }
  ]
}
```

`role` ist `user` oder `assistant`. Der Verlauf ist die vollständige Wahrheit; der Server hält
nichts.

### Antwort

```jsonc
{
  "question": null,                  // oder der Rückfragetext; dann ist items leer
  "items": [
    {
      "label": "Weizenbrötchen",
      "quantityInGrams": 120,
      "mealType": "Breakfast",
      "source": "openfoodfacts",     // oder "estimate"
      "candidates": [ /* bis zu 3 FoodSearchResponse, bester zuerst */ ],
      "estimate": {                  // immer gefüllt, benutzt nur wenn candidates leer
        "calories": 265, "protein": 9, "carbohydrates": 49, "fat": 3.2,
        "fiber": 2.7, "sugar": 3.1, "saturatedFat": 0.7, "sodium": 1.2
      }
    }
  ]
}
```

`question` und `items` schließen sich aus: entweder die KI fragt nach, oder sie liefert Posten.

Die Schätzwerte kommen im selben Gemini-Aufruf mit, auch wenn OpenFoodFacts später etwas findet.
Das kostet ein paar Token mehr, spart aber einen zweiten Aufruf für jeden Posten, den die Suche
nicht kennt — und genau diese Posten sind der Grund für das Feature.

Mikronährstoffe schätzt die KI nicht. Wer Vitamin D in Hausmannskost schätzt, produziert Zahlen,
die niemand widerlegen kann. Geschätzte Posten tragen dort `null`.

## Gemini-Anbindung

Endpunkt und Format nach der Doku vom 2026-09-12 (`ai.google.dev/gemini-api/docs/structured-output`):

```
POST https://generativelanguage.googleapis.com/v1beta/interactions
x-goog-api-key: <NUTRITRACK_GEMINI_KEY>

{
  "model": "<NUTRITRACK_GEMINI_MODEL>",
  "input": "<Systemanweisung + Verlauf>",
  "response_format": {
    "type": "text",
    "mime_type": "application/json",
    "schema": { /* Schema der Antwort oben */ }
  }
}
```

Der Header `Api-Revision: 2026-05-20` nagelt die Revision der Interactions-API fest. Ohne ihn
liefert Google die jeweils neueste — und damit möglicherweise einen anderen Antwortumschlag, als
`GeminiService.ExtractPayload` auspackt.

**Antwortumschlag** (der Teil, den die Doku zur Anfrage nicht zeigt): der erzeugte Text steckt in
`steps[]`, im Schritt mit `type == "model_output"`, dort im ersten `content[]`-Eintrag mit einem
`text`-Feld. Vor diesem Schritt können `user_input`- und Werkzeug-Schritte stehen, deshalb wird
`steps` von hinten durchsucht. Zusätzlich weisen die SDKs das Bequemfeld `output_text` auf der
Wurzel aus; `ExtractPayload` nimmt es, wenn es da ist.

```
{
  "id": "v1_...",
  "model": "<NUTRITRACK_GEMINI_MODEL>",
  "status": "completed",
  "steps": [
    { "type": "user_input",   "content": [ { "type": "text", "text": "..." } ] },
    { "type": "model_output", "content": [ { "type": "text", "text": "<das JSON oben>" } ] }
  ],
  "usage": { "total_input_tokens": 7, "total_output_tokens": 20 }
}
```

Die Handprobe dazu steht in `scripts/gemini-probe.sh`; sie ist der einzige Weg, den Umschlag mit
einem echten Schlüssel gegen den laufenden Dienst zu prüfen.

Das erzwungene Schema ist der Grund, warum kein Freitext-Parser nötig ist.

**Der Modellname ist konfigurierbar** (`NUTRITRACK_GEMINI_MODEL`, Vorgabe `gemini-3.5-flash`).
Googles Modellseite sagt nicht, welche Modelle im Freikontingent liegen; ein fest verdrahteter Name
wäre eine Behauptung über eine Tarifgrenze, die wir nicht kennen. Passt das Modell nicht zum
Schlüssel, ändert eine Zeile in der `.env` das, ohne neu zu bauen.

### Systemanweisung

Sinngemäß, die endgültige Formulierung entsteht beim Bauen:

> Du zerlegst deutschsprachige Beschreibungen von Mahlzeiten in einzelne Posten. Für jeden Posten:
> ein kurzer Suchbegriff für eine Lebensmitteldatenbank, die Menge in Gramm, der Mahlzeitentyp und
> geschätzte Nährwerte je 100 g. Rechne Haushaltsmaße in Gramm um (eine Scheibe Käse ≈ 30 g, eine
> Tasse Kaffee ≈ 200 ml). Fehlt eine Angabe, die den Nährwert deutlich verändert, stelle **eine**
> kurze Rückfrage statt zu raten. Bei Kleinigkeiten nimm den üblichen Wert an, statt nachzufragen.

Die Grenze zwischen „nachfragen" und „annehmen" ist der Punkt, an dem sich das Feature im Alltag
entscheidet. Zu viele Rückfragen sind lästiger als die bestehende Suche.

## Fehlerverhalten

| Fall | Antwort |
|---|---|
| Gemini antwortet nicht in 15 s | 503, „KI gerade nicht erreichbar" |
| Google meldet 429 | 429, „Kontingent erschöpft, später erneut" |
| Kaputtes JSON trotz Schema | ein Wiederholungsversuch, dann 502 |
| `NUTRITRACK_GEMINI_KEY` fehlt | 503 mit klarem Text — **die App startet trotzdem** |
| OpenFoodFacts fällt aus | Posten wird `source: "estimate"`, kein Fehler |
| Mehr als 30 Aufrufe je Stunde und Nutzer | 429 |

Der fehlende Schlüssel ist bewusst anders behandelt als der fehlende JWT-Schlüssel, bei dem die
Anwendung den Start verweigert. Ein fehlendes Zusatzfeature darf das Tagebuch nicht lahmlegen.

### Grenzen

2000 Zeichen je Nachricht, 10 Nachrichten Verlauf, 30 Aufrufe je Stunde und Nutzer (gleitendes
Zeitfenster im Speicher, keine Tabelle). Überschreitung der Eingabegrenzen ist 400, die
Aufrufgrenze ist 429.

Die Postengrenze ist ein Sonderfall: liefert das Modell mehr als 20 Posten, ist das sein Fehler
und nicht der des Nutzers. Ein 400er wäre hier die falsche Antwort — die Liste wird auf 20 gekappt,
der Schnitt protokolliert und in der Maske sichtbar benannt („nur die ersten 20 Posten
übernommen"). Still abzuschneiden wäre schlimmer als abzulehnen.

### Mitgenommen: OpenFoodFactsService härten

`OpenFoodFactsService` hat heute kein try/catch. Ein Ausfall oder ein Überschreiten des Timeouts
fliegt unbehandelt hoch und erreicht den Nutzer als 500 mit leerem Body — beim ersten Testlauf
der frisch ausgelieferten Anwendung am 2026-09-12 genau so passiert. Der Dienst bekommt try/catch,
gibt bei Ausfall eine leere Liste plus Logeintrag zurück, und die Suchendpunkte antworten mit 503
statt 500. Das gehört hierher, weil der KI-Pfad auf demselben Dienst aufsetzt und seine
Rückfall-Logik davon abhängt, dass „nichts gefunden" von „kaputt" unterscheidbar bleibt.

## Oberfläche

Neue Seite `/ai`, in der Navigation neben „Dashboard" und „Suche", geschützt über `ProtectedRoute`
wie die übrigen Seiten.

Oben der Gesprächsverlauf, unten das Eingabefeld. Kommt eine Rückfrage, bleibt das Feld offen und
die Antwort geht in denselben Faden. Kommen Posten, erscheint die Bestätigungsmaske:

```
Erkannt aus: "2 Brötchen mit Gouda, dazu Kaffee"

  [x] Weizenbrötchen     120 g   322 kcal   [Treffer wählen ▾]
  [x] Gouda 48%           30 g   107 kcal   [Treffer wählen ▾]
  [x] Kaffee schwarz     200 g     4 kcal   geschätzt
  ─────────────────────────────────────────────────────
  Summe                          433 kcal

        [ Übernehmen ]   [ Verwerfen ]
```

Haken je Zeile, Menge editierbar, Mahlzeitentyp als Auswahl, bei mehreren OpenFoodFacts-Treffern
ein Dropdown mit den Alternativen. Geschätzte Posten sind sichtbar markiert. „Übernehmen" legt die
Einträge nacheinander an und springt aufs Dashboard; schlägt einer fehl, bleiben die übrigen
stehen und die Maske nennt den fehlgeschlagenen.

Keine neue Abhängigkeit, keine neue Designsprache — dieselben CSS-Klassen und derselbe deutsche,
geduzte Ton wie im Rest der Oberfläche. Fehlermeldungen über den vorhandenen Helfer
`apiErrorMessage`.

## Tests

`StubGeminiHandler` nach dem Muster des vorhandenen `StubOpenFoodFactsHandler`; kein Test geht ins
Netz. Abgedeckt:

- Parse-Pfad: Freitext rein, Posten raus, Mengen und Mahlzeitentyp korrekt übernommen
- Rückfrage-Pfad: `question` gesetzt, `items` leer; die Folgeanfrage mit Verlauf liefert Posten
- OpenFoodFacts-Treffer schlägt Schätzung: `source == "openfoodfacts"`, Kandidaten absteigend
- Leere Suche fällt auf Schätzung zurück: `source == "estimate"`
- Fehlender Schlüssel: 503, und die App läuft weiter
- Timeout, 429 von Google, kaputtes JSON: 503 / 429 / 502
- Alle Grenzen: zu langer Text, zu langer Verlauf, zu viele Posten, Aufrufgrenze
- Ohne Token: 401
- OpenFoodFactsService: Ausfall ergibt leere Liste statt Exception

**Keine Frontend-Tests.** Das Web-Repo hat kein Testframework; eines einzuführen ist ein eigenes
Vorhaben und nicht Teil hiervon. Die neue Seite ist durch `tsc`, Build und Handprobe abgesichert —
das ist eine bewusste Lücke, keine übersehene.

## Bewusst nicht dabei

Zwischenspeichern von KI-Antworten, Nutzungsstatistik, Kostenüberwachung, Bilderkennung von
Mahlzeiten, Sprachnachrichten, Lernen aus früheren Korrekturen. Bei einem Nutzer und einem
Freikontingent ist das Ballast; jedes dieser Themen ist für sich ein eigenes Vorhaben.

## Betrieb

Neuer Wert in der `.env` auf dem Server, neben dem JWT-Schlüssel:

```
NUTRITRACK_GEMINI_KEY=<Schlüssel aus Google AI Studio>
NUTRITRACK_GEMINI_MODEL=gemini-3.5-flash     # optional
```

`docker-compose.yml` reicht beides als `Gemini__ApiKey` und `Gemini__Model` durch. Fehlt der
Schlüssel, startet die Anwendung normal und nur `/api/ai/*` meldet 503.
