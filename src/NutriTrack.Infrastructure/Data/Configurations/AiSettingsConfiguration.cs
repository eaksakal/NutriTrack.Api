using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NutriTrack.Domain.Entities;

namespace NutriTrack.Infrastructure.Data.Configurations;

public class AiSettingsConfiguration : IEntityTypeConfiguration<AiSettings>
{
    public void Configure(EntityTypeBuilder<AiSettings> builder)
    {
        builder.HasKey(s => s.Id);

        // Kein ValueGeneratedOnAdd: die Id ist keine laufende Nummer, sondern die feste Eins der
        // einen Zeile. Ein Autowert lockte dazu, eine zweite anzulegen.
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.Model).HasMaxLength(100);
        builder.Property(s => s.ThinkingLevel).HasMaxLength(20);
    }
}
