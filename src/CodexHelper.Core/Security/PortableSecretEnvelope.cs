using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CodexHelper.Core.Security;

/// <summary>
/// Small, independently encrypted secret stored inside an already encrypted
/// migration bundle. This prevents plaintext credentials from reaching the
/// temporary ZIP used while importing a bundle.
/// </summary>
public static class PortableSecretEnvelope
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CHSEC001");
    private const int HeaderLength = 8 + sizeof(int) * 5 + 16;
    private const int Version = 1;
    private const int Iterations = 3;
    private const int MemoryKiB = 64 * 1024;
    private const int Parallelism = 2;
    private const int SaltLength = 16;

    public static byte[] Encrypt(ReadOnlySpan<byte> plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var key = CryptoEnvelope.DerivePortableKey(password, salt, Iterations, MemoryKiB, Parallelism);
        try
        {
            var cipher = CryptoEnvelope.Encrypt(plain, key, Magic);
            try
            {
                var result = new byte[HeaderLength + cipher.Length];
                Magic.CopyTo(result, 0);
                var offset = Magic.Length;
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, 4), Version); offset += 4;
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, 4), Iterations); offset += 4;
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, 4), MemoryKiB); offset += 4;
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, 4), Parallelism); offset += 4;
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, 4), SaltLength); offset += 4;
                salt.CopyTo(result, offset);
                cipher.CopyTo(result, HeaderLength);
                return result;
            }
            finally { CryptographicOperations.ZeroMemory(cipher); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> envelope, string password)
    {
        if (envelope.Length <= HeaderLength || !envelope[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("连接档案加密头无效。");
        var offset = Magic.Length;
        var version = BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(offset, 4)); offset += 4;
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(offset, 4)); offset += 4;
        var memoryKiB = BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(offset, 4)); offset += 4;
        var parallelism = BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(offset, 4)); offset += 4;
        var saltLength = BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(offset, 4)); offset += 4;
        if (version != Version || saltLength != SaltLength || envelope.Length <= offset + saltLength)
            throw new InvalidDataException("不支持的连接档案加密格式。");
        var salt = envelope.Slice(offset, saltLength);
        var key = CryptoEnvelope.DerivePortableKey(password, salt, iterations, memoryKiB, parallelism);
        try { return CryptoEnvelope.Decrypt(envelope[(offset + saltLength)..], key, Magic); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
