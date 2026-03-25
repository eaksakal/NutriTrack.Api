using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NutriTrack.Domain.Entities;

namespace NutriTrack.Infrastructure.Data.Configurations;

public class FoodItemConfiguration : IEntityTypeConfiguration<FoodItem>
{
    public void Configure(EntityTypeBuilder<FoodItem> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Name).HasMaxLength(500).IsRequired();
        builder.Property(f => f.Brand).HasMaxLength(200);
        builder.Property(f => f.Barcode).HasMaxLength(50);
        builder.Property(f => f.OpenFoodFactsId).HasMaxLength(100);

        builder.Property(f => f.Calories).HasPrecision(10, 2);
        builder.Property(f => f.Protein).HasPrecision(10, 2);
        builder.Property(f => f.Carbohydrates).HasPrecision(10, 2);
        builder.Property(f => f.Fat).HasPrecision(10, 2);
        builder.Property(f => f.Fiber).HasPrecision(10, 2);
        builder.Property(f => f.Sugar).HasPrecision(10, 2);
        builder.Property(f => f.SaturatedFat).HasPrecision(10, 2);
        builder.Property(f => f.Sodium).HasPrecision(10, 2);

        builder.HasIndex(f => f.Barcode);
        builder.HasIndex(f => f.Name);
    }
}
