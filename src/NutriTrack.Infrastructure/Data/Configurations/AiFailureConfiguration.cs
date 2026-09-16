using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NutriTrack.Domain.Entities;

namespace NutriTrack.Infrastructure.Data.Configurations;

public class AiFailureConfiguration : IEntityTypeConfiguration<AiFailure>
{
    public void Configure(EntityTypeBuilder<AiFailure> builder)
    {
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Kind).IsRequired().HasMaxLength(20);
        builder.Property(f => f.Model).HasMaxLength(100);
        builder.Property(f => f.ThinkingLevel).HasMaxLength(20);

        // Die Kappung auf 200 passiert beim Schreiben; die Laenge hier haelt fest, dass sie
        // beabsichtigt ist, und verhindert einen Ausnahmetext von Kilobytelaenge in der Datenbank.
        builder.Property(f => f.Reason).IsRequired().HasMaxLength(200);

        // OHNE DIESEN KONVERTER zeigt die Fehlerliste eine um den Zeitzonenversatz verschobene
        // Uhrzeit - in einem Werkzeug, dessen einziger Zweck die Diagnose ist. Begruendung in
        // SqliteConventions.UtcDateTime.
        builder.Property(f => f.OccurredAt).HasConversion(SqliteConventions.UtcDateTime);

        // Die Oberflaeche liest ausschliesslich "die juengsten 50", und der Ringpuffer loescht
        // nach derselben Ordnung. Ohne Index ist das bei jeder Schreiboperation ein Tabellenscan.
        builder.HasIndex(f => f.OccurredAt);
    }
}
