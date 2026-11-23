namespace Ledgerline.Domain;

public static class LedgerTime
{
    /// <summary>
    /// UTC, truncated to whole microseconds: the precision PostgreSQL stores. Without this, a replayed
    /// result read back from the database would differ from the original response by a few ticks.
    /// </summary>
    public static DateTimeOffset Normalize(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }
}
