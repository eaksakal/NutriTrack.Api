namespace NutriTrack.Api.Services;

/// <summary>
/// Haelt die Suchanfragen an OpenFoodFacts unter dem veroeffentlichten Limit.
///
/// OpenFoodFacts erlaubt 10 Suchen je Minute und IP-Adresse
/// (openfoodfacts.github.io/openfoodfacts-server/api/, Abschnitt Rate limits). Wer darueber geht,
/// bekommt 503 zurueck - genau das, was diese Anwendung am 2026-09-12 im Betrieb gesehen hat - und
/// bei Wiederholung eine Sperre der IP. Der ganze Server sitzt hinter EINER IP, also muss die
/// Bremse global sein und nicht je Nutzer: deshalb ein Singleton und kein Wert je Konto.
///
/// Absichtlich 8 statt 10: der Abstand faengt ab, dass Aufrufe im Netz ein paar hundert
/// Millisekunden versetzt bei OpenFoodFacts ankommen und dort in ein anderes Minutenfenster
/// fallen als hier. Ein Kontingent zu verschenken ist billiger als eine gesperrte IP.
///
/// Der Barcode-Weg (15/min) laeuft bewusst NICHT durch diese Bremse: er hat ein eigenes,
/// hoeheres Limit, und die Suche darf ihn nicht mit aufbrauchen.
/// </summary>
public class OpenFoodFactsThrottle(TimeProvider timeProvider, ILogger<OpenFoodFactsThrottle> logger)
{
    private const int MaxSearchesPerWindow = 8;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Queue<DateTimeOffset> _searches = new();

    /// <summary>
    /// Nimmt ein Kontingent, wenn eines frei ist. Kein Warten: der Aufrufer soll sofort auf
    /// Schaetzwerte ausweichen oder dem Nutzer Bescheid geben, statt eine Anfrage minutenlang
    /// haengen zu lassen.
    /// </summary>
    public bool TryAcquireSearch()
    {
        var now = timeProvider.GetUtcNow();

        lock (_searches)
        {
            while (_searches.Count > 0 && now - _searches.Peek() > Window)
                _searches.Dequeue();

            if (_searches.Count >= MaxSearchesPerWindow)
            {
                logger.LogInformation(
                    "Suchkontingent fuer OpenFoodFacts erschoepft ({Max} je {Seconds} s); Aufruf wird nicht gestellt.",
                    MaxSearchesPerWindow, Window.TotalSeconds);
                return false;
            }

            _searches.Enqueue(now);
            return true;
        }
    }
}
