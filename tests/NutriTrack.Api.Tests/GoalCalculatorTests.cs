using NutriTrack.Api.Services;

namespace NutriTrack.Api.Tests;

/// <summary>
/// Reine Rechenlogik, also reine Rechentests - kein TestServer, keine Datenbank. Die Zahlen sind
/// von Hand gegen die veroeffentlichte Formel nachgerechnet und nicht aus einem Lauf uebernommen:
/// ein Test, der nur festhaelt, was der Code heute tut, findet nie einen Fehler.
/// </summary>
public class GoalCalculatorTests
{
    private static readonly BodyData Mann = new(82m, 180m, 35, Sex.Male, ActivityLevel.Sedentary);
    private static readonly BodyData Frau = new(65m, 168m, 30, Sex.Female, ActivityLevel.Moderate);

    [Fact]
    public void BasalMetabolicRate_Mann_EntsprichtDerFormel()
    {
        // 10*82 + 6.25*180 - 5*35 + 5 = 820 + 1125 - 175 + 5 = 1775
        Assert.Equal(1775m, GoalCalculator.BasalMetabolicRate(Mann));
    }

    [Fact]
    public void BasalMetabolicRate_Frau_ZiehtDieAndereKonstanteAb()
    {
        // 10*65 + 6.25*168 - 5*30 - 161 = 650 + 1050 - 150 - 161 = 1389
        Assert.Equal(1389m, GoalCalculator.BasalMetabolicRate(Frau));
    }

    [Fact]
    public void Calculate_Halten_LiefertDenErhaltungsbedarf()
    {
        var ziele = GoalCalculator.Calculate(Mann, GoalDirection.Hold);

        // 1775 * 1.2 = 2130
        Assert.Equal(2130m, ziele.MaintenanceCalories);
        Assert.Equal(2130m, ziele.Calories);
    }

    [Fact]
    public void Calculate_Abnehmen_ZiehtZwanzigProzentAb()
    {
        var ziele = GoalCalculator.Calculate(Mann, GoalDirection.Lose);

        // 2130 * 0.8 = 1704
        Assert.Equal(1704m, ziele.Calories);
        // 82 kg * 1.8 g = 147.6 -> 148
        Assert.Equal(148m, ziele.Protein);
    }

    /// <summary>
    /// Bei sitzender Taetigkeit ist der Erhaltungsbedarf nur das 1,2-fache des Grundumsatzes -
    /// jedes Defizit ab 17 % liegt rechnerisch darunter. Der Wert wird deshalb NICHT angehoben
    /// (sonst waere die Intensitaet fuer jeden Buerojob wirkungslos), sondern benannt.
    /// </summary>
    [Fact]
    public void Calculate_DefizitUnterGrundumsatz_BleibtStehenUndWirdBenannt()
    {
        var ziele = GoalCalculator.Calculate(Mann, GoalDirection.Lose);

        Assert.True(ziele.Calories < ziele.BasalMetabolicRate);
        Assert.Contains("unter deinem Grundumsatz", ziele.Explanation);
    }

    [Fact]
    public void Calculate_Zunehmen_SchlaegtZehnProzentAuf()
    {
        var ziele = GoalCalculator.Calculate(Mann, GoalDirection.Gain);

        // 2130 * 1.1 = 2343
        Assert.Equal(2343m, ziele.Calories);
        // Im Aufbau weniger Protein je Kilo als im Defizit: 82 * 1.6 = 131.2 -> 131
        Assert.Equal(131m, ziele.Protein);
    }

    [Theory]
    [InlineData(ActivityLevel.Sedentary, 2130)]
    [InlineData(ActivityLevel.Light, 2440)]     // 1775 * 1.375 = 2440.625 -> 2441? siehe unten
    [InlineData(ActivityLevel.Moderate, 2751)]  // 1775 * 1.55  = 2751.25
    [InlineData(ActivityLevel.Active, 3062)]    // 1775 * 1.725 = 3061.875
    [InlineData(ActivityLevel.VeryActive, 3373)]// 1775 * 1.9   = 3372.5
    public void Calculate_Aktivitaet_SkaliertDenErhaltungsbedarf(ActivityLevel stufe, int erwartet)
    {
        var ziele = GoalCalculator.Calculate(Mann with { Activity = stufe }, GoalDirection.Hold);

        // Rundung auf ganze Kalorien, deshalb eine Kalorie Toleranz gegen Banker's Rounding.
        Assert.InRange(ziele.MaintenanceCalories, erwartet - 1, erwartet + 1);
    }

