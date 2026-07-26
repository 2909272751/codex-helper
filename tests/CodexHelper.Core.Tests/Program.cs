using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;
using CodexHelper.Core.Security;
using CodexHelper.Core.Services;

namespace CodexHelper.Core.Tests;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Test)> Tests =
    [
        ("路径越界防护", TestPathSafetyAsync),
        ("认证加密往返", TestCryptoEnvelopeAsync),
        ("迁移文件口令与损坏检测", TestPortableEncryptionAsync),
        ("增量快照去重与恢复", TestBackupRepositoryAsync),
        ("批量迁移包预览与导入", TestBundleAsync),
        ("连接档案双层加密迁移", TestConnectionTransferAsync),
        ("旧版 API 工具批量迁移", TestLegacyApiImportAsync),
        ("官方账号保存与安全切换", TestOfficialAccountsAsync),
        ("缺失保险库档案自动清理", TestOrphanedConnectionCleanupAsync),
        ("官方账号 JSON 批量导入导出", TestOfficialJsonTransferAsync),
        ("官方账号额度响应解析", TestOfficialUsageParsingAsync),
        ("API 配置保留与凭据隔离", TestApiProviderSwitchAsync),
        ("TOML 损坏配置阻断", TestTomlValidationAsync)
    ];

    private static async Task<int> Main()
    {
        var failed = 0;
        foreach (var (name, test) in Tests)
        {
            try { await test(); Console.WriteLine("PASS  " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL  " + name + "\n      " + ex); }
        }
        Console.WriteLine($"\n结果：{Tests.Count - failed}/{Tests.Count} 通过");
        return failed == 0 ? 0 : 1;
    }

    private static Task TestPathSafetyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-helper-path-root");
        var safe = PathSafety.CombineWithin(root, Path.Combine("project", "file.txt"));
        Assert(PathSafety.IsWithin(safe, root), "安全路径应位于根目录内");
        AssertThrows<InvalidDataException>(() => PathSafety.CombineWithin(root, Path.Combine("..", "escape.txt")));
        AssertThrows<InvalidOperationException>(() => PathSafety.EnsureRepositoryOutsideSources(Path.Combine(root, "backup"), [root]));
        return Task.CompletedTask;
    }

    private static Task TestCryptoEnvelopeAsync()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var wrong = RandomNumberGenerator.GetBytes(32);
        var plain = Encoding.UTF8.GetBytes("Codex Helper 加密测试");
        try
        {
            var encrypted = CryptoEnvelope.Encrypt(plain, key, "test"u8);
            var restored = CryptoEnvelope.Decrypt(encrypted, key, "test"u8);
            Assert(plain.SequenceEqual(restored), "认证加密往返不一致");
            AssertThrows<CryptographicException>(() => CryptoEnvelope.Decrypt(encrypted, wrong, "test"u8));
            CryptographicOperations.ZeroMemory(restored);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(wrong); CryptographicOperations.ZeroMemory(plain); }
        return Task.CompletedTask;
    }

    private static async Task TestPortableEncryptionAsync()
    {
        await WithTempDirectoryAsync("portable", async root =>
        {
            var source = Path.Combine(root, "source.bin");
            var encrypted = Path.Combine(root, "bundle.chbundle");
            var restored = Path.Combine(root, "restored.bin");
            var content = RandomNumberGenerator.GetBytes(2_200_000);
            await File.WriteAllBytesAsync(source, content);
            await ChunkedEncryptedFile.EncryptPortableAsync(source, encrypted, "correct horse battery", default);
            await ChunkedEncryptedFile.DecryptPortableAsync(encrypted, restored, "correct horse battery", default);
            Assert(content.SequenceEqual(await File.ReadAllBytesAsync(restored)), "分块加密恢复内容不一致");
            await AssertThrowsAsync<InvalidDataException>(() => ChunkedEncryptedFile.DecryptPortableAsync(encrypted, Path.Combine(root, "wrong.bin"), "incorrect password", default));
            CryptographicOperations.ZeroMemory(content);
        });
    }

    private static async Task TestBackupRepositoryAsync()
    {
        await WithTempDirectoryAsync("backup", async root =>
        {
            var source = Path.Combine(root, "source");
            var repositoryPath = Path.Combine(root, "repository");
            var restore = Path.Combine(root, "restore");
            Directory.CreateDirectory(Path.Combine(source, "子目录"));
            await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "alpha");
            await File.WriteAllTextAsync(Path.Combine(source, "子目录", "中文.txt"), "beta");
            var repository = new BackupRepository(repositoryPath);
            var sources = new[] { new BackupSource("project-test", "测试项目", source) };
            var first = await repository.CreateSnapshotAsync("first", sources);
            var second = await repository.CreateSnapshotAsync("second", sources);
            Assert(first.Summary.NewStoredBytes > 0, "首次快照应写入新数据");
            Assert(second.Summary.NewStoredBytes == 0, "未变化快照应完全去重");
            await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "alpha changed");
            var third = await repository.CreateSnapshotAsync("third", sources);
            Assert(third.Summary.NewStoredBytes > 0, "修改文件后应写入新 Blob");
            var restored = await repository.RestoreAsync(new RestoreRequest(first.Summary.Id, restore));
            Assert(restored.Outcome == OperationOutcome.Success, "恢复结果应成功：" + string.Join(" | ", restored.Issues.Select(issue => issue.Item + ": " + issue.Message)));
            Assert(await File.ReadAllTextAsync(Path.Combine(restore, "project-test", "a.txt")) == "alpha", "应恢复首个快照内容");
        });
    }

    private static async Task TestBundleAsync()
    {
        await WithTempDirectoryAsync("bundle", async root =>
        {
            var app = new AppPaths(Path.Combine(root, "app"));
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(Path.Combine(source, "nested"));
            await File.WriteAllTextAsync(Path.Combine(source, "one.txt"), "one");
            await File.WriteAllTextAsync(Path.Combine(source, "nested", "二.txt"), "two");
            var bundle = Path.Combine(root, "transfer.chbundle");
            var service = new BundleService(app);
            var manifest = await service.ExportAsync(new BundleExportRequest(bundle, "portable secret 123", [new BundleExportItem("skills", "skills", "测试 Skills", source)]));
            Assert(manifest.Files.Count == 2, "迁移包应包含两个文件");
            var preview = await service.PreviewAsync(bundle, "portable secret 123");
            Assert(preview.Manifest.Items.Count == 1, "迁移预览项目数量不正确");
            var destination = Path.Combine(root, "imported");
            var result = await service.ImportAsync(new BundleImportRequest(bundle, "portable secret 123", destination));
            Assert(result.Outcome == OperationOutcome.Success, "迁移导入应成功");
            Assert(await File.ReadAllTextAsync(Path.Combine(destination, "skills", "nested", "二.txt")) == "two", "迁移文件内容不正确");
        });
    }

    private static async Task TestOfficialAccountsAsync()
    {
        await WithTempDirectoryAsync("accounts", async root =>
        {
            var codex = Path.Combine(root, "codex");
            Directory.CreateDirectory(codex);
            var app = new AppPaths(Path.Combine(root, "app"));
            app.EnsureCreated();
            var service = new OfficialAccountService(codex, app, new CodexProcessService());
            await File.WriteAllTextAsync(Path.Combine(codex, "auth.json"), Auth("account-A"));
            var a = service.SaveCurrent("账号 A");
            service.PrepareNewLogin();
            await File.WriteAllTextAsync(Path.Combine(codex, "auth.json"), Auth("account-B"));
            var b = service.SaveCurrent("账号 B");
            service.SwitchTo(a.Id);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(codex, "auth.json")));
            Assert(document.RootElement.GetProperty("account_id").GetString() == "account-A", "账号切换未恢复目标凭据");
            Assert(service.LoadIndex().Profiles.Count == 2, "应保存两个账号档案");
            Assert(a.Id != b.Id, "账号档案 ID 不应重复");
        });
    }

    private static async Task TestConnectionTransferAsync()
    {
        await WithTempDirectoryAsync("connections-transfer", async root =>
        {
            var sourceCodex = Path.Combine(root, "source-codex");
            Directory.CreateDirectory(sourceCodex);
            await File.WriteAllTextAsync(Path.Combine(sourceCodex, "auth.json"), Auth("portable-account"));
            await File.WriteAllTextAsync(Path.Combine(sourceCodex, "config.toml"), "model_provider = \"openai\"\nmodel = \"gpt-test\"\n");
            var sourcePaths = new AppPaths(Path.Combine(root, "source-app"));
            sourcePaths.EnsureCreated();
            var processes = new CodexProcessService();
            var sourceAccounts = new OfficialAccountService(sourceCodex, sourcePaths, processes);
            var sourceProviders = new ApiProviderService(sourceCodex, sourcePaths, processes);
            sourceAccounts.SaveCurrent("便携账号");
            sourceProviders.SaveProfile("便携 API", ConnectionKind.CustomApi, "https://example.invalid", "model-portable", "portable-api-secret");

            var bundle = Path.Combine(root, "connections.chbundle");
            var transfer = new ConnectionTransferService(sourcePaths, sourceAccounts, sourceProviders);
            var manifest = await transfer.ExportAsync(bundle, "portable password 123", []);
            Assert(manifest.Items.Count(item => item.Category == ConnectionTransferService.Category) == 2, "应导出两个连接档案");
            Assert(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(bundle)).Contains("portable-api-secret", StringComparison.Ordinal), "迁移包不得出现明文 API Key");

            var targetCodex = Path.Combine(root, "target-codex");
            Directory.CreateDirectory(targetCodex);
            var targetPaths = new AppPaths(Path.Combine(root, "target-app"));
            targetPaths.EnsureCreated();
            var targetAccounts = new OfficialAccountService(targetCodex, targetPaths, processes);
            var targetProviders = new ApiProviderService(targetCodex, targetPaths, processes);
            var targetTransfer = new ConnectionTransferService(targetPaths, targetAccounts, targetProviders);
            var imported = await targetTransfer.ImportAsync(bundle, "portable password 123");
            var index = targetAccounts.LoadIndex();
            Assert(imported == 2 && index.Profiles.Count == 2, "连接档案批量导入数量不正确");
            Assert(string.IsNullOrEmpty(index.ActiveProfileId), "导入连接档案不得自动激活");
            Assert(targetProviders.EmitSecret(index.Profiles.Single(item => item.Kind == ConnectionKind.CustomApi).Id) == "portable-api-secret", "API Key 迁移内容不一致");
            await AssertThrowsAsync<InvalidDataException>(() => targetTransfer.ImportAsync(bundle, "wrong password 456"));
        });
    }

    private static async Task TestOfficialJsonTransferAsync()
    {
        await WithTempDirectoryAsync("official-json", async root =>
        {
            var sourceCodex = Path.Combine(root, "source-codex");
            Directory.CreateDirectory(sourceCodex);
            var sourcePaths = new AppPaths(Path.Combine(root, "source-app"));
            sourcePaths.EnsureCreated();
            var source = new OfficialAccountService(sourceCodex, sourcePaths, new CodexProcessService());
            await File.WriteAllTextAsync(Path.Combine(sourceCodex, "auth.json"), Auth("json-account-a"));
            source.SaveCurrent("个人账号");
            source.PrepareNewLogin();
            await File.WriteAllTextAsync(Path.Combine(sourceCodex, "auth.json"), Auth("json-account-b"));
            source.SaveCurrent("工作账号");

            var directory = Path.Combine(root, "json-export");
            var exported = source.ExportProfilesAsJson(directory);
            Assert(exported.ExportedCount == 2, "应批量导出两个官方账号 JSON");
            Assert(File.Exists(Path.Combine(directory, "个人账号.json")) && File.Exists(Path.Combine(directory, "工作账号.json")), "导出文件应使用账号名称，而非 auth.json");

            var targetCodex = Path.Combine(root, "target-codex");
            Directory.CreateDirectory(targetCodex);
            var targetPaths = new AppPaths(Path.Combine(root, "target-app"));
            targetPaths.EnsureCreated();
            var target = new OfficialAccountService(targetCodex, targetPaths, new CodexProcessService());
            var imported = target.ImportProfilesFromJson(exported.Paths);
            Assert(imported.ImportedCount == 2 && imported.NewProfiles == 2, "应批量导入两个新账号");
            Assert(target.LoadIndex().Profiles.Count == 2 && string.IsNullOrEmpty(target.LoadIndex().ActiveProfileId), "JSON 导入不应自动切换当前账号");

            var cpaDirectory = Path.Combine(root, "cpa-export");
            var cpa = source.ExportProfiles(cpaDirectory, OfficialAccountExportFormat.CpaJson);
            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(cpa.Paths[0])))
                Assert(document.RootElement.GetProperty("type").GetString() == "codex", "CPA 导出应使用 codex 类型");
            var cpaTarget = new OfficialAccountService(Path.Combine(root, "cpa-target"), new AppPaths(Path.Combine(root, "cpa-target-app")), new CodexProcessService());
            Directory.CreateDirectory(Path.Combine(root, "cpa-target"));
            Assert(cpaTarget.ImportProfilesFromJson(cpa.Paths).ImportedCount == 2, "CPA JSON 应可被统一导入");

            var sub2Directory = Path.Combine(root, "sub2-export");
            var sub2 = source.ExportProfiles(sub2Directory, OfficialAccountExportFormat.Sub2ApiJson);
            Assert(sub2.Paths.Count == 1, "Sub2API 应导出单个批量 JSON");
            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(sub2.Paths[0])))
                Assert(document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() == 2, "Sub2API 导出应为账号数组");
            var sub2TargetRoot = Path.Combine(root, "sub2-target");
            Directory.CreateDirectory(sub2TargetRoot);
            var sub2Target = new OfficialAccountService(sub2TargetRoot, new AppPaths(Path.Combine(root, "sub2-target-app")), new CodexProcessService());
            Assert(sub2Target.ImportProfilesFromJson(sub2.Paths).ImportedCount == 2, "Sub2API JSON 应可被统一导入");

            var work = source.LoadIndex().Profiles.Single(item => item.Label == "工作账号");
            source.DeleteProfile(work.Id);
            Assert(!source.LoadIndex().Profiles.Any(item => item.Id == work.Id), "删除连接应移除账号档案");
            Assert(!File.Exists(Path.Combine(sourceCodex, "auth.json")), "删除活动官方账号应清除 live auth.json");

            var invalid = Path.Combine(root, "invalid.json");
            await File.WriteAllTextAsync(invalid, "{}");
            await AssertThrowsAsync<InvalidDataException>(() => Task.Run(() => target.ImportProfilesFromJson([invalid])));
            Assert(target.LoadIndex().Profiles.Count == 2, "无效 JSON 不得改变已有账号");
        });
    }

    private static async Task TestOrphanedConnectionCleanupAsync()
    {
        await WithTempDirectoryAsync("orphan-cleanup", async root =>
        {
            var codex = Path.Combine(root, "codex");
            Directory.CreateDirectory(codex);
            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureCreated();
            var accounts = new OfficialAccountService(codex, paths, new CodexProcessService());
            await File.WriteAllTextAsync(Path.Combine(codex, "auth.json"), Auth("first"));
            accounts.SaveCurrent("第一个");
            accounts.PrepareNewLogin();
            await File.WriteAllTextAsync(Path.Combine(codex, "auth.json"), Auth("second"));
            accounts.SaveCurrent("第二个");
            var orphan = accounts.LoadIndex().Profiles.Single(item => item.Label == "第一个");
            File.Delete(Path.Combine(paths.VaultDirectory, "accounts", orphan.Id + ".dat"));
            var remaining = accounts.LoadIndex();
            Assert(!remaining.Profiles.Any(item => item.Id == orphan.Id), "缺失保险库文件的连接应在刷新时自动清理");
            Assert(remaining.Profiles.Count == 1, "不应误删仍有保险库文件的连接");
        });
    }

    private static Task TestOfficialUsageParsingAsync()
    {
        var json = Encoding.UTF8.GetBytes("""{ "plan_type": "plus", "rate_limit": { "primary_window": { "used_percent": 25, "reset_after_seconds": 60 }, "secondary_window": { "used_percent": 70.5 } } }""");
        var usage = OfficialAccountVerificationService.ParseUsage(json);
        Assert(usage.Plan == "plus" && usage.PrimaryUsedPercent == 25 && usage.SecondaryUsedPercent == 70.5m, "额度响应应解析两个窗口的已用比例");
        Assert(usage.Summary.Contains("短周期已用 25%") && usage.Summary.Contains("长周期已用 70.5%"), "额度摘要不正确");
        return Task.CompletedTask;
    }

    private static async Task TestLegacyApiImportAsync()
    {
        await WithTempDirectoryAsync("legacy-api", async root =>
        {
            var legacyRoot = Path.Combine(root, "legacy", ".codex", "api-switcher");
            Directory.CreateDirectory(legacyRoot);
            static string Setting(string name, string value) => name + "=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            await File.WriteAllLinesAsync(Path.Combine(legacyRoot, "settings.dat"),
            [
                Setting("url", "https://legacy.example/v1"),
                Setting("thirdModel", "legacy-model"),
                Setting("sub2Url", "https://sub2.example/v1"),
                Setting("sub2Model", "sub2-model")
            ]);
            var entropy = Encoding.UTF8.GetBytes("CodexApiSwitcher-v1");
            try
            {
                foreach (var pair in new[] { ("credential.dat", "legacy-key"), ("sub2api-credential.dat", "legacy-sub2-key") })
                {
                    var plain = Encoding.UTF8.GetBytes(pair.Item2);
                    try { await File.WriteAllBytesAsync(Path.Combine(legacyRoot, pair.Item1), DpapiProtector.Protect(plain, entropy)); }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
            }
            finally { CryptographicOperations.ZeroMemory(entropy); }

            var codex = Path.Combine(root, "target-codex");
            Directory.CreateDirectory(codex);
            var paths = new AppPaths(Path.Combine(root, "target-app"));
            var service = new ApiProviderService(codex, paths, new CodexProcessService());
            Assert(service.ImportLegacyDirectory(Path.Combine(root, "legacy")) == 2, "应迁移两个旧版 API 档案");
            var profiles = service.GetProfiles();
            Assert(profiles.Count == 2, "旧版 API 档案数量不正确");
            Assert(service.EmitSecret(profiles.Single(item => item.Kind == ConnectionKind.Sub2Api).Id) == "legacy-sub2-key", "旧版 Sub2API Key 不一致");
        });
    }

    private static async Task TestApiProviderSwitchAsync()
    {
        await WithTempDirectoryAsync("provider", async root =>
        {
            var codex = Path.Combine(root, "codex");
            Directory.CreateDirectory(codex);
            await File.WriteAllTextAsync(Path.Combine(codex, "config.toml"), "model_provider = \"openai\"\nmodel = \"official-model\"\n\n[mcp_servers.demo]\ncommand = \"demo\"\n");
            var session = Path.Combine(codex, "sessions", "test.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(session)!);
            await File.WriteAllTextAsync(session, "{\"type\":\"session_meta\",\"payload\":{\"model_provider\":\"openai\"}}\n{\"event\":\"test\"}\n");
            var state = Path.Combine(codex, "state_5.sqlite");
            WithNativeSqlite(state, database =>
            {
                NativeExecute(database, "create table threads (id text, source text, first_user_message text, has_user_event integer, model_provider text, rollout_path text)");
                NativeExecute(database, "insert into threads values ('thread-1','vscode','hello',0,'openai','" + session.Replace("'", "''") + "')");
            });
            var app = new AppPaths(Path.Combine(root, "app"));
            app.EnsureCreated();
            var helper = Path.Combine(root, "helper.exe");
            await File.WriteAllBytesAsync(helper, [0x4D, 0x5A, 0x01]);
            var service = new ApiProviderService(codex, app, new CodexProcessService());
            var profile = service.SaveProfile("测试 API", ConnectionKind.CustomApi, "https://example.invalid", "model-x", "secret-do-not-log");
            service.SwitchTo(profile.Id, helper);
            var config = await File.ReadAllTextAsync(Path.Combine(codex, "config.toml"));
            Assert(config.Contains("model_provider = \"custom\""), "未切换 model_provider");
            Assert(config.Contains("[mcp_servers.demo]"), "切换不应删除 MCP 配置");
            Assert(!config.Contains("secret-do-not-log"), "API Key 不得写入 config.toml");
            Assert(config.Contains("CodexHelperCredentialHelper.exe"), "配置应使用稳定凭据助手");
            WithNativeSqlite(state, database => Assert(NativeScalarText(database, "select model_provider from threads where id='thread-1'") == "custom", "状态数据库 provider 未同步"));
            Assert((JsonNode.Parse(File.ReadLines(session).First())?["payload"]?["model_provider"]?.GetValue<string>()) == "custom", "会话 JSONL provider 未同步");
            service.SwitchToOfficial();
            config = await File.ReadAllTextAsync(Path.Combine(codex, "config.toml"));
            Assert(config.Contains("model_provider = \"openai\""), "未恢复官方 provider");
            Assert(config.Contains("model = \"official-model\""), "应记住官方模型");
            WithNativeSqlite(state, database => Assert(NativeScalarText(database, "select model_provider from threads where id='thread-1'") == "openai", "恢复官方时状态数据库 provider 未同步"));
        });
    }

    private static Task TestTomlValidationAsync()
    {
        _ = TomlConfigurationDocument.Parse(["model = \"x\"", "[mcp_servers.demo]", "command = \"demo\""]);
        AssertThrows<InvalidOperationException>(() => TomlConfigurationDocument.Parse(["model = \"x\"", "model = \"y\""]));
        AssertThrows<InvalidOperationException>(() => TomlConfigurationDocument.Parse(["models = [\"x\""]));
        return Task.CompletedTask;
    }

    private static string Auth(string accountId) => JsonSerializer.Serialize(new { auth_mode = "chatgpt", account_id = accountId, tokens = new { access_token = "synthetic" } });

    private static async Task WithTempDirectoryAsync(string name, Func<string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-helper-tests", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await action(root); }
        finally
        {
            var allowed = Path.Combine(Path.GetTempPath(), "codex-helper-tests");
            if (!PathSafety.IsWithin(root, allowed)) throw new InvalidOperationException("拒绝清理测试根目录之外的路径。");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("预期异常未发生：" + typeof(T).Name);
    }

    private static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("预期异常未发生：" + typeof(T).Name);
    }

    private static void WithNativeSqlite(string path, Action<object> action)
    {
        var type = typeof(ApiProviderService).Assembly.GetType("CodexHelper.Core.Services.CodexConversationSynchronizer+NativeSqlite", throwOnError: true)!;
        var open = type.GetMethod("Open", BindingFlags.Public | BindingFlags.Static)!;
        var database = open.Invoke(null, [path]) ?? throw new InvalidOperationException("无法创建 SQLite 测试连接。");
        try { action(database); }
        finally { ((IDisposable)database).Dispose(); }
    }

    private static void NativeExecute(object database, string sql) => database.GetType().GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance)!.Invoke(database, [sql]);
    private static string NativeScalarText(object database, string sql) => (string)database.GetType().GetMethod("ScalarText", BindingFlags.Public | BindingFlags.Instance)!.Invoke(database, [sql])!;
}
