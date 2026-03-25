using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NutriTrack.Domain.Entities;

namespace NutriTrack.Infrastructure.Data.Configurations;

public class MealEntryConfiguration : IEntityTypeConfiguration<MealEntry>
{
    public void Configure(EntityTypeBuilder<MealEntry> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.QuantityInGrams).HasPrecision(10, 2);
        builder.Property(m => m.MealType).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.UserId).HasMaxLength(450).IsRequired();

        builder.HasOne(m => m.FoodItem)
            .WithMany(f => f.MealEntries)
            .HasForeignKey(m => m.FoodItemId);

        builder.HasIndex(m => new { m.UserId, m.Date });
    }
}
