# Verlaufsbezug in der KI-Erfassung

Stand: 2026-09-15

## Problem

„Das halbe Eis von gestern habe ich jetzt fertig gegessen." Heute zerlegt die KI diesen Satz wie
jede andere Eingabe: sie schätzt ein Eis, sucht gegebenenfalls in OpenFoodFacts und liefert Werte,
die mit denen von gestern nichts zu tun haben. Derselbe Becher steht dann mit zwei verschiedenen
Nährwerten im Tagebuch.

Dabei ist der Fall der häufigste überhaupt: Gegessen wird, was schon einmal gegessen wurde — der
Rest von gestern, derselbe Kaffee, dasselbe Frühstück. `POST /api/meals/{id}/repeat` löst das
bereits, aber nur, wenn der Nutzer den Eintrag selbst heraussucht. Im Freitext ist der Bezug
unerreichbar, weil das Modell den Verlauf nicht kennt.

Ziel: Sätze, die sich auf früher Gegessenes beziehen, landen mit den Nährwerten von damals im
Tagebuch — ohne dass der Nutzer die Liste durchsucht.

## Entscheidungen

1. **Der Verlauf der letzten drei Tage geht an Google.** Das ist eine bewusste Abkehr von
   Entscheidung 4 des Ursprungs-Designs vom 2026-09-12 („ausschließlich den Esstext … Google sieht
   ‚2 Brötchen mit Gouda', aber nie, zu wem das gehört"). Was Google künftig sieht, ist ein
   Essverlauf ohne Kennung: Labels, Mengen, Mahlzeit, Uhrzeit. Weiterhin **nicht** übertragen
   werden Nutzer-ID, E-Mail, Tagesbilanz, Ziele und Gewicht. Der Verlauf bleibt damit anonym, ist
   aber deutlich mehr als eine einzelne Essensbeschreibung, und im Freikontingent lesen laut
   Googles Bedingungen menschliche Prüfer mit. Diese Abwägung hat der Betreiber dieser Instanz am
   2026-09-15 ausdrücklich getroffen; sie gehört in `.env.example`, wo die alte Zusage steht.
2. **Das Modell entscheidet, WELCHER Eintrag gemeint ist — nicht, welche Werte er hat.** Gemini
   gibt eine Kennung zurück (`v1`, `v2`, …), nie abgeschriebene Nährwerte. Die Zahlen kommen aus der
   eigenen Datenbank. Ein Modell, das 412 kcal als 421 zurückgibt, erzeugt sonst über
   `HasSameNutrients` einen zweiten FoodItem mit minimal abweichenden Werten — genau die Dublette,
   die der repeat-Endpunkt (`18a8e00`) bewusst vermeidet.
3. **Ein Aufruf, nicht zwei.** Der Verlauf geht unaufgefordert mit, statt dass das Modell ihn per
   Werkzeugaufruf anfordert. Eine zweite Rundreise zu Google verdoppelt die Wartezeit auf einem
   Weg, der schon zweimal am Zeitdeckel gescheitert ist (15 s → 25 s → 45 s, `61ab88a`).
4. **Drei Tage, vollständig.** Heute, gestern, vorgestern mit jedem Eintrag. Das deckt „von
   gestern" und „heute früh" präzise ab. Eine Woche wären bei acht Einträgen am Tag rund 56 Zeilen
   im Prompt — mehr Token, mehr Wartezeit, und bei langen Listen ähnlicher Einträge verwechselt
   das Modell eher, welcher gemeint war. Bezüge auf „letzten Samstag" sind damit ausdrücklich
   nicht abgedeckt.
5. **Der geschriebene Eintrag geht durch `repeat`, nicht durch einen neuen Schreibweg.** Wie im
   Ursprungs-Design legt der KI-Pfad keinen zweiten Weg ins Tagebuch an.

## Architektur

```
Browser ──POST /api/ai/parse-meal──> AiEndpoints ──> AiMealAssistant
                                                        │
                                    ┌───────────────────┼───────────────────┐
                                    ▼                   ▼                   ▼
                              AppDbContext        GeminiService     OpenFoodFactsService
                          (Verlauf 3 Tage,      (Verlauf im Prompt,   (nur für Posten
                           Kennungen v1..vN)        sourceRef zurück)     OHNE sourceRef)

Bestätigung ─ Posten mit source="history" ──> POST /api/meals/{sourceEntryId}/repeat
            └ alle übrigen Posten ─────────> POST /api/meals            (beide bestehend)
```

### Der Verlaufsblock

`AiMealAssistant` lädt vor dem Gemini-Aufruf die Einträge des Nutzers von heute, gestern und
vorgestern (`Include(FoodItem)`, absteigend nach Datum und Uhrzeit) und baut daraus:

```
Bisher gegessen:
[v1] heute 08:10 Breakfast - Haferflocken (80 g)
[v2] gestern 21:30 Snack - Eis, Vanille (100 g)
[v3] gestern 12:15 Lunch - Spaghetti (gekocht) (250 g)
```

Relative Tagesnamen statt Datumsangaben: der Nutzer sagt „gestern", nicht „am 14.09.". Die
Umrechnung passiert hier, wo die Zeitzone des Servers gilt, und nicht im Modell.

Parallel entsteht ein `Dictionary<string, MealEntry>` von Kennung auf Eintrag. Es lebt nur für die
Dauer der Anfrage und enthält ausschließlich Einträge dieses Nutzers — eine vom Modell erfundene
Kennung kann deshalb nie auf fremde Daten zeigen.

Ist der Verlauf leer, entfällt der Block ersatzlos und der Prompt ist Zeichen für Zeichen der
heutige. Der Fall ist nicht exotisch: er gilt für jeden neuen Nutzer.

### Schema und Systemanweisung

`GeminiItem` bekommt ein optionales Feld `sourceRef`. Die Systemanweisung wird ergänzt:

> Bezieht sich der Nutzer auf etwas, das im Abschnitt „Bisher gegessen" steht („das Eis von
> gestern", „nochmal das Frühstück", „den Rest davon"), setze `sourceRef` auf die Kennung der Zeile
> und lasse `estimate` leer — die Nährwerte sind bereits bekannt. `quantityInGrams` gilt
> weiterhin: „die andere Hälfte" und „nochmal dasselbe" meinen die Menge von damals, „die Hälfte
> davon" die halbe. Ohne erkennbaren Bezug lässt du `sourceRef` leer und verfährst wie bisher.

### Auflösung im Assistenten

Für jeden Posten mit gesetztem `sourceRef`:

- **Kennung bekannt** → `ParsedItem` mit `Source = "history"`, `SourceEntryId` der Eintrags-Id,
  Label und Nährwerten des Original-FoodItems. Kein OpenFoodFacts-Aufruf für diesen Posten; er
  belastet damit weder das Suchbudget von 8 s noch das Minutenkontingent des Fremddienstes.
- **Kennung unbekannt** (halluziniert) → der Posten fällt still auf den heutigen Weg zurück:
  `searchTerm` und `estimate` wie bei jedem anderen. Eine Fehlermeldung wäre hier falsch, denn der
  Nutzer hat nichts falsch gemacht und bekommt einen brauchbaren Vorschlag. Eine Logzeile schon:
  häufen sich erfundene Kennungen, stimmt etwas mit dem Prompt nicht.
- **Kennung bekannt, aber `estimate` trotzdem gefüllt** → `estimate` wird verworfen. Die Datenbank
  gewinnt gegen das Modell.

### Frontend

`ai.ts`: `source` bekommt den vierten Wert `'history'`, `ParsedItem` das Feld `sourceEntryId`.
Beim Übernehmen ruft die Bestätigungsliste für solche Posten `meals.repeat(sourceEntryId, …)`
statt `meals.create(…)`; alle übrigen Posten laufen unverändert.

In der Vorschau trägt ein Posten aus dem Verlauf einen sichtbaren Hinweis („aus deinem Verlauf:
gestern 21:30"). Der Bezug ist die eine Stelle, an der das Modell etwas entscheidet, das der
Nutzer nicht getippt hat — er muss vor der Bestätigung sehen können, worauf es sich bezogen hat.

## Tests

| Fall | Erwartung |
|---|---|
| Stub liefert bekanntes `sourceRef` | Posten trägt `source="history"`, Nährwerte des Originals, `sourceEntryId` gesetzt |
| … und wird bestätigt | Kein neuer FoodItem in der Datenbank, dieselbe `FoodItemId` wie das Original |
| `sourceRef` eines FREMDEN Nutzers | Nicht im Wörterbuch, also Rückfall auf Schätzung — kein fremder Eintrag, kein Datenabfluss |
| Erfundenes `sourceRef` („v99") | Rückfall auf Schätzung, Logzeile, kein Fehler nach außen |
| `sourceRef` UND `estimate` gesetzt | Werte des Originals gewinnen |
| Leerer Verlauf | Prompt enthält keinen Verlaufsblock; bestehendes Verhalten unverändert |
| Verlauf vorhanden | Prompt enthält Labels, Mengen und relative Tage — und weiterhin keine Nutzer-ID |

Der letzte Fall ist der Datenschutztest: er hält fest, was tatsächlich hinausgeht, und schlägt an,
wenn jemand später Bilanz oder Ziele in den Prompt legt.

## Nicht in diesem Umfang

- Bezüge über drei Tage hinaus („letzten Samstag").
- Gruppenbezüge wie „mein übliches Frühstück" als ein Posten.
- Änderungen an `GET /api/meals/recent` oder an der bestehenden Wiederholen-Schaltfläche.
- Ein Rückbau der Datenschutzentscheidung auf eine lokale Auflösung ohne Google. Er wurde erwogen
  (Modell meldet nur „Bezug auf früher" plus Suchbegriff, die API sucht selbst) und verworfen: er
  verlagert die Zuordnung in eine unscharfe Textsuche, die genau bei den Fällen versagt, um die es
  geht.
