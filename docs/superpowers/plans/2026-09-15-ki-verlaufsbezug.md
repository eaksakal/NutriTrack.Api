# Verlaufsbezug in der KI-Erfassung — Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** „Das halbe Eis von gestern habe ich fertig gegessen" trägt den Posten mit den Nährwerten von gestern ins Tagebuch ein, statt ihn neu zu schätzen.

**Architecture:** Der Assistent lädt die Mahlzeiten der letzten drei Tage, nummeriert sie als `[v1]…[vN]` und stellt diesen Block dem Gesprächsprotokoll voran. Gemini gibt für einen Verlaufsbezug nur die Kennung zurück (`sourceRef`), nie Nährwerte. Der Assistent schlägt die Kennung in einem anfrage-lokalen Wörterbuch nach — das ausschließlich Einträge dieses Nutzers enthält — und übernimmt Label und Nährwerte des Original-`FoodItem`. Das Frontend schreibt solche Posten über den bestehenden `POST /api/meals/{id}/repeat`, womit keine FoodItem-Dublette entsteht.

**Tech Stack:** .NET 10 Minimal API, EF Core + SQLite, xUnit mit `WebApplicationFactory`, React 19 + TypeScript + axios.

**Spec:** `docs/superpowers/specs/2026-09-15-ki-verlaufsbezug-design.md`

## Global Constraints

- **Quellcode ohne BOM, Zeilenenden CRLF wie im Repo.** Python-Edits mit `utf-8-sig` verschmutzen Zeile 1 des Diffs.
- **Kommentare und Log-Texte ohne Umlaute** (`ue`, `ae`, `oe`, `ss`) — so steht es im gesamten C#-Bestand. Zeichenketten, die der Nutzer sieht (Fehlermeldungen, `Notice`), tragen echte Umlaute.
- **Kommentare begründen, sie beschreiben nicht.** Der Bestand erklärt durchgehend das Warum, oft mit Datum und Messwert. Ein Kommentar, der den Code nacherzählt, ist in diesem Repo falsch.
- **Kein Test spricht zu Google oder OpenFoodFacts.** Immer über `factory.GeminiResponder` bzw. `factory.OpenFoodFactsResponder`.
- **Nach Google gehen: Esstext, Gesprächsverlauf und der Essverlauf (Label, Menge, Mahlzeit, relative Uhrzeit).** Niemals Nutzer-ID, E-Mail, Tagesbilanz, Ziele oder Gewicht.
- **Testlauf:** `dotnet test --nologo` im Verzeichnis `NutriTrack.Api` (aktuell 182 Tests, alle grün). **Frontend-Bau:** `npm run build` in `NutriTrack.Web`. Ein Frontend-Testframework gibt es nicht — der TypeScript-Compiler ist dort die Prüfung.
- **Commit-Stil:** englischer Titel in der Befehlsform, deutscher Rumpf, der das Warum erklärt. Jeder Commit endet mit den zwei Attributionszeilen (`Co-Authored-By:` und `Claude-Session:`), wie in `git log` zu sehen.

---

### Task 1: Verlaufsblock und Kennungswörterbuch

Eine reine Klasse ohne Datenbank und ohne HTTP: sie bekommt fertige Einträge und liefert den Prompt-Text plus das Wörterbuch. Dadurch ist die gesamte Formatierungs- und Auflösungslogik ohne TestServer prüfbar, und der Assistent bleibt in Task 3 klein.

**Files:**
- Create: `src/NutriTrack.Api/Services/MealHistoryContext.cs`
- Test: `tests/NutriTrack.Api.Tests/MealHistoryContextTests.cs`

**Interfaces:**
- Consumes: `NutriTrack.Domain.Entities.MealEntry` und `FoodItem` (bestehend).
- Produces:
  - `MealHistoryContext.Build(IReadOnlyList<MealEntry> entries, DateOnly today) → MealHistoryContext`
  - `MealHistoryContext.Empty → MealHistoryContext`
  - `string Text { get; }` — der Prompt-Block, leer wenn keine Einträge
  - `bool IsEmpty { get; }`
  - `bool TryResolve(string? sourceRef, out MealEntry entry, out string hint)`
  - `const int MaxEntries = 40`
  - `const int Days = 3`

- [ ] **Step 1: Write the failing test**

Datei `tests/NutriTrack.Api.Tests/MealHistoryContextTests.cs`:

