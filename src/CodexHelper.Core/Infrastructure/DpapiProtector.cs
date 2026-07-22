using System.Security.Cryptography;
using System.Text;

namespace CodexHelper.Core.Infrastructure;

public static class DpapiProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexHelper-v1-vault");

    public static byte[] Protect(ReadOnlySpan<byte> plain) =>
        ProtectedData.Protect(plain.ToArray(), Entropy, DataProtectionScope.CurrentUser);

    public static byte[] Unprotect(ReadOnlySpan<byte> protectedData) =>
        ProtectedData.Unprotect(protectedData.ToArray(), Entropy, DataProtectionScope.CurrentUser);

    public static byte[] Protect(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> entropy) =>
        ProtectedData.Protect(plain.ToArray(), entropy.ToArray(), DataProtectionScope.CurrentUser);

    public static byte[] Unprotect(ReadOnlySpan<byte> protectedData, ReadOnlySpan<byte> entropy) =>
        ProtectedData.Unprotect(protectedData.ToArray(), entropy.ToArray(), DataProtectionScope.CurrentUser);
}
