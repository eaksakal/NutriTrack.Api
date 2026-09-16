# Einstellungsseite für die KI-Anbindung — Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Modell, Denkstufe und Ausgabedeckel der Gemini-Anbindung lassen sich in der Oberfläche ändern, mit einem Knopf gegen den echten Dienst prüfen, und die letzten Fehlschläge sind dort sichtbar — ohne SSH, ohne Neustart.

**Architecture:** Zwei neue Tabellen (`AiSettings` mit genau einer Zeile, `AiFailures` als Ringpuffer von 50). Ein Singleton `AiSettingsProvider` liest sie über einen eigenen Scope, hält das Ergebnis im Cache und fällt für jeden fehlenden Wert auf `IConfiguration` zurück; `GeminiService` fragt ihn statt der Konfiguration. Vier Endpunkte unter `/api/admin` prüfen den `ClaimTypes.Email` des Tokens gegen `Admin:Email` und antworten sonst mit 404. Das Fehlerprotokoll schreibt `AiEndpoints` in den `catch`-Blöcken, die die drei Gemini-Ausnahmen ohnehin schon fangen — `GeminiService` bleibt davon unberührt.

**Tech Stack:** .NET 10 Minimal API, EF Core + SQLite, xUnit mit `WebApplicationFactory`, React 19 + TypeScript + axios.

**Spec:** `docs/superpowers/specs/2026-09-16-ki-einstellungsseite-design.md`

## Global Constraints

- **Quelldateien ohne BOM.** Python-Edits mit `utf-8-sig` verschmutzen Zeile 1 des Diffs.
- **Kommentare und Log-Texte ohne Umlaute** (`ue`, `ae`, `oe`, `ss`) — so steht es im gesamten C#-Bestand. Zeichenketten, die ein Nutzer auf dem Bildschirm sieht, tragen echte Umlaute.
- **Kommentare begründen, sie beschreiben nicht.** Der Bestand erklärt durchgehend das Warum, oft mit Datum und Messwert.
- **Kein Test spricht zu Google oder OpenFoodFacts.** Immer über `factory.GeminiResponder` bzw. `factory.OpenFoodFactsResponder`.
- **Das Fehlerprotokoll speichert NIEMALS den Esstext.** Nur Zeitpunkt, Art, Modell, Denkstufe, Dauer, HTTP-Status und einen Kurzgrund aus höchstens 200 Zeichen. Das ist die tragende Zusage dieses Features.
- **Der API-Schlüssel wird nirgends angezeigt, zurückgegeben oder änderbar gemacht.**
- **Fremde Konten bekommen 404, nie 403** — wie bei fremden Mahlzeiten (`MealEndpoints.cs:272`).
- **Ist `Admin:Email` nicht gesetzt, ist die Verwaltung für niemanden erreichbar.**
- **Testlauf:** `dotnet test --nologo` im Verzeichnis `NutriTrack.Api` (aktuell 202 Tests, alle grün). **Frontend-Bau:** `npm run build` in `NutriTrack.Web`. Ein Frontend-Testframework gibt es nicht — der TypeScript-Compiler ist dort die Prüfung.
- **Commit-Stil:** englischer Titel in der Befehlsform, deutscher Rumpf, der das Warum erklärt. Jeder Commit endet mit diesen zwei Zeilen, unverändert, auch wenn ein anderes Modell die Arbeit macht:
  ```
  Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
  ```

---

### Task 1: Tabellen für Einstellungen und Fehlschläge

**Files:**
- Create: `src/NutriTrack.Domain/Entities/AiSettings.cs`
- Create: `src/NutriTrack.Domain/Entities/AiFailure.cs`
- Create: `src/NutriTrack.Infrastructure/Data/Configurations/AiSettingsConfiguration.cs`
- Create: `src/NutriTrack.Infrastructure/Data/Configurations/AiFailureConfiguration.cs`
- Modify: `src/NutriTrack.Infrastructure/Data/AppDbContext.cs` (zwei DbSets)
- Create: Migration über `dotnet ef`
- Test: `tests/NutriTrack.Api.Tests/AiSettingsStorageTests.cs`

**Interfaces:**
- Consumes: nichts.
- Produces:
  - `NutriTrack.Domain.Entities.AiSettings` mit `int Id`, `string? Model`, `string? ThinkingLevel`, `int? MaxOutputTokens`, `DateTime UpdatedAt`
  - `NutriTrack.Domain.Entities.AiFailure` mit `Guid Id`, `DateTime OccurredAt`, `string Kind`, `string? Model`, `string? ThinkingLevel`, `int? DurationMs`, `int? StatusCode`, `string Reason`
  - `AppDbContext.AiSettings` und `AppDbContext.AiFailures`

- [ ] **Step 1: Write the failing test**

Datei `tests/NutriTrack.Api.Tests/AiSettingsStorageTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests;

public class AiSettingsStorageTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    private async Task<T> InScopeAsync<T>(Func<AppDbContext, Task<T>> arbeit)
    {
        factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        return await arbeit(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public async Task AiSettings_RoundTrips()
    {
        var gelesen = await InScopeAsync(async db =>
        {
            db.AiSettings.Add(new AiSettings
            {
                Id = 1,
                Model = "gemini-3.6-flash",
                ThinkingLevel = "low",
                MaxOutputTokens = 2048,
                UpdatedAt = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();

            return await db.AiSettings.AsNoTracking().SingleAsync();
        });

        Assert.Equal("gemini-3.6-flash", gelesen.Model);
        Assert.Equal("low", gelesen.ThinkingLevel);
        Assert.Equal(2048, gelesen.MaxOutputTokens);
    }

    [Fact]
    public async Task AiSettings_AllowsNullValuesMeaningFallBackToEnvironment()
    {
        // Null ist kein Versehen, sondern die Aussage "nimm die Umgebungsvariable". Die Spalten
        // muessen das also zulassen, sonst zwingt das Schema den Nutzer zu einem Wert.
        var gelesen = await InScopeAsync(async db =>
        {
            var vorhandene = await db.AiSettings.SingleOrDefaultAsync();
            if (vorhandene is not null)
                db.AiSettings.Remove(vorhandene);

            db.AiSettings.Add(new AiSettings { Id = 1, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();

            return await db.AiSettings.AsNoTracking().SingleAsync();
        });

        Assert.Null(gelesen.Model);
        Assert.Null(gelesen.ThinkingLevel);
        Assert.Null(gelesen.MaxOutputTokens);
    }

    [Fact]
    public async Task AiFailure_RoundTripsWithoutMealText()
    {
        var gelesen = await InScopeAsync(async db =>
        {
            db.AiFailures.Add(new AiFailure
            {
                Id = Guid.NewGuid(),
                OccurredAt = new DateTime(2026, 9, 16, 9, 30, 0, DateTimeKind.Utc),
                Kind = "Schema",
                Model = "gemini-3.6-flash",
                ThinkingLevel = "minimal",
                DurationMs = 45000,
                StatusCode = null,
                Reason = "The JSON value could not be converted. Path: $.items[0].estimate.sugar"
            });
            await db.SaveChangesAsync();

            return await db.AiFailures.AsNoTracking()
                .OrderByDescending(f => f.OccurredAt).FirstAsync();
        });

        Assert.Equal("Schema", gelesen.Kind);
        Assert.Equal(45000, gelesen.DurationMs);
        Assert.Contains("estimate.sugar", gelesen.Reason);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --nologo --filter AiSettingsStorageTests`
Expected: FAIL — `AiSettings`/`AiFailure` existieren nicht (CS0246), `db.AiSettings` gibt es nicht (CS1061).

- [ ] **Step 3: Write minimal implementation**

3a — `src/NutriTrack.Domain/Entities/AiSettings.cs`:

```csharp
namespace NutriTrack.Domain.Entities;

/// <summary>
/// Die zur Laufzeit verstellbaren Werte der Gemini-Anbindung. GENAU EINE Zeile (Id = 1): es gibt
/// eine Anbindung, nicht eine je Nutzer, und ein Schluessel mit fester Eins erspart die Frage,
/// welche von mehreren Zeilen gilt.
///
/// Jeder Wert ist nullbar, und null heisst NICHT "leer", sondern "nimm die Umgebungsvariable".
/// Das ist die Notbremse: wer sich ueber die Oberflaeche verstellt hat, loescht den Wert und
/// bekommt wieder das, was in der .env steht - ohne in der Datenbank zu hantieren.
/// </summary>
public class AiSettings
{
    public int Id { get; set; }

    public string? Model { get; set; }
    public string? ThinkingLevel { get; set; }
    public int? MaxOutputTokens { get; set; }

    public DateTime UpdatedAt { get; set; }
}
```

3b — `src/NutriTrack.Domain/Entities/AiFailure.cs`:

```csharp
namespace NutriTrack.Domain.Entities;

/// <summary>
/// Ein Fehlschlag der KI-Erfassung, so viel davon wie zur Diagnose noetig - und keinen Deut mehr.
///
/// WAS HIER NICHT HINEINGEHOERT: der Esstext des Nutzers. Am 2026-09-15 kostete die Diagnose
/// zweier Ausfaelle Stunden am Betriebsrechner, und genau deshalb gibt es diese Tabelle; sie darf
/// aber nicht zum Nebeneingang fuer Mahlzeitentexte werden. Dieselbe Linie zieht der Bestand
/// schon bei den Logzeilen (GeminiService.DescribeRoot nennt Feldnamen, nie Inhalte).
/// </summary>
public class AiFailure
{
    public Guid Id { get; set; }

    /// <summary>UTC. Die Oberflaeche rechnet auf die Zeitzone des Betrachters um.</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>"Timeout", "Schema", "Quota" oder "Unavailable".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Modell und Denkstufe ZUM ZEITPUNKT DES FEHLSCHLAGS - der Sinn des Protokolls ist
    /// der Vergleich vorher/nachher, und der geht verloren, wenn hier der aktuelle Wert stuende.</summary>
    public string? Model { get; set; }
    public string? ThinkingLevel { get; set; }

    public int? DurationMs { get; set; }

    /// <summary>Null, wenn gar keine Antwort kam (Zeitdeckel, Netzfehler).</summary>
    public int? StatusCode { get; set; }

    /// <summary>Der Ausnahmetext, auf 200 Zeichen gekappt. Nie der Prompt.</summary>
    public string Reason { get; set; } = string.Empty;
}
```

3c — `src/NutriTrack.Infrastructure/Data/Configurations/AiSettingsConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NutriTrack.Domain.Entities;

namespace NutriTrack.Infrastructure.Data.Configurations;

public class AiSettingsConfiguration : IEntityTypeConfiguration<AiSettings>
{
    public void Configure(EntityTypeBuilder<AiSettings> builder)
    {
        builder.HasKey(s => s.Id);

        // Kein ValueGeneratedOnAdd: die Id ist keine laufende Nummer, sondern die feste Eins der
        // einen Zeile. Ein Autowert lockte dazu, eine zweite anzulegen.
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.Model).HasMaxLength(100);
        builder.Property(s => s.ThinkingLevel).HasMaxLength(20);
    }
}
```

3d — `src/NutriTrack.Infrastructure/Data/Configurations/AiFailureConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NutriTrack.Domain.Entities;

namespace NutriTrack.Infrastructure.Data.Configurations;

public class AiFailureConfiguration : IEntityTypeConfiguration<AiFailure>
{
    public void Configure(EntityTypeBuilder<AiFailure> builder)
    {
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Kind).IsRequired().HasMaxLength(20);
        builder.Property(f => f.Model).HasMaxLength(100);
        builder.Property(f => f.ThinkingLevel).HasMaxLength(20);

        // Die Kappung auf 200 passiert beim Schreiben; die Laenge hier haelt fest, dass sie
        // beabsichtigt ist, und verhindert einen Ausnahmetext von Kilobytelaenge in der Datenbank.
        builder.Property(f => f.Reason).IsRequired().HasMaxLength(200);

        // Die Oberflaeche liest ausschliesslich "die juengsten 50", und der Ringpuffer loescht
        // nach derselben Ordnung. Ohne Index ist das bei jeder Schreiboperation ein Tabellenscan.
        builder.HasIndex(f => f.OccurredAt);
    }
}
```

3e — In `src/NutriTrack.Infrastructure/Data/AppDbContext.cs` bei den bestehenden DbSets ergänzen:

```csharp
    public DbSet<AiSettings> AiSettings => Set<AiSettings>();
    public DbSet<AiFailure> AiFailures => Set<AiFailure>();
```

3f — Migration erzeugen:

```bash
dotnet ef migrations add AiSettingsAndFailures --project src/NutriTrack.Infrastructure --startup-project src/NutriTrack.Api
```

Läuft `dotnet ef` nicht, erst `dotnet tool install --global dotnet-ef` (oder `dotnet tool restore`, falls ein Manifest existiert). Prüfe anschließend, dass unter `src/NutriTrack.Infrastructure/Migrations/` eine neue Datei liegt, die **beide** Tabellen anlegt — und dass sie keine bestehende Tabelle verändert.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --nologo`
Expected: PASS — 202 bisherige plus 3 neue Tests.

- [ ] **Step 5: Commit**

```bash
git add src/NutriTrack.Domain/Entities/AiSettings.cs src/NutriTrack.Domain/Entities/AiFailure.cs src/NutriTrack.Infrastructure/ tests/NutriTrack.Api.Tests/AiSettingsStorageTests.cs
git commit -F - <<'MSG'
Give the AI connection a place to keep its settings

Zwei Tabellen, noch ohne Leser: AiSettings mit genau einer Zeile (Id = 1) und
AiFailures fuer die letzten Fehlschlaege.

Jede Spalte in AiSettings ist nullbar, und null heisst nicht "leer", sondern
"nimm die Umgebungsvariable". Das ist die Notbremse fuer den Fall, dass sich
jemand ueber die Oberflaeche verstellt: Wert loeschen statt in der Datenbank
hantieren.

AiFailure traegt bewusst KEINEN Esstext. Die Tabelle entsteht, weil die Diagnose
zweier Ausfaelle am 2026-09-15 Stunden am Betriebsrechner kostete - sie darf
aber nicht zum Nebeneingang werden, auf dem Mahlzeitentexte doch irgendwo
landen.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

### Task 2: AiSettingsProvider

Das Bindeglied zwischen Tabelle und `GeminiService`: ein Singleton mit Cache, das für jeden fehlenden Wert auf `IConfiguration` zurückfällt und bei einem Datenbankfehler nicht die Erfassung mitreißt.

**Files:**
- Create: `src/NutriTrack.Api/Services/AiSettingsProvider.cs`
- Modify: `src/NutriTrack.Api/Program.cs` (Registrierung bei den übrigen `AddSingleton`, Zeile ~98)
- Test: `tests/NutriTrack.Api.Tests/AiSettingsProviderTests.cs`

**Interfaces:**
- Consumes: `AiSettings`, `AppDbContext.AiSettings` (Task 1).
- Produces:
  - `AiSettingsSnapshot` (record) mit `Model`, `ModelFromDb`, `ThinkingLevel`, `ThinkingLevelFromDb`, `MaxOutputTokens`, `MaxOutputTokensFromDb`
  - `AiSettingsProvider.Read() → AiSettingsSnapshot`
  - `AiSettingsProvider.Invalidate()`
  - Die Vorgabewerte `AiSettingsProvider.DefaultModel = "gemini-3.6-flash"`, `DefaultThinkingLevel = "minimal"`, `DefaultMaxOutputTokens = 4096`

- [ ] **Step 1: Write the failing test**

Datei `tests/NutriTrack.Api.Tests/AiSettingsProviderTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Services;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests;

public class AiSettingsProviderTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    private AiSettingsProvider Provider()
    {
        factory.CreateClient();
        return factory.Services.GetRequiredService<AiSettingsProvider>();
    }

    private async Task SpeichereAsync(string? model, string? level, int? maxTokens)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var vorhandene = await db.AiSettings.SingleOrDefaultAsync();
        if (vorhandene is not null)
            db.AiSettings.Remove(vorhandene);
        await db.SaveChangesAsync();

        db.AiSettings.Add(new AiSettings
        {
            Id = 1, Model = model, ThinkingLevel = level, MaxOutputTokens = maxTokens,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        Provider().Invalidate();
    }

    private async Task LeereAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vorhandene = await db.AiSettings.SingleOrDefaultAsync();
        if (vorhandene is not null)
        {
            db.AiSettings.Remove(vorhandene);
            await db.SaveChangesAsync();
        }

        Provider().Invalidate();
    }

    [Fact]
    public async Task Read_WithoutRow_UsesConfiguration()
    {
        await LeereAsync();

        var snapshot = Provider().Read();

        // Die Testfactory setzt Gemini:Model auf gemini-3.6-flash (NutriTrackApiFactory).
        Assert.Equal("gemini-3.6-flash", snapshot.Model);
        Assert.False(snapshot.ModelFromDb);
        Assert.False(snapshot.ThinkingLevelFromDb);
        Assert.False(snapshot.MaxOutputTokensFromDb);
    }

    [Fact]
    public async Task Read_WithStoredValues_PrefersThem()
    {
        await SpeichereAsync("gemini-3.1-flash-lite", "low", 2048);

        var snapshot = Provider().Read();

        Assert.Equal("gemini-3.1-flash-lite", snapshot.Model);
        Assert.True(snapshot.ModelFromDb);
        Assert.Equal("low", snapshot.ThinkingLevel);
        Assert.True(snapshot.ThinkingLevelFromDb);
        Assert.Equal(2048, snapshot.MaxOutputTokens);
        Assert.True(snapshot.MaxOutputTokensFromDb);
    }

    [Fact]
    public async Task Read_WithPartialRow_FallsBackPerField()
    {
        // Nur die Denkstufe ist gesetzt: das Modell muss weiter aus der Umgebung kommen. Ein
        // Rueckfall, der nur ganz oder gar nicht greift, macht die Zeile zum Alles-oder-nichts.
        await SpeichereAsync(null, "low", null);

        var snapshot = Provider().Read();

        Assert.Equal("gemini-3.6-flash", snapshot.Model);
        Assert.False(snapshot.ModelFromDb);
        Assert.Equal("low", snapshot.ThinkingLevel);
        Assert.True(snapshot.ThinkingLevelFromDb);
    }

    [Fact]
    public async Task Read_WithWhitespaceModel_FallsBackToConfiguration()
    {
        // Ein Feld, das der Nutzer leergeraeumt hat, kommt als "" oder "   " an. Das ist die
        // Aussage "nimm wieder die Umgebung", nicht ein Modell ohne Namen.
        await SpeichereAsync("   ", null, null);

        var snapshot = Provider().Read();

        Assert.Equal("gemini-3.6-flash", snapshot.Model);
        Assert.False(snapshot.ModelFromDb);
    }

    [Fact]
    public async Task Read_WithBrokenDatabase_FallsBackToConfiguration()
    {
        // Der Spec verlangt: eine kaputte Nebensache reisst die Haupterfassung nicht mit. Die
        // Tabelle wird in einer EIGENEN Factory geworfen, damit die uebrigen Tests ihre Datenbank
        // unbeschaedigt behalten - dieselbe Technik wie in AiEndpointTests.
        using var eigene = new NutriTrackApiFactory();
        await eigene.ResetDatabaseAsync();
        eigene.CreateClient();

        using (var scope = eigene.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE AiSettings");
        }

        var provider = eigene.Services.GetRequiredService<AiSettingsProvider>();
        provider.Invalidate();

        var snapshot = provider.Read();

        Assert.Equal("gemini-3.6-flash", snapshot.Model);
        Assert.False(snapshot.ModelFromDb);
    }

    [Fact]
    public async Task Read_IsCachedUntilInvalidated()
    {
        await SpeichereAsync("gemini-3.1-flash-lite", null, null);
        var provider = Provider();
        Assert.Equal("gemini-3.1-flash-lite", provider.Read().Model);

        // Direkt an der Datenbank vorbei am Provider aendern: ohne Invalidate darf er den alten
        // Wert liefern - genau das ist der Zweck des Caches.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var zeile = await db.AiSettings.SingleAsync();
            zeile.Model = "gemini-3.6-flash";
            await db.SaveChangesAsync();
        }

        Assert.Equal("gemini-3.1-flash-lite", provider.Read().Model);

        provider.Invalidate();
        Assert.Equal("gemini-3.6-flash", provider.Read().Model);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --nologo --filter AiSettingsProviderTests`