```csharp
using NutriTrack.Api.Services;
using NutriTrack.Domain.Entities;

namespace NutriTrack.Api.Tests;

public class MealHistoryContextTests
{
    private static readonly DateOnly Heute = new(2026, 9, 15);

    private static MealEntry Eintrag(
        string name, decimal quantity, DateOnly date, TimeOnly time, MealType mealType)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = "u1",
            FoodItemId = Guid.NewGuid(),
            FoodItem = new FoodItem { Name = name, Calories = 200m, Protein = 3m, Carbohydrates = 25m, Fat = 9m },
            QuantityInGrams = quantity,
            MealType = mealType,
            Date = date,
            Time = time
        };

    [Fact]
    public void Build_WithoutEntries_IsEmpty()
    {
        var context = MealHistoryContext.Build([], Heute);

        Assert.True(context.IsEmpty);
        Assert.Equal(string.Empty, context.Text);
    }

    [Fact]
    public void Build_NumbersEntriesAndNamesDaysRelatively()
    {
        var context = MealHistoryContext.Build(
        [
            Eintrag("Haferflocken", 80m, Heute, new TimeOnly(8, 10), MealType.Breakfast),
            Eintrag("Eis, Vanille", 100m, Heute.AddDays(-1), new TimeOnly(21, 30), MealType.Snack),
            Eintrag("Spaghetti (gekocht)", 250m, Heute.AddDays(-2), new TimeOnly(12, 15), MealType.Lunch),
        ], Heute);

        Assert.False(context.IsEmpty);
        Assert.Contains("[v1] heute 08:10 Breakfast - Haferflocken (80 g)", context.Text);
        Assert.Contains("[v2] gestern 21:30 Snack - Eis, Vanille (100 g)", context.Text);
        Assert.Contains("[v3] vorgestern 12:15 Lunch - Spaghetti (gekocht) (250 g)", context.Text);
    }

    [Fact]
    public void TryResolve_WithKnownRef_ReturnsEntryAndHint()
    {
        var eis = Eintrag("Eis, Vanille", 100m, Heute.AddDays(-1), new TimeOnly(21, 30), MealType.Snack);
        var context = MealHistoryContext.Build(
            [Eintrag("Haferflocken", 80m, Heute, new TimeOnly(8, 10), MealType.Breakfast), eis], Heute);

        Assert.True(context.TryResolve("v2", out var entry, out var hint));
        Assert.Equal(eis.Id, entry.Id);
        Assert.Equal("gestern 21:30", hint);
    }

    [Theory]
    [InlineData("v99")]
    [InlineData("V1 ")]   // Kennungen sind kleingeschrieben, werden aber getrimmt und gefaltet
    [InlineData("")]
    [InlineData(null)]
    public void TryResolve_WithUnknownOrEmptyRef_ReturnsFalseOrFolds(string? sourceRef)
    {
        var context = MealHistoryContext.Build(
            [Eintrag("Haferflocken", 80m, Heute, new TimeOnly(8, 10), MealType.Breakfast)], Heute);

        var found = context.TryResolve(sourceRef, out _, out _);

        // "V1 " ist dieselbe Kennung wie "v1"; alles Uebrige darf nicht treffen.
        Assert.Equal(sourceRef == "V1 ", found);
    }

    [Fact]
    public void Build_WithMoreThanMaxEntries_KeepsOnlyTheFirstOnes()
    {
        var viele = Enumerable.Range(0, MealHistoryContext.MaxEntries + 5)
            .Select(i => Eintrag($"Posten {i}", 10m, Heute, new TimeOnly(12, 0), MealType.Snack))
            .ToList();

        var context = MealHistoryContext.Build(viele, Heute);

        Assert.Contains($"[v{MealHistoryContext.MaxEntries}]", context.Text);
        Assert.DoesNotContain($"[v{MealHistoryContext.MaxEntries + 1}]", context.Text);
    }

    [Fact]
    public void Build_WithFractionalQuantity_WritesNoTrailingZeros()
    {
        var context = MealHistoryContext.Build(
            [Eintrag("Olivenoel", 12.5m, Heute, new TimeOnly(19, 0), MealType.Dinner)], Heute);

        Assert.Contains("(12.5 g)", context.Text);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --nologo --filter MealHistoryContextTests`
Expected: FAIL — `MealHistoryContext` existiert nicht (CS0246).

- [ ] **Step 3: Write minimal implementation**

Datei `src/NutriTrack.Api/Services/MealHistoryContext.cs`:

```csharp
using System.Globalization;
using System.Text;
using NutriTrack.Domain.Entities;

namespace NutriTrack.Api.Services;

/// <summary>
/// Der Essverlauf, wie ihn das Modell zu sehen bekommt — und der Rueckweg von seiner Antwort auf
/// den echten Eintrag.
///
/// Das Modell bekommt Kennungen ([v1], [v2] ...) statt Guids: eine Guid kostet im Prompt rund
/// zehnmal so viele Token wie "v2", und ein Modell, das sie abschreiben muss, verdreht sie. Die
/// Kennung ist ausserdem anfrage-lokal - eine aus einer frueheren Antwort geratene Kennung zeigt
/// hoechstens auf einen anderen eigenen Eintrag, nie auf einen fremden.
///
/// Rein, ohne Datenbank und ohne HTTP: Formatierung und Aufloesung sind damit ohne TestServer
/// pruefbar, und der Assistent bleibt bei seiner eigentlichen Aufgabe.
/// </summary>
public sealed class MealHistoryContext
{
    /// <summary>Heute, gestern, vorgestern. Begruendung in Entscheidung 4 des Designs.</summary>
    public const int Days = 3;

    /// <summary>
    /// Deckel gegen den Ausnahmetag: wer 200 Posten in drei Tagen erfasst hat, schiebt sonst
    /// einen Prompt vor sich her, der die Antwortzeit in den Zeitdeckel treibt. Die juengsten
    /// Eintraege sind die, auf die sich jemand bezieht.
    /// </summary>
    public const int MaxEntries = 40;

    public static readonly MealHistoryContext Empty = new(string.Empty, [], []);

    private readonly Dictionary<string, MealEntry> _byRef;
    private readonly Dictionary<string, string> _hintByRef;

    private MealHistoryContext(
        string text, Dictionary<string, MealEntry> byRef, Dictionary<string, string> hintByRef)
    {
        Text = text;
        _byRef = byRef;
        _hintByRef = hintByRef;
    }

    /// <summary>Der Block fuer den Prompt; leer, wenn es nichts zu zeigen gibt.</summary>
    public string Text { get; }

    public bool IsEmpty => Text.Length == 0;

    /// <param name="entries">Juengste zuerst; die Reihenfolge bestimmt die Nummerierung.</param>
    public static MealHistoryContext Build(IReadOnlyList<MealEntry> entries, DateOnly today)
    {
        if (entries.Count == 0)
            return Empty;

        var byRef = new Dictionary<string, MealEntry>(StringComparer.OrdinalIgnoreCase);
        var hintByRef = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var text = new StringBuilder("Bisher gegessen:");
        text.AppendLine();

        foreach (var (entry, index) in entries.Take(MaxEntries).Select((e, i) => (e, i)))
        {
            var key = $"v{index + 1}";
            var hint = $"{TagName(entry.Date, today)} {entry.Time:HH\\:mm}";

            byRef[key] = entry;
            hintByRef[key] = hint;

            text.AppendLine(
                $"[{key}] {hint} {entry.MealType} - {entry.FoodItem.Name} ({Menge(entry.QuantityInGrams)} g)");
        }

        return new MealHistoryContext(text.ToString(), byRef, hintByRef);
    }

    /// <summary>
    /// Schlaegt die Kennung aus der Modellantwort nach. Trifft sie nicht, ist das KEIN Fehler:
    /// der Aufrufer faellt auf den gewoehnlichen Weg zurueck. Erfundene Kennungen sind hier die
    /// erwartete Abweichung, nicht die Ausnahme.
    /// </summary>
    public bool TryResolve(string? sourceRef, out MealEntry entry, out string hint)
    {
        entry = null!;
        hint = string.Empty;

        var key = sourceRef?.Trim();
        if (string.IsNullOrEmpty(key) || !_byRef.TryGetValue(key, out var found))
            return false;

        entry = found;
        hint = _hintByRef[key];
        return true;
    }

    /// <summary>
    /// Relative Tagesnamen, weil der Nutzer "gestern" sagt und nicht "am 14.09.". Die Umrechnung
    /// gehoert hierher, wo die Zeitzone des Servers gilt - ein Modell, das sie selbst anstellt,
    /// rechnet mit dem Datum, das es gerade zu kennen glaubt.
    /// </summary>
    private static string TagName(DateOnly date, DateOnly today) => (today.DayNumber - date.DayNumber) switch
    {
        0 => "heute",
        1 => "gestern",
        2 => "vorgestern",
        _ => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    };

    /// <summary>
    /// Invariant und ohne Nachkommanullen: "80" statt "80,00". Das Komma der deutschen Kultur
    /// waere im Prompt eine zweite Lesart derselben Zahl.
    /// </summary>
    private static string Menge(decimal quantity) =>
        quantity.ToString("0.##", CultureInfo.InvariantCulture);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --nologo --filter MealHistoryContextTests`
