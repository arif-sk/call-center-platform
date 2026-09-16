using CallCenter.Application.Abstractions;

namespace CallCenter.Infrastructure.Time;

public class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
