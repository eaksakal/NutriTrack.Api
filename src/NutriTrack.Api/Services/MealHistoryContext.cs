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
