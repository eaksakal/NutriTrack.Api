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
    public const string Ollama = "ollama";

    /// <summary>Alle Anbieter in der Reihenfolge der Rueckfallkette (siehe AiProviderChain):
    /// zuerst das eigene Heimnetz, dann der kostenlose Vermittler, zuletzt Google mit dem
    /// knappsten Kontingent.</summary>
    public static readonly IReadOnlyList<string> All = [Ollama, OpenRouter, Gemini];

    /// <summary>"gemini", "openrouter" oder "ollama". Wandert ins Fehlerprotokoll, damit nach
    /// einem Wechsel feststeht, welcher Dienst welchen Fehlschlag verursacht hat.</summary>
    string Name { get; }

    /// <summary>Der Konfigurationsschluessel, der diesem Anbieter fehlt ("Gemini:ApiKey",
    /// "OpenRouter:ApiKey", "Ollama:BaseUrl") - null, wenn er eingerichtet ist. Eine Eigenschaft
    /// statt einer Fallunterscheidung an jeder Aufrufstelle: ohne sie stuende dieselbe Abfrage
    /// gleichlautend in AiEndpoints, AdminEndpoints UND GoalsEndpoints - und ein weiterer Anbieter
    /// muesste an drei Stellen zugleich nachgezogen werden statt an einer. Fragt die Antwort
    /// selbst ab statt nur den Namen zu nennen, weil "eingerichtet" nicht ueberall "Schluessel
    /// gesetzt" heisst: Ollama braucht eine Adresse, keinen Schluessel.</summary>
    string? MissingSetting { get; }

    Task<AiParseResult> ParseAsync(
        IReadOnlyList<ChatMessage> messages, string historyBlock, CancellationToken ct);

    Task<AiWishResult> ParseWishAsync(string wish, CancellationToken ct);

    Task<AiProbeResult> ProbeAsync(CancellationToken ct);
}
