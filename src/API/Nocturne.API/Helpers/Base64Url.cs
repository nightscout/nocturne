using System.Buffers;

namespace Nocturne.API.Helpers;

/// <summary>
/// Single-pass Base64URL encoding and decoding, avoiding the triple-allocation
/// <c>.Replace("+", "-").Replace("/", "_").TrimEnd('=')</c> pattern.
/// </summary>
public static class Base64Url
{
    /// <summary>
    /// Encodes bytes to a Base64URL string (no padding) in a single pass.
    /// </summary>
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        var base64 = Convert.ToBase64String(bytes);
        return string.Create(GetEncodedLength(base64), base64, static (span, b64) =>
        {
            var written = 0;
            for (var i = 0; i < b64.Length; i++)
            {
                var c = b64[i];
                if (c == '=') break;
                span[written++] = c switch
                {
                    '+' => '-',
                    '/' => '_',
                    _ => c
                };
            }
        });
    }

    /// <summary>
    /// Decodes a Base64URL string back to bytes, restoring standard Base64
    /// characters and padding in a single pass.
    /// </summary>
    public static byte[] Decode(ReadOnlySpan<char> base64Url)
    {
        var paddingNeeded = (4 - base64Url.Length % 4) % 4;
        var totalLength = base64Url.Length + paddingNeeded;

        char[]? rented = null;
        Span<char> buffer = totalLength <= 256
            ? stackalloc char[256]
            : (rented = ArrayPool<char>.Shared.Rent(totalLength));

        try
        {
            var target = buffer[..totalLength];
            for (var i = 0; i < base64Url.Length; i++)
            {
                target[i] = base64Url[i] switch
                {
                    '-' => '+',
                    '_' => '/',
                    var c => c
                };
            }
            for (var i = base64Url.Length; i < totalLength; i++)
                target[i] = '=';

            return Convert.FromBase64CharArray(target.ToArray(), 0, totalLength);
        }
        finally
        {
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static int GetEncodedLength(string base64)
    {
        var len = base64.Length;
        while (len > 0 && base64[len - 1] == '=') len--;
        return len;
    }
}
