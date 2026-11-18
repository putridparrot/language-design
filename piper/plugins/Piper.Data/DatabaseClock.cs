namespace Piper.Data;

/// <summary>
/// Times leases by the database's clock (<c>DateTime.UtcNow</c> in a query, which EF Core's providers turn into the
/// database's own: <c>now()</c>, <c>SYSUTCDATETIME()</c>, <c>datetime('now')</c>), the one clock every process sharing
/// the database agrees on; or, once a provider can't translate that, this process's.
/// </summary>
internal sealed class DatabaseClock
{
    private volatile bool _usable = true;

    /// <summary>Runs <paramref name="database"/>, timed by the database's clock, or <paramref name="local"/> if it can't be.</summary>
    public async Task<T> Run<T>(Func<Task<T>> database, Func<Task<T>> local)
    {
        if (_usable)
        {
            try
            {
                return await database();
            }
            catch (InvalidOperationException)
            {
                // EF Core couldn't translate the database's clock for this provider.
                _usable = false;
            }
        }
        return await local();
    }
}