Expected: FAIL — `AiSettingsProvider` existiert nicht (CS0246).

- [ ] **Step 3: Write minimal implementation**

Datei `src/NutriTrack.Api/Services/AiSettingsProvider.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Services;

/// <summary>
/// Die geltenden Werte samt ihrer Herkunft. Die Herkunft reist mit, weil sie in der Oberflaeche
/// den Unterschied macht zwischen "der Wert steht so da" und "der Wert kommt aus der .env" - ohne
/// sie raetselt der Betreiber, warum sein Eintrag nicht wirkt.
/// </summary>
public sealed record AiSettingsSnapshot(
    string Model, bool ModelFromDb,
    string ThinkingLevel, bool ThinkingLevelFromDb,
    int MaxOutputTokens, bool MaxOutputTokensFromDb);

/// <summary>
/// Liest die zur Laufzeit verstellbaren Werte und faellt je Feld auf die Konfiguration zurueck.
///
/// Singleton mit Cache, weil GeminiService je Anfrage entsteht: ein Datenbankzugriff vor jedem
/// KI-Aufruf waere reine Verschwendung fuer drei Werte, die sich im Monat vielleicht einmal
/// aendern. Verworfen wird der Cache beim Speichern, nicht nach Ablauf einer Frist - es gibt
/// genau einen Schreiber, und der sitzt im selben Prozess.
/// </summary>
public class AiSettingsProvider(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<AiSettingsProvider> logger)
{
    /// <summary>Gilt, wenn weder Datenbank noch Umgebung etwas sagen. Begruendung der Werte
    /// steht bei ihrer Verwendung in GeminiService.</summary>
    public const string DefaultModel = "gemini-3.6-flash";
    public const string DefaultThinkingLevel = "minimal";
    public const int DefaultMaxOutputTokens = 4096;

    private readonly object _gate = new();
    private AiSettings? _cached;
    private bool _geladen;

    public AiSettingsSnapshot Read()
    {
        var gespeichert = Gespeicherte();

        var model = Text(gespeichert?.Model);
        var level = Text(gespeichert?.ThinkingLevel);
        var tokens = gespeichert?.MaxOutputTokens;

        var modelWert = model ?? (configuration["Gemini:Model"] is { Length: > 0 } m ? m : DefaultModel);
        var levelWert = level ?? (configuration["Gemini:ThinkingLevel"] is { Length: > 0 } l ? l : DefaultThinkingLevel);
        var tokenWert = tokens ?? configuration.GetValue("Gemini:MaxOutputTokens", DefaultMaxOutputTokens);

        return new AiSettingsSnapshot(
            modelWert, model is not null,
            levelWert, level is not null,
            tokenWert, tokens is not null);
    }

    /// <summary>Nach jedem Schreiben aufzurufen. Ohne das gilt der alte Wert bis zum Neustart.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
            _geladen = false;
        }
    }

    private AiSettings? Gespeicherte()
    {
        lock (_gate)
        {
            if (_geladen)
                return _cached;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                _cached = db.AiSettings.AsNoTracking().SingleOrDefault();
            }
            catch (Exception ex)
            {
                // Eine kaputte Nebensache darf die Haupterfassung nicht mitreissen - dieselbe
                // Haltung wie bei LoadHistoryAsync im AiMealAssistant. Ohne Zeile im Log waere
                // der Rueckfall auf die Umgebung allerdings unsichtbar, und dann sucht jemand
                // stundenlang, warum sein gespeichertes Modell nicht wirkt.
                logger.LogWarning(ex, "AiSettings nicht lesbar; es gelten die Werte aus der Konfiguration.");
                _cached = null;
            }

            _geladen = true;
            return _cached;
        }
    }

    /// <summary>Leer oder nur Leerzeichen heisst "nimm die Umgebung", nicht "leerer Wert".</summary>
    private static string? Text(string? wert) => string.IsNullOrWhiteSpace(wert) ? null : wert.Trim();
}
```

Registrierung in `src/NutriTrack.Api/Program.cs` bei den übrigen `AddSingleton`-Zeilen (~98):

```csharp
builder.Services.AddSingleton<AiSettingsProvider>();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --nologo`
Expected: PASS — alle bisherigen plus 6 neue Tests.

- [ ] **Step 5: Commit**

