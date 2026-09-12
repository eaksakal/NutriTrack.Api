namespace NutriTrack.Api.Services;

public enum Sex { Male, Female }

/// <summary>Aktivitaetsfaktoren nach der ueblichen PAL-Staffelung.</summary>
public enum ActivityLevel { Sedentary, Light, Moderate, Active, VeryActive }

/// <summary>Was der Nutzer erreichen will. Mehr Stufen braucht es nicht.</summary>
public enum GoalDirection { Lose, Hold, Gain }

/// <summary>Wie die Kalorien auf die Makros verteilt werden.</summary>
public enum MacroStyle { Balanced, HighProtein, LowCarb }

public record BodyData(decimal WeightKg, decimal HeightCm, int Age, Sex Sex, ActivityLevel Activity);

public record GoalTargets(
    decimal Calories,
    decimal Protein,
    decimal Carbohydrates,
    decimal Fat,
    decimal BasalMetabolicRate,
    decimal MaintenanceCalories,
    string Explanation);

/// <summary>
/// Rechnet Tagesziele aus Koerperdaten. Bewusst eine Formel und kein Sprachmodell:
/// Mifflin-St Jeor ist seit 1990 veroeffentlicht, nachschlagbar und liefert zweimal dasselbe
/// Ergebnis fuer dieselbe Eingabe. Ein Modell wuerde hier dieselbe Formel aus dem Gedaechtnis
/// paraphrasieren und dabei gelegentlich falsch rechnen - ohne dass es jemand merkt.
///
/// Das Sprachmodell wird an anderer Stelle gebraucht: es liest aus "will abnehmen, aber nicht
/// hungern" die Richtung und die Intensitaet heraus. Das ist Sprachverstehen, und dafuer taugt es.
///
/// Die Werte sind ein Startpunkt und keine Ernaehrungsberatung; die Oberflaeche sagt das auch.
/// </summary>
public static class GoalCalculator
{
    // Mifflin-St Jeor, Am J Clin Nutr 1990;51:241-7. Genauer als Harris-Benedict, besonders bei
    // Uebergewicht, und heute der uebliche Ausgangspunkt.
    public static decimal BasalMetabolicRate(BodyData body) =>
        10m * body.WeightKg
        + 6.25m * body.HeightCm
        - 5m * body.Age
        + (body.Sex == Sex.Male ? 5m : -161m);

    private static decimal ActivityFactor(ActivityLevel level) => level switch
    {
        ActivityLevel.Sedentary => 1.2m,    // Buerojob, kaum Bewegung
        ActivityLevel.Light => 1.375m,      // ein bis drei Einheiten Sport je Woche
        ActivityLevel.Moderate => 1.55m,    // drei bis fuenf Einheiten
        ActivityLevel.Active => 1.725m,     // sechs bis sieben Einheiten
        ActivityLevel.VeryActive => 1.9m,   // koerperliche Arbeit oder zweimal taeglich Training
        _ => 1.2m,
    };

