using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace CodexHelper.Core.Security;

public static class CryptoEnvelope
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CHENC001");
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> key, ReadOnlySpan<byte> associatedData = default)
    {
        ValidateKey(key);
        var result = new byte[Magic.Length + sizeof(int) + NonceSize + TagSize + plain.Length];
        Magic.CopyTo(result, 0);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(Magic.Length, sizeof(int)), 1);
        var nonce = result.AsSpan(Magic.Length + sizeof(int), NonceSize);
        var tag = result.AsSpan(Magic.Length + sizeof(int) + NonceSize, TagSize);
        var cipher = result.AsSpan(Magic.Length + sizeof(int) + NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag, associatedData);
        return result;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key, ReadOnlySpan<byte> associatedData = default)
    {
        ValidateKey(key);
        var minimum = Magic.Length + sizeof(int) + NonceSize + TagSize;
        if (envelope.Length < minimum || !envelope[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("加密数据头无效。");
        var version = BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(Magic.Length, sizeof(int)));
        if (version != 1) throw new NotSupportedException($"不支持的加密数据版本：{version}");
        var nonce = envelope.Slice(Magic.Length + sizeof(int), NonceSize);
        var tag = envelope.Slice(Magic.Length + sizeof(int) + NonceSize, TagSize);
        var cipher = envelope[(Magic.Length + sizeof(int) + NonceSize + TagSize)..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain, associatedData);
        return plain;
    }

    public static byte[] DerivePortableKey(string password, ReadOnlySpan<byte> salt, int iterations, int memoryKiB, int parallelism)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 10)
            throw new ArgumentException("迁移口令至少需要 10 个字符。", nameof(password));
        if (iterations is < 1 or > 20 || memoryKiB is < 16 * 1024 or > 1024 * 1024 || parallelism is < 1 or > 16)
            throw new InvalidDataException("迁移包 Argon2id 参数无效。");
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt.ToArray(),
            Iterations = iterations,
            MemorySize = memoryKiB,
            DegreeOfParallelism = parallelism
        };
        return argon2.GetBytes(32);
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw new ArgumentException("AES-256 密钥必须为 32 字节。", nameof(key));
    }
}