```bash
git add src/NutriTrack.Api/Services/AiSettingsProvider.cs src/NutriTrack.Api/Program.cs tests/NutriTrack.Api.Tests/AiSettingsProviderTests.cs
git commit -F - <<'MSG'
Read the AI settings from the database, fall back to the environment

Der Provider liest die eine Zeile aus AiSettings und faellt JE FELD auf die
Konfiguration zurueck. Je Feld und nicht als Ganzes: sonst waere die Zeile ein
Alles-oder-nichts, und wer nur die Denkstufe umstellen will, muesste das Modell
mit abschreiben.

Ein leergeraeumtes Feld kommt als "" oder "   " an und bedeutet "nimm wieder die
Umgebung" - nicht "Modell ohne Namen". Deshalb wird getrimmt statt auf null
geprueft.

Singleton mit Cache, weil GeminiService je Anfrage entsteht und drei Werte, die
sich im Monat einmal aendern, keinen Datenbankzugriff vor jedem KI-Aufruf wert
sind. Gelesen wird ueber einen eigenen Scope: ein Singleton darf den scoped
AppDbContext nicht halten.

Faellt der Lesezugriff aus, gelten die Werte der Umgebung und es entsteht eine
Logzeile. Stumm zurueckzufallen waere schlimmer als der Ausfall selbst - dann
sucht jemand stundenlang, warum sein gespeichertes Modell nicht wirkt.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

### Task 3: GeminiService liest vom Provider

**Files:**
- Modify: `src/NutriTrack.Api/Services/GeminiService.cs` (Konstruktor; `SendAsync` ~Zeile 360-400; neue Methode `ProbeAsync`)
- Test: `tests/NutriTrack.Api.Tests/GeminiServiceTests.cs`

**Interfaces:**
- Consumes: `AiSettingsProvider.Read() → AiSettingsSnapshot` (Task 2).
- Produces:
  - `GeminiProbeResult` (record) mit `int StatusCode`, `long DurationMs`, `string Model`, `string ThinkingLevel`, `string RawBody`
  - `GeminiService.ProbeAsync(CancellationToken ct) → Task<GeminiProbeResult>`

- [ ] **Step 1: Write the failing test**

An `tests/NutriTrack.Api.Tests/GeminiServiceTests.cs` anhängen (innerhalb der Klasse):

```csharp
    [Fact]
    public async Task ParseAsync_UsesStoredSettingsInsteadOfConfiguration()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vorhandene = await db.AiSettings.SingleOrDefaultAsync();
        if (vorhandene is not null)
            db.AiSettings.Remove(vorhandene);
        db.AiSettings.Add(new AiSettings
        {
            Id = 1, Model = "gemini-3.1-flash-lite", ThinkingLevel = "low",
            MaxOutputTokens = 1234, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        factory.Services.GetRequiredService<AiSettingsProvider>().Invalidate();

        string? body = null;
        factory.GeminiResponder = request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubGeminiHandler.Payload("""{"items":[]}""");
        };

        await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None);

        using var sent = JsonDocument.Parse(body!);
        Assert.Equal("gemini-3.1-flash-lite", sent.RootElement.GetProperty("model").GetString());
        var config = sent.RootElement.GetProperty("generation_config");
        Assert.Equal("low", config.GetProperty("thinking_level").GetString());
        Assert.Equal(1234, config.GetProperty("max_output_tokens").GetInt32());

        // Aufraeumen, damit die uebrigen Tests dieser Klasse wieder die Konfiguration sehen.
        db.AiSettings.Remove(await db.AiSettings.SingleAsync());
        await db.SaveChangesAsync();
        factory.Services.GetRequiredService<AiSettingsProvider>().Invalidate();
    }

    [Fact]
    public async Task ProbeAsync_ReturnsStatusDurationAndRawBody()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""{"items":[]}""");

        var result = await Service().ProbeAsync(CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.True(result.DurationMs >= 0);
        Assert.Equal("gemini-3.6-flash", result.Model);
        // Die Rohantwort, nicht der ausgepackte Text: bei einer Diagnose will man den Umschlag
        // sehen, gerade wenn er nicht der erwartete ist.
        Assert.Contains("model_output", result.RawBody);
    }

    [Fact]
    public async Task ProbeAsync_WithErrorStatus_ReportsItInsteadOfThrowing()
    {
        // Eine Probe, die bei einem 429 eine Ausnahme wirft, taugt nicht zur Diagnose: genau
        // dieser Fall ist das, was der Betreiber sehen will.
        factory.GeminiResponder = _ => StubGeminiHandler.InteractionsQuotaFailure();

        var result = await Service().ProbeAsync(CancellationToken.None);

        Assert.Equal(429, result.StatusCode);
        Assert.Contains("quota", result.RawBody, StringComparison.OrdinalIgnoreCase);
    }
```

Ergänze am Kopf der Datei die nötigen `using`-Direktiven: `Microsoft.EntityFrameworkCore`, `NutriTrack.Domain.Entities`, `NutriTrack.Infrastructure.Data`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --nologo --filter GeminiServiceTests`
Expected: FAIL — `ProbeAsync` gibt es nicht (CS1061); der Einstellungstest schlägt fehl, weil noch `IConfiguration` gelesen wird.

- [ ] **Step 3: Write minimal implementation**

3a — Konstruktor von `GeminiService` um den Provider erweitern:

```csharp
public class GeminiService(
    HttpClient httpClient,
    IConfiguration configuration,
    AiSettingsProvider settingsProvider,
    ILogger<GeminiService> logger,
    TimeProvider timeProvider)
```

3b — In `SendAsync` die drei Werte vom Provider holen. Ersetze den Block, der `model`, `thinkingLevel` und `maxOutputTokens` bestimmt, durch:

```csharp
        // Die Werte kommen aus der Verwaltungsoberflaeche, mit Rueckfall auf die Umgebung. Die
        // Begruendungen zu den einzelnen Werten stehen am AiSettingsProvider und in
        // .env.example; hier wird nur noch gelesen.
        var einstellungen = settingsProvider.Read();
        var model = einstellungen.Model;
        var thinkingLevel = einstellungen.ThinkingLevel;
        var maxOutputTokens = einstellungen.MaxOutputTokens;
```

Die bestehenden Erklärkommentare zu `gemini-3.5-flash` („NICHT zurueckstellen…"), zur Denkstufe („Gemessen am echten Dienst…") und zum Ausgabedeckel („OBERGRENZE FUER DIE AUSGABE…") wandern **unverändert** mit nach `AiSettingsProvider` über die drei `Default*`-Konstanten. Sie sind gemessene Erfahrung und dürfen nicht verloren gehen — nur ihr Ort ändert sich.

3c — `ProbeAsync` am Ende der Klasse, vor `ResponseSchema`:

```csharp
    /// <summary>
    /// Ergebnis einer Handprobe: was wirklich zurueckkam, nicht was der Code daraus macht.
    /// </summary>
    public sealed record GeminiProbeResult(
        int StatusCode, long DurationMs, string Model, string ThinkingLevel, string RawBody);

    /// <summary>
    /// EIN Aufruf mit den geltenden Einstellungen und einer FESTEN Beispieleingabe, dessen
    /// Rohantwort zurueckkommt.
    ///
    /// Fest und nicht vom Nutzer gewaehlt aus zwei Gruenden: zwei Laeufe sind nur vergleichbar,
    /// wenn die Eingabe dieselbe ist, und eine Probe mit freiem Text waere ein zweiter Weg, auf
    /// dem Mahlzeitentexte an Google gehen.
    ///
    /// Wirft NICHT bei einem Fehlerstatus. Ein 429 oder ein Schweigen des Dienstes ist genau das,
    /// was der Betreiber hier sehen will - eine Ausnahme wuerde die Diagnose verstecken, um deren
    /// willen es diese Methode gibt.
    /// </summary>
    public async Task<GeminiProbeResult> ProbeAsync(CancellationToken ct)
    {
        var apiKey = configuration["Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw Unavailable("Gemini:ApiKey fehlt.");

        var einstellungen = settingsProvider.Read();
        var start = timeProvider.GetTimestamp();

        var payload = new
        {
            model = einstellungen.Model,
            input = "Nutzer: zwei Broetchen mit Gouda\n",
            system_instruction = SystemInstruction,
            response_format = new
            {
                type = "text",
                mime_type = "application/json",
                schema = ResponseSchema
            },
            generation_config = new
            {
                thinking_level = einstellungen.ThinkingLevel,
                max_output_tokens = einstellungen.MaxOutputTokens
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Headers.Add("Api-Revision", ApiRevision);

        int status;
        string rumpf;
        try
        {
            using var response = await httpClient.SendAsync(request, ct);
            status = (int)response.StatusCode;
            rumpf = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException && !ct.IsCancellationRequested)
        {
            // Status 0 heisst "gar keine Antwort". Genau dieser Fall - Verbindung steht, aber der
            // Dienst schweigt bis zum Zeitdeckel - war am 2026-09-15 die Ursache, und er sieht
            // von innen aus wie ein zu knapp bemessener Deckel.
            status = 0;
            rumpf = ex.Message;
        }

        var dauer = (long)timeProvider.GetElapsedTime(start).TotalMilliseconds;

        // Gekappt, weil eine entgleiste Antwort Megabytes haben kann und niemandem nutzt, der
        // wissen will, WAS zurueckkam.
        if (rumpf.Length > 2000)
            rumpf = rumpf[..2000] + "\n… (gekürzt)";

        return new GeminiProbeResult(status, dauer, einstellungen.Model, einstellungen.ThinkingLevel, rumpf);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --nologo`
Expected: PASS — alle bisherigen plus 3 neue Tests. Achte darauf, dass der bestehende Test `ParseAsync_SendsModelAndSchema` weiter grün ist: er prüft den Vorgabewert, der jetzt über den Provider kommt.

- [ ] **Step 5: Commit**

```bash
git add src/NutriTrack.Api/Services/GeminiService.cs tests/NutriTrack.Api.Tests/GeminiServiceTests.cs
git commit -F - <<'MSG'
Let the stored settings decide, and add a probe that tells the truth

GeminiService liest Modell, Denkstufe und Ausgabedeckel nicht mehr selbst aus
der Konfiguration, sondern fragt den AiSettingsProvider. Die gemessenen
Begruendungen zu den drei Werten wandern mit zu den Vorgabewerten dort - sie
sind Erfahrung aus zwei Ausfaellen und gehoeren dorthin, wo der Wert entsteht.

ProbeAsync macht EINEN Aufruf mit fester Beispieleingabe und gibt zurueck, was
wirklich ankam: Status, Dauer, Rohantwort. Es wirft bewusst NICHT bei einem
Fehlerstatus - ein 429 oder ein schweigender Dienst ist genau das, was der
Betreiber sehen will, und eine Ausnahme wuerde die Diagnose verstecken, um deren
willen es die Methode gibt. Status 0 heisst "gar keine Antwort"; genau dieser
Fall kostete am 2026-09-15 zwei Deckelerhoehungen, bevor jemand mass.

Die Eingabe ist fest und nicht waehlbar: zwei Laeufe sind nur vergleichbar, wenn
sie dieselbe ist, und eine Probe mit freiem Text waere ein zweiter Weg, auf dem
Mahlzeitentexte an Google gehen.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

### Task 4: Fehlschläge protokollieren

Geschrieben wird dort, wo die drei Gemini-Ausnahmen ohnehin schon gefangen werden: in `AiEndpoints`. `GeminiService` bleibt unberührt.

**Files:**
- Create: `src/NutriTrack.Api/Services/AiFailureRecorder.cs`
- Modify: `src/NutriTrack.Api/Endpoints/AiEndpoints.cs` (Stoppuhr um den Aufruf, drei `catch`-Blöcke)
- Modify: `src/NutriTrack.Api/Program.cs` (`AddScoped<AiFailureRecorder>()`)
- Test: `tests/NutriTrack.Api.Tests/AiFailureLogTests.cs`

**Interfaces:**
- Consumes: `AiFailure`, `AppDbContext.AiFailures` (Task 1); `AiSettingsProvider.Read()` (Task 2).
- Produces:
  - `AiFailureRecorder.RecordAsync(string kind, string reason, long durationMs, int? statusCode, CancellationToken ct) → Task`
  - `AiFailureRecorder.MaxEntries = 50`

- [ ] **Step 1: Write the failing test**

Datei `tests/NutriTrack.Api.Tests/AiFailureLogTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Services;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests;

public class AiFailureLogTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    private async Task<List<AiFailure>> ProtokollAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AiFailures.AsNoTracking().OrderByDescending(f => f.OccurredAt).ToListAsync();
    }

    private async Task LeereAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.AiFailures.ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Timeout_IsRecordedWithoutTheMealText()
    {
        await LeereAsync();
        var (client, _, _) = await factory.CreateUserAsync();

        factory.GeminiResponder = _ => throw new TaskCanceledException("Zeitdeckel im Test.");

        // Ein Wort, das ausser in der Mahlzeit nirgends vorkommt: taucht es im Protokoll auf,
        // ist der Esstext hineingeraten. Das ist die tragende Zusage dieses Features.
        await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "Zwetschgendatschi mit Sahne" } }
        });

        var eintrag = Assert.Single(await ProtokollAsync());
        Assert.Equal("Timeout", eintrag.Kind);
        Assert.Equal("gemini-3.6-flash", eintrag.Model);
        Assert.True(eintrag.DurationMs >= 0);
        Assert.DoesNotContain("Zwetschgendatschi", eintrag.Reason);
    }

    [Fact]
    public async Task MalformedResponse_IsRecordedAsSchema()
    {
        await LeereAsync();
        var (client, _, _) = await factory.CreateUserAsync();

        factory.GeminiResponder = _ => StubGeminiHandler.Payload("kein json");

        await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        var protokoll = await ProtokollAsync();
        Assert.Contains(protokoll, f => f.Kind == "Schema");
    }

    [Fact]
    public async Task Quota_IsRecordedWithStatusCode()
    {
        await LeereAsync();
        var (client, _, _) = await factory.CreateUserAsync();

        factory.GeminiResponder = _ => StubGeminiHandler.InteractionsQuotaFailure();

        await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        var eintrag = Assert.Single(await ProtokollAsync());
        Assert.Equal("Quota", eintrag.Kind);
        Assert.Equal(429, eintrag.StatusCode);
    }

    [Fact]
    public async Task Recorder_KeepsOnlyTheNewestEntries()
    {
        await LeereAsync();
        factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<AiFailureRecorder>();

        for (var i = 0; i < AiFailureRecorder.MaxEntries + 5; i++)
            await recorder.RecordAsync("Timeout", $"Fehlschlag {i}", 10, null, CancellationToken.None);

        var protokoll = await ProtokollAsync();
        Assert.Equal(AiFailureRecorder.MaxEntries, protokoll.Count);
        Assert.Contains(protokoll, f => f.Reason.EndsWith($"{AiFailureRecorder.MaxEntries + 4}"));
        Assert.DoesNotContain(protokoll, f => f.Reason == "Fehlschlag 0");
    }

    [Fact]
    public async Task Recorder_TruncatesLongReasons()
    {
        await LeereAsync();
        factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<AiFailureRecorder>();

        await recorder.RecordAsync("Schema", new string('x', 900), 10, null, CancellationToken.None);

        var eintrag = Assert.Single(await ProtokollAsync());
        Assert.True(eintrag.Reason.Length <= 200);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --nologo --filter AiFailureLogTests`
Expected: FAIL — `AiFailureRecorder` existiert nicht (CS0246).

- [ ] **Step 3: Write minimal implementation**

3a — `src/NutriTrack.Api/Services/AiFailureRecorder.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Services;

/// <summary>
/// Schreibt Fehlschlaege der KI-Erfassung mit, damit die Diagnose nicht wieder am
/// Betriebsrechner stattfinden muss - und kappt die Tabelle dabei selbst.
///
/// WAS NIE HINEINGEHOERT: der Esstext. Der Aufrufer uebergibt den Ausnahmetext, und die
/// Ausnahmen dieses Pfades nennen Feldnamen und Pfade, keine Inhalte (siehe
/// GeminiService.DescribeRoot). Kaeme hier je ein Aufrufer mit dem Prompt um die Ecke, waere das
/// ein Rueckschritt hinter die Zusage in .env.example.
/// </summary>
public class AiFailureRecorder(
    AppDbContext db,
    AiSettingsProvider settingsProvider,
    TimeProvider timeProvider,
    ILogger<AiFailureRecorder> logger)
{
    /// <summary>
    /// Ringpuffer statt Aufbewahrungsfrist: eine Tabelle, die nie waechst, braucht keinen
    /// Aufraeumjob und keine Einstellung, die jemand pflegen muss. 50 sind genug, um ein Muster
    /// zu erkennen ("seit dem Modellwechsel jedes Mal Schema"), und wenig genug, um sie ohne
    /// Blaettern zu lesen.
    /// </summary>
    public const int MaxEntries = 50;

    private const int MaxReasonLength = 200;

    public async Task RecordAsync(
        string kind, string reason, long durationMs, int? statusCode, CancellationToken ct)
    {
        var einstellungen = settingsProvider.Read();

        try
        {
            db.AiFailures.Add(new AiFailure
            {
                Id = Guid.NewGuid(),
                OccurredAt = timeProvider.GetUtcNow().UtcDateTime,
                Kind = kind,
                Model = einstellungen.Model,
                ThinkingLevel = einstellungen.ThinkingLevel,
                DurationMs = (int)Math.Min(durationMs, int.MaxValue),
                StatusCode = statusCode,
                Reason = reason.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason
            });

            await db.SaveChangesAsync(ct);

            // Erst schreiben, dann kappen: faellt das Kappen aus, ist der neue Eintrag trotzdem
            // da. Andersherum verloere man im Fehlerfall genau die Zeile, derentwegen jemand
            // nachsieht.
            var zuViele = await db.AiFailures
                .OrderByDescending(f => f.OccurredAt)
                .Skip(MaxEntries)
                .Select(f => f.Id)
                .ToListAsync(ct);

            if (zuViele.Count > 0)
            {
                await db.AiFailures.Where(f => zuViele.Contains(f.Id)).ExecuteDeleteAsync(ct);
            }
        }
        catch (Exception ex)
        {
            // Das Protokoll ist eine Nebensache. Scheitert es, hat der Nutzer bereits einen
            // Fehlschlag vor sich - ihm daraufhin einen zweiten zu zeigen, hilft niemandem.
            logger.LogWarning(ex, "Fehlschlag liess sich nicht protokollieren.");
        }
    }
}
```

3b — In `src/NutriTrack.Api/Program.cs` bei den `AddScoped`-Zeilen:

```csharp
builder.Services.AddScoped<AiFailureRecorder>();
```

3c — `src/NutriTrack.Api/Endpoints/AiEndpoints.cs`: `AiFailureRecorder recorder` in die Parameterliste des Handlers aufnehmen, den Aufruf mit einer Stoppuhr umgeben und in jedem `catch` protokollieren. Der `try`-Block wird zu:

```csharp
            var uhr = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await assistant.ParseAsync(request.Messages, userId, ct);
                return Results.Ok(result);
            }
            catch (GeminiQuotaException ex)
            {
                await recorder.RecordAsync("Quota", ex.Message, uhr.ElapsedMilliseconds, 429, ct);
                return AiQuotaResponse.From(ex, httpResponse);
            }
            catch (GeminiMalformedResponseException ex)
            {
                await recorder.RecordAsync("Schema", ex.Message, uhr.ElapsedMilliseconds, null, ct);

                // Ein Wiederholungsversuch steckt bereits im Assistenten; kommt es hier an,
                // hat auch der zweite Anlauf Unsinn geliefert.
                return AiFailureResponse.From(
                    ex,
                    "Die KI hat unverständlich geantwortet. Formuliere es bitte anders.",
                    StatusCodes.Status502BadGateway);
            }
            catch (GeminiUnavailableException ex)
            {
                // Zeitdeckel und Netzausfall sehen von aussen gleich aus, verlangen aber
                // Verschiedenes: der eine ist eine Frage der Einstellung, der andere nicht.
                var art = ex.Detail.Contains("rechtzeitig", StringComparison.OrdinalIgnoreCase)
                    ? "Timeout"
                    : "Unavailable";
                await recorder.RecordAsync(art, ex.Detail, uhr.ElapsedMilliseconds, null, ct);

                return AiFailureResponse.From(
                    ex,
                    "Die KI ist gerade nicht erreichbar.",
                    StatusCodes.Status503ServiceUnavailable);
            }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --nologo`
Expected: PASS — alle bisherigen plus 5 neue Tests.

- [ ] **Step 5: Commit**

```bash
git add src/NutriTrack.Api/Services/AiFailureRecorder.cs src/NutriTrack.Api/Endpoints/AiEndpoints.cs src/NutriTrack.Api/Program.cs tests/NutriTrack.Api.Tests/AiFailureLogTests.cs
git commit -F - <<'MSG'
Write down why the AI failed, never what was eaten

Protokolliert wird dort, wo die drei Gemini-Ausnahmen ohnehin schon gefangen
werden - in AiEndpoints. GeminiService bleibt unberuehrt; er wirft weiter und
weiss nichts von einer Tabelle.

Festgehalten werden Art, Modell, Denkstufe, Dauer und Status. Modell und
Denkstufe ZUM ZEITPUNKT DES FEHLSCHLAGS, nicht die heutigen: der Sinn des
Protokolls ist der Vergleich vorher/nachher, und der ginge sonst verloren.

Der Esstext bleibt draussen. Ein Test legt eine Mahlzeit mit einem Wort an, das
sonst nirgends vorkommt, und prueft, dass es im Protokoll nicht auftaucht.

Ringpuffer von 50 statt Aufbewahrungsfrist: eine Tabelle, die nie waechst,
braucht keinen Aufraeumjob. Gekappt wird NACH dem Schreiben - faellt das Kappen
aus, ist der neue Eintrag trotzdem da; andersherum verloere man genau die Zeile,
derentwegen jemand nachsieht.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

### Task 5: Die vier Admin-Endpunkte

**Files:**
- Create: `src/NutriTrack.Api/Endpoints/AdminEndpoints.cs`
- Create: `src/NutriTrack.Api/Contracts/Admin/AiSettingsResponse.cs` (enthält auch `UpdateAiSettingsRequest`, `AiProbeResponse`, `AiFailureResponse`)
- Modify: `src/NutriTrack.Api/Program.cs` (`app.MapAdminEndpoints();` bei den übrigen Map-Aufrufen)
- Modify: `NutriTrack.Api/.env.example` und `NutriTrack.Api/docker-compose.yml` (`Admin__Email`)
- Test: `tests/NutriTrack.Api.Tests/AdminEndpointTests.cs`

**Interfaces:**
- Consumes: `AiSettingsProvider.Read()`/`Invalidate()` (Task 2); `GeminiService.ProbeAsync` und `GeminiProbeResult` (Task 3); `AiFailureRecorder.MaxEntries` (Task 4).
- Produces: die vier Endpunkte unter `/api/admin`.

- [ ] **Step 1: Write the failing test**

Datei `tests/NutriTrack.Api.Tests/AdminEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Services;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests;

public class AdminEndpointTests
{
    /// <summary>
    /// Eine Factory, die eine bestimmte Adresse zum Administrator macht. Eigene Instanz je Test,
    /// weil Admin:Email beim Start gelesen wird.
    /// </summary>
    private sealed class AdminFactory(string? adminEmail) : NutriTrackApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Admin:Email", adminEmail ?? string.Empty);
        }
    }

    [Fact]
    public async Task Settings_ForNonAdmin_ReturnsNotFound()
    {
        using var f = new AdminFactory("chef@example.com");
        await f.ResetDatabaseAsync();
        var (client, _, _) = await f.CreateUserAsync();

        // 404 und nicht 403: ein 403 bestaetigt, dass es eine Verwaltung gibt.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/failures")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync("/api/admin/settings", new { model = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync("/api/admin/settings/probe", null)).StatusCode);
    }

    [Fact]
    public async Task Settings_WithoutConfiguredAdmin_ReturnsNotFoundForEveryone()
    {
        // Ein vergessener Eintrag darf die Verwaltung nicht fuer alle oeffnen.
        using var f = new AdminFactory(null);
        await f.ResetDatabaseAsync();
        var (client, _, _) = await f.CreateUserAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/settings")).StatusCode);
    }

    [Fact]
    public async Task Settings_ForAdmin_ReturnsValuesAndOrigin()
    {
        using var f = new AdminFactory("chef@example.com");
        await f.ResetDatabaseAsync();
        var (client, _, _) = await f.CreateUserAsync("chef@example.com");

        var response = await client.GetAsync("/api/admin/settings");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("gemini-3.6-flash", json.GetProperty("model").GetString());
        Assert.False(json.GetProperty("modelFromDatabase").GetBoolean());
        // Der Schluessel darf nirgends auftauchen.
        Assert.DoesNotContain("apiKey", json.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Put_StoresValuesAndTakesEffect()
    {
        using var f = new AdminFactory("chef@example.com");
        await f.ResetDatabaseAsync();
        var (client, _, _) = await f.CreateUserAsync("chef@example.com");

        var put = await client.PutAsJsonAsync("/api/admin/settings", new
        {
            model = "gemini-3.1-flash-lite", thinkingLevel = "low", maxOutputTokens = 2048
        });
        put.EnsureSuccessStatusCode();

        var json = await (await client.GetAsync("/api/admin/settings")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gemini-3.1-flash-lite", json.GetProperty("model").GetString());
        Assert.True(json.GetProperty("modelFromDatabase").GetBoolean());

        // Und der Provider sieht es auch - ohne Invalidate haette der Cache den alten Wert.
        Assert.Equal("gemini-3.1-flash-lite", f.Services.GetRequiredService<AiSettingsProvider>().Read().Model);
    }

    [Fact]
    public async Task Put_WithEmptyModel_FallsBackToEnvironment()
    {
        using var f = new AdminFactory("chef@example.com");
        await f.ResetDatabaseAsync();
        var (client, _, _) = await f.CreateUserAsync("chef@example.com");

        await client.PutAsJsonAsync("/api/admin/settings", new { model = "gemini-3.1-flash-lite" });
        await client.PutAsJsonAsync("/api/admin/settings", new { model = "" });

        var json = await (await client.GetAsync("/api/admin/settings")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gemini-3.6-flash", json.GetProperty("model").GetString());
        Assert.False(json.GetProperty("modelFromDatabase").GetBoolean());
    }

    [Fact]
    public async Task Probe_CallsGeminiOnceAndReportsResult()
    {
        using var f = new AdminFactory("chef@example.com");
        await f.ResetDatabaseAsync();
        var (client, _, _) = await f.CreateUserAsync("chef@example.com");

        var aufrufe = 0;
        f.GeminiResponder = _ =>
        {
            aufrufe++;
            return StubGeminiHandler.Payload("""{"items":[]}""");
        };

        var response = await client.PostAsync("/api/admin/settings/probe", null);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, aufrufe);
        Assert.Equal(200, json.GetProperty("statusCode").GetInt32());
        Assert.Equal("gemini-3.6-flash", json.GetProperty("model").GetString());
        Assert.Contains("model_output", json.GetProperty("rawBody").GetString()!);
    }

    [Fact]
    public async Task Failures_ReturnsNewestFirst()
    {
        using var f = new AdminFactory("chef@example.com");
        await f.ResetDatabaseAsync();
        var (client, _, _) = await f.CreateUserAsync("chef@example.com");

        using (var scope = f.Services.CreateScope())
        {
            var recorder = scope.ServiceProvider.GetRequiredService<AiFailureRecorder>();
            await recorder.RecordAsync("Timeout", "erster", 10, null, CancellationToken.None);
            await recorder.RecordAsync("Schema", "zweiter", 20, null, CancellationToken.None);
        }

        var json = await (await client.GetAsync("/api/admin/failures")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetArrayLength() >= 2);
    }
}
```

**Hinweis:** `CreateUserAsync` nimmt heute keine Adresse entgegen (`TestClientExtensions.cs:17` gibt `(Client, Email, UserId)` zurück). Erweitere den Helfer um einen optionalen Parameter `string? email = null`, der statt der erzeugten Zufallsadresse verwendet wird — bestehende Aufrufe bleiben dadurch unverändert.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --nologo --filter AdminEndpointTests`
Expected: FAIL — es gibt keine `/api/admin`-Route, alle Aufrufe liefern 404 … aber der Admin-Test erwartet 200 und schlägt fehl.

- [ ] **Step 3: Write minimal implementation**

3a — `src/NutriTrack.Api/Contracts/Admin/AiSettingsResponse.cs`:

```csharp
namespace NutriTrack.Api.Contracts.Admin;

/// <summary>
/// Die geltenden Werte samt Herkunft. Die Herkunft ist kein Beiwerk: ohne sie raetselt der
/// Betreiber, warum ein eingetragener Wert nicht wirkt - oder warum einer wirkt, den er nie
/// eingetragen hat.
/// KEIN Feld fuer den API-Schluessel. Er steht in der .env und hat in einer Web-Oberflaeche
/// nichts zu suchen, auch nicht lesend.
/// </summary>
public class AiSettingsResponse
{
    public string Model { get; set; } = string.Empty;
    public bool ModelFromDatabase { get; set; }

    public string ThinkingLevel { get; set; } = string.Empty;
    public bool ThinkingLevelFromDatabase { get; set; }

    public int MaxOutputTokens { get; set; }
    public bool MaxOutputTokensFromDatabase { get; set; }
}

/// <summary>
/// Alle Felder optional: ein leeres oder fehlendes Feld heisst "zurueck zur Umgebungsvariable".
/// </summary>
public class UpdateAiSettingsRequest
{
    public string? Model { get; set; }
    public string? ThinkingLevel { get; set; }
    public int? MaxOutputTokens { get; set; }
}

public class AiProbeResponse
{
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public string Model { get; set; } = string.Empty;
    public string ThinkingLevel { get; set; } = string.Empty;
    public string RawBody { get; set; } = string.Empty;
}

public class AiFailureResponse
{
    public DateTime OccurredAt { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string? ThinkingLevel { get; set; }
    public int? DurationMs { get; set; }
    public int? StatusCode { get; set; }
    public string Reason { get; set; } = string.Empty;
}
```

3b — `src/NutriTrack.Api/Endpoints/AdminEndpoints.cs`:

```csharp
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NutriTrack.Api.Contracts.Admin;
using NutriTrack.Api.Services;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization();

        group.MapGet("/settings", (ClaimsPrincipal user, IConfiguration configuration, AiSettingsProvider provider) =>
        {
            if (!IstAdmin(user, configuration))
                return Results.NotFound();

            return Results.Ok(Antwort(provider));
        });

        group.MapPut("/settings", async (
            UpdateAiSettingsRequest request,
            ClaimsPrincipal user,
            IConfiguration configuration,
            AiSettingsProvider provider,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!IstAdmin(user, configuration))
                return Results.NotFound();

            if (request.MaxOutputTokens is { } tokens && (tokens < 256 || tokens > 65536))
                return Results.BadRequest(new { Error = "maxOutputTokens muss zwischen 256 und 65536 liegen." });

            var zeile = await db.AiSettings.SingleOrDefaultAsync(ct);
            if (zeile is null)
            {
                zeile = new AiSettings { Id = 1 };
                db.AiSettings.Add(zeile);
            }

            // Leer heisst "zurueck zur Umgebungsvariable", nicht "leerer Modellname". Deshalb
            // wird auf null normalisiert statt die Eingabe durchzureichen.
            zeile.Model = Leer(request.Model);
            zeile.ThinkingLevel = Leer(request.ThinkingLevel);
            zeile.MaxOutputTokens = request.MaxOutputTokens;
            zeile.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);

            // Ohne das gilt der alte Wert bis zum Neustart - und der Betreiber haelt die
            // Oberflaeche fuer kaputt.
            provider.Invalidate();

            return Results.Ok(Antwort(provider));
        });

        group.MapPost("/settings/probe", async (
            ClaimsPrincipal user,
            IConfiguration configuration,
            GeminiService gemini,
            AiRateLimiter limiter,
            CancellationToken ct) =>
        {
            if (!IstAdmin(user, configuration))
                return Results.NotFound();

            var userId = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;

            // Dieselbe Bremse wie die normale Erfassung: jede Probe verbraucht eine Anfrage aus
            // Googles Minutenkontingent von 20. Am 2026-09-15 brauchte genau so eine Diagnose das
            // Kontingent auf, und die Erfassung sah danach nur noch 429.
            if (!limiter.TryAcquire(userId))
                return Results.Json(
                    new { Error = "Zu viele KI-Anfragen in der letzten Stunde. Versuche es später noch einmal." },
                    statusCode: StatusCodes.Status429TooManyRequests);

            var ergebnis = await gemini.ProbeAsync(ct);

            return Results.Ok(new AiProbeResponse
            {
                StatusCode = ergebnis.StatusCode,
                DurationMs = ergebnis.DurationMs,
                Model = ergebnis.Model,
                ThinkingLevel = ergebnis.ThinkingLevel,
                RawBody = ergebnis.RawBody
            });
        });

        group.MapGet("/failures", async (
            ClaimsPrincipal user, IConfiguration configuration, AppDbContext db, CancellationToken ct) =>
        {
            if (!IstAdmin(user, configuration))
                return Results.NotFound();

            var eintraege = await db.AiFailures
                .AsNoTracking()
                .OrderByDescending(f => f.OccurredAt)
                .Take(AiFailureRecorder.MaxEntries)
                .Select(f => new AiFailureResponse
                {
                    OccurredAt = f.OccurredAt,
                    Kind = f.Kind,
                    Model = f.Model,
                    ThinkingLevel = f.ThinkingLevel,
                    DurationMs = f.DurationMs,
                    StatusCode = f.StatusCode,
                    Reason = f.Reason
                })
                .ToListAsync(ct);

            return Results.Ok(eintraege);
        });
    }

    /// <summary>
    /// Genau ein Administrator, benannt in der Konfiguration. Ist nichts gesetzt, ist NIEMAND
    /// Administrator - ein vergessener Eintrag darf die Verwaltung nicht fuer alle oeffnen.
    /// </summary>
    private static bool IstAdmin(ClaimsPrincipal user, IConfiguration configuration)
    {
        var erlaubt = configuration["Admin:Email"];
        if (string.IsNullOrWhiteSpace(erlaubt))
            return false;

        var email = user.FindFirst(ClaimTypes.Email)?.Value;
        return !string.IsNullOrWhiteSpace(email)
               && string.Equals(email.Trim(), erlaubt.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static AiSettingsResponse Antwort(AiSettingsProvider provider)
    {
        var s = provider.Read();

        return new AiSettingsResponse
        {
            Model = s.Model,
            ModelFromDatabase = s.ModelFromDb,
            ThinkingLevel = s.ThinkingLevel,
            ThinkingLevelFromDatabase = s.ThinkingLevelFromDb,
            MaxOutputTokens = s.MaxOutputTokens,
            MaxOutputTokensFromDatabase = s.MaxOutputTokensFromDb
        };
    }

    private static string? Leer(string? wert) => string.IsNullOrWhiteSpace(wert) ? null : wert.Trim();
}
```

3c — In `Program.cs` bei den übrigen `Map…Endpoints()`-Aufrufen:

```csharp
app.MapAdminEndpoints();
```

3d — `docker-compose.yml` bei den übrigen Umgebungsvariablen:

```yaml
      # Wer die KI-Verwaltung sehen darf. NICHT gesetzt heisst: niemand - ein vergessener
      # Eintrag darf die Seite nicht fuer alle oeffnen. Es gibt bewusst nur einen Administrator;
      # Rollen waeren fuer einen Betreiber viel Maschinerie.
      - Admin__Email=${NUTRITRACK_ADMIN_EMAIL:-}
```

3e — `.env.example` im selben Abschnitt wie die Gemini-Werte:

```
# E-Mail-Adresse des Kontos, das die KI-Verwaltung unter /admin sehen darf (Modell, Denkstufe,
# Ausgabedeckel, Handprobe, Fehlerprotokoll). Leer lassen heisst: NIEMAND kommt hinein, auch du
# nicht - das ist Absicht, damit ein vergessener Eintrag die Seite nicht fuer alle oeffnet.
# Die Adresse muss genau der entsprechen, mit der du dich anmeldest.
NUTRITRACK_ADMIN_EMAIL=
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --nologo`
Expected: PASS — alle bisherigen plus 7 neue Tests.

- [ ] **Step 5: Commit**

```bash
git add src/NutriTrack.Api/Endpoints/AdminEndpoints.cs src/NutriTrack.Api/Contracts/Admin/ src/NutriTrack.Api/Program.cs .env.example docker-compose.yml tests/NutriTrack.Api.Tests/
git commit -F - <<'MSG'
Open the AI settings to exactly one administrator

Vier Endpunkte unter /api/admin: lesen, speichern, Handprobe, Fehlerprotokoll.
Wer nicht der in Admin:Email benannte Nutzer ist, bekommt 404 - nicht 403. Ein
403 bestaetigt, dass es eine Verwaltung gibt; ein 404 sagt nichts. Dieselbe
Haltung wie bei fremden Mahlzeiten.

Ist Admin:Email nicht gesetzt, ist NIEMAND Administrator. Ein vergessener
Eintrag darf die Verwaltung nicht fuer alle oeffnen, und die Registrierung
dieser Anwendung steht jedem offen.

Die Antwort traegt zu jedem Wert seine Herkunft. Ohne sie raetselt der
Betreiber, warum ein eingetragener Wert nicht wirkt - oder warum einer wirkt,
den er nie eingetragen hat. Ein Feld fuer den API-Schluessel gibt es nicht, auch
kein lesendes.

Die Handprobe laeuft durch denselben AiRateLimiter wie die Erfassung: jede
verbraucht eine Anfrage aus Googles Minutenkontingent von 20. Am 2026-09-15
brauchte genau so eine Diagnose das Kontingent auf, und die Erfassung sah danach
nur noch 429.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

### Task 6: Die Verwaltungsseite

**Files:**
- Create: `NutriTrack.Web/src/api/admin.ts`
- Create: `NutriTrack.Web/src/pages/AdminPage.tsx`
- Modify: `NutriTrack.Web/src/App.tsx` (Route `/admin`)
- Modify: `NutriTrack.Web/src/components/Layout.tsx` (Menüpunkt, nur bei Erfolg)
- Modify: `NutriTrack.Web/src/App.css` (Klassen für die Seite)

**Interfaces:**
- Consumes: die vier Endpunkte aus Task 5.
- Produces: nichts, worauf spätere Tasks aufbauen.

- [ ] **Step 1: API-Modul anlegen**

Datei `NutriTrack.Web/src/api/admin.ts`:

```ts
import client from './client';

// Zu jedem Wert seine Herkunft: "aus der Datenbank" heisst, er ueberschreibt die
// Umgebungsvariable. Ohne diese Unterscheidung raetselt man, warum ein Eintrag nicht wirkt.
export interface AiSettings {
  model: string;
  modelFromDatabase: boolean;
  thinkingLevel: string;
  thinkingLevelFromDatabase: boolean;
  maxOutputTokens: number;
  maxOutputTokensFromDatabase: boolean;
}

export interface AiProbeResult {
  statusCode: number;
  durationMs: number;
  model: string;
  thinkingLevel: string;
  rawBody: string;
}

export interface AiFailure {
  occurredAt: string;
  kind: string;
  model: string | null;
  thinkingLevel: string | null;
  durationMs: number | null;
  statusCode: number | null;
  reason: string;
}

// Ein leeres Feld bedeutet "zurueck zur Umgebungsvariable" - deshalb sind alle drei optional.
export interface UpdateAiSettings {
  model?: string;
  thinkingLevel?: string;
  maxOutputTokens?: number;
}

export const adminApi = {
  getSettings: () => client.get<AiSettings>('/api/admin/settings'),
  updateSettings: (data: UpdateAiSettings) => client.put<AiSettings>('/api/admin/settings', data),
  probe: () => client.post<AiProbeResult>('/api/admin/settings/probe'),
  getFailures: () => client.get<AiFailure[]>('/api/admin/failures'),
};
```

- [ ] **Step 2: Seite anlegen**

Datei `NutriTrack.Web/src/pages/AdminPage.tsx`:

```tsx
import { useEffect, useState } from 'react';
import { adminApi, type AiSettings, type AiFailure, type AiProbeResult } from '../api/admin';
import { apiErrorMessage } from '../api/errors';

export default function AdminPage() {
  const [settings, setSettings] = useState<AiSettings | null>(null);
  const [failures, setFailures] = useState<AiFailure[]>([]);
  const [model, setModel] = useState('');
  const [thinkingLevel, setThinkingLevel] = useState('');
  const [maxOutputTokens, setMaxOutputTokens] = useState('');
  const [probe, setProbe] = useState<AiProbeResult | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  // Nur die Felder vorbelegen, die WIRKLICH gespeichert sind. Stuende der Wert aus der Umgebung
  // im Feld, machte das erste Speichern ihn unbemerkt zu einem gespeicherten - und die Notbremse
  // waere weg.
  const uebernehmen = (s: AiSettings) => {
    setSettings(s);
    setModel(s.modelFromDatabase ? s.model : '');
    setThinkingLevel(s.thinkingLevelFromDatabase ? s.thinkingLevel : '');
    setMaxOutputTokens(s.maxOutputTokensFromDatabase ? String(s.maxOutputTokens) : '');
  };

  useEffect(() => {
    adminApi.getSettings().then(res => uebernehmen(res.data)).catch(err => setError(apiErrorMessage(err, 'Einstellungen nicht ladbar.')));
    adminApi.getFailures().then(res => setFailures(res.data)).catch(() => { /* Nebensache */ });
  }, []);

  const speichern = async (e: React.SyntheticEvent) => {
    e.preventDefault();
    setBusy(true);
    setError('');
    try {
      const res = await adminApi.updateSettings({
        model: model.trim() || undefined,
        thinkingLevel: thinkingLevel.trim() || undefined,
        maxOutputTokens: maxOutputTokens.trim() ? Number(maxOutputTokens) : undefined,
      });
      uebernehmen(res.data);
    } catch (err) {
      setError(apiErrorMessage(err, 'Einstellungen nicht ladbar.'));
    } finally {
      setBusy(false);
    }
  };

  const testen = async () => {
    setBusy(true);
    setError('');
    setProbe(null);
    try {
      const res = await adminApi.probe();
      setProbe(res.data);
      const aktuell = await adminApi.getFailures();
      setFailures(aktuell.data);
    } catch (err) {
      setError(apiErrorMessage(err, 'Einstellungen nicht ladbar.'));
    } finally {
      setBusy(false);
    }
  };

  const herkunft = (ausDatenbank: boolean, wert: string | number) =>
    ausDatenbank ? 'gespeichert' : `aus der Umgebung: ${wert}`;

  if (!settings && !error) return <p>Lädt…</p>;

  return (
    <div className="admin-page">
      <h1>KI-Einstellungen</h1>
      {error && <p className="error-msg">{error}</p>}

      {settings && (
        <form onSubmit={speichern} className="admin-form">
          <label>
            Modell
            <input value={model} onChange={e => setModel(e.target.value)} placeholder={settings.model} />
            <small>{herkunft(settings.modelFromDatabase, settings.model)}</small>
          </label>

          <label>
            Denkstufe
            <input
              value={thinkingLevel}
              onChange={e => setThinkingLevel(e.target.value)}
              placeholder={settings.thinkingLevel}
            />
            <small>{herkunft(settings.thinkingLevelFromDatabase, settings.thinkingLevel)}</small>
          </label>

          <label>
            Ausgabe-Token
            <input
              type="number"
              value={maxOutputTokens}
              onChange={e => setMaxOutputTokens(e.target.value)}
              placeholder={String(settings.maxOutputTokens)}
            />
            <small>{herkunft(settings.maxOutputTokensFromDatabase, settings.maxOutputTokens)}</small>
          </label>

          <p className="hint">
            Ein leeres Feld bedeutet: der Wert aus der Umgebung gilt wieder.
          </p>

          <div className="admin-actions">
            <button type="submit" disabled={busy}>Speichern</button>
            <button type="button" onClick={testen} disabled={busy}>Verbindung testen</button>
          </div>
        </form>
      )}

      {probe && (
        <section className="probe-result">
          <h2>Ergebnis der Probe</h2>
          <p>
            {probe.model} · {probe.thinkingLevel} · Status {probe.statusCode === 0 ? 'keine Antwort' : probe.statusCode}
            {' · '}{probe.durationMs} ms
          </p>
          <pre>{probe.rawBody}</pre>
        </section>
      )}

      <section className="failures">
        <h2>Letzte Fehlschläge</h2>
        {failures.length === 0 ? (
          <p>Keine Fehlschläge aufgezeichnet.</p>
        ) : (
          <table>
            <thead>
              <tr><th>Zeitpunkt</th><th>Art</th><th>Modell</th><th>Dauer</th><th>Status</th><th>Grund</th></tr>
            </thead>
            <tbody>
              {failures.map((f, i) => (
                <tr key={i}>
                  <td>{new Date(f.occurredAt).toLocaleString('de-DE')}</td>
                  <td>{f.kind}</td>
                  <td>{f.model ?? '–'}</td>
                  <td>{f.durationMs != null ? `${f.durationMs} ms` : '–'}</td>
                  <td>{f.statusCode ?? '–'}</td>
                  <td className="reason">{f.reason}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}
```

Der Helfer heißt `apiErrorMessage(err, fallback)` (`NutriTrack.Web/src/api/errors.ts:45`) und verlangt beide Argumente — der Rückfalltext wird angezeigt, wenn der Server keinen eigenen liefert.

- [ ] **Step 3: Route und Menüpunkt**

In `NutriTrack.Web/src/App.tsx` bei den geschützten Routen:

```tsx
            <Route path="/admin" element={<AdminPage />} />
```

samt Import. In `NutriTrack.Web/src/components/Layout.tsx` den Menüpunkt nur zeigen, wenn die Verwaltung erreichbar ist:

```tsx
  // Die Sichtbarkeit haengt an derselben 404-Antwort, die auch den Zugriff regelt. Ein zweites
  // Merkmal im Token waere eine zweite Wahrheit darueber, wer Administrator ist - und die beiden
  // liefen frueher oder spaeter auseinander.
  const [istAdmin, setIstAdmin] = useState(false);

  useEffect(() => {
    adminApi.getSettings().then(() => setIstAdmin(true)).catch(() => setIstAdmin(false));
  }, []);
```

und im `<nav>` vor der E-Mail-Anzeige:

```tsx
            {istAdmin && <Link to="/admin">KI</Link>}
```

- [ ] **Step 4: Stil ergänzen**

An `NutriTrack.Web/src/App.css` anhängen. Die Variablen (`--radius`, `--red`, …) sind im Bestand
definiert; halte dich an die dort übliche einzeilige Schreibweise:

```css
/* KI-Verwaltung. Die beiden overflow-Regeln sind kein Beiwerk: ein Ausnahmetext von 200 Zeichen
   und eine entgleiste Rohantwort sprengen sonst auf dem Telefon die Seitenbreite. */
.admin-page h1 { font-size: 1.3rem; margin-bottom: 1rem; }
.admin-page h2 { font-size: 1.05rem; margin: 1.5rem 0 0.6rem; }
.admin-form { display: flex; flex-direction: column; gap: 1rem; max-width: 30rem; }
.admin-form label { display: flex; flex-direction: column; gap: 0.3rem; font-size: 0.9rem; }
.admin-form input { padding: 0.5rem 0.7rem; border-radius: var(--radius); }
.admin-form small { color: var(--muted); font-size: 0.8rem; }
.admin-page .hint { color: var(--muted); font-size: 0.85rem; margin: 0; }
.admin-actions { display: flex; gap: 0.6rem; flex-wrap: wrap; }
.probe-result pre { background: rgba(0,0,0,0.25); padding: 0.8rem; border-radius: var(--radius); font-size: 0.8rem; white-space: pre-wrap; word-break: break-all; max-height: 18rem; overflow-y: auto; }
.failures { overflow-x: auto; }
.failures table { width: 100%; border-collapse: collapse; font-size: 0.85rem; }
.failures th, .failures td { text-align: left; padding: 0.4rem 0.6rem; border-bottom: 1px solid rgba(255,255,255,0.08); white-space: nowrap; }
.failures td.reason { white-space: normal; min-width: 18rem; word-break: break-word; }
```

Prüfe vorher, ob `--muted` im Bestand so heißt; trifft es nicht zu, nimm die Variable, die die
übrigen Seiten für abgeschwächten Text verwenden.

- [ ] **Step 5: Bauen**

Run: `cd NutriTrack.Web && npm run build`
Expected: kein TypeScript-Fehler.

- [ ] **Step 6: Commit**

```bash
git add NutriTrack.Web/src/
git commit -F - <<'MSG'
Put the AI settings where they can actually be changed

Eine Seite unter /admin mit drei Feldern, einem Probe-Knopf und den letzten
Fehlschlaegen. Der Menuepunkt erscheint, wenn GET /api/admin/settings nicht 404
liefert - dieselbe Antwort, die auch den Zugriff regelt. Ein zweites Merkmal im
Token waere eine zweite Wahrheit darueber, wer Administrator ist, und die beiden
liefen frueher oder spaeter auseinander.

Die Felder werden NUR mit wirklich gespeicherten Werten vorbelegt. Stuende der
Wert aus der Umgebung darin, machte das erste Speichern ihn unbemerkt zu einem
gespeicherten - und die Notbremse waere weg. Was aus der Umgebung kommt, steht
als Platzhalter und im Hinweis darunter.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01S6spjgAJhbJtVaD2ksuuFS
MSG
```

---

## Abschluss

- [ ] `dotnet test --nologo` in `NutriTrack.Api` — alles grün
- [ ] `npm run build` in `NutriTrack.Web` — kein TypeScript-Fehler
- [ ] `NUTRITRACK_ADMIN_EMAIL` in der `.env` des Zielrechners eintragen (sonst ist die Seite für niemanden da — das ist Absicht)
- [ ] Beide Repos pushen, `scripts/deploy.sh`
- [ ] Am laufenden Stand: anmelden, Menüpunkt „KI" sichtbar? Probe drücken, Ergebnis plausibel? Ein zweites Konto darf `/admin` nicht sehen und bekommt auf `/api/admin/settings` einen 404.