    public static GoalTargets Calculate(
        BodyData body,
        GoalDirection direction,
        MacroStyle style = MacroStyle.Balanced,
        decimal? intensityPercent = null)
    {
        var bmr = BasalMetabolicRate(body);
        var maintenance = bmr * ActivityFactor(body.Activity);

        // Abweichung vom Erhaltungsbedarf. 20 % Defizit sind die uebliche Empfehlung fuer
        // stetige Abnahme ohne Muskelverlust; 10 % Ueberschuss reichen fuer Aufbau, mehr landet
        // groesstenteils im Fettgewebe. Die Spanne ist bewusst eng: wer schneller abnehmen will,
        // soll das nicht ueber eine Zahl steuern, die niemand mehr hinterfragt.
        var abweichung = direction switch
        {
            GoalDirection.Lose => -(Math.Clamp(intensityPercent ?? 20m, 10m, 25m) / 100m),
            GoalDirection.Gain => Math.Clamp(intensityPercent ?? 10m, 5m, 20m) / 100m,
            _ => 0m,
        };

        var kalorien = maintenance * (1m + abweichung);

        // Zwei verschiedene Grenzen, und die Unterscheidung ist wichtig:
        //
        // Der GRUNDUMSATZ ist keine harte Grenze. Bei sitzender Taetigkeit betraegt der
        // Erhaltungsbedarf nur das 1,2-fache des Grundumsatzes - jedes Defizit ab 17 % liegt
        // damit rechnerisch darunter. Wuerde hier angehoben, waere die Intensitaetseinstellung
        // fuer jeden Buerojob wirkungslos, ohne dass es jemand merkt. Also: Wert stehen lassen,
        // aber ausdruecklich benennen.
        //
        // Der ABSOLUTE BODEN ist eine harte Grenze. Darunter wird aus einem Ziel eine
        // Mangelernaehrung, und niemand soll sich so etwas versehentlich aus einem Satz Freitext
        // einhandeln.
        var absoluterBoden = body.Sex == Sex.Male ? 1500m : 1200m;
        var aufBodenAngehoben = kalorien < absoluterBoden;
        if (aufBodenAngehoben)
            kalorien = absoluterBoden;

        var unterGrundumsatz = kalorien < bmr;

        // Protein je Kilogramm Koerpergewicht - der in der Sporternaehrung uebliche Bezug.
        // Im Defizit hoeher, weil Protein die Muskulatur schuetzt und satt macht.
        var proteinJeKg = style switch
        {
            MacroStyle.HighProtein => 2.2m,
            MacroStyle.LowCarb => 2.0m,
            _ => direction == GoalDirection.Lose ? 1.8m : 1.6m,
        };

        var protein = body.WeightKg * proteinJeKg;

        // Fettanteil an den Gesamtkalorien. Unter 20 % leidet auf Dauer der Hormonhaushalt.
        var fettAnteil = style == MacroStyle.LowCarb ? 0.40m : 0.28m;
        var fett = kalorien * fettAnteil / 9m;

        // Kohlenhydrate sind der Rest - sie haben als einzige keinen eigenen Bedarfswert.
        var kohlenhydrate = (kalorien - protein * 4m - fett * 9m) / 4m;

        // Rechnerisch kann der Rest negativ werden (viel Protein, wenig Kalorien, Low Carb).
        // Dann wird am Fett gespart, nicht am Protein: das Fett hat die weichere Untergrenze.
        if (kohlenhydrate < 0m)
        {
            fett = Math.Max((kalorien - protein * 4m) * 0.5m / 9m, 0m);
            kohlenhydrate = Math.Max((kalorien - protein * 4m - fett * 9m) / 4m, 0m);
        }

        var richtungText = direction switch
        {
            GoalDirection.Lose => $"{Math.Abs(abweichung) * 100m:0} % unter dem Erhaltungsbedarf",
            GoalDirection.Gain => $"{abweichung * 100m:0} % über dem Erhaltungsbedarf",
            _ => "auf Höhe des Erhaltungsbedarfs",
        };

        var erklaerung =
            $"Grundumsatz {Math.Round(bmr)} kcal (Mifflin-St Jeor), "
            + $"Erhaltungsbedarf {Math.Round(maintenance)} kcal bei {AktivitaetText(body.Activity)}. "
            + $"Ziel {richtungText}. "
            + $"Protein {proteinJeKg:0.0} g je kg Körpergewicht, Fett {fettAnteil * 100m:0} % der Kalorien, "
            + "Kohlenhydrate der Rest."
            + (aufBodenAngehoben
                ? $" Angehoben auf {absoluterBoden:0} kcal — darunter wird aus einem Ziel eine Mangelernährung."
                : unterGrundumsatz
                    ? $" Hinweis: Das liegt unter deinem Grundumsatz von {Math.Round(bmr)} kcal. Bei sitzender Tätigkeit ist das rechnerisch normal, dauerhaft aber nichts, was man ohne Rücksprache durchziehen sollte."
                    : string.Empty);

        return new GoalTargets(
            Math.Round(kalorien),
            Math.Round(protein),
            Math.Round(kohlenhydrate),
            Math.Round(fett),
            Math.Round(bmr),
            Math.Round(maintenance),
            erklaerung);
    }

    private static string AktivitaetText(ActivityLevel level) => level switch
    {
        ActivityLevel.Sedentary => "sitzender Tätigkeit",
        ActivityLevel.Light => "leichter Aktivität",
        ActivityLevel.Moderate => "mittlerer Aktivität",
        ActivityLevel.Active => "hoher Aktivität",
        ActivityLevel.VeryActive => "sehr hoher Aktivität",
        _ => "sitzender Tätigkeit",
    };
}
