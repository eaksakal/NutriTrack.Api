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
    /// <summary>Gilt, wenn weder Datenbank noch Umgebung etwas sagen.</summary>

    // NICHT auf gemini-3.5-flash zurueckstellen. Das Modell steht zwar weiterhin in
    // /v1beta/models, ist ueber /v1beta/interactions aber tot: gemessen am 2026-09-15 vom
    // Betriebsrechner schickt Google darauf ueber 50 s KEIN EINZIGES BYTE - kein 404, kein
    // 400, nur Schweigen, bis der Zeitdeckel zuschlaegt. Derselbe Rumpf gegen
    // gemini-3.6-flash: 200 nach 2,9 s. Das sah wie ein zu knapper Deckel aus und kostete
    // zwei Erhoehungen (15 -> 25 -> 45 s), bevor jemand die Antwortzeit wirklich MASS.
    // 3.7 und 3.8 scheiden aus: sie lehnen thinking_level=minimal ab.
    public const string DefaultModel = "gemini-3.6-flash";

    // Gemessen am echten Dienst (gemini-3.5-flash, 2026-09-12, gleiche Eingabe):
    //   Standard  8-15 s, 859 Denk-Token, 1185 Token gesamt  (riss den Zeitdeckel)
    //   low        5,3 s, 637 Denk-Token,  843 Token gesamt
    //   minimal    3,0 s,   0 Denk-Token,  210 Token gesamt
    // Gleiche Qualitaet bei einem Fuenftel der Token - deshalb minimal. Konfigurierbar, weil
    // nicht jedes Modell dieselben Stufen kennt (minimal/low/medium/high).
    public const string DefaultThinkingLevel = "minimal";

    // OBERGRENZE FUER DIE AUSGABE. Ohne sie schreibt ein entgleistes Modell, bis der
    // Zeitdeckel zuschlaegt. Am 2026-09-15 im Betrieb beobachtet: estimate.sugar kam mit
    // ueber 9000 Ziffern zurueck (JsonException "too large for a Decimal"), und mehrere
    // Anfragen liefen dabei in die vollen 45 s. Mit Deckel bricht derselbe Fall nach wenigen
    // Sekunden ab - wichtig vor allem, weil der zweite Anlauf in AiMealAssistant sonst gar
    // nicht mehr stattfindet: zwei Laeufe a 45 s sprengen jede Geduld.
    // 4096 ist reichlich bemessen: eine normale Antwort mit zwei Posten misst rund 600 Token,
    // die erlaubten 20 Posten liegen bei etwa 1700. Der Deckel soll Entgleisungen fangen,
    // nicht lange Mahlzeiten.
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
