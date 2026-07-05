using System.Security.Cryptography;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;

namespace FunctionalWebApi.Security;

/// <summary>
/// PBKDF2‑HMAC‑SHA256 password hasher. The stored format is the
/// <c>pbkdf2‑sha256$iter$base64(salt)$base64(hash)</c> PHC‑like string,
/// base‑encoded, with all param metadata in the prefix. Stored length is
/// constant per user; iteration count is fixed (<see cref="Iterations"/>)
/// and retrieved from the prefix during verification.
/// </summary>
public static class ArgumentPasswordHasher
{
    /// <summary>
    /// Iteration count tuned to comfortably exceed 50 ms of PBKDF2‑HMAC‑SHA256
    /// work on a typical x86_64 server. Stored verbatim in the prefix so the
    /// parameter can be raised in a future migration.
    /// </summary>
    public const int Iterations = 600_000;

    public const int SaltSize = 16;
    public const int HashSize = 32;

    /// <summary>
    /// Length of the literal prefix <c>pbkdf2-sha256$</c> (13 chars + '$').
    /// </summary>
    public const int EncodingHeaderLen = 14;

    /// <summary>
    /// Deterministic salt used when comparing two plaintexts for equality
    /// without storing any hash. The salt being all‑zeros means
    /// <see cref="KeyDerivation.Pbkdf2"/> produces a deterministic digest
    /// for a given input — that's what enables the constant‑time equality test.
    /// </summary>
    internal static readonly byte[] ComparisonSalt = new byte[SaltSize];

    /// <summary>
    /// Hashes the password and returns the
    /// <c>pbkdf2‑sha256$iter$base64(salt)$base64(hash)</c> representation.
    /// The supplied <paramref name="passwordChars"/> is wiped after use.
    /// </summary>
    public static string Hash(char[] passwordChars)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(SaltSize);
        var hashBytes = KeyDerivation.Pbkdf2(
            password:           new string(passwordChars),
            salt:               saltBytes,
            prf:                KeyDerivationPrf.HMACSHA256,
            iterationCount:     Iterations,
            numBytesRequested:  HashSize);

        Array.Clear(passwordChars, 0, passwordChars.Length);

        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(saltBytes)}${Convert.ToBase64String(hashBytes)}";
    }

    /// <summary>
    /// Constant‑time test for two plaintexts being identical. Each input is
    /// hashed with the same deterministic salt and the digests are compared
    /// using <see cref="CryptographicOperations.FixedTimeEquals"/>. Identical
    /// inputs produce identical digests; mismatched inputs produce disjoint
    /// hash bytes that fail the equality test. The work is uniform regardless
    /// of length or mismatched prefix length. Both inputs are wiped after
    /// use.
    /// </summary>
    public static bool AreEqual(char[] a, char[] b)
    {
        if (a is null || b is null) return false;
        try
        {
            var hashA = KeyDerivation.Pbkdf2(
                password:           new string(a),
                salt:               ComparisonSalt,
                prf:                KeyDerivationPrf.HMACSHA256,
                iterationCount:     Iterations,
                numBytesRequested:  HashSize);
            var hashB = KeyDerivation.Pbkdf2(
                password:           new string(b),
                salt:               ComparisonSalt,
                prf:                KeyDerivationPrf.HMACSHA256,
                iterationCount:     Iterations,
                numBytesRequested:  HashSize);

            return CryptographicOperations.FixedTimeEquals(hashA, hashB);
        }
        finally
        {
            Array.Clear(a, 0, a.Length);
            Array.Clear(b, 0, b.Length);
        }
    }

    /// <summary>
    /// Constant‑time compare against an <c>pbkdf2‑sha256$…$…$…</c> prefix.
    /// Returns <c>false</c> for any malformed input. The supplied
    /// <paramref name="passwordChars"/> is wiped after use.
    /// </summary>
    public static bool Verify(char[] passwordChars, string stored)
    {
        if (string.IsNullOrEmpty(stored) || stored.Length < EncodingHeaderLen)
        {
            Array.Clear(passwordChars, 0, passwordChars.Length);
            BurnCpu();
            return false;
        }

        // "pbkdf2-sha256$" is 15 characters; find the '$' that closes it.
        var headerEnd = stored.IndexOf('$', EncodingHeaderLen);
        if (headerEnd <= 10 || headerEnd > EncodingHeaderLen + 8)
        {
            Array.Clear(passwordChars, 0, passwordChars.Length);
            BurnCpu();
            return false;
        }

        if (!int.TryParse(stored.AsSpan(EncodingHeaderLen, headerEnd - EncodingHeaderLen), out var iters) ||
            iters != Iterations)
        {
            Array.Clear(passwordChars, 0, passwordChars.Length);
            BurnCpu();
            return false;
        }

        var secondSep = stored.IndexOf('$', headerEnd + 1);
        if (secondSep < headerEnd + 4 || !TryDecode(stored.AsSpan(headerEnd + 1, secondSep - (headerEnd + 1)), out var salt))
        {
            Array.Clear(passwordChars, 0, passwordChars.Length);
            BurnCpu();
            return false;
        }

        if (!TryDecode(stored.AsSpan(secondSep + 1), out var expected))
        {
            Array.Clear(passwordChars, 0, passwordChars.Length);
            BurnCpu();
            return false;
        }

        if (salt.Length != SaltSize || expected.Length != HashSize)
        {
            Array.Clear(passwordChars, 0, passwordChars.Length);
            BurnCpu();
            return false;
        }

        // The plaintext‑hashing path is always taken so the timing profile is
        // uniform across the two outcomes.
        var actual = KeyDerivation.Pbkdf2(
            password:           new string(passwordChars),
            salt:               salt,
            prf:                KeyDerivationPrf.HMACSHA256,
            iterationCount:     Iterations,
            numBytesRequested:  HashSize);

        Array.Clear(passwordChars, 0, passwordChars.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Equivalents to the value returned from <see cref="Hash"/> but a deterministic,
    /// uniform‑work alternative for branches where we still need to spend CPU but
    /// the hash material is unavailable.
    /// </summary>
    private static void BurnCpu()
    {
        _ = KeyDerivation.Pbkdf2(
            password:           string.Empty,
            salt:               new byte[SaltSize],
            prf:                KeyDerivationPrf.HMACSHA256,
            iterationCount:     Iterations,
            numBytesRequested:  HashSize);
    }

    private static bool TryDecode(ReadOnlySpan<char> slice, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            // `Convert.TryFromBase64Chars` requires a destination Span, which we
            // cannot size here. `Convert.FromBase64String` allocates the exact
            // number of bytes we need; a malformed input throws FormatException.
            bytes = Convert.FromBase64String(slice.ToString());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
