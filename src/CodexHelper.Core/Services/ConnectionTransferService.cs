using System.Security.Cryptography;
using System.Text.Json;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;
using CodexHelper.Core.Security;

namespace CodexHelper.Core.Services;

public sealed class ConnectionTransferService
{
    public const string Category = "connection";
    private readonly BundleService bundles;
    private readonly OfficialAccountService accounts;
    private readonly ApiProviderService providers;

    public ConnectionTransferService(AppPaths paths, OfficialAccountService accounts, ApiProviderService providers)
    {
        bundles = new BundleService(paths);
        this.accounts = accounts;
        this.providers = providers;
    }

    public async Task<BundleManifest> ExportAsync(
        string destinationPath,
        string password,
        IReadOnlyList<BundleExportItem> fileItems,
        IReadOnlyCollection<string>? selectedProfileIds = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var profiles = accounts.LoadIndex().Profiles
            .Where(profile => selectedProfileIds is null || selectedProfileIds.Contains(profile.Id, StringComparer.Ordinal))
            .ToList();
        var virtualFiles = new List<BundleVirtualFile>();
        try
        {
            foreach (var profile in profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] secret = profile.Kind == ConnectionKind.OfficialAccount
                    ? accounts.ExportDecryptedProfileForBundle(profile.Id)
                    : providers.ExportSecretBytesForBundle(profile.Id);
                try
                {
                    var transfer = new TransferProfile
                    {
                        Label = profile.Label,
                        Kind = profile.Kind,
                        BaseUrl = profile.BaseUrl,
                        Model = profile.Model,
                        Secret = secret
                    };
                    var plain = JsonSerializer.SerializeToUtf8Bytes(transfer);
                    try
                    {
                        var encrypted = PortableSecretEnvelope.Encrypt(plain, password);
                        virtualFiles.Add(new BundleVirtualFile(
                            "connection-" + profile.Id,
                            Category,
                            profile.Label,
                            "profile.chsecret",
                            encrypted,
                            DateTime.UtcNow));
                    }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                finally { CryptographicOperations.ZeroMemory(secret); }
            }

            return await bundles.ExportAsync(
                new BundleExportRequest(destinationPath, password, fileItems, virtualFiles),
                progress,
                cancellationToken);
        }
        catch
        {
            foreach (var file in virtualFiles) CryptographicOperations.ZeroMemory(file.Content);
            throw;
        }
    }

    public async Task<int> ImportAsync(string bundlePath, string password, CancellationToken cancellationToken = default)
    {
        var contents = await bundles.ReadVirtualFilesAsync(bundlePath, password, Category, cancellationToken);
        var imported = 0;
        try
        {
            foreach (var content in contents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plain = PortableSecretEnvelope.Decrypt(content.Content, password);
                try
                {
                    var profile = JsonSerializer.Deserialize<TransferProfile>(plain)
                        ?? throw new InvalidDataException("连接档案内容无效。");
                    if (profile.Secret.Length == 0) throw new InvalidDataException("连接档案缺少凭据。");
                    try
                    {
                        if (profile.Kind == ConnectionKind.OfficialAccount)
                            accounts.ImportDecryptedProfile(profile.Label, profile.Secret);
                        else if (profile.Kind is ConnectionKind.CustomApi or ConnectionKind.Sub2Api or ConnectionKind.ResponsesSubagent)
                            providers.ImportDecryptedProfile(profile.Label, profile.Kind, profile.BaseUrl, profile.Model, profile.Secret);
                        else
                            throw new InvalidDataException("连接档案类型无效。");
                        imported++;
                    }
                    finally { CryptographicOperations.ZeroMemory(profile.Secret); }
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            return imported;
        }
        finally
        {
            foreach (var content in contents) CryptographicOperations.ZeroMemory(content.Content);
        }
    }

    private sealed class TransferProfile
    {
        public string Label { get; set; } = string.Empty;
        public ConnectionKind Kind { get; set; }
        public string BaseUrl { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public byte[] Secret { get; set; } = Array.Empty<byte>();
    }
}