Expected: PASS (7 Tests, `Theory` mit vier Fällen).

- [ ] **Step 5: Commit**

```bash
git add src/NutriTrack.Api/Services/MealHistoryContext.cs tests/NutriTrack.Api.Tests/MealHistoryContextTests.cs
git commit -F - <<'MSG'
Describe the last three days in a form a model can point at

Der Verlaufsblock und sein Rueckweg, noch ohne Anschluss: eine reine Klasse, die
Eintraege in [v1]..[vN] nummeriert und die Kennung wieder aufloest.

Kennungen statt Guids, weil eine Guid im Prompt rund zehnmal so viele Token
kostet und ein Modell, das sie abschreiben muss, sie verdreht. Sie sind
ausserdem anfrage-lokal: eine geratene Kennung zeigt hoechstens auf einen
anderen eigenen Eintrag.

Ohne Datenbank und ohne HTTP, damit Formatierung und Aufloesung ohne TestServer
pruefbar bleiben - die beiden Stellen, an denen sich spaeter Fehler verstecken.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

### Task 2: `sourceRef` im Gemini-Vertrag

Der Verlaufsblock geht als Text in den Prompt, das Antwortschema bekommt das neue Feld. `GeminiService` kennt weiterhin keine Datenbank — er nimmt den fertigen Block entgegen.

**Files:**
- Modify: `src/NutriTrack.Api/Services/GeminiService.cs` (Klasse `GeminiItem`; `SystemInstruction`; `ParseAsync`; `ResponseSchema`)
- Modify: `src/NutriTrack.Api/Services/AiMealAssistant.cs:41` (nur die zwei `gemini.ParseAsync`-Aufrufe, damit der Bau durchläuft)
- Test: `tests/NutriTrack.Api.Tests/GeminiServiceTests.cs`

**Interfaces:**
- Consumes: nichts aus Task 1 — der Block kommt als `string`.
- Produces:
  - `GeminiService.ParseAsync(IReadOnlyList<ChatMessage> messages, string historyBlock, CancellationToken ct) → Task<GeminiParseResult>`
  - `GeminiItem.SourceRef` (`string?`, JSON-Name `sourceRef`, nach dem Lesen getrimmt; leer wird zu `null`)

- [ ] **Step 1: Write the failing test**

An `tests/NutriTrack.Api.Tests/GeminiServiceTests.cs` anhängen (innerhalb der Klasse):

```csharp
    [Fact]
    public async Task ParseAsync_WithHistory_PutsItInTheRequestBody()
    {
        string? body = null;
        factory.GeminiResponder = request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubGeminiHandler.Payload("""{"items":[]}""");
        };

        await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "den Rest vom Eis" }],
            "Bisher gegessen:\n[v1] gestern 21:30 Snack - Eis, Vanille (100 g)\n",
            CancellationToken.None);

        Assert.NotNull(body);
        Assert.Contains("Bisher gegessen", body);
        Assert.Contains("Eis, Vanille", body);
    }

    [Fact]
    public async Task ParseAsync_WithoutHistory_SendsNoHistoryBlock()
    {
        string? body = null;
        factory.GeminiResponder = request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubGeminiHandler.Payload("""{"items":[]}""");
        };

        await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None);

        Assert.NotNull(body);
        Assert.DoesNotContain("Bisher gegessen", body);
    }

    [Fact]
    public async Task ParseAsync_WithSourceRef_ReadsIt()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Eis", "label": "Eis, Vanille", "sourceRef": " v2 ",
              "quantityInGrams": 100, "mealType": "Snack", "productKind": "generic",
              "estimate": { "calories": 200, "protein": 3, "carbohydrates": 25, "fat": 9 } }
          ]
        }
        """);

        var result = await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "den Rest vom Eis" }], string.Empty, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("v2", item.SourceRef);
    }

    [Fact]
    public async Task ParseAsync_WithoutSourceRef_LeavesItNull()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Apfel", "label": "Apfel", "sourceRef": "",
              "quantityInGrams": 150, "mealType": "Snack", "productKind": "generic",
              "estimate": { "calories": 52, "protein": 0.3, "carbohydrates": 14, "fat": 0.2 } }
          ]
        }
        """);

        var result = await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None);

        Assert.Null(Assert.Single(result.Items).SourceRef);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --nologo --filter GeminiServiceTests`
Expected: FAIL — `ParseAsync` nimmt keine drei Argumente (CS1501) und `GeminiItem.SourceRef` gibt es nicht (CS1061).

- [ ] **Step 3: Write minimal implementation**

3a — In `GeminiService.cs` die Klasse `GeminiItem` um das Feld ergänzen, direkt nach `Label`:

```csharp
    /// <summary>
    /// Kennung einer Zeile aus dem Verlaufsblock ("v2"), wenn der Nutzer sich auf etwas frueher
    /// Gegessenes bezogen hat. Null im Normalfall. Aufgeloest wird sie im Assistenten - hier steht
    /// nur, was das Modell gesagt hat.
    /// </summary>
    [JsonPropertyName("sourceRef")]
    public string? SourceRef { get; set; }
