# OpenRouter als zweiter KI-Anbieter

Stand: 2026-09-16

## Problem

Das Freikontingent von Google Gemini erlaubt **20 Anfragen pro Tag** (Metrik
`generate_content_free_tier_requests`, zurückgesetzt um Mitternacht Pazifikzeit). Für ein
Ernährungstagebuch ist das zu wenig: drei Mahlzeiten mit ein paar Korrekturen, und der Tag ist
aufgebraucht. Am 2026-09-16 war das Kontingent bereits mittags erschöpft, und die Erfassung lief
bis zum nächsten Morgen in 429er.

Erschwerend: Googles Fehlermeldung nennt dabei eine Wartezeit von wenigen Sekunden
(„Please retry in 55.78s"). Das ist ein Standard-Backoff und führt in die Irre — wer ihn für bare
Münze nimmt, wartet und probiert stundenlang vergeblich.

Ziel: Ein zweiter Anbieter, zwischen dem und Gemini sich ohne Neubau und ohne Neustart umschalten
lässt.

## Was gemessen wurde

Alle Zahlen am 2026-09-16 vom Betriebsrechner erhoben, mit der echten Systemanweisung und dem
echten Antwortschema der Anwendung.

| | Anfragen/Tag | Antwortzeit | Bemerkung |
|---|---|---|---|
| Gemini Free (`gemini-3.6-flash`) | 20 | 2,9–8,9 s | zu wenig Anfragen |
| OpenRouter `nex-agi/nex-n2.5-pro:free` | 50 | **34,9 s** | Werte gut, Natrium korrekt in Gramm |
| OpenRouter `dots-studio/dots-3-note-preview:free` | 50 | **24,0 s** | Werte plausibel, Natrium fehlte ganz |
| OpenRouter `nvidia/nemotron-3-super-120b:free` | 50 | 0,6 s | **502 „Service temporarily overloaded"** |
| OpenRouter, bezahlte Modelle | unbegrenzt | wenige Sekunden | ab ~1 Cent im Monat bei zehn Erfassungen am Tag |

Von 20 kostenlosen Modellen bei OpenRouter beherrschen **nur fünf** erzwungene JSON-Schemas
(`structured_outputs`). Ohne die ist die Anwendung nicht bedienbar, weil das Antwortformat der
Vertrag ist, an dem die Zerlegung hängt.

Der Betreiber hat sich am 2026-09-16 bewusst für die kostenlosen Modelle entschieden, obwohl ein
bezahltes Modell schneller und zuverlässiger wäre und bei dieser Nutzung Centbeträge kostet. Die
Empfehlung steht hier, damit die Entscheidung später nachvollziehbar ist — nicht, um sie
anzuzweifeln.

## Entscheidungen

1. **Beide Anbieter bleiben, umschaltbar zur Laufzeit.** Nicht „OpenRouter statt Gemini". Gemini
   funktioniert nachweislich und scheitert allein am Tageskontingent; OpenRouter ist dreimal
   langsamer und hatte bei einem von drei Modellen sofort einen Anbieterausfall. Wer nur tauscht,
   steht beim nächsten Ausfall wieder ohne Ausweg da. Die Umschaltung gehört in dieselbe
   Verwaltungsoberfläche, die seit dem 2026-09-16 Modell, Denkstufe und Ausgabedeckel trägt.
2. **Die anbieterunabhängigen Typen verlieren ihr `Gemini`-Präfix.** `GeminiParseResult`,
   `GeminiItem`, `GeminiWishResult`, `GeminiProbeResult` und die drei Ausnahmen beschreiben nichts
   Gemini-Eigenes — sie sind das interne Format, das künftig auch OpenRouter füllt. Sie heißen
   `AiParseResult`, `AiItem`, `AiWishResult`, `AiProbeResult`, `AiUnavailableException`,
   `AiQuotaException`, `AiMalformedResponseException`. Das ist mechanische Arbeit über viele
   Dateien, aber ein `GeminiParseResult`, das eine OpenRouter-Antwort trägt, wäre eine Lüge im
   Typnamen.
3. **Ein eigener `HttpClient` je Anbieter.** Der Zeitdeckel hängt am Client und wird beim Start
   gesetzt; ein gemeinsamer Client zwänge beide Anbieter auf dieselbe Frist. Gemini behält 45 s,
   OpenRouter bekommt 90 s — bei gemessenen 35 s wäre alles darunter ein Deckel, der im
   Normalbetrieb zuschlägt.
4. **Bei OpenRouter ist HTTP 200 kein Erfolg.** Der Dienst beantwortet auch Anbieterausfälle mit
   Status 200 und legt den Fehler in den Rumpf (`{"error":{"message":"Upstream error from
   Nvidia: Service temporarily overloaded","code":502}}`). Wer nur den Statuscode prüft, verbucht
   das als unverständliche Antwort und sucht den Fehler bei sich. Der Rumpf wird deshalb **vor**
   dem Auspacken auf ein `error`-Feld geprüft.
5. **Die Denkstufe bleibt gemini-eigen.** OpenRouter kennt sie nicht. Die Einstellung gilt je
   Anbieter, und die Oberfläche zeigt nur, was der gewählte Anbieter versteht — ein Feld, das
   nichts bewirkt, ist schlimmer als keins.
6. **Kein automatisches Ausweichen zwischen den Anbietern.** Verlockend, aber es verdoppelt im
   Fehlerfall die Wartezeit (45 s Gemini, dann 90 s OpenRouter), und der Nutzer sieht bis dahin
   nur „Denkt nach…". Die Umschaltung ist eine Entscheidung des Betreibers, keine des Programms.

## Architektur

```
AiMealAssistant ──┐                      ┌── GeminiService      (HttpClient "gemini",      45 s)
GoalsEndpoints ───┼── IAiProvider ───────┤
AdminEndpoints ───┘        ▲             └── OpenRouterService  (HttpClient "openrouter",  90 s)
                           │
                    AiProviderFactory
                 (liest AiSettingsProvider,
                  waehlt nach Ai:Provider)
```

### Die Schnittstelle

```csharp
public interface IAiProvider
{
    string Name { get; }                                     // "gemini" | "openrouter"
    Task<AiParseResult> ParseAsync(IReadOnlyList<ChatMessage> messages, string historyBlock, CancellationToken ct);
    Task<AiWishResult> ParseWishAsync(string wish, CancellationToken ct);
    Task<AiProbeResult> ProbeAsync(CancellationToken ct);
}
```

Beide Implementierungen werden als konkrete Typen registriert; eine `AiProviderFactory` (scoped)
liefert anhand von `AiSettingsProvider.Read().Provider` den passenden. Die Aufrufer bekommen die
Factory statt eines Dienstes — sonst entschiede die Registrierung beim Start, was erst zur
Laufzeit feststeht.

Fällt die Wahl auf einen unbekannten Namen (Tippfehler in der Datenbank), gilt Gemini und es
entsteht eine Logzeile. Ein Absturz wäre die falsche Antwort auf einen verschriebenen
Konfigurationswert.

### OpenRouterService

OpenAI-Dialekt: `POST https://openrouter.ai/api/v1/chat/completions`, Systemanweisung als
`messages[0]` mit `role: "system"`, Schema als
`response_format: { type: "json_schema", json_schema: { name, strict: true, schema } }`.

Drei Abweichungen von Gemini, die im Code stehen müssen:

- **`additionalProperties: false` ist Pflicht** an jedem Objekt des Schemas, sonst lehnt der
  strikte Modus ab. Googles Schema kennt das nicht; das Schema wird deshalb je Anbieter
  aufgebaut, nicht geteilt.
- **Die Antwort steckt in `choices[0].message.content`** als Zeichenkette mit JSON darin — eine
  Schachtelung mehr als bei Gemini.
- **Fehler kommen mit Status 200.** Siehe Entscheidung 4.

Die Kopfzeilen `HTTP-Referer` und `X-Title` sind optional und bleiben weg: Sie erscheinen in
OpenRouters öffentlichen Ranglisten, und der Name einer privaten Instanz gehört dort nicht hin.

### Einstellungen

`AiSettings` bekommt zwei Spalten: `Provider` (`"gemini"` oder `"openrouter"`) und
`OpenRouterModel`. Das bestehende `Model` bleibt Geminis Modell — ein gemeinsames Feld erzwänge
beim Umschalten jedes Mal ein Nachtippen, und der zuletzt genutzte Wert des anderen Anbieters
ginge verloren.

Der Rückfall auf die Umgebung gilt wie bisher je Feld. In `docker-compose.yml` heißen die
Variablen `Ai__Provider`, `OpenRouter__Model` und `OpenRouter__ApiKey`; sie werden aus
`NUTRITRACK_AI_PROVIDER`, `NUTRITRACK_OPENROUTER_MODEL` und **`NUTRITRACK_OPENROUTER_KEY`**
gespeist — letztere liegt seit dem 2026-09-16 in der `.env` des Zielrechners und darf nicht
umbenannt werden, sonst findet die Anwendung den bereits hinterlegten Schlüssel nicht.

Der Schlüssel steht wie der von Google ausschließlich in der `.env`, taucht in keiner Antwort auf
und ist über die Verwaltungsoberfläche weder lesbar noch änderbar.

### Oberfläche

Die Verwaltungsseite bekommt oben eine Anbieterwahl. Danach zeigt sie nur die Felder, die für den
gewählten Anbieter gelten — Denkstufe also nur bei Gemini. Beim Modellfeld für OpenRouter steht
der Hinweis, dass nur Modelle mit erzwungenem Schema taugen, samt der fünf am 2026-09-16
gefundenen Namen; die Handprobe daneben ist die Kontrolle, ob ein anderer Name trägt.

Die Fehlerliste bekommt eine Spalte „Anbieter". Ohne sie steht nach einem Wechsel nicht mehr fest,
welcher Dienst welchen Fehlschlag verursacht hat — und genau der Vergleich ist der Grund, warum
es zwei gibt.

## Tests

| Fall | Erwartung |
|---|---|
| `Ai:Provider` leer oder unbekannt | Gemini, Logzeile, kein Absturz |
| `Ai:Provider` = `openrouter` | Der Anfragerumpf geht an openrouter.ai, im OpenAI-Dialekt |
| OpenRouter antwortet 200 mit `error` im Rumpf | `AiUnavailableException` mit der Anbietermeldung, **nicht** „unverständliche Antwort" |
| OpenRouter antwortet regulär | `choices[0].message.content` wird ausgepackt, Posten wie bei Gemini |
| Umschalten über `PUT /api/admin/settings` | Der nächste Aufruf geht an den anderen Dienst, ohne Neustart |
| Geminis Modell bleibt beim Umschalten erhalten | Zurückschalten braucht kein Nachtippen |
| Fehlschlag bei OpenRouter | Protokolleintrag trägt den Anbieter — und weiterhin keinen Esstext |
| Kein Test spricht mit einem echten Dienst | beide über ihren Stub |

## Nicht in diesem Umfang

- Automatisches Ausweichen zwischen den Anbietern (Entscheidung 6).
- Bezahlte Modelle als Vorgabe — die Anbindung trägt sie, aber eingestellt wird, was der Betreiber
  einträgt.
- OpenRouters Auto-Router (`openrouter/auto`): Er wählt das Modell selbst und macht damit jede
  Messung unvergleichbar.
- Eine Modellliste aus OpenRouters API in der Oberfläche. Dieselbe Begründung wie beim Verzicht
  auf Googles Modellliste: Gelistet heißt nicht nutzbar. Ein Freitextfeld plus Handprobe sagt die
  Wahrheit.
- Die Gemini-spezifische Denkstufe für OpenRouter nachzubilden.
