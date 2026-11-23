using CsCheck;
using Ledgerline.Application.Queries;
using Ledgerline.Domain;

namespace Ledgerline.Tests.Application;

public sealed class StatementCursorTests
{
    [Fact]
    public void Cursors_round_trip_for_any_posting_id() =>
        Gen.Long[1, long.MaxValue].Sample(id => Assert.Equal(id, StatementCursor.Decode(StatementCursor.Encode(id))));

    [Fact]
    public void Cursors_are_opaque_url_safe_tokens()
    {
        var cursor = StatementCursor.Encode(1234567);

        Assert.Matches("^[A-Za-z0-9_-]+$", cursor);
        Assert.DoesNotContain("1234567", cursor, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("AQAAAAAAAAAA")]
    [InlineData("AgAAAAAAAAAB")]
    [InlineData("AQAAAAAAAAABAAAA")]
    public void Garbage_is_rejected_as_a_validation_error(string cursor)
    {
        var ex = Assert.Throws<LedgerValidationException>(() => StatementCursor.Decode(cursor));
        Assert.Equal("cursor", ex.Field);
    }
}
