# OpenRouter als zweiter KI-Anbieter — Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Modell **und Anbieter** der KI-Erfassung sind zur Laufzeit umschaltbar — Gemini oder OpenRouter, ohne Neubau und ohne Neustart.

**Architecture:** Eine Schnittstelle `IAiProvider` mit den drei Aufrufen, die die Anwendung braucht (`ParseAsync`, `ParseWishAsync`, `ProbeAsync`). `GeminiService` wird eine Implementierung, `OpenRouterService` die zweite; beide bekommen einen eigenen `HttpClient` mit eigenem Zeitdeckel. Eine `AiProviderFactory` wählt anhand der gespeicherten Einstellung. Die anbieterunabhängigen Typen verlieren dabei ihr `Gemini`-Präfix, weil sie künftig beide Anbieter tragen.

**Tech Stack:** .NET 10 Minimal API, EF Core + SQLite, xUnit mit `WebApplicationFactory`, React 19 + TypeScript + axios.

**Spec:** `docs/superpowers/specs/2026-09-16-openrouter-als-zweiter-anbieter-design.md`

## Global Constraints

- **Quelldateien ohne BOM.**
- **Kommentare und Log-Texte ohne Umlaute** (`ue`, `ae`, `oe`, `ss`). Nur Zeichenketten, die ein Nutzer auf dem Bildschirm sieht, tragen echte Umlaute.
- **Kommentare begründen, sie beschreiben nicht.**
- **Kein Test spricht mit einem echten Dienst** — weder Google noch OpenRouter. Beide laufen über ihren Stub.
- **Der OpenRouter-Schlüssel liegt bereits als `NUTRITRACK_OPENROUTER_KEY` in der `.env` des Zielrechners.** Diesen Namen nicht ändern, sonst findet die Anwendung den hinterlegten Schlüssel nicht. Er wird in `docker-compose.yml` auf `OpenRouter__ApiKey` abgebildet.
- **Kein Schlüssel — weder Googles noch OpenRouters — erscheint in einer Antwort, einem Log oder dem Fehlerprotokoll.**
- **Das Fehlerprotokoll speichert weiterhin keinen Esstext.**
- **Testlauf:** `dotnet test --nologo` im Verzeichnis `NutriTrack.Api` (aktuell 231 Tests, alle grün). **Frontend-Bau:** `npm run build` in `NutriTrack.Web`. Ein Frontend-Testframework gibt es nicht.
- **Commit-Stil:** englischer Titel in der Befehlsform, deutscher Rumpf, der das Warum erklärt. Jeder Commit endet mit diesen zwei Zeilen, unverändert, auch wenn ein anderes Modell die Arbeit macht:
  ```
  Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
  ```

---

### Task 1: Die geteilten Typen verlieren ihr Gemini-Präfix

Rein mechanisch, aber die Grundlage für alles Weitere: Solange die Ergebnistypen `Gemini…` heißen, wäre jede OpenRouter-Antwort darin eine Lüge im Typnamen.

**Files:**
- Modify: `src/NutriTrack.Api/Services/GeminiService.cs`, `src/NutriTrack.Api/Services/AiMealAssistant.cs`, `src/NutriTrack.Api/Endpoints/AiEndpoints.cs`, `src/NutriTrack.Api/Endpoints/GoalsEndpoints.cs`, `src/NutriTrack.Api/Endpoints/AdminEndpoints.cs`, `src/NutriTrack.Api/Endpoints/AiFailureResponse.cs`, `src/NutriTrack.Api/Endpoints/AiQuotaResponse.cs`
- Modify: `tests/NutriTrack.Api.Tests/GeminiServiceTests.cs`, `tests/NutriTrack.Api.Tests/AiFailureLogTests.cs`, `tests/NutriTrack.Api.Tests/AdminEndpointTests.cs`

**Interfaces:**
- Consumes: nichts.
- Produces: die umbenannten Typen `AiParseResult`, `AiItem`, `AiWishResult`, `AiProbeResult`, `AiUnavailableException`, `AiQuotaException`, `AiMalformedResponseException`, `AiQuotaScope`.

- [ ] **Step 1: Umbenennen**

Genau diese acht Namen, jeweils als ganzes Wort. `GeminiService` selbst wird **nicht** umbenannt — die Klasse bleibt gemini-spezifisch:

| alt | neu |
|---|---|
| `GeminiParseResult` | `AiParseResult` |
| `GeminiItem` | `AiItem` |
| `GeminiWishResult` | `AiWishResult` |
| `GeminiProbeResult` | `AiProbeResult` |
| `GeminiUnavailableException` | `AiUnavailableException` |
| `GeminiQuotaException` | `AiQuotaException` |
| `GeminiMalformedResponseException` | `AiMalformedResponseException` |
| `GeminiQuotaScope` | `AiQuotaScope` |

```bash
cd /c/Projects/NutriTrack/NutriTrack.Api
for f in $(grep -rl "GeminiParseResult\|GeminiItem\|GeminiWishResult\|GeminiProbeResult\|GeminiUnavailableException\|GeminiQuotaException\|GeminiMalformedResponseException\|GeminiQuotaScope" --include='*.cs' src tests | grep -v obj); do
  sed -i \
    -e 's/\bGeminiParseResult\b/AiParseResult/g' \
    -e 's/\bGeminiItem\b/AiItem/g' \
    -e 's/\bGeminiWishResult\b/AiWishResult/g' \
    -e 's/\bGeminiProbeResult\b/AiProbeResult/g' \
    -e 's/\bGeminiUnavailableException\b/AiUnavailableException/g' \
    -e 's/\bGeminiQuotaException\b/AiQuotaException/g' \
    -e 's/\bGeminiMalformedResponseException\b/AiMalformedResponseException/g' \
    -e 's/\bGeminiQuotaScope\b/AiQuotaScope/g' \
    "$f"
done
```

