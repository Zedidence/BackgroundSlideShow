using System.Security.Cryptography;
using System.Text;

namespace BackgroundSlideShow.Services;

/// <summary>
/// Process-stable string hashing for filename derivation. <see cref="string.GetHashCode"/>
/// is randomized per process since .NET Core, so any temp file derived from it gets a
/// different name on every app restart and stale files accumulate forever. Use this
/// helper instead whenever a hash needs to survive across process boundaries.
/// </summary>
internal static class StableHash
{
    /// <summary>Returns the first 8 hex chars of MD5(value), safe for filenames.</summary>
    public static string Short(string value)
    {
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(Encoding.UTF8.GetBytes(value), hash);
        return Convert.ToHexString(hash[..4]);
    }
}
