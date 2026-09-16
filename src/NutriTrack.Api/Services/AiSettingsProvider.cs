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