Danach die Doc-Kommentare durchgehen, die noch von „Gemini" sprechen, wo jetzt „der Anbieter" gemeint ist — etwa an `AiUnavailableException` („Google ist erreichbar, aber nicht nutzbar"). Sätze, die **wirklich** Google meinen (die gemessenen Modellbefunde in `GeminiService`), bleiben unverändert.

- [ ] **Step 2: Übersetzen und Tests laufen**

Run: `dotnet build -v q --nologo` dann `dotnet test --nologo`
Expected: 231/231 grün. Eine Umbenennung darf kein Verhalten ändern; ändert sich eine Testzahl, ist etwas schiefgegangen.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -F - <<'MSG'
Drop the Gemini prefix from the types both providers will fill

AiParseResult, AiItem, AiWishResult, AiProbeResult und die drei Ausnahmen
beschreiben nichts Gemini-Eigenes - sie sind das interne Format der Anwendung.
Sobald ein zweiter Anbieter sie fuellt, waere das Praefix eine Luege im
Typnamen.

GeminiService selbst behaelt seinen Namen: die Klasse spricht mit Googles
Interactions-API und mit nichts sonst.

Reine Umbenennung, kein Verhalten geaendert - die Testzahl ist vorher wie
nachher dieselbe.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

### Task 2: Die Schnittstelle, und Gemini erfüllt sie

**Files:**
- Create: `src/NutriTrack.Api/Services/IAiProvider.cs`
- Modify: `src/NutriTrack.Api/Services/GeminiService.cs` (Klassendeklaration, `Name`-Eigenschaft)
- Test: `tests/NutriTrack.Api.Tests/GeminiServiceTests.cs`

**Interfaces:**
- Consumes: die umbenannten Typen aus Task 1.
- Produces:
  - `IAiProvider` mit `string Name { get; }`, `ParseAsync(IReadOnlyList<ChatMessage>, string, CancellationToken) → Task<AiParseResult>`, `ParseWishAsync(string, CancellationToken) → Task<AiWishResult>`, `ProbeAsync(CancellationToken) → Task<AiProbeResult>`
  - `GeminiService.Name` liefert `"gemini"`
  - Die Konstante `IAiProvider.Gemini = "gemini"` und `IAiProvider.OpenRouter = "openrouter"`

- [ ] **Step 1: Write the failing test**

An `tests/NutriTrack.Api.Tests/GeminiServiceTests.cs` anhängen:

```csharp
    [Fact]
    public void GeminiService_IsAnAiProvider()
    {
        // Die Anwendung soll den Anbieter ueber die Schnittstelle ansprechen, nicht ueber den
        // konkreten Typ - sonst entscheidet die Registrierung beim Start, was erst zur Laufzeit
        // feststeht.
        IAiProvider provider = Service();

        Assert.Equal(IAiProvider.Gemini, provider.Name);
        Assert.Equal("gemini", provider.Name);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --nologo --filter GeminiServiceTests`
Expected: FAIL — `IAiProvider` existiert nicht (CS0246).

- [ ] **Step 3: Write minimal implementation**

3a — `src/NutriTrack.Api/Services/IAiProvider.cs`:

```csharp
using NutriTrack.Api.Contracts.Ai;

namespace NutriTrack.Api.Services;

/// <summary>
/// Was die Anwendung von einem KI-Anbieter braucht - nicht mehr.
///
/// Es gibt sie, weil das Freikontingent von Google 20 Anfragen am TAG erlaubt (gemessen am
/// 2026-09-16) und damit fuer ein Ernaehrungstagebuch zu klein ist. Ein zweiter Anbieter loest
/// das; die Wahl zwischen beiden trifft der Betreiber zur Laufzeit, nicht der Uebersetzer.
///
/// Absichtlich schmal: drei Aufrufe, keine anbieterspezifischen Begriffe. Die Denkstufe etwa ist
/// eine Eigenheit von Gemini und hat hier nichts zu suchen - sie steckt in dessen Umsetzung.
/// </summary>
public interface IAiProvider
{
    public const string Gemini = "gemini";
    public const string OpenRouter = "openrouter";

    /// <summary>"gemini" oder "openrouter". Wandert ins Fehlerprotokoll, damit nach einem
    /// Wechsel feststeht, welcher Dienst welchen Fehlschlag verursacht hat.</summary>
    string Name { get; }

    Task<AiParseResult> ParseAsync(
        IReadOnlyList<ChatMessage> messages, string historyBlock, CancellationToken ct);

    Task<AiWishResult> ParseWishAsync(string wish, CancellationToken ct);

    Task<AiProbeResult> ProbeAsync(CancellationToken ct);
}
```

3b — **`AiProbeResult` steht heute als geschachtelter Record INNERHALB der Klasse `GeminiService`** (dort entstanden, als die Handprobe gebaut wurde). So kann die anbieterneutrale Schnittstelle ihn nicht referenzieren, ohne sich an Gemini zu binden. Zieh ihn deshalb aus dem Klassenkörper heraus auf die Namensraumebene `NutriTrack.Api.Services`, zu `AiParseResult`, `AiItem` und `AiWishResult` — die vier sind dieselbe Art von Ding, das interne Ergebnisformat der Erfassung. Nicht nach `NutriTrack.Api.Contracts.Ai`: dort liegen die Verträge nach außen, und `AiProbeResult` geht nie so hinaus (`AdminEndpoints` bildet es erst auf `AiProbeResponse` ab). An seinen Feldern und an der Logik von `ProbeAsync` ändert sich dabei nichts. Zieh die Verwendungsstellen nach, falls eine den qualifizierten Namen nutzt.

3c — In `GeminiService.cs` die Klassendeklaration erweitern und die Eigenschaft ergänzen. Aus

```csharp
public class GeminiService(
    HttpClient httpClient,
    IConfiguration configuration,
    AiSettingsProvider settingsProvider,
    ILogger<GeminiService> logger,
    TimeProvider timeProvider)
{
```

wird

```csharp
public class GeminiService(
    HttpClient httpClient,
    IConfiguration configuration,
    AiSettingsProvider settingsProvider,
    ILogger<GeminiService> logger,
    TimeProvider timeProvider) : IAiProvider
{
    public string Name => IAiProvider.Gemini;
```

Die drei Methoden erfüllen die Schnittstelle bereits — ihre Signaturen bleiben unverändert.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --nologo`
Expected: PASS, eine Prüfung mehr als zuvor.

- [ ] **Step 5: Commit**

```bash
git add src/NutriTrack.Api/Services/IAiProvider.cs src/NutriTrack.Api/Services/GeminiService.cs tests/NutriTrack.Api.Tests/GeminiServiceTests.cs
git commit -F - <<'MSG'
Describe what the application needs from an AI provider

Drei Aufrufe, keine anbieterspezifischen Begriffe: zerlegen, Zielwunsch deuten,
Handprobe. Die Denkstufe fehlt hier bewusst - sie ist eine Eigenheit von Gemini
und steckt in dessen Umsetzung.

GeminiService erfuellt die Schnittstelle ohne Aenderung an seinen Methoden; es
kommt nur der Name hinzu, der spaeter im Fehlerprotokoll steht. Ein zweiter
Anbieter kommt im uebernaechsten Schritt.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

### Task 3: Anbieter und OpenRouter-Modell in den Einstellungen

**Files:**
- Modify: `src/NutriTrack.Domain/Entities/AiSettings.cs` (zwei Spalten)
- Modify: `src/NutriTrack.Infrastructure/Data/Configurations/AiSettingsConfiguration.cs` (Längen)
- Create: Migration über `dotnet ef`
- Modify: `src/NutriTrack.Api/Services/AiSettingsProvider.cs` (Snapshot, Rückfall)
- Test: `tests/NutriTrack.Api.Tests/AiSettingsProviderTests.cs`

**Interfaces:**
- Consumes: `IAiProvider.Gemini` / `IAiProvider.OpenRouter` (Task 2).
- Produces:
  - `AiSettings.Provider` (`string?`) und `AiSettings.OpenRouterModel` (`string?`)
  - `AiSettingsSnapshot` trägt zusätzlich `Provider`, `ProviderFromDb`, `OpenRouterModel`, `OpenRouterModelFromDb`
  - `AiSettingsProvider.DefaultProvider = IAiProvider.Gemini` und `DefaultOpenRouterModel = "nex-agi/nex-n2.5-pro:free"`

- [ ] **Step 1: Write the failing test**

An `tests/NutriTrack.Api.Tests/AiSettingsProviderTests.cs` anhängen:

```csharp
    [Fact]
    public async Task Read_WithoutRow_UsesGeminiAsProvider()
    {
        await LeereAsync();

        var snapshot = Provider().Read();

        // Ohne Eintrag bleibt alles wie vor diesem Feature - ein neuer Anbieter darf sich nicht
        // dadurch einschalten, dass jemand die Tabelle leert.
        Assert.Equal("gemini", snapshot.Provider);
        Assert.False(snapshot.ProviderFromDb);
    }

    [Fact]
    public async Task Read_WithStoredProvider_PrefersIt()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vorhandene = await db.AiSettings.SingleOrDefaultAsync();
        if (vorhandene is not null)
            db.AiSettings.Remove(vorhandene);
        await db.SaveChangesAsync();

        db.AiSettings.Add(new AiSettings
        {
            Id = 1,
            Provider = "openrouter",
            OpenRouterModel = "dots-studio/dots-3-note-preview:free",
            Model = "gemini-3.6-flash",
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        Provider().Invalidate();

        var snapshot = Provider().Read();

        Assert.Equal("openrouter", snapshot.Provider);
        Assert.True(snapshot.ProviderFromDb);
        Assert.Equal("dots-studio/dots-3-note-preview:free", snapshot.OpenRouterModel);
        Assert.True(snapshot.OpenRouterModelFromDb);
        // Geminis Modell bleibt daneben stehen - Zurueckschalten soll kein Nachtippen kosten.
        Assert.Equal("gemini-3.6-flash", snapshot.Model);

        db.AiSettings.Remove(await db.AiSettings.SingleAsync());
        await db.SaveChangesAsync();
        Provider().Invalidate();
    }

    [Fact]
    public async Task Read_WithUnknownProvider_FallsBackToGemini()
    {
        // Ein Tippfehler in der Datenbank darf die Erfassung nicht lahmlegen.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vorhandene = await db.AiSettings.SingleOrDefaultAsync();
        if (vorhandene is not null)
            db.AiSettings.Remove(vorhandene);
        await db.SaveChangesAsync();

        db.AiSettings.Add(new AiSettings { Id = 1, Provider = "opendrouter", UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Provider().Invalidate();

        try
        {
            Assert.Equal("gemini", Provider().Read().Provider);
        }
        finally
        {
            db.AiSettings.Remove(await db.AiSettings.SingleAsync());
            await db.SaveChangesAsync();
            Provider().Invalidate();
        }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --nologo --filter AiSettingsProviderTests`
Expected: FAIL — `AiSettings.Provider` gibt es nicht (CS0117), `AiSettingsSnapshot` kennt `Provider` nicht.

- [ ] **Step 3: Write minimal implementation**

3a — `src/NutriTrack.Domain/Entities/AiSettings.cs` um zwei Spalten ergänzen, mit Begründung:

```csharp
    /// <summary>
    /// "gemini" oder "openrouter". Null heisst wie bei den uebrigen Feldern "nimm die
    /// Umgebungsvariable", und die steht ohne Eintrag auf Gemini.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Das Modell FUER OPENROUTER, getrennt von <see cref="Model"/>, das Gemini gehoert. Ein
    /// gemeinsames Feld zwaenge beim Umschalten jedes Mal zum Nachtippen, und der zuletzt
    /// genutzte Wert des anderen Anbieters waere weg.
    /// </summary>
    public string? OpenRouterModel { get; set; }
```

3b — `AiSettingsConfiguration.cs` bei den übrigen Längen:

```csharp
        builder.Property(s => s.Provider).HasMaxLength(20);

        // Laenger als Geminis Modellnamen: OpenRouter stellt den Anbieter voran
        // ("nex-agi/nex-n2.5-pro:free").
        builder.Property(s => s.OpenRouterModel).HasMaxLength(150);
```

3c — Migration:

```bash
dotnet ef migrations add AiProviderSelection --project src/NutriTrack.Infrastructure --startup-project src/NutriTrack.Api
```

Prüfe, dass sie **nur** zwei Spalten zu `AiSettings` hinzufügt und keine bestehende Tabelle anfasst.

3d — `AiSettingsProvider.cs`: Snapshot und Vorgabewerte erweitern. Der Record wird zu:

```csharp
public sealed record AiSettingsSnapshot(
    string Provider, bool ProviderFromDb,
    string Model, bool ModelFromDb,
    string OpenRouterModel, bool OpenRouterModelFromDb,
    string ThinkingLevel, bool ThinkingLevelFromDb,
    int MaxOutputTokens, bool MaxOutputTokensFromDb);
```

Neue Konstanten neben die bestehenden:

```csharp
    /// <summary>Ohne Eintrag bleibt alles beim Bisherigen. Ein zweiter Anbieter darf sich nicht
    /// dadurch einschalten, dass jemand die Tabelle leert.</summary>
    public const string DefaultProvider = IAiProvider.Gemini;

    /// <summary>
    /// Am 2026-09-16 gemessen: 34,9 s, Werte plausibel, Natrium korrekt in Gramm. Von den fuenf
    /// kostenlosen Modellen mit erzwungenem Schema das einzige, das im Test vollstaendig
    /// brauchbare Naehrwerte lieferte - dots-3-note-preview liess Natrium ganz weg,
    /// nemotron-3-super war ueberlastet.
    /// </summary>
    public const string DefaultOpenRouterModel = "nex-agi/nex-n2.5-pro:free";
```

In `Read()` die beiden neuen Felder ergänzen, nach demselben Muster wie die bestehenden. Der Anbietername wird zusätzlich auf die zwei bekannten Werte eingeschränkt:

```csharp
        var providerWert = provider ?? (configuration["Ai:Provider"] is { Length: > 0 } p ? p : DefaultProvider);

        // Ein verschriebener Name darf die Erfassung nicht lahmlegen. Der Rueckfall ist Gemini,
        // und die Logzeile sagt warum - sonst sucht der Betreiber an der falschen Stelle.
        if (providerWert != IAiProvider.Gemini && providerWert != IAiProvider.OpenRouter)
        {
            logger.LogWarning(
                "Unbekannter KI-Anbieter {Provider}; es gilt {Fallback}.", providerWert, DefaultProvider);
            providerWert = DefaultProvider;
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --nologo`
Expected: PASS. Bestehende Tests, die `AiSettingsSnapshot` konstruieren oder auf seine Felder zugreifen, müssen um die neuen Felder nachgezogen werden — der Übersetzer zeigt jede Stelle.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -F - <<'MSG'
Let the stored settings name the provider, not just the model

Zwei Spalten: Provider und OpenRouterModel. Getrennt von Model, das Gemini
gehoert - ein gemeinsames Feld zwaenge beim Umschalten jedes Mal zum Nachtippen,
und der zuletzt genutzte Wert des anderen Anbieters waere weg.

Ohne Eintrag gilt Gemini. Ein zweiter Anbieter darf sich nicht dadurch
einschalten, dass jemand die Tabelle leert.

Ein verschriebener Anbietername faellt auf Gemini zurueck und hinterlaesst eine
Logzeile. Ein Absturz waere die falsche Antwort auf einen Tippfehler in der
Konfiguration, und ein stiller Rueckfall liesse den Betreiber an der falschen
Stelle suchen.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

### Task 4: OpenRouterService

**Files:**
- Create: `src/NutriTrack.Api/Services/OpenRouterService.cs`
- Modify: `src/NutriTrack.Api/Program.cs` (zweiter `AddHttpClient`)
- Create: `tests/NutriTrack.Api.Tests/Infrastructure/StubOpenRouterHandler.cs`
- Modify: `tests/NutriTrack.Api.Tests/Infrastructure/NutriTrackApiFactory.cs` (Responder + Einstellung)
- Test: `tests/NutriTrack.Api.Tests/OpenRouterServiceTests.cs`

**Interfaces:**
- Consumes: `IAiProvider` (Task 2), `AiSettingsProvider.Read().OpenRouterModel` (Task 3), die Typen aus Task 1.
- Produces: `OpenRouterService` als zweite `IAiProvider`-Umsetzung, `Name` = `"openrouter"`. `NutriTrackApiFactory.OpenRouterResponder`.

- [ ] **Step 1: Write the failing test**

Datei `tests/NutriTrack.Api.Tests/Infrastructure/StubOpenRouterHandler.cs`:

```csharp
using System.Net;
using System.Text;

namespace NutriTrack.Api.Tests.Infrastructure;

/// <summary>
/// Ersetzt den Primary-Handler des OpenRouterService. Kein Test darf zu openrouter.ai sprechen -
/// das Freikontingent liegt bei 50 Anfragen am Tag, eine Testsuite haette es in einem Lauf
/// aufgebraucht.
/// </summary>
public sealed class StubOpenRouterHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    /// <summary>
    /// Der Umschlag, den OpenRouter wirklich schickt (am 2026-09-16 gegen den echten Dienst
    /// abgeholt): die Modellausgabe steckt als Zeichenkette in choices[0].message.content, das
    /// enthaltene JSON also eine Schachtelung tiefer als bei Gemini.
    /// </summary>
    public static HttpResponseMessage Payload(string innerJson)
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(innerJson);
        var envelope = $$"""
        {
          "id": "gen-stub",
          "model": "nex-agi/nex-n2.5-pro:free",
          "object": "chat.completion",
          "choices": [
            {
              "index": 0,
              "finish_reason": "stop",
              "message": { "role": "assistant", "content": {{escaped}} }
            }
          ],
          "usage": { "prompt_tokens": 900, "completion_tokens": 120, "total_tokens": 1020 }
        }
        """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>
    /// EIN ANBIETERFEHLER MIT STATUS 200 - der Wortlaut stammt vom 2026-09-16, als
    /// nvidia/nemotron-3-super-120b-a12b:free so antwortete. Wer nur den Statuscode prueft,
    /// verbucht das als unverstaendliche Antwort und sucht den Fehler bei sich.
    /// </summary>
    public static HttpResponseMessage UpstreamError(
        string message = "Upstream error from Nvidia: Service temporarily overloaded", int code = 502)
    {
        var body = $$"""
        {
          "id": "gen-stub-error",
          "error": {
            "message": "{{message}}",
            "code": {{code}},
            "metadata": { "error_type": "provider_unavailable" }
          }
        }
        """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>Das Tageskontingent von 50 ist erschoepft.</summary>
    public static HttpResponseMessage RateLimited() =>
        new(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                """{"error":{"message":"Rate limit exceeded: free-models-per-day","code":429}}""",
                Encoding.UTF8, "application/json")
        };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(responder(request));
}
```

In `NutriTrackApiFactory.cs` neben `GeminiResponder`:

```csharp
    /// <summary>Antwortverhalten des OpenRouter-Stubs, analog zu <see cref="GeminiResponder"/>.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> OpenRouterResponder { get; set; } =
        _ => StubOpenRouterHandler.Payload("""{"items":[]}""");
```

und in `ConfigureWebHost` bei den übrigen Einstellungen und Clients:

```csharp
        builder.UseSetting("OpenRouter:ApiKey", "test-openrouter-key");
```

```csharp
            services.AddHttpClient<OpenRouterService>()
                .ConfigurePrimaryHttpMessageHandler(() => new StubOpenRouterHandler(request => OpenRouterResponder(request)));
```

Datei `tests/NutriTrack.Api.Tests/OpenRouterServiceTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Contracts.Ai;
using NutriTrack.Api.Services;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests;

public class OpenRouterServiceTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    private OpenRouterService Service()
    {
        factory.CreateClient();
        return factory.Services.GetRequiredService<OpenRouterService>();
    }

    [Fact]
    public void Name_IsOpenRouter()
    {
        IAiProvider provider = Service();
        Assert.Equal("openrouter", provider.Name);
    }

    [Fact]
    public async Task ParseAsync_SendsOpenAiDialect()
    {
        string? body = null;
        factory.OpenRouterResponder = request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubOpenRouterHandler.Payload("""{"items":[]}""");
        };

        await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None);

        using var sent = JsonDocument.Parse(body!);
        var root = sent.RootElement;

        // Systemanweisung als erste Nachricht mit role "system" - Gemini hat dafuer ein eigenes
        // Feld, OpenRouter nicht.
        var messages = root.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("NICHT Milligramm", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());

        var format = root.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schema = format.GetProperty("json_schema");
        Assert.True(schema.GetProperty("strict").GetBoolean());
        // Ohne additionalProperties:false lehnt der strikte Modus das Schema ab.
        Assert.False(schema.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task ParseAsync_UnwrapsContentFromChoices()
    {
        factory.OpenRouterResponder = _ => StubOpenRouterHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Apfel", "label": "Apfel", "quantityInGrams": 150,
              "mealType": "Snack", "productKind": "generic",
              "estimate": { "calories": 52, "protein": 0.3, "carbohydrates": 14, "fat": 0.2 } }
          ]
        }
        """);

        var result = await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Apfel", item.Label);
        Assert.Equal(52m, item.Estimate.Calories);
    }

    [Fact]
    public async Task ParseAsync_WithUpstreamErrorInsideA200_ReportsItAsUnavailable()
    {
        // Der Fall, der ohne eigene Pruefung als "unverstaendliche Antwort" durchginge: Status
        // 200, Fehler im Rumpf. Am 2026-09-16 beim echten Dienst beobachtet.
        factory.OpenRouterResponder = _ => StubOpenRouterHandler.UpstreamError();

        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None));

        Assert.Contains("overloaded", ex.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseAsync_WithRateLimit_ThrowsQuota()
    {
        factory.OpenRouterResponder = _ => StubOpenRouterHandler.RateLimited();

        await Assert.ThrowsAsync<AiQuotaException>(() => Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ProbeAsync_ReportsStatusAndRawBody()
    {
        factory.OpenRouterResponder = _ => StubOpenRouterHandler.UpstreamError();

        var result = await Service().ProbeAsync(CancellationToken.None);

        // Die Probe wirft nicht - ein Anbieterfehler ist genau das, was der Betreiber sehen will.
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("overloaded", result.RawBody);
        Assert.Equal("nex-agi/nex-n2.5-pro:free", result.Model);
    }

    [Fact]
    public async Task ParseAsync_UsesStoredOpenRouterModel()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vorhandene = await db.AiSettings.SingleOrDefaultAsync();
        if (vorhandene is not null)
            db.AiSettings.Remove(vorhandene);
        db.AiSettings.Add(new AiSettings
        {
            Id = 1, OpenRouterModel = "dots-studio/dots-3-note-preview:free", UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        factory.Services.GetRequiredService<AiSettingsProvider>().Invalidate();

        string? body = null;
        factory.OpenRouterResponder = request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubOpenRouterHandler.Payload("""{"items":[]}""");
        };

        try
        {
            await Service().ParseAsync(
                [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None);

            using var sent = JsonDocument.Parse(body!);
            Assert.Equal("dots-studio/dots-3-note-preview:free", sent.RootElement.GetProperty("model").GetString());
        }
        finally
        {
            db.AiSettings.Remove(await db.AiSettings.SingleAsync());
            await db.SaveChangesAsync();
            factory.Services.GetRequiredService<AiSettingsProvider>().Invalidate();
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --nologo --filter OpenRouterServiceTests`
Expected: FAIL — `OpenRouterService` existiert nicht (CS0246).

- [ ] **Step 3: Write minimal implementation**

`src/NutriTrack.Api/Services/OpenRouterService.cs`. Systemanweisung, Normalisierung der Posten und die Deutung des Zielwunsches sind dieselben wie bei Gemini — was sich unterscheidet, ist ausschließlich der Umschlag. Hol dir die Systemanweisung und die Normalisierung **nicht** per Kopie, sondern mach sie in `GeminiService` `internal static` und nutze sie hier; eine zweite Kopie der Nährwertregeln liefe unweigerlich auseinander.

Gerüst:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using NutriTrack.Api.Contracts.Ai;

namespace NutriTrack.Api.Services;

/// <summary>
/// Der zweite Anbieter, angebunden ueber OpenRouters OpenAI-Dialekt.
///
/// Es gibt ihn, weil Googles Freikontingent 20 Anfragen am TAG erlaubt (gemessen 2026-09-16) und
/// damit fuer ein Ernaehrungstagebuch zu klein ist. OpenRouter gibt 50 - dafuer brauchten die
/// kostenlosen Modelle im selben Test 24 bis 35 Sekunden statt Geminis 3 bis 9.
/// </summary>
public class OpenRouterService(
    HttpClient httpClient,
    IConfiguration configuration,
    AiSettingsProvider settingsProvider,
    ILogger<OpenRouterService> logger,
    TimeProvider timeProvider) : IAiProvider
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";

    public string Name => IAiProvider.OpenRouter;
}
```

**Die drei Methoden im Einzelnen.** Jede hat ihr Gegenstück in `GeminiService`; nimm dessen Aufbau
und tausche nur den Umschlag aus. Konkret:

| Methode | Vorlage in `GeminiService` | Was anders ist |
|---|---|---|
| `ParseAsync` | `ParseAsync` (~Zeile 210) | Rumpf über `BuildBody(...)` statt Geminis Feldern; nach dem Senden erst `ThrowIfErrorInBody`, dann `ExtractContent`, dann dieselbe Deserialisierung und dieselbe Normalisierungsschleife (`NormalizeMealType`, `ProductKind`, `NormalizeEstimate`). |
| `ParseWishAsync` | `ParseWishAsync` (~Zeile 587) | Genauso: gleicher Rumpfaufbau, gleiche Auswertung, nur mit dem Zielwunsch-Schema und dessen Systemanweisung. |
| `ProbeAsync` | `ProbeAsync` (~Zeile 711) | Feste Beispieleingabe „zwei Broetchen mit Gouda", **wirft nicht** bei Fehlerstatus, Rohantwort auf 2000 Zeichen gekappt, Status 0 heißt „gar keine Antwort". Der `error`-im-Rumpf-Fall wird hier NICHT geworfen — er gehört in die Rohantwort, denn genau ihn will der Betreiber sehen. |

Der gemeinsame Rumpfaufbau als eine Methode, damit die drei nicht auseinanderlaufen:

```csharp
    /// <summary>
    /// Der Anfragerumpf im OpenAI-Dialekt. Anders als bei Gemini gibt es kein eigenes Feld fuer
    /// die Systemanweisung - sie ist die erste Nachricht mit der Rolle "system".
    /// </summary>
    private object BuildBody(string systemInstruction, string input, object schema)
    {
        var einstellungen = settingsProvider.Read();

        return new
        {
            model = einstellungen.OpenRouterModel,
            messages = new object[]
            {
                new { role = "system", content = systemInstruction },
                new { role = "user", content = input }
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new { name = "nutritrack", strict = true, schema }
            },
            max_tokens = einstellungen.MaxOutputTokens
        };
    }
