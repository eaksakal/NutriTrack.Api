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
