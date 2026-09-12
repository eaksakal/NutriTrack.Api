using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NutriTrack.Domain.Entities;

namespace NutriTrack.Infrastructure.Data.Configurations;

public class FoodItemConfiguration : IEntityTypeConfiguration<FoodItem>
{
    public void Configure(EntityTypeBuilder<FoodItem> builder)
    {
        builder.HasKey(f => f.Id);

        // SQLite erzwingt keine Laengen. Die Angaben bleiben trotzdem stehen, weil sie das
        // fachliche Limit dokumentieren und eine spaetere Rueckmigration auf ein RDBMS mit
        // echten VARCHAR-Spalten sonst an ueberlangen OpenFoodFacts-Produktnamen scheitert.
        builder.Property(f => f.Name).HasMaxLength(500).IsRequired();
        builder.Property(f => f.Brand).HasMaxLength(200);
        builder.Property(f => f.Barcode).HasMaxLength(50);
        builder.Property(f => f.OpenFoodFactsId).HasMaxLength(100);

        // Alle Naehrwerte einheitlich als TEXT - Begruendung siehe SqliteConventions.
        // Die sechs Mikronaehrstoffe waren unter Postgres gar nicht konfiguriert; sie stehen
        // jetzt mit in der Liste, damit die Spaltentypen nicht auseinanderlaufen.
        foreach (var nutrient in new[]
                 {
                     nameof(FoodItem.Calories), nameof(FoodItem.Protein), nameof(FoodItem.Carbohydrates),
                     nameof(FoodItem.Fat), nameof(FoodItem.Fiber), nameof(FoodItem.Sugar),
                     nameof(FoodItem.SaturatedFat), nameof(FoodItem.Sodium), nameof(FoodItem.VitaminA),
                     nameof(FoodItem.VitaminC), nameof(FoodItem.VitaminD), nameof(FoodItem.Calcium),
                     nameof(FoodItem.Iron), nameof(FoodItem.Potassium)
                 })
        {
            builder.Property(nutrient).HasColumnType(SqliteConventions.DecimalColumnType);
        }

        builder.Property(f => f.CreatedAt).HasConversion(SqliteConventions.UtcDateTime);
        builder.Property(f => f.UpdatedAt).HasConversion(SqliteConventions.UtcDateTime);

        builder.HasIndex(f => f.Barcode);
        builder.HasIndex(f => f.Name);
    }
}