```

3b — `SystemInstruction` ergänzen, unmittelbar vor der Zeile `Antworte ausschliesslich im vorgegebenen Schema.`:

```
        Steht ueber dem Gespraech ein Abschnitt "Bisher gegessen", dann ist das der Verlauf der
        letzten Tage, jede Zeile mit einer Kennung in eckigen Klammern. Bezieht sich der Nutzer auf
        eine dieser Zeilen ("das Eis von gestern", "nochmal das Fruehstueck", "den Rest davon"),
        setze sourceRef auf ihre Kennung, zum Beispiel "v2". Die Naehrwerte sind dann bereits
        bekannt und dein estimate wird verworfen - fuelle es trotzdem, das Schema verlangt es.
        quantityInGrams gilt weiterhin und ist deine Aufgabe: "die andere Haelfte" und "nochmal
        dasselbe" meinen die Menge aus der Zeile, "die Haelfte davon" die halbe.
        Ohne erkennbaren Bezug laesst du sourceRef weg und verfaehrst wie bisher. Erfinde NIE eine
        Kennung, die nicht im Abschnitt steht.
```

3c — `ParseAsync` bekommt den Block und stellt ihn voran:

```csharp
    public async Task<GeminiParseResult> ParseAsync(
        IReadOnlyList<ChatMessage> messages, string historyBlock, CancellationToken ct)
    {
        var apiKey = configuration["Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw Unavailable("Gemini:ApiKey fehlt.");

        var transcript = new StringBuilder();

        // Der Verlauf steht VOR dem Gespraech: er ist Hintergrund, nicht Teil dessen, was der
        // Nutzer gerade gesagt hat. Stuende er dahinter, liest ihn das Modell leicht als letzte
        // Aeusserung und zerlegt den Verlauf selbst in Posten.
        if (!string.IsNullOrWhiteSpace(historyBlock))
        {
            transcript.AppendLine(historyBlock.TrimEnd());
            transcript.AppendLine();
        }

        foreach (var message in messages)
            transcript.AppendLine($"{(message.Role == "assistant" ? "Rueckfrage" : "Nutzer")}: {message.Text}");

        var inner = await SendAsync(SystemInstruction, transcript.ToString(), ResponseSchema, ct);
```

Der Rest der Methode bleibt unverändert. In der Normalisierungsschleife darunter, direkt hinter der `ProductKind`-Zeile:

```csharp
            // Leere Zeichenkette ist dasselbe wie "kein Bezug". Das Modell liefert bei einem
            // optionalen Feld gern "" statt es wegzulassen, und ein leerer Schluessel wuerde im
            // Woerterbuch spaeter als sinnlose Suche auflaufen.
            item.SourceRef = string.IsNullOrWhiteSpace(item.SourceRef) ? null : item.SourceRef.Trim();
```

3d — `ResponseSchema`: `sourceRef` als Eigenschaft ergänzen, direkt hinter `label`. **Nicht** in `required` aufnehmen, `estimate` bleibt dort stehen:

```csharp
                        sourceRef = new
                        {
                            type = "string",
                            description = "Kennung einer Zeile aus dem Abschnitt \"Bisher gegessen\" "
                                          + "(zum Beispiel \"v2\"), wenn der Nutzer sich auf diesen "
                                          + "Eintrag bezieht. Sonst weglassen."
                        },
```

3e — Damit der Bau durchläuft, in `AiMealAssistant.ParseAsync` beide Aufrufe vorläufig auf den leeren Block setzen (Task 3 füllt ihn):

```csharp
            parsed = await gemini.ParseAsync(messages, string.Empty, ct);
```

3f — Die übrigen bestehenden Aufrufe in den Tests nachziehen. In `tests/NutriTrack.Api.Tests/GeminiServiceTests.cs` rufen rund 15 Stellen `ParseAsync` mit zwei Argumenten auf; jede bekommt `string.Empty` vor das `CancellationToken`. Die Aufrufe stehen alle in der Form

```csharp
            CancellationToken.None);
```

nach einer Nachrichtenliste. Sie einzeln durchgehen und ergänzen — die vier neuen Tests aus Schritt 1 sind bereits richtig. Zur Kontrolle, dass keine Stelle vergessen wurde:

```bash
grep -n "ParseAsync(" tests/NutriTrack.Api.Tests/GeminiServiceTests.cs
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --nologo`
Expected: PASS — 182 bisherige plus 7 aus Task 1 plus 4 neue = 193 Tests, alle grün.

- [ ] **Step 5: Commit**

```bash
git add src/NutriTrack.Api/Services/GeminiService.cs src/NutriTrack.Api/Services/AiMealAssistant.cs tests/NutriTrack.Api.Tests/GeminiServiceTests.cs
git commit -F - <<'MSG'
Let the model point at an earlier meal

sourceRef im Antwortschema und der Verlaufsblock im Prompt. Der Block steht VOR
dem Gespraech: dahinter liest das Modell ihn leicht als letzte Aeusserung und
zerlegt den Verlauf selbst in Posten.

estimate bleibt Pflichtfeld. Googles Schema kennt kein "required, ausser wenn
sourceRef gesetzt ist", und den Vertrag fuer alle Posten zu lockern, um den
Sonderfall sauber abzubilden, waere der schlechtere Tausch - die Schaetzung
wandert bei einem Verlaufsbezug in den Papierkorb, mehr passiert nicht.

GeminiService kennt weiterhin keine Datenbank: er bekommt den fertigen Block als
Text. Angeschlossen wird er im naechsten Schritt.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

### Task 3: Verlauf laden und Bezug auflösen

Der Assistent holt die Einträge, baut den Kontext, löst `sourceRef` auf und überspringt für solche Posten die Produktdatenbank.

**Files:**
- Modify: `src/NutriTrack.Api/Services/AiMealAssistant.cs` (Konstruktor, `ParseAsync`, Posten-Schleife)
- Modify: `src/NutriTrack.Api/Contracts/Ai/ParseMealResponse.cs` (Klasse `ParsedItem`)
- Modify: `src/NutriTrack.Api/Endpoints/AiEndpoints.cs:48` (userId durchreichen)
- Test: `tests/NutriTrack.Api.Tests/AiEndpointTests.cs`

**Interfaces:**
- Consumes: `MealHistoryContext.Build` / `TryResolve` / `MaxEntries` / `Days` (Task 1); `GeminiItem.SourceRef` und `GeminiService.ParseAsync(messages, historyBlock, ct)` (Task 2).
- Produces:
  - `AiMealAssistant.ParseAsync(IReadOnlyList<ChatMessage> messages, string userId, CancellationToken ct) → Task<ParseMealResponse>`
  - `ParsedItem.Source` kennt zusätzlich den Wert `"history"`
  - `ParsedItem.SourceEntryId` (`Guid?`) und `ParsedItem.SourceHint` (`string?`)

- [ ] **Step 1: Write the failing test**

An `tests/NutriTrack.Api.Tests/AiEndpointTests.cs` anhängen (innerhalb der Klasse). Die Hilfsmethode legt einen Eintrag über den bestehenden Schreibweg an, damit der Test dieselbe Datenbank sieht wie die API:

```csharp
    /// <summary>
    /// Legt einen Eintrag ueber den echten Schreibweg an und gibt dessen Id zurueck. Ueber die API
    /// und nicht per DbContext: so steht in der Datenbank genau das, was im Betrieb dort stuende.
    /// </summary>
    private static async Task<Guid> AnlegenAsync(
        HttpClient client, string name, decimal calories, decimal quantity, DateOnly date)
    {
        var response = await client.PostAsJsonAsync("/api/meals", new
        {
            foodName = name,
            calories,
            protein = 3m,
            carbohydrates = 25m,
            fat = 9m,
            quantityInGrams = quantity,
            mealType = "Snack",
            date
        });

        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task ParseMeal_WithSourceRef_UsesTheOriginalsNutrients()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        var gestern = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
        var eisId = await AnlegenAsync(client, "Eis, Vanille", calories: 207m, quantity: 100m, date: gestern);

        // Das Modell bezieht sich auf die Zeile - und schaetzt daneben. Die Schaetzung darf nicht
        // gewinnen, sonst stuende derselbe Becher mit zwei Werten im Tagebuch.
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Eis", "label": "Eis", "sourceRef": "v1",
              "quantityInGrams": 100, "mealType": "Snack", "productKind": "generic",
              "estimate": { "calories": 999, "protein": 1, "carbohydrates": 1, "fat": 1 } }
          ]
        }
        """);

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "den Rest vom Eis von gestern" } }
        });

        response.EnsureSuccessStatusCode();
        var parsed = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = parsed.GetProperty("items")[0];

        Assert.Equal("history", item.GetProperty("source").GetString());
        Assert.Equal(eisId, item.GetProperty("sourceEntryId").GetGuid());
        Assert.Equal("Eis, Vanille", item.GetProperty("label").GetString());
        Assert.Equal(207m, item.GetProperty("estimate").GetProperty("calories").GetDecimal());
        Assert.Equal("gestern", item.GetProperty("sourceHint").GetString()![..7]);
    }

    [Fact]
    public async Task ParseMeal_WithInventedSourceRef_FallsBackToEstimate()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        await AnlegenAsync(client, "Eis, Vanille", 207m, 100m, DateOnly.FromDateTime(DateTime.Now));

        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Apfel", "label": "Apfel", "sourceRef": "v99",
              "quantityInGrams": 150, "mealType": "Snack", "productKind": "generic",
              "estimate": { "calories": 52, "protein": 0.3, "carbohydrates": 14, "fat": 0.2 } }
          ]
        }
        """);

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        response.EnsureSuccessStatusCode();
        var item = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];

        Assert.Equal("generic", item.GetProperty("source").GetString());
        Assert.Equal(52m, item.GetProperty("estimate").GetProperty("calories").GetDecimal());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("sourceEntryId").ValueKind);
    }

    [Fact]
    public async Task ParseMeal_WithSourceRef_NeverReachesAnotherUsersEntry()
    {
        // Der fremde Nutzer isst zuerst - seine Eintraege sind die einzigen, die "v1" treffen
        // koennte, wenn die Aufloesung nicht am Besitzer haengt.
        var (fremd, _, _) = await factory.CreateUserAsync();
        await AnlegenAsync(fremd, "Fremdes Eis", 999m, 100m, DateOnly.FromDateTime(DateTime.Now));

        var (client, _, _) = await factory.CreateUserAsync();

        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Eis", "label": "Eis", "sourceRef": "v1",
              "quantityInGrams": 100, "mealType": "Snack", "productKind": "generic",
              "estimate": { "calories": 200, "protein": 3, "carbohydrates": 25, "fat": 9 } }
          ]
        }
        """);

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "das Eis von gestern" } }
        });

        response.EnsureSuccessStatusCode();
        var item = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];

        Assert.NotEqual("history", item.GetProperty("source").GetString());
        Assert.Equal(200m, item.GetProperty("estimate").GetProperty("calories").GetDecimal());
    }

    [Fact]
    public async Task ParseMeal_SendsHistoryButNeverTheUsersIdentity()
    {
        var (client, email, userId) = await factory.CreateUserAsync();
        await AnlegenAsync(client, "Eis, Vanille", 207m, 100m, DateOnly.FromDateTime(DateTime.Now));

        string? body = null;
        factory.GeminiResponder = request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubGeminiHandler.Payload("""{"items":[]}""");
        };

        await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "was habe ich gegessen" } }
        });

        Assert.NotNull(body);
        Assert.Contains("Eis, Vanille", body);
        Assert.DoesNotContain(email, body);
        Assert.DoesNotContain(userId, body);
    }

    [Fact]
    public async Task ParseMeal_WithoutHistory_BehavesAsBefore()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        string? body = null;
        factory.GeminiResponder = request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubGeminiHandler.Payload("""
            {
              "items": [
                { "searchTerm": "Apfel", "label": "Apfel", "quantityInGrams": 150,
                  "mealType": "Snack", "productKind": "generic",
                  "estimate": { "calories": 52, "protein": 0.3, "carbohydrates": 14, "fat": 0.2 } }
              ]
            }
            """);
        };

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        response.EnsureSuccessStatusCode();
        Assert.NotNull(body);
        Assert.DoesNotContain("Bisher gegessen", body);
        var item = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];
        Assert.Equal("generic", item.GetProperty("source").GetString());
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --nologo --filter AiEndpointTests`
Expected: FAIL — die Antwort kennt weder `sourceEntryId` noch `sourceHint`, und `source` ist `"generic"` statt `"history"`.

- [ ] **Step 3: Write minimal implementation**

3a — `src/NutriTrack.Api/Contracts/Ai/ParseMealResponse.cs`, Klasse `ParsedItem`: den Kommentarblock über `Source` um den vierten Zustand ergänzen und zwei Felder anhängen:

```csharp
    ///   "history"        Bezug auf einen eigenen frueheren Eintrag. Die Werte stammen aus dem
    ///                    Tagebuch, nicht vom Modell; SourceEntryId zeigt auf das Original.
```

```csharp
    /// <summary>
    /// Der Eintrag, auf den sich dieser Posten bezieht. Gesetzt genau dann, wenn Source
    /// "history" ist. Das Frontend traegt solche Posten ueber POST /api/meals/{id}/repeat ein -
    /// derselbe FoodItem, also keine Dublette mit minimal abweichenden Werten.
    /// </summary>
    public Guid? SourceEntryId { get; set; }

    /// <summary>
    /// Der Zeitpunkt des Originals in Worten ("gestern 21:30"), damit der Nutzer VOR der
    /// Bestaetigung sieht, worauf das Modell sich bezogen hat. Der Bezug ist die eine Stelle, an
    /// der es etwas entscheidet, das niemand getippt hat.
    /// </summary>
    public string? SourceHint { get; set; }
```

3b — `src/NutriTrack.Api/Services/AiMealAssistant.cs`: `AppDbContext` in den Konstruktor, `using` für EF Core und die Entitäten ergänzen:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NutriTrack.Api.Contracts.Ai;
using NutriTrack.Api.Contracts.Food;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Services;

public class AiMealAssistant(
    GeminiService gemini,
    OpenFoodFactsService openFoodFacts,
    IMemoryCache cache,
    AppDbContext db,
    ILogger<AiMealAssistant> logger)
{
```

3c — `ParseAsync` bekommt `userId` und lädt den Verlauf, bevor Gemini gefragt wird:

```csharp
    public async Task<ParseMealResponse> ParseAsync(
        IReadOnlyList<ChatMessage> messages, string userId, CancellationToken ct)
    {
        var history = await LoadHistoryAsync(userId, ct);

        GeminiParseResult parsed;
        try
        {
            parsed = await gemini.ParseAsync(messages, history.Text, ct);
        }
        catch (GeminiMalformedResponseException)
        {
            // Genau ein zweiter Anlauf: Modelle straucheln gelegentlich einmalig am Schema.
            // Mehr Versuche kosten Kontingent und Wartezeit, ohne die Trefferquote zu heben.
            logger.LogWarning("Gemini-Antwort unbrauchbar, ein Wiederholungsversuch.");
            parsed = await gemini.ParseAsync(messages, history.Text, ct);
        }
```

Und die neue Methode am Ende der Klasse:

```csharp
    /// <summary>
    /// Die juengsten Eintraege der letzten <see cref="MealHistoryContext.Days"/> Tage. Absteigend,
    /// weil der Deckel dann die aeltesten abschneidet - auf die bezieht sich am seltensten jemand.
    /// </summary>
    private async Task<MealHistoryContext> LoadHistoryAsync(string userId, CancellationToken ct)
    {
        // DateTime.Now und nicht UtcNow: Date und Time der Eintraege sind lokale Angaben (siehe
        // den repeat-Endpunkt), und "gestern" muss dieselbe Grenze meinen wie dort.
        var heute = DateOnly.FromDateTime(DateTime.Now);
        var seit = heute.AddDays(-(MealHistoryContext.Days - 1));

        var entries = await db.MealEntries
            .Include(entry => entry.FoodItem)
            .Where(entry => entry.UserId == userId && entry.Date >= seit)
            .OrderByDescending(entry => entry.Date)
            .ThenByDescending(entry => entry.Time)
            .Take(MealHistoryContext.MaxEntries)
            .ToListAsync(ct);

        return MealHistoryContext.Build(entries, heute);
    }
```

3d — Ein Posten mit aufgelöstem Bezug darf nicht in die Produktdatenbank. Die Begriffsliste (`var terms = items…`) filtert ihn mit aus. Dafür vor der Liste die Auflösung einmal durchführen und merken:

```csharp
        // Erst aufloesen, dann suchen: ein Posten mit Bezug hat seine Werte schon und darf weder
        // Suchbudget noch das Minutenkontingent von OpenFoodFacts verbrauchen.
        var resolved = new Dictionary<GeminiItem, (MealEntry Entry, string Hint)>();
        foreach (var item in items)
        {
            if (item.SourceRef is null)
                continue;

            if (history.TryResolve(item.SourceRef, out var entry, out var hint))
            {
                resolved[item] = (entry, hint);
            }
            else
            {
                // Erfundene Kennungen sind die erwartete Abweichung, kein Fehlerfall: der Posten
                // laeuft den gewoehnlichen Weg. Haeufen sie sich, stimmt etwas mit dem Prompt
                // nicht - deshalb ueberhaupt eine Zeile.
                logger.LogWarning(
                    "Gemini nannte die unbekannte Verlaufskennung {SourceRef}; Posten faellt auf die Schaetzung zurueck.",
                    item.SourceRef);
            }
        }

        var terms = items
            .Where(item => !resolved.ContainsKey(item))
            .Where(IstMarkenprodukt)
```

3e — In der abschließenden `foreach (var item in items)`-Schleife den Verlaufsfall zuerst behandeln:

```csharp
        foreach (var item in items)
        {
            if (resolved.TryGetValue(item, out var bezug))
            {
                var food = bezug.Entry.FoodItem;

                response.Items.Add(new ParsedItem
                {
                    // Der Name aus dem Tagebuch, nicht der des Modells: der Nutzer erkennt daran,
                    // welcher Eintrag gemeint ist, und genau das soll er vor dem Haken pruefen.
                    Label = food.Name,
                    QuantityInGrams = item.QuantityInGrams,
                    // Die Mahlzeit kommt vom Modell und nicht aus dem Original: der Rest des
                    // Abendessens ist mittags ein Mittagessen.
                    MealType = item.MealType,
                    Source = "history",
                    SourceEntryId = bezug.Entry.Id,
                    SourceHint = bezug.Hint,
                    Candidates = [],
                    Estimate = new NutrientEstimate
                    {
                        Calories = food.Calories,
                        Protein = food.Protein,
                        Carbohydrates = food.Carbohydrates,
                        Fat = food.Fat,
                        Fiber = food.Fiber,
                        Sugar = food.Sugar,
                        SaturatedFat = food.SaturatedFat,
                        Sodium = food.Sodium
                    }
                });

                continue;
            }

            var markenprodukt = IstMarkenprodukt(item);
```

Der Rest der Schleife bleibt unverändert.

3f — `src/NutriTrack.Api/Endpoints/AiEndpoints.cs`: `userId` wird bereits für den Zähler ermittelt, er muss nur weitergereicht werden:

```csharp
                var result = await assistant.ParseAsync(request.Messages, userId, ct);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --nologo`
Expected: PASS — 198 Tests grün (193 aus Task 2 plus 5 neue).

- [ ] **Step 5: Commit**

```bash
git add src/NutriTrack.Api/Services/AiMealAssistant.cs src/NutriTrack.Api/Contracts/Ai/ParseMealResponse.cs src/NutriTrack.Api/Endpoints/AiEndpoints.cs tests/NutriTrack.Api.Tests/AiEndpointTests.cs
git commit -F - <<'MSG'
Resolve a reference to yesterday against your own diary

Der Assistent laedt die letzten drei Tage, gibt sie dem Modell als [v1]..[vN]
und schlaegt die genannte Kennung wieder nach. Trifft sie, kommen Label und
Naehrwerte aus dem Tagebuch und die Schaetzung des Modells wandert in den
Papierkorb - im Test schaetzt der Stub absichtlich 999 kcal fuer ein Eis mit
207, damit sichtbar ist, welche Quelle gewinnt.

Das Woerterbuch entsteht je Anfrage aus den Eintraegen dieses Nutzers. Eine
erfundene Kennung kann damit nicht auf fremde Daten zeigen, sondern trifft
nichts; ein Test legt dafuer zuerst einen fremden Nutzer mit einem Eintrag an,
der ohne Besitzerbindung genau die "v1" waere.

Ein aufgeloester Posten geht NICHT an OpenFoodFacts: seine Werte stehen fest,
und das Suchbudget von 8 s wie das Minutenkontingent des Fremddienstes sind zu
knapp, um sie fuer eine Antwort auszugeben, die ohnehin verworfen wuerde.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

### Task 4: Die Oberfläche trägt den Bezug ein

**Files:**
- Modify: `NutriTrack.Web/src/api/ai.ts` (Interface `ParsedItem`)
- Modify: `NutriTrack.Web/src/pages/AiEntryPage.tsx` (`herkunft`, `submit`, Zeilendarstellung)

**Interfaces:**
- Consumes: `ParsedItem.source === 'history'`, `sourceEntryId`, `sourceHint` (Task 3); `mealsApi.repeat(id, data)` aus `NutriTrack.Web/src/api/meals.ts:169` (bestehend).
- Produces: nichts, worauf spätere Tasks aufbauen.

Den Testfall des Designs „… und wird bestätigt → kein neuer FoodItem" gibt es schon:
`tests/NutriTrack.Api.Tests/MealEndpointTests.cs:824`
(`Repeat_ReusesSameFoodItemInsteadOfCreatingADuplicate`). Er deckt genau den Schreibweg ab, den
diese Task benutzt — hier ist deshalb kein zweiter Test dafür zu schreiben.

- [ ] **Step 1: Typen erweitern**

In `NutriTrack.Web/src/api/ai.ts`, Interface `ParsedItem`: den Kommentarblock über `source` um den vierten Zustand ergänzen, die Union erweitern und zwei Felder anhängen:

```ts
   *  history       – Bezug auf einen eigenen frueheren Eintrag. Die Werte kommen aus dem
   *                  Tagebuch, nicht vom Modell; eingetragen wird ueber den repeat-Endpunkt.
   */
  source: 'openfoodfacts' | 'generic' | 'estimate' | 'history';
  candidates: FoodItem[];
  estimate: NutrientEstimate | null;
  /** Gesetzt genau dann, wenn `source === 'history'`: der Eintrag, der wiederholt wird. */
  sourceEntryId: string | null;
  /** Der Zeitpunkt des Originals in Worten, z. B. "gestern 21:30". */
  sourceHint: string | null;
```

- [ ] **Step 2: Herkunft benennen**

In `NutriTrack.Web/src/pages/AiEntryPage.tsx`, Funktion `herkunft`, vor `default`:

```ts
    case 'history':
      return 'aus deinem Verlauf';
```

- [ ] **Step 3: Beim Übernehmen den repeat-Weg nehmen**

In `submit`, am Anfang der `for (const { item, draft, index } of chosen)`-Schleife — **vor** `const source = item.candidates[...]`:

```tsx
      // Ein Posten aus dem Verlauf geht ueber repeat: dort wird dieselbe FoodItemId
      // weiterverwendet. Ueber create entstuende aus denselben Naehrwerten ein zweiter,
      // rundungsbedingt leicht abweichender FoodItem.
      if (item.source === 'history' && item.sourceEntryId) {
        try {
          await mealsApi.repeat(item.sourceEntryId, {
            quantityInGrams: draft.quantityInGrams,
            mealType: draft.mealType,
            date,
          });
          saved.push(index);
        } catch {
          failed.push(item.label);
        }
        continue;
      }
```

- [ ] **Step 4: Den Bezug sichtbar machen**

In `NutriTrack.Web/src/pages/AiEntryPage.tsx:316` steht `{herkunft(item.source)}` allein in einem
`<span>`. Den Zeitpunkt anhängen:

```tsx
{herkunft(item.source)}{item.sourceHint ? `: ${item.sourceHint}` : ''}
```

- [ ] **Step 5: Bauen**

Run: `cd NutriTrack.Web && npm run build`
Expected: kein TypeScript-Fehler. Erwartbarer Stolperstein: `RepeatMealRequest` in `meals.ts` prüfen — `date` ist dort ein `string` im Format `YYYY-MM-DD`, genau wie beim `create`-Aufruf darunter, also passt der durchgereichte Wert unverändert.

- [ ] **Step 6: Von Hand ausprobieren**

Mit gesetztem `NUTRITRACK_GEMINI_KEY` lokal starten, gestern einen Posten eintragen, dann „den Rest von dem X von gestern" tippen. Erwartung: die Zeile zeigt „aus deinem Verlauf: gestern HH:MM", die Kalorien stimmen mit dem Original überein, und nach dem Übernehmen steht in der Datenbank kein zweiter FoodItem mit demselben Namen.

- [ ] **Step 7: Commit**

```bash
git add NutriTrack.Web/src/api/ai.ts NutriTrack.Web/src/pages/AiEntryPage.tsx
git commit -F - <<'MSG'
Show what the AI pointed at before writing it down

Ein Posten mit source "history" zeigt jetzt "aus deinem Verlauf: gestern 21:30"
und geht beim Uebernehmen ueber POST /api/meals/{id}/repeat statt ueber create.
Der Bezug ist die eine Stelle, an der das Modell etwas entscheidet, das der
Nutzer nicht getippt hat - er muss ihn vor dem Haken sehen koennen.

Ueber create statt repeat entstuende aus denselben Naehrwerten ein zweiter,
rundungsbedingt leicht abweichender FoodItem.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

### Task 5: Die Datenschutzzusage richtigstellen

`.env.example` verspricht heute etwas, das nach Task 3 nicht mehr stimmt. Eine unwahre Zusage ist schlimmer als gar keine.

**Files:**
- Modify: `NutriTrack.Api/.env.example:60-63`

**Interfaces:**
- Consumes: nichts. Produces: nichts.

- [ ] **Step 1: Den Absatz ersetzen**

Der heutige Wortlaut:

```
# ACHTUNG: im kostenlosen Kontingent nutzt Google die Inhalte zur Produktverbesserung und
# menschliche Pruefer duerfen mitlesen. Deshalb schickt NutriTrack ausschliesslich den Esstext
# an Google - keine Kennung, keine Bilanz, keine Ziele.
```

wird zu:

```
# ACHTUNG: im kostenlosen Kontingent nutzt Google die Inhalte zur Produktverbesserung und
# menschliche Pruefer duerfen mitlesen. An Google gehen: der Esstext, der Gespraechsverlauf und
# - seit dem 2026-09-15 - die Mahlzeiten der letzten drei Tage (Bezeichnung, Menge, Mahlzeit,
# Uhrzeit). Letzteres, damit "das halbe Eis von gestern" die Naehrwerte von gestern trifft statt
# neu geschaetzt zu werden; die Abwaegung steht in
# docs/superpowers/specs/2026-09-15-ki-verlaufsbezug-design.md.
# NICHT uebertragen werden Kennung, E-Mail, Tagesbilanz, Ziele und Gewicht.
```

- [ ] **Step 2: Prüfen, dass keine zweite Stelle dasselbe verspricht**

Run: `grep -rn "ausschliesslich den Esstext\|nur den Esstext" --include='*.cs' --include='*.md' --include='*.yml' --include='*.example' --include='*.tsx' . | grep -v docs/superpowers/specs/2026-09-12`
Expected: keine Treffer außer dem historischen Spec vom 2026-09-12, das bewusst stehen bleibt — es hält fest, was damals galt.

- [ ] **Step 3: Commit**

```bash
git add .env.example
git commit -F - <<'MSG'
Stop promising something that is no longer true

.env.example sagte zu, dass ausschliesslich der Esstext an Google geht. Seit dem
Verlaufsbezug stimmt das nicht mehr: die Mahlzeiten der letzten drei Tage gehen
mit. Eine unwahre Zusage ist schlimmer als gar keine - wer sie liest,
entscheidet auf ihrer Grundlage, ob er einen Schluessel eintraegt.

Das Spec vom 2026-09-12 bleibt im alten Wortlaut stehen: es haelt fest, was
damals galt, und ist kein Versprechen an den Betreiber.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

## Abschluss

- [ ] `dotnet test --nologo` im Verzeichnis `NutriTrack.Api` — alle Tests grün
- [ ] `npm run build` im Verzeichnis `NutriTrack.Web` — kein TypeScript-Fehler
- [ ] `git -C NutriTrack.Api push origin main` und `git -C NutriTrack.Web push origin main`
- [ ] `NutriTrack.Api/scripts/deploy.sh` — baut am Ziel und startet neu
- [ ] Am laufenden Stand von Hand: gestrigen Posten anlegen, „den Rest von X von gestern" eingeben, Nährwerte und `repeat`-Verhalten prüfen
