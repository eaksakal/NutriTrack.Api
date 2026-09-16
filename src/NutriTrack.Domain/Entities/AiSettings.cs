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

    public DateTime UpdatedAt { get; set; }
}