```

Die Fehlerabbildung folgt der von `GeminiService`: `HttpStatusCode.TooManyRequests` wird zur
`AiQuotaException`, ein `TaskCanceledException` ohne Abbruch durch den Aufrufer zur
`AiUnavailableException` („OpenRouter hat nicht rechtzeitig geantwortet."), jeder andere
Fehlerstatus ebenfalls zur `AiUnavailableException` mit dem Statuscode im Text. Zusätzlich — und
das hat kein Gegenstück bei Gemini — der `error`-im-Rumpf-Fall aus `ThrowIfErrorInBody`.

Die drei Besonderheiten, jede mit Kommentar im Code:

```csharp
    /// <summary>
    /// OpenRouter beantwortet auch ANBIETERAUSFAELLE mit Status 200 und legt den Fehler in den
    /// Rumpf - am 2026-09-16 beobachtet, als ein Modell "Upstream error from Nvidia: Service
    /// temporarily overloaded" mit 200 zurueckgab. Wer nur den Statuscode prueft, verbucht das
    /// als unverstaendliche Antwort und sucht den Fehler bei sich.
    /// </summary>
    private static void ThrowIfErrorInBody(string body, Func<string, Exception?, Exception> unavailable)
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("error", out var error))
        {
            return;
        }

        var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
        throw unavailable($"OpenRouter meldet: {message ?? "unbekannter Fehler"}", null);
    }
