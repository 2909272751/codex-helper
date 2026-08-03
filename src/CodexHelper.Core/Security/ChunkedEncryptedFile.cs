using System.Security.Cryptography;
using System.Text;

namespace CodexHelper.Core.Security;

public static class ChunkedEncryptedFile
{
    private static readonly byte[] BlobMagic = Encoding.ASCII.GetBytes("CHBLOB01");
    private static readonly byte[] BundleMagic = Encoding.ASCII.GetBytes("CHBNDL01");
    private const int Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    public const int DefaultChunkSize = 1024 * 1024;
    public const int PortableKdfIterations = 3;
    public const int PortableKdfMemoryKiB = 64 * 1024;
    public const int PortableKdfParallelism = 2;

    public static async Task EncryptWithKeyAsync(string sourcePath, string destinationPath, ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
    {
        await EncryptCoreAsync(sourcePath, destinationPath, key, BlobMagic, null, cancellationToken);
    }

    public static async Task DecryptWithKeyAsync(string sourcePath, string destinationPath, ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
    {
        await DecryptCoreAsync(sourcePath, destinationPath, key, BlobMagic, null, cancellationToken);
    }

    public static async Task EncryptPortableAsync(string sourcePath, string destinationPath, string password, CancellationToken cancellationToken)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = CryptoEnvelope.DerivePortableKey(password, salt, PortableKdfIterations, PortableKdfMemoryKiB, PortableKdfParallelism);
        try
        {
            await EncryptCoreAsync(sourcePath, destinationPath, key, BundleMagic, salt, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static async Task DecryptPortableAsync(string sourcePath, string destinationPath, string password, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        var magic = reader.ReadBytes(BundleMagic.Length);
        if (!magic.SequenceEqual(BundleMagic)) throw new InvalidDataException("不是有效的 Codex Helper 迁移包。");
        var version = reader.ReadInt32();
        if (version != Version) throw new NotSupportedException($"不支持的迁移包版本：{version}");
        var chunkSize = reader.ReadInt32();
        var iterations = reader.ReadInt32();
        var memoryKiB = reader.ReadInt32();
        var parallelism = reader.ReadInt32();
        var salt = reader.ReadBytes(16);
        if (salt.Length != 16) throw new InvalidDataException("迁移包盐值损坏。");
        if (iterations != PortableKdfIterations || memoryKiB != PortableKdfMemoryKiB || parallelism != PortableKdfParallelism)
            throw new InvalidDataException("迁移包 KDF 参数不受支持。");
        var key = CryptoEnvelope.DerivePortableKey(password, salt, iterations, memoryKiB, parallelism);
        try
        {
            await DecryptChunksAsync(input, destinationPath, key, chunkSize, cancellationToken);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("迁移口令错误或迁移包已经损坏。", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task EncryptCoreAsync(
        string sourcePath,
        string destinationPath,
        ReadOnlyMemory<byte> key,
        byte[] magic,
        byte[]? portableSalt,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(directory);
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, DefaultChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(magic);
        writer.Write(Version);
        writer.Write(DefaultChunkSize);
        if (portableSalt is not null)
        {
            writer.Write(PortableKdfIterations);
            writer.Write(PortableKdfMemoryKiB);
            writer.Write(PortableKdfParallelism);
            writer.Write(portableSalt);
        }

        var plain = new byte[DefaultChunkSize];
        try
        {
            using var aes = new AesGcm(key.Span, TagSize);
            while (true)
            {
                var read = await input.ReadAsync(plain, cancellationToken);
                if (read == 0) break;
                var nonce = RandomNumberGenerator.GetBytes(NonceSize);
                var tag = new byte[TagSize];
                var cipher = new byte[read];
                aes.Encrypt(nonce, plain.AsSpan(0, read), cipher, tag);
                writer.Write(read);
                writer.Write(nonce);
                writer.Write(tag);
                await output.WriteAsync(cipher, cancellationToken);
            }
            writer.Write(0);
            await output.FlushAsync(cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static async Task DecryptCoreAsync(
        string sourcePath,
        string destinationPath,
        ReadOnlyMemory<byte> key,
        byte[] expectedMagic,
        byte[]? ignoredSalt,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        var magic = reader.ReadBytes(expectedMagic.Length);
        if (!magic.SequenceEqual(expectedMagic)) throw new InvalidDataException("加密文件头无效。");
        var version = reader.ReadInt32();
        if (version != Version) throw new NotSupportedException($"不支持的加密文件版本：{version}");
        var chunkSize = reader.ReadInt32();
        await DecryptChunksAsync(input, destinationPath, key, chunkSize, cancellationToken);
    }

    private static async Task DecryptChunksAsync(Stream input, string destinationPath, ReadOnlyMemory<byte> key, int chunkSize, CancellationToken cancellationToken)
    {
        if (chunkSize < 4096 || chunkSize > 64 * 1024 * 1024) throw new InvalidDataException("加密块大小无效。");
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(directory);
        var destinationCreated = false;
        try
        {
            await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, chunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            destinationCreated = true;
            using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
            using var aes = new AesGcm(key.Span, TagSize);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = reader.ReadInt32();
                if (length == 0) break;
                if (length < 0 || length > chunkSize) throw new InvalidDataException("加密块长度无效。");
                var nonce = reader.ReadBytes(NonceSize);
                var tag = reader.ReadBytes(TagSize);
                var cipher = reader.ReadBytes(length);
                if (nonce.Length != NonceSize || tag.Length != TagSize || cipher.Length != length)
                    throw new EndOfStreamException("加密文件被截断。");
                var plain = new byte[length];
                try
                {
                    aes.Decrypt(nonce, cipher, tag, plain);
                    await output.WriteAsync(plain, cancellationToken);
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            await output.FlushAsync(cancellationToken);
        }
        catch
        {
            try { if (destinationCreated && File.Exists(destinationPath)) File.Delete(destinationPath); }
            catch { /* Preserve the original authentication or format failure. */ }
            throw;
        }
    }
}
