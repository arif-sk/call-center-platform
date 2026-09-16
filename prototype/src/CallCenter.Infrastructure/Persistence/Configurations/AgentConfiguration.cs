using CallCenter.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CallCenter.Infrastructure.Persistence.Configurations;

public class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("Agents");
        builder.HasKey(agent => agent.Id);

        builder.Property(agent => agent.Name).HasMaxLength(120).IsRequired();
        builder.Property(agent => agent.Extension).HasMaxLength(10).IsRequired();

        // Stored as text: a support engineer reading this table at 2am should not have to look up
        // what state 3 means.
        builder.Property(agent => agent.State).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Milliseconds is plenty for call timing, and saves two bytes a row against the default.
        builder.Property(agent => agent.StateChangedAt).HasColumnType("datetimeoffset(3)");
    }
}