```

```csharp
    /// <summary>
    /// Die Modellausgabe steckt in choices[0].message.content als Zeichenkette mit JSON darin -
    /// eine Schachtelung mehr als bei Gemini.
    /// </summary>
    private static string ExtractContent(string body)
    {
        using var document = JsonDocument.Parse(body);

        if (document.RootElement.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
            && content.GetString() is { Length: > 0 } text)
        {
            return text;
        }

        throw new AiMalformedResponseException(
            "Unerwarteter Antwortumschlag; erwartet wurde choices[0].message.content.");
    }
```

Das Schema wird aus Geminis Schema abgeleitet, aber mit `additionalProperties: false` an jedem Objekt — ohne das lehnt der strikte Modus ab. Bau es als eigene Eigenschaft `ResponseSchema` in dieser Klasse auf, mit diesem Kommentar darüber:

```csharp
    /// <summary>
    /// Dasselbe Schema wie bei Gemini, aber mit additionalProperties:false an JEDEM Objekt:
    /// OpenRouters strikter Modus lehnt sonst ab. Googles Schema kennt das Feld nicht, deshalb
    /// zwei Aufbauten statt eines geteilten.
    /// </summary>
```

Registrierung in `Program.cs` neben der von `GeminiService`:

```csharp
builder.Services.AddHttpClient<OpenRouterService>(client =>
{
    // 90 s statt Geminis 45: die kostenlosen Modelle brauchten am 2026-09-16 gemessene 24 bis
    // 35 Sekunden. Ein Deckel darunter schluege im Normalbetrieb zu, nicht im Fehlerfall.
    var seconds = builder.Configuration.GetValue("OpenRouter:TimeoutSeconds", 90);
    client.Timeout = TimeSpan.FromSeconds(seconds);
});
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --nologo`
Expected: PASS, sieben Prüfungen mehr.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -F - <<'MSG'
Speak OpenRouter's dialect as a second provider

OpenAI-Format statt Googles Interactions-API: Systemanweisung als erste
Nachricht, Schema unter response_format.json_schema, Antwort eine Schachtelung
tiefer in choices[0].message.content.

Drei Dinge, die die Messung vom 2026-09-16 erzwungen hat, stehen als Kommentar
im Code. Erstens: OpenRouter beantwortet auch Anbieterausfaelle mit Status 200
und legt den Fehler in den Rumpf - geprueft wird deshalb VOR dem Auspacken.
Zweitens: additionalProperties:false muss an jedem Objekt des Schemas stehen,
sonst lehnt der strikte Modus ab. Drittens: 90 s Zeitdeckel statt Geminis 45,
weil die kostenlosen Modelle 24 bis 35 Sekunden brauchen.

Systemanweisung und Naehrwert-Normalisierung kommen aus GeminiService statt aus
einer Kopie. Zwei Fassungen derselben Natriumregel liefen unweigerlich
auseinander.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

### Task 5: Die Wahl verdrahten

**Files:**
- Create: `src/NutriTrack.Api/Services/AiProviderFactory.cs`
- Modify: `src/NutriTrack.Api/Services/AiMealAssistant.cs` (Konstruktor, Aufrufe)
- Modify: `src/NutriTrack.Api/Endpoints/GoalsEndpoints.cs`, `src/NutriTrack.Api/Endpoints/AdminEndpoints.cs`
- Modify: `src/NutriTrack.Api/Services/AiFailureRecorder.cs` (Anbieter mitschreiben)
- Modify: `src/NutriTrack.Domain/Entities/AiFailure.cs` + Konfiguration + Migration (Spalte `Provider`)
- Modify: `src/NutriTrack.Api/Program.cs`, `docker-compose.yml`, `.env.example`
- Test: `tests/NutriTrack.Api.Tests/AiProviderFactoryTests.cs`, `tests/NutriTrack.Api.Tests/AiFailureLogTests.cs`

**Interfaces:**
- Consumes: `IAiProvider` (Task 2), `AiSettingsProvider.Read().Provider` (Task 3), `OpenRouterService` (Task 4).
- Produces: `AiProviderFactory.Current() → IAiProvider`.

- [ ] **Step 1: Write the failing test**

Datei `tests/NutriTrack.Api.Tests/AiProviderFactoryTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Services;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests;

