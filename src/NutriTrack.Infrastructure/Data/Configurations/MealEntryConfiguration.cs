using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NutriTrack.Domain.Entities;

namespace NutriTrack.Infrastructure.Data.Configurations;

public class MealEntryConfiguration : IEntityTypeConfiguration<MealEntry>
{
    public void Configure(EntityTypeBuilder<MealEntry> builder)
    {
        builder.HasKey(m => m.Id);

        builder.Property(m => m.QuantityInGrams).HasColumnType(SqliteConventions.DecimalColumnType);
        builder.Property(m => m.MealType).HasConversion<string>().HasMaxLength(20);

        // HasMaxLength(450) bleibt, damit der Schluessel deckungsgleich zu AspNetUsers.Id ist;
        // SQLite erzwingt die Laenge nicht, der Index unten setzt sie aber voraus.
        builder.Property(m => m.UserId).HasMaxLength(450).IsRequired();

        // Date/Time bleiben DateOnly/TimeOnly: der SQLite-Provider mappt sie seit EF Core 6
        // nativ auf TEXT in 'yyyy-MM-dd' bzw. 'HH:mm:ss.fffffff'. Beide Formate sind
        // nullengepolstert und damit lexikografisch sortierbar - Where(m.Date == x) und
        // OrderBy(m.Time) in MealEndpoints uebersetzen und sortieren korrekt. Der einzige
        // Bruch waere ein per Hand eingefuegter Satz in abweichendem Format.
        builder.Property(m => m.CreatedAt).HasConversion(SqliteConventions.UtcDateTime);

        builder.HasOne(m => m.FoodItem)
            .WithMany(f => f.MealEntries)
            .HasForeignKey(m => m.FoodItemId);

        builder.HasIndex(m => new { m.UserId, m.Date });
    }
}