    [Fact]
    public void Calculate_MakrosErgebenWiederDieKalorien()
    {
        var ziele = GoalCalculator.Calculate(Frau, GoalDirection.Lose);

        var ausMakros = ziele.Protein * 4m + ziele.Carbohydrates * 4m + ziele.Fat * 9m;

        // Rundung auf ganze Gramm erlaubt eine kleine Abweichung; mehr als zehn Kalorien waeren
        // ein Rechenfehler und keine Rundung.
        Assert.InRange(ausMakros, ziele.Calories - 10m, ziele.Calories + 10m);
    }

    /// <summary>
    /// Der wichtigste Test hier: aus einem Satz Freitext darf niemand ein Ziel bekommen, das in
    /// eine Mangelernaehrung fuehrt. Diese Grenze ist hart, anders als der Grundumsatz.
    /// </summary>
    [Fact]
    public void Calculate_ExtremesDefizit_FaelltNichtUnterDenAbsolutenBoden()
    {
        var zierlich = new BodyData(48m, 155m, 60, Sex.Female, ActivityLevel.Sedentary);

        var ziele = GoalCalculator.Calculate(zierlich, GoalDirection.Lose, intensityPercent: 25m);

        Assert.True(ziele.Calories >= 1200m,
            $"Ziel {ziele.Calories} lag unter dem absoluten Boden von 1200 kcal.");
        Assert.Contains("Mangelernährung", ziele.Explanation);
    }

    [Fact]
    public void Calculate_Intensitaet_WirdAufVernuenftigeGrenzenGekappt()
    {
        var uebertrieben = GoalCalculator.Calculate(Mann, GoalDirection.Lose, intensityPercent: 80m);
        var maximal = GoalCalculator.Calculate(Mann, GoalDirection.Lose, intensityPercent: 25m);

        // 80 % Defizit darf nicht durchschlagen - gekappt bei 25 %.
        Assert.Equal(maximal.Calories, uebertrieben.Calories);
    }

    [Fact]
    public void Calculate_LowCarb_VerschiebtFettUndKohlenhydrate()
    {
        var ausgewogen = GoalCalculator.Calculate(Mann, GoalDirection.Hold);
        var lowCarb = GoalCalculator.Calculate(Mann, GoalDirection.Hold, MacroStyle.LowCarb);

        Assert.Equal(ausgewogen.Calories, lowCarb.Calories);
        Assert.True(lowCarb.Fat > ausgewogen.Fat);
        Assert.True(lowCarb.Carbohydrates < ausgewogen.Carbohydrates);
    }

    [Fact]
    public void Calculate_HighProtein_HebtNurDasProtein()
    {
        var ausgewogen = GoalCalculator.Calculate(Mann, GoalDirection.Hold);
        var proteinbetont = GoalCalculator.Calculate(Mann, GoalDirection.Hold, MacroStyle.HighProtein);

        // 82 * 2.2 = 180.4 -> 180 gegen 82 * 1.6 = 131.2 -> 131
        Assert.Equal(180m, proteinbetont.Protein);
        Assert.True(proteinbetont.Protein > ausgewogen.Protein);
    }

    [Fact]
    public void Calculate_AlleWerteSindPositiv()
    {
        foreach (var richtung in Enum.GetValues<GoalDirection>())
        foreach (var stil in Enum.GetValues<MacroStyle>())
        {
            var ziele = GoalCalculator.Calculate(Frau, richtung, stil);

            Assert.True(ziele.Calories > 0, $"{richtung}/{stil}: Kalorien {ziele.Calories}");
            Assert.True(ziele.Protein > 0, $"{richtung}/{stil}: Protein {ziele.Protein}");
            Assert.True(ziele.Carbohydrates >= 0, $"{richtung}/{stil}: KH {ziele.Carbohydrates}");
            Assert.True(ziele.Fat > 0, $"{richtung}/{stil}: Fett {ziele.Fat}");
        }
    }
}
