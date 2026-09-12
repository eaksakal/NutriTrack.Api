using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NutriTrack.Domain.Entities;

namespace NutriTrack.Infrastructure.Data.Configurations;

public class UserGoalConfiguration : IEntityTypeConfiguration<UserGoal>
{
    public void Configure(EntityTypeBuilder<UserGoal> builder)
    {
        builder.HasKey(g => g.Id);

        builder.Property(g => g.UserId).HasMaxLength(450).IsRequired();

        builder.Property(g => g.CalorieGoal).HasColumnType(SqliteConventions.DecimalColumnType);
        builder.Property(g => g.ProteinGoal).HasColumnType(SqliteConventions.DecimalColumnType);
        builder.Property(g => g.CarbohydrateGoal).HasColumnType(SqliteConventions.DecimalColumnType);
        builder.Property(g => g.FatGoal).HasColumnType(SqliteConventions.DecimalColumnType);

        builder.Property(g => g.CreatedAt).HasConversion(SqliteConventions.UtcDateTime);
        builder.Property(g => g.UpdatedAt).HasConversion(SqliteConventions.UtcDateTime);

        // Genau ein Zielsatz pro Nutzer. Der Unique-Index ist der eigentliche Schutz des
        // Upsert in PUT /api/goals - ein Read-dann-Insert alleine laesst bei zwei parallelen
        // Requests zwei Zeilen entstehen, und GET /api/goals liefert danach willkuerlich eine.
        builder.HasIndex(g => g.UserId).IsUnique();
    }
}
