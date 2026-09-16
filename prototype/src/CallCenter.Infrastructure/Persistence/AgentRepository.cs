using CallCenter.Application.Abstractions;
using CallCenter.Domain.Agents;
using Microsoft.EntityFrameworkCore;

namespace CallCenter.Infrastructure.Persistence;

public class AgentRepository : IAgentRepository
{
    private readonly CallCenterDbContext _dbContext;

    public AgentRepository(CallCenterDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Agent?> FindAsync(Guid agentId, CancellationToken cancellationToken) =>
        _dbContext.Agents.FirstOrDefaultAsync(agent => agent.Id == agentId, cancellationToken);

    public async Task<IReadOnlyList<Agent>> ListAllAsync(CancellationToken cancellationToken) =>
        await _dbContext.Agents
            .OrderBy(agent => agent.Name)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Agent>> ListAvailableLongestWaitingFirstAsync(
        CancellationToken cancellationToken) =>
        await _dbContext.Agents
            .Where(agent => agent.State == AgentState.Available)
            .OrderBy(agent => agent.StateChangedAt)
            .ToListAsync(cancellationToken);

    public void Add(Agent agent) => _dbContext.Agents.Add(agent);
}
