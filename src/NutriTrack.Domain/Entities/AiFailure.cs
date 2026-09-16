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
