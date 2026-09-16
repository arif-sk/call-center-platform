using CallCenter.Domain.Calls;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CallCenter.Infrastructure.Persistence.Configurations;

public class CallConfiguration : IEntityTypeConfiguration<Call>
{
    public void Configure(EntityTypeBuilder<Call> builder)
    {
        builder.ToTable("Calls");
        builder.HasKey(call => call.Id);

        builder.Property(call => call.FromNumber).HasMaxLength(32).IsRequired();
        builder.Property(call => call.ToNumber).HasMaxLength(32).IsRequired();
        builder.Property(call => call.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(call => call.AgentName).HasMaxLength(120);
        builder.Property(call => call.Disposition).HasMaxLength(60);
        builder.Property(call => call.Notes).HasMaxLength(2000);

        builder.Property(call => call.QueuedAt).HasColumnType("datetimeoffset(3)");
        builder.Property(call => call.AnsweredAt).HasColumnType("datetimeoffset(3)");
        builder.Property(call => call.EndedAt).HasColumnType("datetimeoffset(3)");

        // "Oldest waiting call first" is the query routing runs constantly, so it is the index.
        builder.HasIndex(call => new { call.Status, call.QueuedAt });
    }
}