public class AiProviderFactoryTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    private async Task SetzeAnbieterAsync(string? provider)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vorhandene = await db.AiSettings.SingleOrDefaultAsync();
        if (vorhandene is not null)
            db.AiSettings.Remove(vorhandene);
        await db.SaveChangesAsync();

        if (provider is not null)
        {
            db.AiSettings.Add(new AiSettings { Id = 1, Provider = provider, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        factory.Services.GetRequiredService<AiSettingsProvider>().Invalidate();
    }

    private IAiProvider Current()
    {
        factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AiProviderFactory>().Current();
    }

    [Fact]
    public async Task Current_WithoutSetting_IsGemini()
    {
        await SetzeAnbieterAsync(null);
        Assert.Equal("gemini", Current().Name);
    }

    [Fact]
    public async Task Current_WithOpenRouter_IsOpenRouter()
    {
        await SetzeAnbieterAsync("openrouter");
        try
        {
            Assert.Equal("openrouter", Current().Name);
        }
        finally
        {
            await SetzeAnbieterAsync(null);
        }
    }

    [Fact]
    public async Task Current_ChangesWithoutRestart()
    {
        // Der Kern des Features: umschalten, ohne die Anwendung neu zu starten.
        await SetzeAnbieterAsync(null);
        Assert.Equal("gemini", Current().Name);

        await SetzeAnbieterAsync("openrouter");
        try
        {
            Assert.Equal("openrouter", Current().Name);
        }
        finally
        {
            await SetzeAnbieterAsync(null);
        }
    }
}
```

Und in `AiFailureLogTests.cs` ergänzen:

```csharp
    [Fact]
    public async Task Failure_RecordsWhichProviderItWas()
    {
        await LeereAsync();
        var (client, _, _) = await factory.CreateUserAsync();

        factory.GeminiResponder = _ => throw new TaskCanceledException("Zeitdeckel im Test.");

        await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        // Ohne diese Spalte steht nach einem Wechsel nicht mehr fest, welcher Dienst welchen
        // Fehlschlag verursacht hat - und genau der Vergleich ist der Grund, warum es zwei gibt.
        var eintrag = Assert.Single(await ProtokollAsync());
        Assert.Equal("gemini", eintrag.Provider);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --nologo --filter "AiProviderFactoryTests|AiFailureLogTests"`
Expected: FAIL — `AiProviderFactory` existiert nicht, `AiFailure.Provider` existiert nicht.

- [ ] **Step 3: Write minimal implementation**

3a — `src/NutriTrack.Api/Services/AiProviderFactory.cs`:

```csharp
namespace NutriTrack.Api.Services;

/// <summary>
/// Waehlt den Anbieter nach der gespeicherten Einstellung.
///
/// Scoped und nicht als Registrierung beim Start: welcher Anbieter gilt, steht erst zur Laufzeit
/// fest und aendert sich, sobald der Betreiber in der Verwaltung umschaltet. Ein beim Start
/// gebundener Dienst braeuchte dafuer einen Neustart - und genau den soll dieses Feature ersparen.
/// </summary>
public class AiProviderFactory(
    GeminiService gemini,
    OpenRouterService openRouter,
    AiSettingsProvider settingsProvider)
{
    public IAiProvider Current() =>
        settingsProvider.Read().Provider == IAiProvider.OpenRouter ? openRouter : gemini;
}
```

Den Rückfall bei unbekanntem Namen macht bereits `AiSettingsProvider.Read()` (Task 3) — hier steht deshalb kein zweiter.

3b — `AiFailure` bekommt eine Spalte `Provider` (`string?`, `HasMaxLength(20)`), samt Migration. `AiFailureRecorder` füllt sie aus `settingsProvider.Read().Provider`.

3c — `AiMealAssistant` bekommt statt `GeminiService gemini` die `AiProviderFactory providerFactory` und ruft `providerFactory.Current().ParseAsync(...)`. Dasselbe in `GoalsEndpoints` (dort `ParseWishAsync`) und `AdminEndpoints` (dort `ProbeAsync` und die Schlüsselprüfung — beachte, dass der zu prüfende Schlüssel je nach Anbieter `Gemini:ApiKey` oder `OpenRouter:ApiKey` heißt).

3c-2 — **Die neuen Felder müssen auch gespeichert werden können.** `UpdateAiSettingsRequest` (in `src/NutriTrack.Api/Contracts/Admin/AiSettingsResponse.cs`) bekommt `Provider` und `OpenRouterModel` als `string?`, `AiSettingsResponse` zusätzlich `Provider`/`ProviderFromDatabase` und `OpenRouterModel`/`OpenRouterModelFromDatabase`. Im `PUT`-Handler werden beide wie die bestehenden Felder über `Leer()` normalisiert und geschrieben; ein unbekannter Anbietername wird dabei **abgewiesen**, nicht stillschweigend gespeichert:

```csharp
            if (request.Provider is { Length: > 0 } gewaehlt
                && gewaehlt != IAiProvider.Gemini && gewaehlt != IAiProvider.OpenRouter)
            {
                return Results.BadRequest(new { Error = "Anbieter muss \"gemini\" oder \"openrouter\" sein." });
            }
```

Der Rückfall im `AiSettingsProvider` fängt zwar auch einen falschen Wert ab, aber wer ihn über die Oberfläche einträgt, soll es sofort erfahren statt später zu rätseln, warum die Wahl nicht greift.

Dazu ein Test in `AdminEndpointTests.cs`:

```csharp
    [Fact]
    public async Task Put_SwitchesProviderAndRejectsUnknownOnes()
    {
        using var f = new AdminFactory("chef@example.com");
        await f.ResetDatabaseAsync();
        var (client, _, _) = await f.CreateUserAsync("chef@example.com");

        var ok = await client.PutAsJsonAsync("/api/admin/settings", new
        {
            provider = "openrouter", openRouterModel = "dots-studio/dots-3-note-preview:free"
        });
        ok.EnsureSuccessStatusCode();

        var json = await (await client.GetAsync("/api/admin/settings")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("openrouter", json.GetProperty("provider").GetString());
        Assert.True(json.GetProperty("providerFromDatabase").GetBoolean());

        var schlecht = await client.PutAsJsonAsync("/api/admin/settings", new { provider = "opendrouter" });
        Assert.Equal(HttpStatusCode.BadRequest, schlecht.StatusCode);

        // Der abgewiesene Wert darf nichts ueberschrieben haben.
        var danach = await (await client.GetAsync("/api/admin/settings")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("openrouter", danach.GetProperty("provider").GetString());
    }
```

3d — `docker-compose.yml`:

```yaml
      # Welcher KI-Anbieter gilt: gemini oder openrouter. Umschaltbar auch in der Verwaltung
      # unter /admin; dieser Wert ist der Rueckfall und die Notbremse.
      - Ai__Provider=${NUTRITRACK_AI_PROVIDER:-gemini}
      - OpenRouter__ApiKey=${NUTRITRACK_OPENROUTER_KEY:-}
      - OpenRouter__Model=${NUTRITRACK_OPENROUTER_MODEL:-nex-agi/nex-n2.5-pro:free}
      # 90 s statt Geminis 45: die kostenlosen Modelle brauchten gemessene 24 bis 35 Sekunden.
      - OpenRouter__TimeoutSeconds=${NUTRITRACK_OPENROUTER_TIMEOUT:-90}
```

3e — `.env.example` im selben Abschnitt wie die Gemini-Werte:

```
# Schluessel von openrouter.ai. Zweiter KI-Anbieter, umschaltbar in der Verwaltung unter /admin.
# Grund fuer die Zweitanbindung: Googles Freikontingent erlaubt nur 20 Anfragen am TAG, was fuer
# ein Ernaehrungstagebuch zu wenig ist. OpenRouters kostenlose Modelle geben 50 am Tag, brauchen
# dafuer aber 24 bis 35 Sekunden statt Geminis 3 bis 9 (gemessen 2026-09-16).
# ACHTUNG: auch hier lesen die Anbieter im Freikontingent mit - datenschutzrechtlich ist das
# keine Verbesserung gegenueber Google, sondern ein Vermittler mehr.
NUTRITRACK_OPENROUTER_KEY=

# Optional. NUR Modelle mit erzwungenem Schema (structured outputs) taugen; am 2026-09-16 waren
# das fuenf der zwanzig kostenlosen. Neuen Wert mit der Handprobe unter /admin pruefen, BEVOR er
# in Betrieb geht.
#NUTRITRACK_OPENROUTER_MODEL=nex-agi/nex-n2.5-pro:free

# Optional: gemini (Vorgabe) oder openrouter.
#NUTRITRACK_AI_PROVIDER=gemini
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -F - <<'MSG'
Choose the provider at request time, not at startup

Eine Factory liest die gespeicherte Einstellung und liefert den passenden
Anbieter. Scoped und nicht beim Start gebunden: welcher gilt, aendert sich in
dem Moment, in dem der Betreiber in der Verwaltung umschaltet - ein beim Start
gebundener Dienst braeuchte dafuer einen Neustart, und genau den soll dieses
Feature ersparen.

Das Fehlerprotokoll bekommt eine Spalte fuer den Anbieter. Ohne sie steht nach
einem Wechsel nicht mehr fest, welcher Dienst welchen Fehlschlag verursacht hat
- und genau dieser Vergleich ist der Grund, warum es zwei gibt.

.env.example nennt beim neuen Schluessel ausdruecklich, dass OpenRouter
datenschutzrechtlich keine Verbesserung gegenueber Google ist, sondern ein
Vermittler mehr. Wer die Zweitanbindung fuer einen Datenschutzgewinn haelt,
soll es dort lesen.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

### Task 6: Die Anbieterwahl in der Oberfläche

**Files:**
- Modify: `NutriTrack.Web/src/api/admin.ts`
- Modify: `NutriTrack.Web/src/pages/AdminPage.tsx`
- Modify: `NutriTrack.Web/src/App.css`

**Interfaces:**
- Consumes: die erweiterte Antwort von `GET/PUT /api/admin/settings` (Task 5) mit `provider`, `providerFromDatabase`, `openRouterModel`, `openRouterModelFromDatabase`; `AiFailure.provider`.
- Produces: nichts.

- [ ] **Step 1: Typen erweitern**

In `NutriTrack.Web/src/api/admin.ts`:

```ts
export interface AiSettings {
  provider: 'gemini' | 'openrouter';
  providerFromDatabase: boolean;
  model: string;
  modelFromDatabase: boolean;
  openRouterModel: string;
  openRouterModelFromDatabase: boolean;
  thinkingLevel: string;
  thinkingLevelFromDatabase: boolean;
  maxOutputTokens: number;
  maxOutputTokensFromDatabase: boolean;
}

export interface UpdateAiSettings {
  provider?: string;
  model?: string;
  openRouterModel?: string;
  thinkingLevel?: string;
  maxOutputTokens?: number;
}
```

und `AiFailure` um `provider: string | null` ergänzen.

- [ ] **Step 2: Anbieterwahl und bedingte Felder**

In `AdminPage.tsx` über den bestehenden Feldern eine Auswahl:

```tsx
          <label>
            Anbieter
            <select value={provider} onChange={e => setProvider(e.target.value)}>
              <option value="gemini">Google Gemini</option>
              <option value="openrouter">OpenRouter</option>
            </select>
            <small>{settings.providerFromDatabase ? 'gespeichert' : `aus der Umgebung: ${settings.provider}`}</small>
          </label>
```

Darunter gelten die Felder je Anbieter. Bei `gemini`: Modell und Denkstufe wie bisher. Bei `openrouter`: das OpenRouter-Modell, **keine** Denkstufe — OpenRouter kennt sie nicht, und ein Feld, das nichts bewirkt, ist schlimmer als keins. Der Ausgabedeckel gilt für beide.

Beim OpenRouter-Modellfeld dieser Hinweis darunter:

```tsx
            <small>
              Nur Modelle mit erzwungenem Schema funktionieren. Am 2026-09-16 waren das:
              nex-agi/nex-n2.5-pro:free, nex-agi/nex-n2.5-mini:free,
              dots-studio/dots-3-note-preview:free, nvidia/nemotron-3-super-120b-a12b:free,
              liquid/lfm-2.5-2.6b:free. Prüfe einen anderen Namen mit „Verbindung testen".
            </small>
```

- [ ] **Step 3: Anbieterspalte in der Fehlerliste**

Eine Spalte „Anbieter" zwischen „Art" und „Modell", gefüllt aus `f.provider ?? '–'`.

- [ ] **Step 4: Stil**

An `App.css` anhängen, im Stil des vorhandenen Blocks:

```css
.admin-form select { padding: 0.5rem 0.7rem; border-radius: var(--radius); }
```

- [ ] **Step 5: Bauen**

Run: `cd NutriTrack.Web && npm run build`
Expected: kein TypeScript-Fehler.

- [ ] **Step 6: Commit**

```bash
git add NutriTrack.Web/src/
git commit -F - <<'MSG'
Let the operator pick the provider, and show only what it understands

Eine Auswahl oben, darunter nur die Felder des gewaehlten Anbieters: die
Denkstufe erscheint bei OpenRouter gar nicht, weil er sie nicht kennt. Ein Feld,
das nichts bewirkt, ist schlimmer als keins.

Am OpenRouter-Modellfeld stehen die fuenf kostenlosen Modelle, die am 2026-09-16
erzwungene Schemas beherrschten - von zwanzig. Ohne diesen Hinweis probiert man
reihum Modelle durch, die gar nicht taugen koennen.

Die Fehlerliste bekommt eine Anbieterspalte. Ohne sie ist nach einem Wechsel
nicht mehr zu sehen, welcher Dienst welchen Fehlschlag verursacht hat.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

## Abschluss

- [ ] `dotnet test --nologo` in `NutriTrack.Api` — alles grün
- [ ] `npm run build` in `NutriTrack.Web` — kein TypeScript-Fehler
- [ ] Beide Repos pushen, `scripts/deploy.sh`
- [ ] Am laufenden Stand: unter `/admin` auf OpenRouter umschalten, „Verbindung testen" drücken — erwartet werden rund 25–35 Sekunden und ein Ergebnis, kein Fehler
- [ ] Eine echte Erfassung über OpenRouter, danach zurück auf Gemini schalten und prüfen, dass Geminis Modellname unverändert dasteht
