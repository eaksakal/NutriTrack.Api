using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace NutriTrack.Infrastructure.Data.Configurations;

/// <summary>
/// Gemeinsame Bausteine fuer die Entity-Konfigurationen unter dem SQLite-Provider.
/// </summary>
internal static class SqliteConventions
{
    /// <summary>
    /// SQLite hat keinen decimal-Typ. Der EF-Provider legt decimal deshalb als TEXT ab und
    /// rundet nichts - HasPrecision(10,2) waere hier wirkungslos und nur irrefuehrend, weshalb
    /// die Spalten stattdessen explizit als TEXT deklariert sind.
    ///
    /// Das ist hier unbedenklich, weil KEINE Rechnung in SQL passiert: die beiden Lese-Endpunkte
    /// in MealEndpoints (GET / und GET /summary) holen die Zeilen mit ToListAsync() und bilden
    /// Summen und Umrechnungen danach mit LINQ-to-Objects auf echtem decimal (die Sum(...)-Aufrufe
    /// in DailySummaryResponse sowie Calc/CalcN/CalcMicro in MapToResponse). Aus demselben Grund
    /// vergleicht FindReusableFoodItemAsync die Naehrwerte erst nach ToListAsync() im Speicher -
    /// in SQL waere das ein Stringvergleich, der "0.50" und "0.5" als verschieden werten wuerde.
    /// Es gibt im gesamten Repo kein .Sum()/.OrderBy()/.Where(&gt; x) auf einem decimal innerhalb
    /// einer IQueryable - nur Vergleiche auf UserId, Date, Guid, Barcode und Name/Brand.
    /// (Bewusst ohne Zeilennummern: die gingen beim ersten Umbau von MealEndpoints daneben.)
    ///
    /// Bewusst NICHT HasConversion&lt;double&gt;(): das wuerde zwar SQL-seitige Aggregate
    /// ermoeglichen, tauscht die TEXT-Sortierung aber gegen Gleitkomma-Drift in den
    /// Tagessummen - der teurere Fehler, weil er still ist.
    ///
    /// Wenn spaeter doch in SQL aggregiert werden soll, ist der Weg nicht der Konverter,
    /// sondern eine eigene, in Ganzzahlen (Milligramm) gefuehrte Spalte.
    /// </summary>
    public const string DecimalColumnType = "TEXT";

    /// <summary>
    /// Npgsql gab DateTime-Spalten als Kind=Utc zurueck, SQLite liefert Kind=Unspecified.
    /// System.Text.Json serialisiert Unspecified ohne "Z", das Frontend liest den Wert dann
    /// als Lokalzeit - ein stiller Zeitversatz statt eines Fehlers. Der Konverter haelt die
    /// UTC-Semantik ueber den Round-Trip fest.
    /// </summary>
    public static readonly ValueConverter<DateTime, DateTime> UtcDateTime = new(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
