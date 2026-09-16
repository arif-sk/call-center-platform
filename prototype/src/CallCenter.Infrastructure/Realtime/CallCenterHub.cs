using Microsoft.AspNetCore.SignalR;

namespace CallCenter.Infrastructure.Realtime;

/// <summary>
/// Push, not polling. The server sends a "snapshot" message to every connected screen whenever
/// anything changes, so an incoming call appears on the agent's desktop without them refreshing.
///
/// There are no methods here because clients never tell the hub anything — they send commands
/// over HTTP and receive the result on this connection.
/// </summary>
public class CallCenterHub : Hub
{
}
