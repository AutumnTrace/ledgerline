using System.Buffers.Binary;
using System.Buffers.Text;
using Ledgerline.Domain;

namespace Ledgerline.Application.Queries;

/// <summary>
/// Opaque keyset cursor: the id of the last posting on the previous page, base64url-encoded.
/// Clients treat it as a token; the server can change its shape without breaking them.
/// </summary>
public static class StatementCursor
{
    private const byte Version = 1;

    public static string Encode(long lastPostingId)
    {
        Span<byte> bytes = stackalloc byte[9];
        bytes[0] = Version;
        BinaryPrimitives.WriteInt64BigEndian(bytes[1..], lastPostingId);
        return Base64Url.EncodeToString(bytes);
    }

    public static long Decode(string cursor)
    {
        Span<byte> bytes = stackalloc byte[12];
        if (cursor.Length is 0 or > 16
            || !Base64Url.TryDecodeFromChars(cursor, bytes, out var written)
            || written != 9
            || bytes[0] != Version)
        {
            throw new LedgerValidationException("cursor", "The cursor is not valid. Use the nextCursor value from the previous page.");
        }

        var id = BinaryPrimitives.ReadInt64BigEndian(bytes[1..9]);
        return id > 0 ? id : throw new LedgerValidationException("cursor", "The cursor is not valid.");
    }
}
