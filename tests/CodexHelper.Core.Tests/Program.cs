using System.Security.Cryptography;
using System.Reflection;
using System.IO.Compression;
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
        ("迁移磁盘空间与解密残留防护", TestBundleSafetyAsync),
        ("连接档案双层加密迁移", TestConnectionTransferAsync),
        ("旧版 API 工具批量迁移", TestLegacyApiImportAsync),
        ("官方账号保存与安全切换", TestOfficialAccountsAsync),
        ("缺失保险库档案自动清理", TestOrphanedConnectionCleanupAsync),
        ("官方账号 JSON 批量导入导出", TestOfficialJsonTransferAsync),
        ("官方账号额度响应解析", TestOfficialUsageParsingAsync),
        ("API 配置保留与凭据隔离", TestApiProviderSwitchAsync),
        ("DeepSeek 临时模型目录切换与恢复", TestDeepSeekCatalogSwitchAsync),
        ("DeepSeek 文件交付子智能体配置与恢复", TestDeepSeekPlanWorkerConfigurationAsync),
        ("旧 Responses 档案迁移清理", TestResponsesSubagentConfigurationAsync),
        ("Codex 子智能体总开关与旧配置清理", TestSubagentSettingsAsync),
        ("DeepSeek 会话缓存统计", TestDeepSeekCacheStatsAsync),
        ("DeepSeek 缓存统计批量（300 文件/损坏/锁定）", TestDeepSeekCacheStatsBulkAsync),
        ("DeepSeek 缓存统计真实 JSON 变体（null/数组/字符串）", TestDeepSeekCacheStatsJsonVariantsAsync),
        ("DeepSeek 缓存统计过滤与溢出饱和", TestDeepSeekCacheStatsOverflowAndFilterAsync),
        ("Reasonix 协作编码启用与恢复", TestReasonixIntegrationAsync),
        ("Reasonix 实时会话变体（预算/退出异常/双项目隔离）", TestReasonixLiveSessionVariantsAsync),
        ("Reasonix 执行强度策略解析与回退", TestReasonixExecutionPolicyAsync),
        ("Reasonix 状态文案与统计展示", TestReasonixUiTextAsync),
        ("Reasonix 运行时间展示与预计剩余百分比", TestReasonixTimeAndRemainingAsync),
        ("Reasonix workerChecks 步骤映射与 manifest 降级", TestReasonixWorkerStepsAsync),
        ("Reasonix 返回原任务 URI 严格校验", TestCodexThreadUriAsync),
        ("Reasonix Windows 诊断 JSON 兼容", TestReasonixWindowsJsonAsync),
        ("Reasonix 最近任务旧日期/ISO/损坏隔离与排序", TestReasonixRecentTasksAsync),
        ("Reasonix CLI 多来源发现与择优（默认/注册表/双版本/去重/迁移）", TestReasonixCliDiscoveryAsync),
        ("Reasonix doctor 容错与诊断脱敏（exit1/空输出/旧版/BOM/ANSI/噪声）", TestReasonixDoctorCompatibilityAsync),
        ("Reasonix 手动选择 CLI（合法/非法/失败不改状态/启用刷新）", TestReasonixManualSelectAsync),
        ("Reasonix 自动迁移持久化（保留字段/启用刷新脚本）", TestReasonixMigrationPersistsAsync),
        ("Reasonix 无兼容候选不改状态", TestReasonixNoMigrationWhenNoCandidateAsync),
        ("Reasonix 单次刷新仅一次探测且预计算复用", TestReasonixRefreshReuseProbeAsync),
        ("Reasonix 中文 manifest UTF-8 解析进入命令", TestReasonixChineseManifestUtf8Async),
        ("Reasonix PROGRESS 阶段协议与损坏安全处理", TestReasonixProgressStagesAsync),
        ("Reasonix 完成后实际步骤取自 metrics", TestReasonixFinalStepsMetricsAsync),
        ("Reasonix DeepSeek effort 规范化与启动失败 stderr", TestReasonixEffortNormalizationAsync),
        ("Reasonix 权限参数真实进入 CLI argv", TestReasonixPermissionArgsReachCliAsync),
        ("Reasonix 软预算超支与超支量", TestReasonixBudgetOverrunAsync),
        ("Reasonix 失败诊断与脱敏 FAILURE_REPORT", TestReasonixFailureDiagnosisAsync),
        ("Reasonix 安全原地重试", TestReasonixSafeRetryAsync),
        ("Reasonix 重试失败回滚（旧归档不变 + 取消回滚）", TestReasonixRetryRollbackAsync),
        ("DeepSeek 历史统计安全回填", TestDeepSeekBackfillAsync),
        ("DeepSeek 缓存范围与可取消", TestDeepSeekCacheRangeAndCancelAsync),
        ("TOML 损坏配置阻断", TestTomlValidationAsync),
        ("隔离 GUI 烟测配置", TestGuiSmokeFixtureAsync),
        ("Reasonix 标准 JSON 往返（中文路径/引号/反斜杠/换行）", TestReasonixStandardJsonRoundTripAsync),
        ("Reasonix 预计剩余百分比单调保护", TestReasonixRemainingMonotonicAsync),
        ("Reasonix PROGRESS checks 协议与陈旧忽略", TestReasonixProgressChecksProtocolAsync),
        ("Reasonix 合同启动前体检与安全归一化", TestReasonixContractHealthAsync),
        ("Reasonix 影响范围增量验收映射", TestReasonixAcceptanceScopeAsync),
        ("精简发布策略静态校验（4.0.0 版本一致/无 full-portable/运行库检测文案）", TestThinReleasePolicyStaticAsync),
        ("Reasonix 并行调度模型（并发2/依赖链/同文件/目录覆盖/大小写/不同项目/失败依赖/槽位/通配符）", TestReasonixParallelSchedulerAsync),
        ("Reasonix worktree 准备（并行就绪/串行回退/阻断/脏文件/未跟踪/非 Git/越界/通配符/唯一目录/cleanup 计划）", TestReasonixWorktreePreparationAsync),
        ("Reasonix worktree 协作设置字段默认值与并发边界", TestReasonixWorktreeSettingsAsync),
        ("Reasonix 任务规模三档路由（2文件80行GPT/3文件81行/高风险单文件/用户指定/验收微修/并行候选/需接线退回）", TestReasonixTaskRoutingAsync),
        ("Reasonix 漏报告自动恢复（真阳性/无活动/无证据/模型失败/非零退出/不伪造测试）", TestReasonixAutoRecoveryAsync),
        ("Reasonix 连续失败熔断（2次阻断/用户停止不计入/单次可重试）", TestReasonixCircuitBreakerAsync),
        ("Reasonix 终态状态归一化（完成/失败/取消剩余0与阶段归一/运行中保持）", TestReasonixStateNormalizationAsync),
        ("Reasonix 历史预算校准（样本不足/截尾/上下限/持久化）", TestReasonixBudgetHistoryAsync),
        ("执行器设置迁移（旧 Reasonix 启用→Reasonix，未启用→Off）", TestCollaborationModeMigrationAsync),
        ("Harness Node 版本规则（22.19+/24+，禁 latest）", TestHarnessVersionRuleAsync),
        ("Harness 能力探测与降级（未确认/已确认/无 Node 不崩溃）", TestHarnessCapabilityDiagnoseAsync),
        ("Harness 发现（旧 PATH 下绝对 Node + dsh 入口 + rc.6 识别）", TestHarnessDiscoveryOldPathAsync),
        ("Harness 语义版本与风险分级（基线/同系列/跨minor/跨major/非法/latest）", TestHarnessSemVerRiskAsync),
        ("Harness 能力门禁（新版本能力通过可用/未通过拒绝/跨主已验证）", TestHarnessCapabilityGateAsync),
        ("Harness 能力缓存失效与强制刷新（不写敏感数据）", TestHarnessCapabilityCacheAsync),
        ("执行器路由（Harness 写规则/切走移除/Off 关闭 Reasonix）", TestHarnessRoutingAsync)
    ];

    private static async Task<int> Main()
    {
        // 可选过滤：CODEX_HELPER_TEST_FILTER=关键词（分号分隔，任一命中即运行）与
        // CODEX_HELPER_TEST_EXCLUDE=关键词（任一命中即跳过）。不设置时运行全部测试，
        // 行为与之前完全一致；设置后可用于在受限环境下分批执行。
        var filterText = Environment.GetEnvironmentVariable("CODEX_HELPER_TEST_FILTER");
        var excludeText = Environment.GetEnvironmentVariable("CODEX_HELPER_TEST_EXCLUDE");
        var filters = (filterText ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var excludes = (excludeText ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var failed = 0;
        var ran = 0;
        foreach (var (name, test) in Tests)
        {
            if (filters.Length > 0 && !filters.Any(filter => name.Contains(filter, StringComparison.OrdinalIgnoreCase))) continue;
            if (excludes.Any(exclude => name.Contains(exclude, StringComparison.OrdinalIgnoreCase))) continue;
            ran++;
            try { await test(); Console.WriteLine("PASS  " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL  " + name + "\n      " + ex); }
        }
        Console.WriteLine($"\n结果：{ran - failed}/{ran} 通过");
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

            var virtualBundle = Path.Combine(root, "virtual.chbundle");
            await service.ExportAsync(new BundleExportRequest(virtualBundle, "portable secret 123", [],
            [
                new BundleVirtualFile("connections", "connections", "连接", "a.json", Encoding.UTF8.GetBytes("a"), DateTime.UtcNow),
                new BundleVirtualFile("connections", "connections", "连接", "b.json", Encoding.UTF8.GetBytes("b"), DateTime.UtcNow)
            ]));
            var virtualFiles = await service.ReadVirtualFilesAsync(virtualBundle, "portable secret 123", "connections");
            Assert(virtualFiles.Count == 2, "同一迁移项目应支持多个不同路径的虚拟文件。");
            foreach (var content in virtualFiles) CryptographicOperations.ZeroMemory(content.Content);

            var maliciousZip = Path.Combine(root, "malicious.zip");
            var maliciousBundle = Path.Combine(root, "malicious.chbundle");
            using (var archive = ZipFile.Open(maliciousZip, ZipArchiveMode.Create))
            {
                var payload = archive.CreateEntry("payload/item/a.txt");
                await using (var stream = payload.Open()) await stream.WriteAsync(Encoding.UTF8.GetBytes("too long"));
                var maliciousManifest = new BundleManifest
                {
                    BundleId = "malicious",
                    CreatedUtc = DateTime.UtcNow,
                    DeviceName = "test",
                    CodexHelperVersion = "test",
                    Items = [new BundleManifestItem("item", "project", "项目", "test", 1, 1)],
                    Files = [new BundleFileEntry("item", "a.txt", Convert.ToHexString(SHA256.HashData("x"u8)).ToLowerInvariant(), 1, DateTime.UtcNow)]
                };
                var manifestEntry = archive.CreateEntry("manifest.json");
                await using var manifestStream = manifestEntry.Open();
                await manifestStream.WriteAsync(JsonStore.Serialize(maliciousManifest));
            }
            await ChunkedEncryptedFile.EncryptPortableAsync(maliciousZip, maliciousBundle, "portable secret 123", CancellationToken.None);
            await AssertThrowsAsync<InvalidDataException>(() => service.ImportAsync(new BundleImportRequest(maliciousBundle, "portable secret 123", Path.Combine(root, "malicious-output"))));
            Assert(!Directory.Exists(Path.Combine(root, "malicious-output", "item")), "声明长度不匹配的迁移文件不得写入目标目录。");
        });
    }

    private static async Task TestBundleSafetyAsync()
    {
        await WithTempDirectoryAsync("bundle-safety", async root =>
        {
            var app = new AppPaths(Path.Combine(root, "app"));
            var source = Path.Combine(root, "source.txt");
            var bundle = Path.Combine(root, "space.chbundle");
            await File.WriteAllTextAsync(source, new string('x', 4096));
            var service = new BundleService(app);
            await service.ExportAsync(new BundleExportRequest(bundle, "portable secret 123", [new BundleExportItem("project", "project", "项目", source)]));

            var freeSpaceChecks = 0;
            var constrained = new BundleService(app, _ => ++freeSpaceChecks == 1 ? long.MaxValue : 0);
            var destination = Path.Combine(root, "destination");
            await AssertThrowsAsync<IOException>(() => constrained.ImportAsync(new BundleImportRequest(bundle, "portable secret 123", destination)));
            Assert(!File.Exists(Path.Combine(destination, "project", "source.txt")), "磁盘空间不足时不得开始写入目标文件。");

            var incomplete = Path.Combine(root, "incomplete.zip");
            await AssertThrowsAsync<InvalidDataException>(() => ChunkedEncryptedFile.DecryptPortableAsync(bundle, incomplete, "wrong password 456", CancellationToken.None));
            Assert(!File.Exists(incomplete), "解密认证失败后必须删除不完整目标文件。");
            var existingDestination = Path.Combine(root, "existing.zip");
            await File.WriteAllTextAsync(existingDestination, "user-owned");
            await AssertThrowsAsync<IOException>(() => ChunkedEncryptedFile.DecryptPortableAsync(bundle, existingDestination, "portable secret 123", CancellationToken.None));
            Assert(await File.ReadAllTextAsync(existingDestination) == "user-owned", "解密不得删除预先存在的用户目标文件。");
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
            await CreateCredentialHelperPayloadAsync(helper);
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

    private static async Task TestDeepSeekCatalogSwitchAsync()
    {
        await WithTempDirectoryAsync("deepseek-catalog", async root =>
        {
            var helper = Path.Combine(root, "helper.exe");
            await CreateCredentialHelperPayloadAsync(helper);

            var codex = Path.Combine(root, "no-user-catalog", "codex");
            Directory.CreateDirectory(codex);
            var originalConfig = "model_provider = \"openai\"\nmodel = \"gpt-template\"\n[agents]\nenabled = true\n";
            await File.WriteAllTextAsync(Path.Combine(codex, "config.toml"), originalConfig);
            await File.WriteAllTextAsync(Path.Combine(codex, "models.json"), CreateHarnessCatalog("builtin-marker"));
            var app = new AppPaths(Path.Combine(root, "no-user-catalog", "app"));
            app.EnsureCreated();
            var service = new ApiProviderService(codex, app, new CodexProcessService());
            var deepSeek = service.SaveProfile("DeepSeek", ConnectionKind.CustomApi, "https://api.deepseek.com/v1/responses", "deepseek-v4-flash", "secret-do-not-log");
            service.SwitchTo(deepSeek.Id, helper);
            var helperCatalog = Path.Combine(codex, "codex-helper-model-catalog.json");
            Assert(File.Exists(helperCatalog), "DeepSeek switching must create a temporary merged catalog.");
            var switchedConfig = await File.ReadAllTextAsync(Path.Combine(codex, "config.toml"));
            Assert(switchedConfig.Contains("model_catalog_json") && switchedConfig.Contains("model = \"deepseek-v4-flash\""), "DeepSeek switching must activate the temporary catalog and model.");
            using (var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(helperCatalog)))
            {
                Assert(catalog.RootElement.GetProperty("codex_helper").GetProperty("marker").GetString() == "codex-helper-deepseek-v1", "Helper temporary catalogs must carry an ownership marker.");
                var models = catalog.RootElement.GetProperty("models").EnumerateArray().ToList();
                Assert(models.Any(model => model.GetProperty("slug").GetString() == "gpt-template"), "The merged catalog must retain GPT models.");
                var model = models.Single(item => item.GetProperty("slug").GetString() == "deepseek-v4-flash");
                Assert(model.GetProperty("context_window").GetInt32() == 1_048_576, "DeepSeek context window is incorrect.");
                Assert(model.GetProperty("apply_patch_tool_type").GetString() == "freeform" && model.GetProperty("web_search_tool_type").GetString() == "text", "DeepSeek harness tools are incorrect.");
                Assert(model.GetProperty("input_modalities").EnumerateArray().Select(item => item.GetString()).SequenceEqual(["text"]), "DeepSeek must remain text-only.");
                Assert(!model.GetProperty("use_responses_lite").GetBoolean() && model.GetProperty("tool_mode").ValueKind == JsonValueKind.Null, "DeepSeek must use full Responses without GPT code-only tool mode.");
                Assert(model.GetProperty("supported_reasoning_levels").EnumerateArray().Select(item => item.GetProperty("effort").GetString()).SequenceEqual(["low", "medium", "high", "max"]), "DeepSeek reasoning levels are incorrect.");
            }
            service.SwitchToOfficial();
            var restoredConfig = await File.ReadAllTextAsync(Path.Combine(codex, "config.toml"));
            Assert(!restoredConfig.Contains("model_catalog_json"), "Switching back to official must remove only Helper's temporary catalog reference.");
            Assert(!File.Exists(helperCatalog), "Switching back to official must remove Helper's temporary catalog file.");

            var userRoot = Path.Combine(root, "user-catalog");
            var userCodex = Path.Combine(userRoot, "codex");
            Directory.CreateDirectory(userCodex);
            var userCatalog = Path.Combine(userRoot, "my-models.json");
            var userCatalogContent = CreateHarnessCatalog("user-owned-marker");
            await File.WriteAllTextAsync(userCatalog, userCatalogContent);
            var escapedUserCatalog = userCatalog.Replace("\\", "\\\\");
            await File.WriteAllTextAsync(Path.Combine(userCodex, "config.toml"), $"model_provider = \"openai\"\nmodel = \"gpt-template\"\nmodel_catalog_json = \"{escapedUserCatalog}\"\n");
            var userApp = new AppPaths(Path.Combine(userRoot, "app"));
            userApp.EnsureCreated();
            var userService = new ApiProviderService(userCodex, userApp, new CodexProcessService());
            var userDeepSeek = userService.SaveProfile("DeepSeek", ConnectionKind.CustomApi, "https://api.deepseek.com", "deepseek-v4-flash", "secret-do-not-log");
            userService.SwitchTo(userDeepSeek.Id, helper);
            Assert(await File.ReadAllTextAsync(userCatalog) == userCatalogContent, "Helper must never modify a user-owned model catalog.");
            userService.SwitchToOfficial();
            var userRestoredConfig = await File.ReadAllTextAsync(Path.Combine(userCodex, "config.toml"));
            Assert(userRestoredConfig.Contains($"model_catalog_json = \"{escapedUserCatalog}\""), "Switching back must restore the user's exact catalog reference.");
            Assert(await File.ReadAllTextAsync(userCatalog) == userCatalogContent, "The user-owned catalog must remain byte-for-byte unchanged.");

            var missingRoot = Path.Combine(root, "missing-template");
            var missingCodex = Path.Combine(missingRoot, "codex");
            Directory.CreateDirectory(missingCodex);
            var missingConfig = "model_provider = \"openai\"\nmodel = \"gpt-template\"\n";
            await File.WriteAllTextAsync(Path.Combine(missingCodex, "config.toml"), missingConfig);
            var missingApp = new AppPaths(Path.Combine(missingRoot, "app"));
            missingApp.EnsureCreated();
            var missingService = new ApiProviderService(missingCodex, missingApp, new CodexProcessService());
            var missingProfile = missingService.SaveProfile("DeepSeek", ConnectionKind.CustomApi, "https://api.deepseek.com", "deepseek-v4-flash", "secret-do-not-log");
            AssertThrows<InvalidOperationException>(() => missingService.SwitchTo(missingProfile.Id, helper));
            Assert(await File.ReadAllTextAsync(Path.Combine(missingCodex, "config.toml")) == missingConfig, "Missing harness template must fail before config mutation.");
            Assert(!File.Exists(Path.Combine(missingCodex, "codex-helper-model-catalog.json")), "Missing harness template must not leave a partial catalog.");

            var rollbackRoot = Path.Combine(root, "rollback");
            var rollbackCodex = Path.Combine(rollbackRoot, "codex");
            Directory.CreateDirectory(rollbackCodex);
            var rollbackConfig = "model_provider = \"openai\"\nmodel = \"gpt-template\"\n";
            await File.WriteAllTextAsync(Path.Combine(rollbackCodex, "config.toml"), rollbackConfig);
            await File.WriteAllTextAsync(Path.Combine(rollbackCodex, "models.json"), CreateHarnessCatalog("rollback-marker"));
            var rollbackApp = new AppPaths(Path.Combine(rollbackRoot, "app"));
            rollbackApp.EnsureCreated();
            var rollbackService = new ApiProviderService(rollbackCodex, rollbackApp, new CodexProcessService());
            var rollbackProfile = rollbackService.SaveProfile("DeepSeek", ConnectionKind.CustomApi, "https://api.deepseek.com", "deepseek-v4-flash", "secret-do-not-log");
            Directory.CreateDirectory(Path.Combine(rollbackApp.VaultDirectory, "providers", "metadata.json"));
            AssertThrows<IOException>(() => rollbackService.SwitchTo(rollbackProfile.Id, helper));
            Assert(await File.ReadAllTextAsync(Path.Combine(rollbackCodex, "config.toml")) == rollbackConfig, "A late switch failure must restore config.toml exactly.");
            Assert(!File.Exists(Path.Combine(rollbackCodex, "codex-helper-model-catalog.json")), "A late switch failure must roll back the temporary catalog.");
            Assert(!File.Exists(Path.Combine(rollbackCodex, "codex-helper", "bin", "CodexHelperCredentialHelper.exe")), "A late switch failure must roll back the installed helper copy.");
            Assert(!rollbackService.GetProfiles().Single(item => item.Id == rollbackProfile.Id).IsActive, "A late switch failure must roll back the active profile state.");
        });
    }

    private static string CreateHarnessCatalog(string marker)
    {
        var root = new JsonObject
        {
            ["fetched_at"] = "2026-08-01T00:00:00Z",
            ["client_version"] = "test",
            ["marker"] = marker,
            ["models"] = new JsonArray(new JsonObject
            {
                ["slug"] = "gpt-template",
                ["display_name"] = "GPT Template",
                ["description"] = "Test harness template",
                ["default_reasoning_level"] = "high",
                ["supported_reasoning_levels"] = new JsonArray(new JsonObject { ["effort"] = "high", ["description"] = "test" }),
                ["shell_type"] = "shell_command",
                ["visibility"] = "list",
                ["supported_in_api"] = true,
                ["base_instructions"] = "Use tools and complete the task.",
                ["model_messages"] = new JsonObject { ["instructions_template"] = "Complete the task." },
                ["apply_patch_tool_type"] = "freeform",
                ["web_search_tool_type"] = "text",
                ["truncation_policy"] = new JsonObject { ["mode"] = "tokens", ["limit"] = 10000 },
                ["supports_parallel_tool_calls"] = true,
                ["input_modalities"] = new JsonArray(JsonValue.Create("text")),
                ["use_responses_lite"] = true,
                ["tool_mode"] = "code_mode",
                ["context_window"] = 272000
            })
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    private static async Task TestDeepSeekPlanWorkerConfigurationAsync()
    {
        await WithTempDirectoryAsync("deepseek-plan-worker", async root =>
        {
            var codex = Path.Combine(root, "codex");
            Directory.CreateDirectory(codex);
            var originalConfig = "model_provider = \"openai\"\nmodel = \"gpt-template\"\n[agents]\nenabled = false\n";
            await File.WriteAllTextAsync(Path.Combine(codex, "config.toml"), originalConfig);
            await File.WriteAllTextAsync(Path.Combine(codex, "models.json"), CreateHarnessCatalog("plan-worker"));
            await File.WriteAllTextAsync(Path.Combine(codex, "AGENTS.md"), "# User rule\nKeep this.\n");
            var helper = Path.Combine(root, "helper.exe");
            await CreateCredentialHelperPayloadAsync(helper);
            var app = new AppPaths(Path.Combine(root, "app"));
            app.EnsureCreated();
            var service = new ApiProviderService(codex, app, new CodexProcessService());
            var profile = service.SaveProfile("DeepSeek", ConnectionKind.CustomApi, "https://api.deepseek.com/v1/responses", "deepseek-v4-flash", "synthetic-secret");

            service.EnableDeepSeekPlanWorker(profile.Id, helper);
            var enabledConfig = await File.ReadAllTextAsync(Path.Combine(codex, "config.toml"));
            var workerPath = Path.Combine(codex, "agents", "deepseek_plan_worker.toml");
            var guidance = await File.ReadAllTextAsync(Path.Combine(codex, "AGENTS.md"));
            Assert(enabledConfig.Contains("model_provider = \"openai\"") && enabledConfig.Contains("model = \"gpt-template\""), "Enabling the coding worker must not change the GPT main model.");
            Assert(enabledConfig.Contains("[model_providers.deepseek_plan_worker]") && enabledConfig.Contains("enabled = true"), "The dedicated provider and native agents switch must be configured.");
            Assert(File.Exists(workerPath), "The dedicated plan worker must be created.");
            var worker = await File.ReadAllTextAsync(workerPath);
            Assert(worker.Contains("deepseek_plan_worker") && worker.Contains("CODEX-HELPER-DEEPSEEK-PLAN-WORKER"), "The dedicated plan worker marker and agent name must be present.");
            Assert(worker.Contains("absolute pointer path") && worker.Contains("taskFile") && worker.Contains("resultFile"), "The worker must read the absolute pointer path and the validated pointer fields from the wake-up message.");
            Assert(!worker.Contains("<workspace>/.codex-helper-runtime/current-task.json") && !worker.Contains("<workspace>/.codex-helper-runtime"), "The worker must not look up a fixed workspace-local pointer.");
            Assert(!worker.Contains("sandbox_mode"), "The generated worker must inherit the parent permission mode without a sandbox override.");
            Assert(worker.Contains("model_reasoning_effort = \"high\""), "The worker must default to high reasoning effort.");
            service.UpdateDeepSeekPlanWorkerReasoningEffort("max");
            Assert((await File.ReadAllTextAsync(workerPath)).Contains("model_reasoning_effort = \"max\""), "The selected reasoning effort must be written to the managed worker.");
            Assert(service.GetDeepSeekPlanWorkerReasoningEffort() == "max", "The managed worker reasoning effort must be readable for the settings UI.");
            service.UpdateDeepSeekPlanWorkerReasoningEffort("medium");
            Assert((await File.ReadAllTextAsync(workerPath)).Contains("model_reasoning_effort = \"medium\""), "The balanced reasoning effort must be accepted and written to the managed worker.");
            Assert(guidance.Contains("CODEX-HELPER-DEEPSEEK-PLAN-RELAY-START") && guidance.Contains("tasks/task-<id>.md") && guidance.Contains("pointers/task-<id>.json") && guidance.Contains("results/result-<id>.md"), "The parent relay guidance must require absolute per-task pointer files under the relay root.");
            Assert(guidance.Contains("relayRoot") && guidance.Contains("canonical") && guidance.Contains("Read both files back"), "The parent relay guidance must require absolute pointer paths, canonical containment, and pre-spawn verification.");
            Assert(guidance.Contains("wake-up message that contains only the task id and the absolute pointer path"), "The parent must wake the worker with only the task id and the absolute pointer path.");
            Assert(guidance.Contains("Never spawn the worker first"), "The parent must never spawn first and repair the pointer afterward.");
            Assert(guidance.Contains("# User rule") && guidance.Contains("Keep this."), "Enabling must preserve user guidance.");
            Assert(service.IsDeepSeekPlanWorkerEnabled(), "The service must report the managed worker as enabled.");

            service.DisableDeepSeekPlanWorker();
            var disabledConfig = await File.ReadAllTextAsync(Path.Combine(codex, "config.toml"));
            var disabledGuidance = await File.ReadAllTextAsync(Path.Combine(codex, "AGENTS.md"));
            Assert(!disabledConfig.Contains("model_providers.deepseek_plan_worker"), "Disabling must remove only the dedicated worker provider.");
            Assert(disabledConfig.Contains("model_provider = \"openai\"") && disabledConfig.Contains("model = \"gpt-template\""), "Disabling must keep the original GPT main model.");
            Assert(!File.Exists(workerPath), "Disabling must remove only the Helper-managed worker file.");
            Assert(!disabledGuidance.Contains("CODEX-HELPER-DEEPSEEK-PLAN-RELAY-START") && disabledGuidance.Contains("Keep this."), "Disabling must remove only Helper guidance.");
            Assert(!service.IsDeepSeekPlanWorkerEnabled(), "The service must report the worker as disabled.");

            await File.WriteAllTextAsync(Path.Combine(codex, "config.toml"), "model = \"gpt-template\"\n[agents]\nenabled = false\n");
            service.EnableDeepSeekPlanWorker(profile.Id, helper);
            Assert(service.IsDeepSeekPlanWorkerEnabled(), "An official Codex configuration with no explicit model_provider must allow the DeepSeek worker.");
            service.DisableDeepSeekPlanWorker();
        });
    }

    private static async Task TestResponsesSubagentConfigurationAsync()
    {
        await WithTempDirectoryAsync("responses-subagent", async root =>
        {
            var codex = Path.Combine(root, "codex");
            Directory.CreateDirectory(codex);
            await File.WriteAllTextAsync(Path.Combine(codex, "config.toml"), "model_provider = \"openai\"\nmodel = \"official-model\"\n");
            await File.WriteAllTextAsync(Path.Combine(codex, "AGENTS.md"), "# Existing user guidance\nKeep this rule.\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            app.EnsureCreated();
            var helper = Path.Combine(root, "helper.exe");
            await CreateCredentialHelperPayloadAsync(helper);
            var service = new ApiProviderService(codex, app, new CodexProcessService());
            Assert(ApiProviderService.NormalizeBaseUrl("https://root.example.invalid/responses") == "https://root.example.invalid", "完整根路径 Responses URL 不应被错误追加 /v1");
            var profile = service.SaveProfile("Responses worker", ConnectionKind.ResponsesSubagent, "https://api.deepseek.com/v1/responses", "deepseek-v4-flash", "secret-do-not-log");
            Assert(profile.BaseUrl == "https://api.deepseek.com/v1", "完整 Responses URL 应去除重复后缀");
            Directory.CreateDirectory(Path.Combine(codex, "agents"));
            await File.WriteAllTextAsync(Path.Combine(codex, "agents", "worker.toml"), "description = \"Dedicated Responses coding worker\"\nmodel_provider = \"responses_subagent\"\n");
            await File.WriteAllTextAsync(Path.Combine(codex, "codex-helper-model-catalog.json"), "{\"models\":[]}");
            await File.AppendAllTextAsync(Path.Combine(codex, "config.toml"), "model_catalog_json = \"codex-helper-model-catalog.json\"\n[model_providers.responses_subagent]\nname = \"legacy\"\n[model_providers.responses_subagent.auth]\ncommand = \"helper\"\n");
            await File.AppendAllTextAsync(Path.Combine(codex, "AGENTS.md"), "\n<!-- CODEX-HELPER-DELEGATION-START -->\nlegacy rule\n<!-- CODEX-HELPER-DELEGATION-END -->\n");
            var report = service.CleanUnsupportedNativeSubagent(profile.Id);
            var cleanedConfig = await File.ReadAllTextAsync(Path.Combine(codex, "config.toml"));
            Assert(report.Contains("blocked", StringComparison.OrdinalIgnoreCase), "Unsupported native worker cleanup must report the block.");
            Assert(cleanedConfig.Contains("model_provider = \"openai\""), "Cleanup must preserve the official main provider.");
            Assert(!cleanedConfig.Contains("responses_subagent"), "Cleanup must remove only the legacy Responses worker provider.");
            Assert(!cleanedConfig.Contains("model_catalog_json"), "Cleanup must remove only the Helper worker model catalog reference.");
            Assert(!File.Exists(Path.Combine(codex, "agents", "worker.toml")), "Cleanup must remove the Helper-managed worker file.");
            Assert(!File.Exists(Path.Combine(codex, "codex-helper-model-catalog.json")), "Cleanup must remove the Helper worker catalog.");
            var cleanedGuidance = await File.ReadAllTextAsync(Path.Combine(codex, "AGENTS.md"));
            Assert(cleanedGuidance.Contains("Keep this rule."), "Cleanup must preserve unrelated user AGENTS guidance.");
            Assert(!cleanedGuidance.Contains("CODEX-HELPER-DELEGATION-START"), "Cleanup must remove only the Helper delegation marker.");
            Assert(!service.GetProfiles().Single(item => item.Id == profile.Id).IsDefaultSubagent, "Cleanup must not show a default third-party subagent.");
            var recovery = Path.Combine(app.RecoveryDirectory, "provider-switches");
            Assert(Directory.EnumerateFiles(recovery, "*.bak").Count() >= 3, "Cleanup must back up worker, catalog, and AGENTS before changing them.");
            service.CleanUnsupportedNativeSubagent(profile.Id);
            var remoteProfile = service.SaveProfile("Remote cleanup", ConnectionKind.ResponsesSubagent, "http://remote.example.invalid/v1", "model-x", "secret-do-not-log");
            await File.WriteAllTextAsync(Path.Combine(codex, "agents", "worker.toml"), "name = \"user-worker\"\nmodel_provider = \"openai\"\n");
            service.CleanUnsupportedNativeSubagent(remoteProfile.Id);
            Assert(File.Exists(Path.Combine(codex, "agents", "worker.toml")), "Cleanup must not remove an unrelated user worker.toml.");
            Assert(service.GetProfiles().Single(item => item.Id == remoteProfile.Id).StatusMessage.Contains("unavailable", StringComparison.OrdinalIgnoreCase), "Remote HTTP profile must remain cleanable because cleanup makes no network request.");
        });
    }

    private static async Task TestSubagentSettingsAsync()
    {
        await WithTempDirectoryAsync("subagent-settings", async root =>
        {
            var codex = Path.Combine(root, "codex");
            var app = new AppPaths(Path.Combine(root, "app"));
            app.EnsureCreated();
            Directory.CreateDirectory(codex);
            var configPath = Path.Combine(codex, "config.toml");
            await File.WriteAllTextAsync(configPath,
                "# 用户注释必须保留\n" +
                "model_provider = \"openai\"\n" +
                "[agents]\n" +
                "enabled = false # 保留行尾注释\n" +
                "[mcp_servers.demo]\n" +
                "command = \"demo\"\n");

            var service = new SubagentSettingsService(codex, app);
            Assert(!service.ReadState().Enabled, "Fresh settings must start disabled.");
            Assert(service.ReadState().DisplayText.Contains("已关闭（推荐）"), "Disabled UX must explicitly recommend the safer state.");
            service.SetEnabled(true);
            var enabledConfig = await File.ReadAllTextAsync(configPath);
            Assert(service.ReadState().Enabled, "The native Codex agents switch must be enabled without requiring a profile.");
            Assert(enabledConfig.Contains("enabled = true # 保留行尾注释"), "Changing the native switch must preserve an inline comment.");
            Assert(enabledConfig.Contains("# 用户注释必须保留") && enabledConfig.Contains("[mcp_servers.demo]"), "Enabling must preserve unknown TOML and comments.");
            Assert(!enabledConfig.Contains("responses_subagent") && !Directory.Exists(Path.Combine(codex, "agents")), "Enabling must not create a provider or worker profile.");
            Assert(service.ReadState().DisplayText.Contains("会增加当前主模型用量"), "Enabled UX must warn about current-model usage.");

            var managedCatalog = Path.Combine(codex, "codex-helper-model-catalog.json");
            var managedWorker = Path.Combine(codex, "agents", "worker.toml");
            var guidance = Path.Combine(codex, "AGENTS.md");
            await File.WriteAllTextAsync(managedCatalog, "{\"userOwned\":true}");
            await File.WriteAllTextAsync(configPath,
                "model_provider = \"openai\"\n" +
                $"model_catalog_json = \"{managedCatalog.Replace("\\", "\\\\")}\"\n" +
                "[agents]\nenabled = true\n" +
                "[mcp_servers.demo]\ncommand = \"demo\"\n");
            service.SetEnabled(false);
            Assert(File.Exists(managedCatalog), "A fixed filename without a Helper marker or legacy metadata must remain user-owned.");
            Assert((await File.ReadAllTextAsync(configPath)).Contains("model_catalog_json"), "A user-owned fixed-name catalog reference must be preserved.");

            await File.WriteAllTextAsync(managedCatalog, "{\"codex_helper\":{\"marker\":\"codex-helper-deepseek-v1\"},\"models\":[]}");
            Directory.CreateDirectory(Path.Combine(app.VaultDirectory, "providers"));
            new JsonStore().Save(Path.Combine(app.VaultDirectory, "providers", "metadata.json"), new
            {
                helperCatalogActive = true,
                helperCatalogMarker = "codex-helper-deepseek-v1"
            });
            await File.WriteAllTextAsync(configPath,
                "model_provider = \"custom\"\nmodel = \"deepseek-v4-flash\"\n" +
                $"model_catalog_json = \"{managedCatalog.Replace("\\", "\\\\")}\"\n" +
                "[agents]\nenabled = true\n");
            service.SetEnabled(false);
            Assert(File.Exists(managedCatalog) && (await File.ReadAllTextAsync(configPath)).Contains("model_catalog_json"), "Disabling agents must preserve the active DeepSeek main-model catalog.");
            Assert(!service.ReadState().HasLegacyResidue, "An active marked main-model catalog is not subagent residue.");

            Directory.CreateDirectory(Path.GetDirectoryName(managedWorker)!);
            await File.WriteAllTextAsync(managedCatalog, "{\"managed\":true}");
            await File.WriteAllTextAsync(managedWorker, "description = \"Dedicated Responses coding worker\"\nmodel_provider = \"responses_subagent\"\n");
            await File.WriteAllTextAsync(guidance,
                "Keep this rule.\n<!-- CODEX-HELPER-DELEGATION-START -->\nlegacy helper rule\n<!-- CODEX-HELPER-DELEGATION-END -->\n");
            await File.WriteAllTextAsync(configPath,
                "# 用户注释必须保留\n" +
                "model_provider = \"openai\"\n" +
                "default_subagent_model = \"deepseek-v4-flash\"\n" +
                $"model_catalog_json = \"{managedCatalog.Replace("\\", "\\\\")}\"\n" +
                "[agents]\n" +
                "enabled = true # 保留行尾注释\n" +
                "[mcp_servers.demo]\n" +
                "command = \"demo\"\n" +
                "[model_providers.responses_subagent]\n" +
                "name = \"legacy\"\n" +
                "[model_providers.responses_subagent.auth]\n" +
                "command = \"helper\"\n" +
                "[model_providers.user_custom]\n" +
                "name = \"keep\"\n");
            var index = new ConnectionIndex
            {
                Profiles = [new ConnectionProfile { Id = "legacy-profile", Label = "保留的档案", Kind = ConnectionKind.ResponsesSubagent, IsDefaultSubagent = true }]
            };
            new JsonStore().Save(Path.Combine(app.VaultDirectory, "connections.json"), index);
            var secretPath = Path.Combine(app.VaultDirectory, "providers", "legacy-profile.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(secretPath)!);
            var secretBytes = Encoding.UTF8.GetBytes("encrypted-secret-placeholder");
            await File.WriteAllBytesAsync(secretPath, secretBytes);
            var personalSkill = Path.Combine(codex, "skills", "personal", "SKILL.md");
            Directory.CreateDirectory(Path.GetDirectoryName(personalSkill)!);
            await File.WriteAllTextAsync(personalSkill, "# Personal skill\nNever remove me.\n");

            Assert(service.ReadState().HasLegacyResidue, "Legacy Helper residue must be detected.");
            service.SetEnabled(false);
            var disabledConfig = await File.ReadAllTextAsync(configPath);
            Assert(!service.ReadState().Enabled && !service.ReadState().HasLegacyResidue, "Disabling must clear the native switch and Helper legacy residue.");
            Assert(disabledConfig.Contains("enabled = false # 保留行尾注释"), "Disabling must preserve the inline comment.");
            Assert(disabledConfig.Contains("# 用户注释必须保留") && disabledConfig.Contains("[mcp_servers.demo]") && disabledConfig.Contains("[model_providers.user_custom]"), "Disabling must preserve unknown TOML sections and comments.");
            Assert(!disabledConfig.Contains("default_subagent_model") && !disabledConfig.Contains("responses_subagent") && !disabledConfig.Contains("codex-helper-model-catalog.json"), "Disabling must remove all legacy Helper TOML entries.");
            Assert(!File.Exists(managedWorker) && !File.Exists(managedCatalog), "Disabling must remove Helper-managed worker and catalog files.");
            Assert((await File.ReadAllTextAsync(guidance)).Contains("Keep this rule.") && !(await File.ReadAllTextAsync(guidance)).Contains("CODEX-HELPER-DELEGATION"), "Disabling must preserve user guidance and remove only the Helper marker.");
            var retainedIndex = new JsonStore().LoadOrCreate(Path.Combine(app.VaultDirectory, "connections.json"), () => new ConnectionIndex());
            Assert(retainedIndex.Profiles.Single().Id == "legacy-profile" && !retainedIndex.Profiles.Single().IsDefaultSubagent, "Disabling must retain profiles and only clear the retired default flag.");
            Assert((await File.ReadAllBytesAsync(secretPath)).SequenceEqual(secretBytes), "Disabling must retain encrypted provider secrets.");
            Assert(File.Exists(personalSkill) && (await File.ReadAllTextAsync(personalSkill)).Contains("Never remove me."), "Disabling must never delete unrelated Skills.");

            await File.WriteAllTextAsync(managedWorker, "name = \"user-worker\"\nmodel_provider = \"openai\"\n");
            service.SetEnabled(false);
            service.SetEnabled(false);
            Assert(File.Exists(managedWorker), "Repeated disable must be idempotent and preserve a user-managed worker file.");

            var rollbackConfig =
                "model_provider = \"openai\"\n" +
                "default_subagent_model = \"deepseek-v4-flash\"\n" +
                $"model_catalog_json = \"{managedCatalog.Replace("\\", "\\\\")}\"\n" +
                "[agents]\nenabled = true\n" +
                "[model_providers.responses_subagent]\nname = \"legacy\"\n";
            var rollbackWorker = "description = \"Dedicated Responses coding worker\"\nmodel_provider = \"responses_subagent\"\n";
            var rollbackGuidance = "User text.\n<!-- CODEX-HELPER-DELEGATION-START -->\nlegacy\n<!-- CODEX-HELPER-DELEGATION-END -->\n";
            const string rollbackCatalog = "{\"rollback\":true}";
            await File.WriteAllTextAsync(configPath, rollbackConfig);
            await File.WriteAllTextAsync(managedWorker, rollbackWorker);
            await File.WriteAllTextAsync(guidance, rollbackGuidance);
            await File.WriteAllTextAsync(managedCatalog, rollbackCatalog);
            await File.WriteAllTextAsync(Path.Combine(app.VaultDirectory, "connections.json"), "{ invalid json");
            AssertThrows<JsonException>(() => service.SetEnabled(false));
            Assert(await File.ReadAllTextAsync(configPath) == rollbackConfig, "A failed cleanup must roll back config.toml exactly.");
            Assert(await File.ReadAllTextAsync(managedWorker) == rollbackWorker, "A failed cleanup must restore the managed worker file.");
            Assert(await File.ReadAllTextAsync(guidance) == rollbackGuidance, "A failed cleanup must restore AGENTS.md.");
            Assert(await File.ReadAllTextAsync(managedCatalog) == rollbackCatalog, "A failed cleanup must restore the model catalog.");
        });
    }

    private static async Task TestDeepSeekCacheStatsAsync()
    {
        await WithTempDirectoryAsync("deepseek-cache-stats", async root =>
        {
            var app = new AppPaths(Path.Combine(root, "app"));
            var codexRoot = Path.Combine(root, "codex");
            var sessions = Path.Combine(codexRoot, "sessions");
            Directory.CreateDirectory(sessions);

            // 真格式：deepseek_plan_worker + turn_context.model=deepseek-v4-flash
            await File.WriteAllTextAsync(Path.Combine(sessions, "worker.jsonl"),
                "{\"type\":\"session_meta\",\"payload\":{\"model_provider\":\"deepseek_plan_worker\"}}\n" +
                "{\"type\":\"turn_context\",\"payload\":{\"model\":\"deepseek-v4-flash\"}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":700}}}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":500,\"cached_input_tokens\":100}}}}\n");
            // deepseek-v4-pro 计入
            await File.WriteAllTextAsync(Path.Combine(sessions, "pro.jsonl"),
                "{\"type\":\"turn_context\",\"payload\":{\"model\":\"deepseek-v4-pro\"}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":200,\"cached_input_tokens\":50}}}}\n");
            // 普通 OpenAI 会话不计入
            await File.WriteAllTextAsync(Path.Combine(sessions, "official.jsonl"),
                "{\"type\":\"session_meta\",\"payload\":{\"model_provider\":\"openai\"}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":9999,\"cached_input_tokens\":9999}}}}\n");
            // 旧 responses_subagent provider + 非 DeepSeek model → 不得误算
            await File.WriteAllTextAsync(Path.Combine(sessions, "subagent-openai.jsonl"),
                "{\"type\":\"session_meta\",\"payload\":{\"model_provider\":\"responses_subagent\"}}\n" +
                "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-4o\"}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":90}}}}\n");
            // 中间半写损坏行：不得放弃同文件后续有效记录
            await File.WriteAllTextAsync(Path.Combine(sessions, "damaged.jsonl"),
                "{\"type\":\"session_meta\",\"payload\":{\"model\":\"deepseek-v4-flash\"}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":300,\"cached_input_tokens\":200}}}}\n" +
                "{ 半写坏行未闭合\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":400,\"cached_input_tokens\":150}}}}\n");

            // Reasonix 任务状态：明确 DeepSeek 模型、非 DeepSeek 模型、缺字段旧状态、cached>input 越界
            Directory.CreateDirectory(app.ReasonixTasksDirectory);
            var now = DateTime.UtcNow;
            async Task WriteStatus(string id, ReasonixTaskStatus status)
                => await File.WriteAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, id + ".json"),
                    JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
            await WriteStatus("run-deepseek", new ReasonixTaskStatus("run-deepseek", @"C:\p", @"C:\p\.codex-helper\runs\run-deepseek", "completed", "review", "Full", now, now, 1, 10, "done", ExecutionModel: "opencode/deepseek-v4-flash", TokenInput: 2000, CacheHitTokens: 800));
            await WriteStatus("run-openai", new ReasonixTaskStatus("run-openai", @"C:\p", @"C:\p\.codex-helper\runs\run-openai", "completed", "review", "Full", now, now, 1, 10, "done", ExecutionModel: "opencode/gpt-4o", TokenInput: 5000, CacheHitTokens: 4000));
            await WriteStatus("run-legacy", new ReasonixTaskStatus("run-legacy", @"C:\p", @"C:\p\.codex-helper\runs\run-legacy", "completed", "review", "Full", now, now, 1, 10, "done", TokenInput: 5000, CacheHitTokens: 4000));
            await WriteStatus("run-clamped", new ReasonixTaskStatus("run-clamped", @"C:\p", @"C:\p\.codex-helper\runs\run-clamped", "completed", "review", "Full", now, now, 1, 10, "done", ExecutionModel: "opencode/deepseek-v4-pro", TokenInput: 1000, CacheHitTokens: 5000));

            var stats = new DeepSeekCacheStatsService(codexRoot, app.ReasonixTasksDirectory).ReadRecent(TimeSpan.FromDays(3650));

            // Codex：worker(1500/800) + pro(200/50) + damaged(700/350) = 输入2400 命中1200，5 次请求
            Assert(stats.CodexRequests == 5 && stats.CodexInputTokens == 2400 && stats.CodexHitTokens == 1200, "Codex DeepSeek 会话统计不正确。");
            // Reasonix：deepseek(2000/800) + clamped(1000/1000) = 输入3000 命中1800，2 个任务
            Assert(stats.ReasonixTaskCount == 2 && stats.ReasonixInputTokens == 3000 && stats.ReasonixHitTokens == 1800, "Reasonix DeepSeek 任务统计不正确。");
            // 总体：输入5400 命中3000 未命中2400 命中率55.6%
            Assert(stats.TotalInputTokens == 5400 && stats.HitTokens == 3000 && stats.MissTokens == 2400, "DeepSeek 总体汇总不正确。");
            Assert(stats.HitRatePercent == 55.6m, "DeepSeek 命中率不正确。");
            // 诊断：OpenAI/缺字段各 1 条被跳过 + Codex 2 个非 DeepSeek 文件跳过；1 处越界被夹取
            Assert(stats.SkippedFiles == 4, "非 DeepSeek/缺字段条目应被安全跳过。");
            Assert(stats.ClampedValues == 1, "cached>input 应被夹取并计入诊断。");
            Assert(stats.ScannedFiles == 9, "扫描文件数量不正确。");
            Assert(stats.CorruptFiles == 0 && stats.UnreadableFiles == 0, "不应出现损坏/不可访问文件计数误判。");

            var text = stats.ToDisplayText();
            Assert(text.Contains("Codex 会话", StringComparison.Ordinal) && text.Contains("Reasonix 任务", StringComparison.Ordinal), "成功态应同时展示两个来源。");
            Assert(text.Contains("55.6%", StringComparison.Ordinal), "成功态应包含命中率。");
            Assert(text.Contains("已过滤 4 条", StringComparison.Ordinal), "成功文案应以中性文字显示过滤数量。");
            Assert(!text.Contains("C:\\\\", StringComparison.Ordinal), "文案不得泄露文件路径。");
        });
    }

    private static async Task TestDeepSeekCacheStatsBulkAsync()
    {
        await WithTempDirectoryAsync("deepseek-cache-bulk", async root =>
        {
            var sessions = Path.Combine(root, "sessions");
            Directory.CreateDirectory(sessions);
            const int validCount = 160;   // 每个 1 条有效事件（1000/500）；与其余文件合计 310 > 300，验证全量不截断
            const int damagedCount = 50;  // 中间损坏行 + 2 条有效事件（200/100 与 300/150）
            const int openaiCount = 99;   // 不计入
            const int lockedCount = 1;    // 独占锁定 → 不可访问，整个刷新不得失败

            for (var i = 0; i < validCount; i++)
                await File.WriteAllTextAsync(Path.Combine(sessions, $"d{i}.jsonl"),
                    "{\"type\":\"session_meta\",\"payload\":{\"model\":\"deepseek-v4-flash\"}}\n" +
                    "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":500}}}}\n");
            for (var i = 0; i < damagedCount; i++)
                await File.WriteAllTextAsync(Path.Combine(sessions, $"dd{i}.jsonl"),
                    "{\"type\":\"session_meta\",\"payload\":{\"model\":\"deepseek-v4-pro\"}}\n" +
                    "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":200,\"cached_input_tokens\":100}}}}\n" +
                    "{ 半写坏行\n" +
                    "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":300,\"cached_input_tokens\":150}}}}\n");
            for (var i = 0; i < openaiCount; i++)
                await File.WriteAllTextAsync(Path.Combine(sessions, $"o{i}.jsonl"),
                    "{\"type\":\"session_meta\",\"payload\":{\"model_provider\":\"openai\"}}\n" +
                    "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":9999,\"cached_input_tokens\":9999}}}}\n");

            // 一个被独占锁定的 DeepSeek 文件：即使不可访问也不得让刷新失败。
            var lockedPath = Path.Combine(sessions, "locked.jsonl");
            await File.WriteAllTextAsync(lockedPath,
                "{\"type\":\"session_meta\",\"payload\":{\"model\":\"deepseek-v4-flash\"}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":50,\"cached_input_tokens\":25}}}}\n");
            using (var lockedHandle = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var stats = new DeepSeekCacheStatsService(root).ReadRecent(TimeSpan.FromDays(3650));
                // 扫描全部 310 个文件（超过旧 300 上限），一个也不漏
                Assert(stats.ScannedFiles == validCount + damagedCount + openaiCount + lockedCount, "批量扫描文件数量不正确。");
                // Codex 请求：有效160*1 + 损坏50*2 = 260；锁定文件不可读不计入；OpenAI 不计
                Assert(stats.CodexRequests == 260, "批量 Codex 请求数不正确。");
                // 输入：有效160*1000 + 损坏50*500 = 185000；命中 160*500 + 50*250 = 92500
                Assert(stats.CodexInputTokens == 185000, "批量 Codex 输入不正确。");
                Assert(stats.CodexHitTokens == 92500, "批量 Codex 命中不正确。");
                Assert(stats.UnreadableFiles == 1, "锁定文件应计为不可访问且不影响其他文件。");
                Assert(stats.CorruptFiles == 0, "逐行损坏不应计入文件级损坏。");
            }
        });
    }

    private static async Task TestDeepSeekCacheStatsJsonVariantsAsync()
    {
        await WithTempDirectoryAsync("deepseek-cache-json-variants", async root =>
        {
            var sessions = Path.Combine(root, "sessions");
            Directory.CreateDirectory(sessions);

            // 正文统计的真实异常形态：payload=null/[]、info=null、last_token_usage=null/[]/字符串，
            // 全部必须安全跳过，且后接有效行继续统计。
            await File.WriteAllTextAsync(Path.Combine(sessions, "variants.jsonl"),
                "{\"type\":\"session_meta\",\"payload\":{\"model\":\"deepseek-v4-flash\"}}\n" +
                "{\"type\":\"event_msg\",\"payload\":null}\n" +
                "{\"type\":\"event_msg\",\"payload\":[]}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":null}}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":[]}}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":\"oops\"}}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":\"bad\",\"cached_input_tokens\":20}}}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":true,\"cached_input_tokens\":20}}}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":40}}}}\n");

            // 头部识别的真实异常形态：model_provider=null/数组、model=null/数组，
            // 全部跳过该行且继续扫描，后接有效 deepseek model 命中，正文照常统计。
            await File.WriteAllTextAsync(Path.Combine(sessions, "provider-variants.jsonl"),
                "{\"type\":\"session_meta\",\"payload\":{\"model_provider\":null}}\n" +
                "{\"type\":\"session_meta\",\"payload\":{\"model_provider\":[]}}\n" +
                "{\"type\":\"turn_context\",\"payload\":{\"model\":null}}\n" +
                "{\"type\":\"turn_context\",\"payload\":{\"model\":[]}}\n" +
                "{\"type\":\"turn_context\",\"payload\":{\"model\":\"deepseek-v4-flash\"}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":50,\"cached_input_tokens\":10}}}}\n");

            var stats = new DeepSeekCacheStatsService(root).ReadRecent(TimeSpan.FromDays(3650));

            // variants 1 条有效（100/40）+ provider-variants 1 条有效（50/10）= 输入150 命中50，2 次请求
            Assert(stats.CodexRequests == 2, "异常形态行不得计入请求，后接有效行应计入。");
            Assert(stats.CodexInputTokens == 150, "异常形态行不得污染输入统计。");
            Assert(stats.CodexHitTokens == 50, "异常形态行不得污染命中统计。");
            Assert(stats.SkippedFiles == 0, "两个变体文件均被识别为 DeepSeek 会话。");
            Assert(stats.CorruptFiles == 0 && stats.UnreadableFiles == 0, "异常形态行不应计为文件级损坏/不可访问。");
            Assert(stats.TotalInputTokens == 150 && stats.HitTokens == 50, "真实 JSON 变体后总体汇总应准确。");
        });
    }

    private static async Task TestDeepSeekCacheStatsOverflowAndFilterAsync()
    {
        await WithTempDirectoryAsync("deepseek-overflow-filter", async root =>
        {
            var app = new AppPaths(Path.Combine(root, "app"));
            var sessions = Path.Combine(root, "sessions");
            Directory.CreateDirectory(sessions);

            // 正常过滤：普通 OpenAI 会话 + 缺 ExecutionModel 的旧 Reasonix 状态 → 仅正常跳过，不触发异常提示。
            await File.WriteAllTextAsync(Path.Combine(sessions, "official.jsonl"),
                "{\"type\":\"session_meta\",\"payload\":{\"model_provider\":\"openai\"}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":9999,\"cached_input_tokens\":9999}}}}\n");
            Directory.CreateDirectory(app.ReasonixTasksDirectory);
            var now = DateTime.UtcNow;
            await File.WriteAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "legacy.json"),
                JsonSerializer.Serialize(new ReasonixTaskStatus("legacy", @"C:\p", @"C:\p\runs\legacy", "completed", "review", "Full", now, now, 1, 1, "done", TokenInput: 5000, CacheHitTokens: 4000)));
            var filterOnly = new DeepSeekCacheStatsService(root, app.ReasonixTasksDirectory).ReadRecent(TimeSpan.FromDays(3650));
            Assert(filterOnly.SkippedFiles == 2 && filterOnly.IssueCount == 0, "正常过滤不应计入 IssueCount。");
            Assert(!filterOnly.ToDisplayText().Contains("部分数据不可用", StringComparison.Ordinal), "正常过滤不应触发异常提示。");
            Assert(filterOnly.ToDisplayText().Contains("未发现 DeepSeek 用量记录", StringComparison.Ordinal), "无数据时应走中性无数据文案。");

            // 溢出：两个极端 long 输入饱和，不回绕为负，命中率保持 0–100%。
            var max = long.MaxValue;
            await File.WriteAllTextAsync(Path.Combine(sessions, "big1.jsonl"),
                "{\"type\":\"session_meta\",\"payload\":{\"model\":\"deepseek-v4-flash\"}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":" + max + ",\"cached_input_tokens\":" + max + "}}}}\n");
            await File.WriteAllTextAsync(Path.Combine(sessions, "big2.jsonl"),
                "{\"type\":\"session_meta\",\"payload\":{\"model\":\"deepseek-v4-pro\"}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":" + max + ",\"cached_input_tokens\":0}}}}\n");
            var overflow = new DeepSeekCacheStatsService(root, app.ReasonixTasksDirectory).ReadRecent(TimeSpan.FromDays(3650));
            Assert(overflow.TotalInputTokens == long.MaxValue && overflow.HitTokens == long.MaxValue, "极端输入必须饱和到 long.MaxValue 而非回绕为负。");
            Assert(overflow.TotalInputTokens >= 0 && overflow.HitTokens >= 0, "总数不得为负。");
            Assert(overflow.HitRatePercent >= 0m && overflow.HitRatePercent <= 100m, "命中率必须保持 0–100%。");
        });
    }

    private static async Task TestReasonixFinalStepsMetricsAsync()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        await WithTempDirectoryAsync("reasonix-final-steps", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var writeScript = Path.Combine(root, "wmetrics.ps1");
            await File.WriteAllTextAsync(writeScript, "[IO.File]::WriteAllText((Join-Path $env:CODEX_HELPER_TEST_TASK 'metrics.json'),'{\"steps\":7}',[Text.UTF8Encoding]::new($false)); [IO.File]::WriteAllText((Join-Path $env:CODEX_HELPER_TEST_TASK 'EXECUTION_REPORT.md'),'done',[Text.UTF8Encoding]::new($false))");
            var executable = Path.Combine(root, "reasonix-cli.cmd");
            await File.WriteAllTextAsync(executable, """
                @echo off
                echo {"kind":"turn_started"}
                echo {"kind":"turn_started"}
                powershell -NoProfile -ExecutionPolicy Bypass -File %CODEX_HELPER_TEST_WRITE_SCRIPT%
                exit /b 0
                """);
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            var task = Path.Combine(root, "p", ".codex-helper", "runs", "run-final-steps");
            Directory.CreateDirectory(task);
            foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                await File.WriteAllTextAsync(Path.Combine(task, contract), contract == "manifest.json" ? "{}" : "test");
            var result = await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), Path.Combine(root, "p"), task, Path.Combine(root, "rh"), "", "thread-final", taskDir: task, writeScriptPath: writeScript);
            Assert(result.ExitCode == 0, "final-steps runner 应正常结束：" + result.Output);
            var status = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-final-steps.json")), options)!;
            Assert(status.State == "completed", "final-steps 任务应完成：" + status.State);
            Assert(status.StepCount == 7, "完成后 StepCount 应取 metrics.json 的实际步骤：" + status.StepCount);
            Assert(status.ModelTurnCount == 2, "ModelTurnCount 应记录模型轮次：" + status.ModelTurnCount);
            var review = await File.ReadAllTextAsync(Path.Combine(task, "REVIEW_PACKET.md"));
            Assert(review.Contains("Model turns", StringComparison.Ordinal) && review.Contains("Final metrics steps: 7", StringComparison.Ordinal), "Review Packet 应同时标明 model turns 与 final metrics steps。");

            // 未知 estimatedSteps：写不泄密诊断并按合同推断预算，绝不伪装成声明的 35。
            var estTask = Path.Combine(root, "p", ".codex-helper", "runs", "run-estimated");
            Directory.CreateDirectory(estTask);
            foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                await File.WriteAllTextAsync(Path.Combine(estTask, contract), contract == "manifest.json" ? """{"estimatedSteps":35}""" : "test");
            var estResult = await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), Path.Combine(root, "p"), estTask, Path.Combine(root, "rh"), "", "thread-est", taskDir: estTask, writeScriptPath: writeScript);
            Assert(estResult.ExitCode == 0, "estimatedSteps runner 应正常结束：" + estResult.Output);
            var estStatus = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-estimated.json")), options)!;
            Assert(estStatus.ManifestDiagnostic != null && estStatus.ManifestDiagnostic.Contains("estimatedSteps", StringComparison.Ordinal), "未知 estimatedSteps 应写诊断：" + (estStatus.ManifestDiagnostic ?? "无"));
            Assert(estStatus.EstimatedSteps != 35, "estimatedSteps 不得伪装成预算：" + estStatus.EstimatedSteps);
            service.Disable();
        });
    }

    private static async Task TestGuiSmokeFixtureAsync()
    {
        var fixture = Path.Combine(Directory.GetCurrentDirectory(), "tests", "fixtures", "gui-smoke");
        var settingsPath = Path.Combine(fixture, "app", "settings.json");
        var configPath = Path.Combine(fixture, "codex", "config.toml");
        var settingsBytes = await File.ReadAllBytesAsync(settingsPath);
        var settingsText = new UTF8Encoding(false, true).GetString(settingsBytes);
        Assert(!settingsText.Contains('\uFFFD'), "GUI smoke settings must be valid UTF-8 without replacement characters.");
        var settings = JsonStore.Deserialize<AppSettings>(settingsBytes);
        Assert(settings.HasCompletedOnboarding && settings.LastSelectedPage == "Collaboration", "GUI smoke fixture must open directly on the Collaboration page.");
        Assert(settings.HasCompletedOnboarding, "GUI smoke fixture must have completed onboarding so the window opens without the guide overlay.");
        Assert(settings.LastSelectedPage != "Connections", "GUI smoke fixture must open the Collaboration page, not the Connection Center.");
        Assert(settings.CodexRoot.Contains("实用软件开发", StringComparison.Ordinal) && Directory.Exists(settings.CodexRoot), "GUI smoke fixture Codex root is invalid.");
        _ = TomlConfigurationDocument.Parse(await File.ReadAllLinesAsync(configPath));
    }

    private static async Task TestThinReleasePolicyStaticAsync()
    {
        // 从 CWD 向上定位仓库根（含 CodexHelper.sln）。
        var root = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(root, "CodexHelper.sln")))
        {
            var parent = Path.GetDirectoryName(root);
            if (parent is null) throw new InvalidOperationException("无法定位仓库根：" + Directory.GetCurrentDirectory());
            root = parent;
        }

        // 版本源必须是 3.4.0。
        var props = await File.ReadAllTextAsync(Path.Combine(root, "Directory.Build.props"));
        var match = System.Text.RegularExpressions.Regex.Match(props, @"<Version>([^<]+)</Version>");
        Assert(match.Success && match.Groups[1].Value == "4.0.0", "版本源必须为 4.0.0，实际：" + (match.Success ? match.Groups[1].Value : "未找到"));

        // 安装器：含微软官方链接、无 full/portable 旧引导、运行库检测不依赖单一目录。
        var iss = await File.ReadAllTextAsync(Path.Combine(root, "installer", "CodexHelperRuntimeRequired.iss"));
        Assert(iss.Contains("https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0", StringComparison.Ordinal), "安装器必须包含微软官方 .NET 8 下载链接。");
        Assert(!iss.Contains("完整离线安装包", StringComparison.Ordinal) && !iss.Contains("便携 ZIP", StringComparison.Ordinal) && !iss.Contains("setup-full", StringComparison.OrdinalIgnoreCase), "安装器中文文案不得再引导完整离线包或便携 ZIP。");
        Assert(iss.Contains("RegGetValueNames", StringComparison.Ordinal) && iss.Contains(@"InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App", StringComparison.Ordinal), "运行库检测应枚举注册表已登记版本，而非仅依赖键存在或单个固定目录。");
        Assert(iss.Contains("Copy(VersionNames[i], 1, 2) = '8.'", StringComparison.Ordinal), "运行库检测应仅接受以 8. 开头的主版本，避免把只装 9/10 的电脑误判为已装 8。");

        // 精简发布脚本：引用精简 iss、不生成 full/portable。
        var release = await File.ReadAllTextAsync(Path.Combine(root, "scripts", "build-release.ps1"));
        Assert(release.Contains("CodexHelperRuntimeRequired.iss", StringComparison.Ordinal), "精简发布必须使用精简安装器脚本。");
        Assert(!release.Contains("CodexHelper.iss", StringComparison.Ordinal) && !release.Contains("portable", StringComparison.OrdinalIgnoreCase) && !release.Contains("self-contained true", StringComparison.OrdinalIgnoreCase), "精简发布入口不得生成或选入 full/portable 资产。");
        Assert(release.Contains("codex-helper-v$version-setup.exe", StringComparison.Ordinal) && release.Contains("sha256", StringComparison.Ordinal), "精简发布必须产出版本化 setup 与 SHA-256。");

        // README：开发版本 3.4.0，但首页下载区必须指向已发布的 v3.3.3（不得生成虚假的 v3.4.0 Release 下载链接）。
        var readme = await File.ReadAllTextAsync(Path.Combine(root, "README.md"));
        Assert(readme.Contains("当前开发版本：`4.0.0`", StringComparison.Ordinal), "README 当前开发版本应为 4.0.0。");
        Assert(readme.Contains("releases/download/v4.0.0/codex-helper-v4.0.0-setup.exe", StringComparison.Ordinal) && readme.Contains("releases/tag/v4.0.0", StringComparison.Ordinal), "README 首页应指向 v4.0.0 正式 Release 与版本化安装包。");
        Assert(readme.Contains("https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0", StringComparison.Ordinal), "README 下载区应提供微软官方 .NET 8 下载页。");
        Assert(!readme.Contains("setup-full", StringComparison.OrdinalIgnoreCase) && !readme.Contains("portable.zip", StringComparison.OrdinalIgnoreCase), "README 不得再推荐 full/portable 下载。");
    }

    private static async Task TestReasonixStandardJsonRoundTripAsync()
    {
        await WithTempDirectoryAsync("reasonix-status-json", async root =>
        {
            // 中文项目路径、引号、反斜杠与换行文本：标准 JSON 往返必须无损。
            var status = new ReasonixTaskStatus(
                "run-中文任务", @"C:\实用 软件\codex-helper", @"C:\实用 软件\codex-helper\.codex-helper\runs\run-中文任务",
                "running", "executing", "Full", DateTime.UtcNow.AddMinutes(-3), DateTime.UtcNow, 42, 5,
                "消息含引号 \"双引号\" 与 反斜杠 C:\\dir\\file 与换行\n第二行",
                CodexThreadId: "019f89ac-b333-78a0-8b77-f7c5925f7052",
                ExecutionModel: "opencode/deepseek-v4-flash",
                EstimatedSteps: 16, StepCount: 3, ModelTurnCount: 3, CompletedChecks: 1, TotalChecks: 4,
                RemainingPercent: 25, ContractDiagnostic: "合同已归一化；中文摘要", ContractNormalized: true);
            var statusPath = Path.Combine(root, "run-中文任务.json");
            ReasonixStatusJson.WriteStatus(statusPath, status);

            // 1) System.Text.Json 标准读取无损。
            var restored = ReasonixStatusJson.TryReadStatus(statusPath);
            Assert(restored is not null, "标准 System.Text.Json 应能读取新写状态。");
            Assert(restored!.ProjectRoot == status.ProjectRoot && restored.TaskId == "run-中文任务", "中文路径必须往返无损：" + restored.ProjectRoot);
            Assert(restored.Message.Contains("双引号") && restored.Message.Contains(@"C:\dir\file") && restored.Message.Contains("第二行"), "引号/反斜杠/换行文本必须往返无损。");
            Assert(restored.RemainingPercent == 25 && restored.ContractNormalized == true, "新字段应往返无损。");
            Assert(restored.StartedUtc == status.StartedUtc && restored.UpdatedUtc == status.UpdatedUtc, "日期字段应往返无损。");

            // 2) 无 BOM UTF-8。
            var bytes = await File.ReadAllBytesAsync(statusPath);
            Assert(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "状态文件必须无 BOM。");

            // 3) PowerShell ConvertFrom-Json 可读（兼容性要求）。
            await AssertPowerShellReadsJsonAsync(statusPath);

            // 4) 损坏/空状态宽容读取：返回 null，绝不抛异常。
            var corruptPath = Path.Combine(root, "corrupt.json");
            await File.WriteAllTextAsync(corruptPath, "{ 不是合法 json", new UTF8Encoding(false));
            Assert(ReasonixStatusJson.TryReadStatus(corruptPath) is null, "损坏状态应宽容返回 null。");
            await File.WriteAllTextAsync(corruptPath, "", new UTF8Encoding(false));
            Assert(ReasonixStatusJson.TryReadStatus(corruptPath) is null, "空状态应宽容返回 null。");
            Assert(ReasonixStatusJson.TryReadStatus(Path.Combine(root, "missing.json")) is null, "缺失状态应宽容返回 null。");

            // 5) 历史状态文件含未转义 Windows 反斜杠路径与中文路径：先标准 JSON 失败，再兼容读取。
            //    \n 是合法转义必须保留（不得吞掉），路径里的 \a \d 等非法转义则被补成 \\。
            var legacyPath = Path.Combine(root, "legacy.json");
            await File.WriteAllTextAsync(legacyPath,
                "{\"TaskId\":\"run-legacy\",\"ProjectRoot\":\"C:\\项目\\dirA\\dirB\",\"Message\":\"第一行\\n第二行 路径 C:\\abc\\def\"}",
                new UTF8Encoding(false));
            var legacy = ReasonixStatusJson.TryReadStatus(legacyPath);
            Assert(legacy is not null, "未转义反斜杠的历史状态应被兼容读取。");
            Assert(legacy!.ProjectRoot == @"C:\项目\dirA\dirB", "未转义中文路径应被兼容恢复：" + legacy.ProjectRoot);
            Assert(legacy.Message.Contains("第一行\n第二行", StringComparison.Ordinal), "合法转义 \\n 不得被吞掉（应保留为换行）。");
            Assert(legacy.Message.Contains(@"C:\abc\def", StringComparison.Ordinal), "未转义反斜杠路径应被兼容恢复：" + legacy.Message);
            // 兼容读取不得修改原文件：原内容仍是未转义反斜杠。
            Assert(File.ReadAllText(legacyPath).Contains(@"C:\项目\dirA\dirB", StringComparison.Ordinal), "兼容读取不得修改原文件。");
        });
    }

    private static async Task TestReasonixRemainingMonotonicAsync()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        await WithTempDirectoryAsync("reasonix-remaining", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var writeScript = Path.Combine(root, "wremaining.ps1");
            await File.WriteAllTextAsync(writeScript, """
                param([string]$Action)
                $now=[DateTime]::UtcNow.ToString('o')
                switch ($Action) {
                  'progress3' { [IO.File]::WriteAllText((Join-Path $env:CODEX_HELPER_TEST_TASK 'PROGRESS.json'), '{"taskId":"run-rem","stage":"implementing","updatedUtc":"' + $now + '","completedChecks":3,"totalChecks":4}', [Text.UTF8Encoding]::new($false)) }
                  'progress1' { [IO.File]::WriteAllText((Join-Path $env:CODEX_HELPER_TEST_TASK 'PROGRESS.json'), '{"taskId":"run-rem","stage":"implementing","updatedUtc":"' + $now + '","completedChecks":1,"totalChecks":4}', [Text.UTF8Encoding]::new($false)) }
                  'report' { [IO.File]::WriteAllText((Join-Path $env:CODEX_HELPER_TEST_TASK 'EXECUTION_REPORT.md'), 'done', [Text.UTF8Encoding]::new($false)) }
                }
                """, new UTF8Encoding(true));
            var executable = Path.Combine(root, "reasonix-cli.cmd");
            await File.WriteAllTextAsync(executable, """
                @echo off
                setlocal EnableDelayedExpansion
                echo {"kind":"turn_started"}
                powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Sleep -Milliseconds 400"
                powershell -NoProfile -ExecutionPolicy Bypass -File %CODEX_HELPER_TEST_WRITE_SCRIPT% progress3
                echo {"kind":"turn_started"}
                powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Sleep -Milliseconds 400"
                powershell -NoProfile -ExecutionPolicy Bypass -File %CODEX_HELPER_TEST_WRITE_SCRIPT% progress1
                echo {"kind":"turn_started"}
                powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Sleep -Milliseconds 400"
                powershell -NoProfile -ExecutionPolicy Bypass -File %CODEX_HELPER_TEST_WRITE_SCRIPT% report
                exit /b 0
                """);
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            var project = Path.Combine(root, "project");
            var task = Path.Combine(project, ".codex-helper", "runs", "run-rem");
            Directory.CreateDirectory(task);
            await File.WriteAllTextAsync(Path.Combine(task, "SPEC.md"), "spec");
            await File.WriteAllTextAsync(Path.Combine(task, "ACCEPTANCE.md"), "- a");
            await File.WriteAllTextAsync(Path.Combine(task, "HANDOFF.md"), "允许读取：src；允许修改：src；直接依赖：无。");
            await File.WriteAllTextAsync(Path.Combine(task, "manifest.json"), """{"budgetSteps":16,"intensity":"fast"}""");
            var statusPath = Path.Combine(app.ReasonixTasksDirectory, "run-rem.json");

            // 第一次运行：步骤源 94 → workerChecks 源 25（下降）→ 再写回退进度不得回升（min 单调保持 25）。
            var result = await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), project, task, Path.Combine(root, "rh"), "", "thread-rem", taskDir: task, writeScriptPath: writeScript);
            Assert(result.ExitCode == 0, "单调场景 runner 应正常结束：" + result.Output);
            var status1 = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(statusPath), options)!;
            Assert(status1.RemainingPercent == 25, $"进度源切换后剩余应单调到 25（3/4 检查完成），实际：{status1.RemainingPercent}。");
            Assert(status1.State == "completed", "单调场景任务应完成：" + status1.State);

            // 第二次运行（同 attempt 重启恢复）：仅步骤源 1/16（候选 94），必须继承 25 而不回升。
            var executable2 = Path.Combine(root, "reasonix-cli2.cmd");
            await File.WriteAllTextAsync(executable2, "@echo off\r\necho {\"kind\":\"turn_started\"}\r\nif defined CODEX_HELPER_TEST_TASK ( echo done>\"%CODEX_HELPER_TEST_TASK%\\EXECUTION_REPORT.md\" )\r\nexit /b 0\r\n");
            service.Enable(executable2, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            var result2 = await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), project, task, Path.Combine(root, "rh"), "", "thread-rem2", taskDir: task);
            Assert(result2.ExitCode == 0, "重启恢复 runner 应正常结束：" + result2.Output);
            var status2 = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(statusPath), options)!;
            Assert(status2.RemainingPercent is <= 25, $"重启恢复不得回升（应保持 ≤25），实际：{status2.RemainingPercent}。");
            service.Disable();
        });
    }

    private static async Task TestReasonixProgressChecksProtocolAsync()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        await WithTempDirectoryAsync("reasonix-progress-checks", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var executable = Path.Combine(root, "reasonix-cli.cmd");
            await File.WriteAllTextAsync(executable, "@echo off\r\nexit /b 0\r\n");
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);

            async Task<ReasonixTaskStatus> RunCaseAsync(string id, string progressText, bool preStale = false)
            {
                var project = Path.Combine(root, "p" + id);
                var task = Path.Combine(project, ".codex-helper", "runs", id);
                Directory.CreateDirectory(task);
                foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                    await File.WriteAllTextAsync(Path.Combine(task, contract), contract == "manifest.json" ? "{}" : "test");
                // 陈旧用例：updatedUtc 固定为任务开始前 1 小时（先写 PROGRESS 再启动 runner）。
                var progress = preStale
                    ? "{\"stage\":\"testing\",\"updatedUtc\":\"" + DateTime.UtcNow.AddHours(-1).ToString("o") + "\"}"
                    : progressText;
                await File.WriteAllTextAsync(Path.Combine(task, "PROGRESS.json"), progress, new UTF8Encoding(false));
                var result = await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), project, task, Path.Combine(root, "rh"), "", "thread-" + id, taskDir: task);
                Assert(result.ExitCode == 0, $"case {id} 不应中断任务：" + result.Output);
                return JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, id + ".json")), options)!;
            }

            // 标准 checks 数组 + currentCheck：passed 计数排除视觉/GPT 项，total 只统计合法状态项。
            var withChecks = await RunCaseAsync("run-checks", """{"stage":"testing","currentCheck":"Run the project test script once","checks":[{"name":"build","status":"passed"},{"name":"截图验收","status":"passed"},{"name":"test","status":"running"},{"name":"plain","status":"bogus"}]}""");
            Assert(withChecks.TotalChecks == 3, $"checks 数组 total 应只统计合法状态项：{withChecks.TotalChecks}。");
            Assert(withChecks.CompletedChecks == 1, $"passed 计数必须排除视觉/GPT 检查（build=1，截图验收不计）：{withChecks.CompletedChecks}。");
            Assert(withChecks.CurrentCheck == "Run the project test script once", "currentCheck 应持久化：" + (withChecks.CurrentCheck ?? "无"));

            // 视觉项全部 passed：Helper 不得把它计为 worker 完成。
            var visualOnly = await RunCaseAsync("run-visualonly", """{"stage":"testing","checks":[{"name":"截图验收","status":"passed"},{"name":"发布安装包","status":"passed"}]}""");
            Assert(visualOnly.TotalChecks == 2 && visualOnly.CompletedChecks == 0, $"视觉/GPT 项不得计为 worker 完成：{visualOnly.CompletedChecks}/{visualOnly.TotalChecks}。");

            // 陈旧内容安全忽略：updatedUtc 早于任务开始 → 阶段不采信，诊断提示。
            var stale = await RunCaseAsync("run-stale", "", preStale: true);
            Assert(stale.ProgressDiagnostic != null && stale.ProgressDiagnostic.Contains("陈旧", StringComparison.Ordinal), "陈旧 PROGRESS 应产生诊断：" + (stale.ProgressDiagnostic ?? "无"));
            Assert(stale.ProgressStage != "testing", "陈旧 PROGRESS 的阶段不得被采信：" + (stale.ProgressStage ?? "无"));
            service.Disable();
        });
    }

    private static Task TestReasonixContractHealthAsync()
    {
        // ---- C# 纯函数：体检与归一化规则 ----
        // 肯定式 HANDOFF 违规：要求读 ACCEPTANCE + 写 REVIEW_PACKET → 归一化诊断。
        var violating = ReasonixContractHealth.Inspect(null, "Reasonix 必须读取 ACCEPTANCE.md 并写 REVIEW_PACKET.md。", null, false);
        Assert(violating.Normalized && !violating.Blocked, "HANDOFF 要求读 ACCEPTANCE/写 REVIEW_PACKET 应归一化而非阻止。");
        Assert(violating.Diagnostics.Any(d => d.Contains("ACCEPTANCE", StringComparison.Ordinal)) && violating.Diagnostics.Any(d => d.Contains("REVIEW_PACKET", StringComparison.Ordinal)), "归一化诊断应覆盖两项矛盾执行要求。");

        // 否定式约束说明不误报。
        var negated = ReasonixContractHealth.Inspect(null, "Reasonix 不读取 ACCEPTANCE.md，不写 REVIEW_PACKET.md。", null, false);
        Assert(!negated.Normalized, "否定式约束说明不得触发归一化诊断。");

        // workerChecks 重复去重 + 视觉职责移交。
        var dup = ReasonixContractHealth.Inspect(null, "允许读取：a；允许修改：b；直接依赖：c。", ["build", "build", "test", "screenshot the app"], false);
        Assert(dup.DeduplicatedChecks.Count == 3 && dup.DeduplicatedChecks[0] == "build" && dup.DeduplicatedChecks[1] == "test", "重复 workerChecks 应去重保留首个。");
        Assert(dup.Diagnostics.Any(d => d.Contains("重复", StringComparison.Ordinal)), "去重应产生诊断。");
        Assert(dup.Diagnostics.Any(d => d.Contains("移交 GPT", StringComparison.Ordinal)), "视觉 workerChecks 应移交 GPT。");
        Assert(dup.Normalized, "去重/职责过滤应标记归一化。");

        // delivery profile：托管强制 balanced。
        var delivery = ReasonixContractHealth.Inspect(new ReasonixManifestPolicy(null, "delivery", null, null, null, null, null, null, null), "允许读取：a；允许修改：b；直接依赖：c。", null, false);
        Assert(delivery.Diagnostics.Any(d => d.Contains("balanced", StringComparison.Ordinal)), "delivery profile 应产生强制 balanced 诊断。");

        // 普通 small/medium DeepSeek 显式 high/max：合同预检规范——派生运行计划 effort 降为 low。
        var deepSeek = ReasonixContractHealth.Inspect(new ReasonixManifestPolicy("small", null, "high", null, null, null, null, null, null), "允许读取：a；允许修改：b；直接依赖：c。", null, true);
        Assert(deepSeek.Diagnostics.Any(d => d.Contains("high", StringComparison.Ordinal)), "small DeepSeek 显式 high 应产生降级诊断。");
        Assert(deepSeek.Normalized, "small DeepSeek 显式 high 应标记归一化（派生 effort 降为 low）。");
        Assert(deepSeek.Diagnostics.Any(d => d.Contains("降为 low", StringComparison.Ordinal)), "降级诊断应说明派生 effort 降为 low。");
        // strict 保留 high，不降级。
        var deepSeekStrict = ReasonixContractHealth.Inspect(new ReasonixManifestPolicy("small", null, "high", "strict", null, null, null, null, null), "允许读取：a；允许修改：b；直接依赖：c。", null, true);
        Assert(!deepSeekStrict.Diagnostics.Any(d => d.Contains("降为 low", StringComparison.Ordinal)), "strict DeepSeek 不应降级 effort。");

        // HANDOFF 缺少范围。
        var missingScope = ReasonixContractHealth.Inspect(null, "h", null, false);
        Assert(missingScope.Diagnostics.Any(d => d.Contains("缺少", StringComparison.Ordinal) && d.Contains("直接依赖", StringComparison.Ordinal)), "HANDOFF 缺范围应产生诊断。");

        // 无法安全修正的阻止：HANDOFF 要求 Reasonix 截图交付。
        var blocked = ReasonixContractHealth.Inspect(null, "Reasonix 必须截图交付验收证据。", null, false);
        Assert(blocked.Blocked && blocked.BlockReason!.Contains("截图", StringComparison.Ordinal), "要求 Reasonix 截图交付应阻止并给出中文原因。");

        // ---- PowerShell job host 集成：blocked 合同阻止启动并落盘诊断 ----
        return WithTempDirectoryAsync("reasonix-contract-health", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var executable = Path.Combine(root, "reasonix-cli.cmd");
            await File.WriteAllTextAsync(executable, "@echo off\r\nexit /b 0\r\n");
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            var project = Path.Combine(root, "project");
            var task = Path.Combine(project, ".codex-helper", "runs", "run-blocked");
            Directory.CreateDirectory(task);
            await File.WriteAllTextAsync(Path.Combine(task, "SPEC.md"), "spec");
            await File.WriteAllTextAsync(Path.Combine(task, "ACCEPTANCE.md"), "- a");
            await File.WriteAllTextAsync(Path.Combine(task, "HANDOFF.md"), "Reasonix 必须截图交付验收证据。");
            await File.WriteAllTextAsync(Path.Combine(task, "manifest.json"), "{}");
            var result = await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), project, task, Path.Combine(root, "rh"), "", "thread-blocked", taskDir: task);
            Assert(result.ExitCode != 0, "blocked 合同 runner 应非零退出：" + result.Output);
            var status = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-blocked.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert(string.Equals(status.State, "failed", StringComparison.OrdinalIgnoreCase) && status.Phase == "blocked", "blocked 合同状态应为 failed/blocked：" + status.State + "/" + status.Phase);
            Assert(status.FailureKind == "contract-blocked", "blocked 合同失败类型应为 contract-blocked：" + status.FailureKind);
            Assert(status.Message.Contains("截图", StringComparison.Ordinal), "blocked 合同消息应含中文原因。");
            Assert(File.Exists(Path.Combine(task, "FAILURE_REPORT.md")), "blocked 合同应生成 FAILURE_REPORT.md。");

            // ---- 归一化落盘：delivery profile 强制 balanced + workerChecks 去重 + 职责过滤 ----
            var reasonixHome = Path.Combine(root, "rh");
            Directory.CreateDirectory(reasonixHome);
            await File.WriteAllTextAsync(Path.Combine(reasonixHome, "config.toml"), "default_model = \"opencode/deepseek-v4-flash\"\n");
            var project2 = Path.Combine(root, "project2");
            var task2 = Path.Combine(project2, ".codex-helper", "runs", "run-normalized");
            Directory.CreateDirectory(task2);
            await File.WriteAllTextAsync(Path.Combine(task2, "SPEC.md"), "spec");
            await File.WriteAllTextAsync(Path.Combine(task2, "ACCEPTANCE.md"), "- a");
            await File.WriteAllTextAsync(Path.Combine(task2, "HANDOFF.md"), "允许读取：src；允许修改：src；直接依赖：无。");
            await File.WriteAllTextAsync(Path.Combine(task2, "manifest.json"), """{"profile":"delivery","workerChecks":["build","build","screenshot the app"]}""");
            var result2 = await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), project2, task2, reasonixHome, "", "thread-norm", taskDir: task2);
            Assert(result2.ExitCode == 0, "归一化合同 runner 应正常结束：" + result2.Output);
            var normalized = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-normalized.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert(normalized.ContractNormalized == true, "delivery/去重/职责过滤应标记合同已归一化。");
            Assert(normalized.ContractDiagnostic != null
                && normalized.ContractDiagnostic.Contains("balanced", StringComparison.Ordinal)
                && normalized.ContractDiagnostic.Contains("重复", StringComparison.Ordinal)
                && normalized.ContractDiagnostic.Contains("移交 GPT", StringComparison.Ordinal), "归一化诊断应覆盖 delivery/去重/职责过滤：" + (normalized.ContractDiagnostic ?? "无"));
            var workerAccept2 = await File.ReadAllTextAsync(Path.Combine(task2, "WORKER_ACCEPTANCE.md"));
            var buildLines = workerAccept2.Split('\n').Count(line => line.TrimEnd() == "- build");
            Assert(buildLines == 1, "WORKER_ACCEPTANCE 应去重 build 只保留一项。内容：" + workerAccept2);
            Assert(!workerAccept2.Contains("screenshot the app", StringComparison.Ordinal), "视觉 workerCheck 应整体移交 GPT，不披露正文。内容：" + workerAccept2);
            service.Disable();
        });
    }

    private static Task TestReasonixAcceptanceScopeAsync()
    {
        // 文案/文档 → focused。
        var docs = ReasonixAcceptanceScope.Recommend(["README.md", "docs/USER_GUIDE_zh-CN.md"]);
        Assert(docs.Scopes.SequenceEqual(["focused"]), "纯文档应建议 focused：" + docs.Label);

        // 单个策略/纯函数文件 → focused。
        var single = ReasonixAcceptanceScope.Recommend(["src/CodexHelper.Core/Services/ReasonixUiText.cs"]);
        Assert(single.Scopes.SequenceEqual(["focused"]), "单文件应建议 focused：" + single.Label);

        // UI/XAML → focused + visual。
        var xaml = ReasonixAcceptanceScope.Recommend(["src/CodexHelper.App/MainWindow.xaml", "src/CodexHelper.App/MainWindow.xaml.cs"]);
        Assert(xaml.Scopes.Contains("focused") && xaml.Scopes.Contains("visual"), "UI/XAML 应建议 focused + visual：" + xaml.Label);

        // 凭据/加密/备份/迁移 → security + full。
        var security = ReasonixAcceptanceScope.Recommend(["src/CodexHelper.Core/Security/CryptoEnvelope.cs"]);
        Assert(security.Scopes.Contains("security") && security.Scopes.Contains("full"), "凭据/加密应建议 security + full：" + security.Label);

        // installer/版本/发布脚本 → release + full。
        var release = ReasonixAcceptanceScope.Recommend(["Directory.Build.props", "scripts/build-release.ps1"]);
        Assert(release.Scopes.Contains("release") && release.Scopes.Contains("full"), "版本/发布脚本应建议 release + full：" + release.Label);

        // 多核心模块 → full。
        var multi = ReasonixAcceptanceScope.Recommend(["src/CodexHelper.Core/Services/A.cs", "src/CodexHelper.Core/Services/B.cs", "src/CodexHelper.Core/Services/C.cs"]);
        Assert(multi.Scopes.SequenceEqual(["full"]), "多核心模块应建议 full：" + multi.Label);

        // 普通 scripts/ 变化不再自动归类 release（仅安装/发布/版本/CI workflow 命中 release）。
        var scriptsOnly = ReasonixAcceptanceScope.Recommend(["scripts/common.ps1", "scripts/build.ps1"]);
        Assert(!scriptsOnly.Scopes.Any(s => s == "release") && scriptsOnly.Scopes.Any(s => s == "focused"), "普通 scripts/ 变化不应自动归类 release：" + scriptsOnly.Label);
        var buildReleaseScript = ReasonixAcceptanceScope.Recommend(["scripts/build-release.ps1"]);
        Assert(buildReleaseScript.Scopes.Any(s => s == "release"), "发布脚本仍应建议 release：" + buildReleaseScript.Label);

        // 纯测试 → focused。
        var tests = ReasonixAcceptanceScope.Recommend(["tests/CodexHelper.Core.Tests/Program.cs", "tests/CodexHelper.Core.Tests/TestHelper.cs"]);
        Assert(tests.Scopes.SequenceEqual(["focused"]), "纯测试改动应建议 focused：" + tests.Label);

        // 少量普通源码 → focused。
        var twoSrc = ReasonixAcceptanceScope.Recommend(["src/CodexHelper.Core/Services/A.cs", "src/CodexHelper.Core/Services/B.cs"]);
        Assert(twoSrc.Scopes.SequenceEqual(["focused"]), "少量普通源码应建议 focused：" + twoSrc.Label);

        // 无法识别（空列表）→ full。
        var empty = ReasonixAcceptanceScope.Recommend(null);
        Assert(empty.Scopes.SequenceEqual(["full"]), "无法识别应建议 full：" + empty.Label);

        return Task.CompletedTask;
    }

    private static async Task AssertPowerShellReadsJsonAsync(string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.Environment["CODEX_HELPER_JSON_TO_READ"] = path;
        start.ArgumentList.Add("$p=Get-Content -LiteralPath $env:CODEX_HELPER_JSON_TO_READ -Raw -Encoding UTF8|ConvertFrom-Json; if($null-eq$p){exit 1}; if($null-eq$p.TaskId){exit 2}; [Console]::WriteLine($p.TaskId)");
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert(process.ExitCode == 0, "PowerShell ConvertFrom-Json 无法读取状态文件：" + await stdout + await stderr);
    }

    private static async Task TestReasonixIntegrationAsync()
    {
        await WithTempDirectoryAsync("r", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            var app = new AppPaths(Path.Combine(root, "app"));
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep-this-rule\n");
            var executable = Path.Combine(root, "reasonix-cli.cmd");
            await File.WriteAllTextAsync(executable, "@echo off\r\necho {}>\"%CODEX_HELPER_TEST_SESSION_PATH%\"\r\necho {\"type\":\"event\"}\r\nexit /b 0\r\n");

            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);

            var skillRoot = Path.Combine(codexRoot, "skills", "reasonix-executor");
            var guidance = await File.ReadAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"));
            var runner = await File.ReadAllTextAsync(Path.Combine(skillRoot, "invoke-reasonix.ps1"));
            var jobHost = await File.ReadAllTextAsync(Path.Combine(skillRoot, "run-reasonix-job.ps1"));
            var skillDoc = await File.ReadAllTextAsync(Path.Combine(skillRoot, "SKILL.md"));
            await AssertPowerShellParsesAsync(Path.Combine(skillRoot, "invoke-reasonix.ps1"));
            await AssertPowerShellParsesAsync(Path.Combine(skillRoot, "run-reasonix-job.ps1"));
            Assert(service.IsEnabled(), "Reasonix integration should be enabled after installation.");
            Assert(File.Exists(Path.Combine(skillRoot, "SKILL.md")), "Managed Reasonix skill is missing.");
            Assert(guidance.Contains(ReasonixIntegrationService.GuidanceStart), "Managed Reasonix guidance is missing.");
            Assert(!guidance.Contains("default_subagent_model", StringComparison.OrdinalIgnoreCase) && !guidance.Contains("[agents]", StringComparison.OrdinalIgnoreCase), "Native subagent configuration must not be enabled.");
            Assert(runner.Contains("SPEC.md") && runner.Contains("ACCEPTANCE.md") && runner.Contains("manifest.json"), "Runner contract files are incomplete.");
            Assert(runner.Contains("Start-Process") && runner.Contains("WindowStyle Hidden") && runner.Contains("WaitForExit") && runner.Contains("same turn", StringComparison.OrdinalIgnoreCase), "Runner must wait efficiently so the same GPT turn resumes for acceptance.");
            Assert(runner.Contains("no command timeout", StringComparison.OrdinalIgnoreCase), "Runner instructions must mandate no command timeout / infinite wait.");
            Assert(!runner.Contains("a one-hour timeout", StringComparison.OrdinalIgnoreCase) && !guidance.Contains("a one-hour timeout", StringComparison.OrdinalIgnoreCase), "Managed guidance must not recommend a fixed one-hour timeout.");
            Assert(jobHost.Contains(".reasonix.lock") && jobHost.Contains("events.jsonl") && jobHost.Contains("metrics.json") && jobHost.Contains("REVIEW_PACKET.md"), "Task host isolation or evidence files are incomplete.");
            Assert(jobHost.Contains("Register-DesktopSession") && jobHost.Contains("desktop-projects.json") && jobHost.Contains("desktop-tabs.json") && jobHost.Contains("ReasonixSessionPath"), "Reasonix Desktop session registration is incomplete.");
            Assert(jobHost.Contains("Get-SessionBaseline") && jobHost.Contains("Find-NewSession") && jobHost.Contains("Find-ResumedSession"), "Task host must record a session baseline and bind only new/resumed sessions.");
            Assert(runner.Contains("CodexThreadId") && jobHost.Contains("same-turn-resume"), "Original Codex task identity or same-turn completion state is missing.");
            Assert(jobHost.Contains("UpdatedUtc=[DateTime]::UtcNow.ToString('o')") && jobHost.Contains("StartedUtc=$startedText") && jobHost.Contains("$startedText=$started.ToString('o')"), "Task host must write ISO 8601 UTC dates instead of the default PowerShell /Date(...)/ format.");
            Assert(jobHost.Contains("'--permission-mode','auto'") && !jobHost.Contains("bypassPermissions"), "Full permission mode must use the Reasonix 1.19 compatible auto mode.");
            Assert(jobHost.IndexOf("$runArgs += $permissionArgs", StringComparison.Ordinal) < jobHost.IndexOf("$runArgs+=@('--events-jsonl','--metrics',$metrics,$prompt)", StringComparison.Ordinal), "Reasonix options must be appended before the final task prompt.");
            Assert(!runner.Contains(" --model ", StringComparison.Ordinal), "Runner must dynamically use the current Reasonix default model.");
            Assert(jobHost.Contains("'--profile',$script:planProfile") && jobHost.Contains("$cliEffort=if($script:planEffort -eq 'medium'){'high'}else{$script:planEffort}") && jobHost.Contains("'--effort',$cliEffort"), "Task host must map unsupported medium effort to high before invoking Reasonix 1.19.x.");
            Assert(!jobHost.Contains("--profile delivery", StringComparison.Ordinal), "Task host must not hard-code the delivery profile.");
            Assert(jobHost.Contains("Do not auto-start review, security-review, or explore subagents"), "Fast/Standard must forbid automatic review subagents in the managed prompt.");
            Assert(jobHost.Contains("workerChecks") && jobHost.Contains("gptChecks") && jobHost.Contains("releaseChecks"), "Acceptance split (worker/gpt/release checks) must be described in the managed prompt.");
            Assert(jobHost.Contains("WORKER_ACCEPTANCE.md") && jobHost.Contains("do not read ACCEPTANCE.md"), "Managed prompt must derive from WORKER_ACCEPTANCE.md and not read ACCEPTANCE.md.");
            Assert(jobHost.Contains("Test-IsGptOrReleaseCheck") && jobHost.Contains("Write-WorkerAcceptance") && jobHost.Contains("已移交 GPT"), "Task host must filter and derive the worker contract with a GPT-delegation note.");
            Assert(jobHost.Contains("final_readiness") && jobHost.Contains("final-readiness-blocked"), "Task host must detect and classify final-readiness events.");
            Assert(guidance.Contains(ReasonixIntegrationService.VisualBoundaryRule, StringComparison.Ordinal), "Managed AGENTS guidance must forbid Reasonix screenshots and defer visual acceptance to GPT.");
            Assert(skillDoc.Contains(ReasonixIntegrationService.VisualBoundaryRule, StringComparison.Ordinal), "Managed SKILL must forbid Reasonix visual acceptance.");
            Assert(jobHost.Contains(ReasonixIntegrationService.VisualBoundaryRule, StringComparison.Ordinal), "Managed task prompt must forbid Reasonix visual acceptance.");
            Assert(jobHost.Contains("GUI smoke testing runs at most once", StringComparison.Ordinal) && jobHost.Contains("skip it and hand it to GPT", StringComparison.Ordinal) && jobHost.Contains("PrintWindow", StringComparison.Ordinal), "Task prompt must cap GUI smoke at one attempt, defer visual workerChecks to GPT, and name forbidden capture APIs.");
            Assert(jobHost.Contains("helper_budget_notice"), "Task host must emit a soft budget notice without aborting.");
            Assert(jobHost.Contains("ExecutionModel=$script:reasonixModel"), "Task host must persist the resolved execution model for DeepSeek stats.");
            Assert(jobHost.Contains("do not write REVIEW_PACKET.md") && jobHost.Contains("write only that file"), "Managed prompt must make Reasonix write only EXECUTION_REPORT.md and never REVIEW_PACKET.md.");
            Assert(jobHost.Contains("never re-read a file that has not changed") && jobHost.Contains("parallel reads are fine") && jobHost.Contains("one consolidated edit set"), "Managed prompt must mandate centralized reading and batched editing.");
            Assert(jobHost.Contains("HANDOFF.md explicitly lists allowed-read files") && jobHost.Contains("do not recursively scan the tree"), "Managed prompt must require HANDOFF structure (allowed-read/allowed-write/direct-dependencies) and forbid recursive scanning once the goal is clear.");
            Assert(jobHost.Contains("Run each workerCheck at most once and deduplicate it") && jobHost.Contains("reporting/readiness phase when the affected files are unchanged"), "Managed prompt must deduplicate workerChecks across the reporting/readiness phase.");
            Assert(jobHost.Contains("# 否定约束优先"), "Task host PowerShell filter must apply negation-constraint handling.");
            Assert(jobHost.Contains("{$budget=16}") && jobHost.Contains("{$budget=35}") && jobHost.Contains("{$budget=56}"), "Task host must use the new default soft-budget tiers (16/35/56).");
            Assert(!jobHost.Contains("'layout',") && !jobHost.Contains("'image',") && !jobHost.Contains("'布局',") && !jobHost.Contains("'屏幕',"), "Task host PowerShell filter must drop the over-broad layout/image words that used to mis-delegate structural checks.");
            Assert(jobHost.Contains("$script:reasonixModel=Get-ReasonixModel") && jobHost.Contains("default_model") && jobHost.Contains("config.toml"), "Task host must read the current Reasonix default model.");
            Assert(!jobHost.Contains("$StatusPath+'.tmp'", StringComparison.Ordinal), "Save-Status must not use a fixed status temp file name.");
            Assert(jobHost.Contains("$StatusPath+'.status-'+[Guid]::NewGuid()", StringComparison.Ordinal) && jobHost.Contains("finally{") && jobHost.Contains("Remove-Item -LiteralPath $tmp"), "Save-Status must use a unique GUID temp name and clean up residuals in finally.");

            // 并发写探针：验证生成的 Save-Status 原子写行（唯一 GUID 临时名 + try/finally 清理）
            // 在高频并发写同一 StatusPath 时不冲突、不残留、最终状态 JSON 有效。
            var statusDir = Path.Combine(root, "status");
            Directory.CreateDirectory(statusDir);
            var concurrentStatusPath = Path.Combine(statusDir, "status.json");
            var probeScript = Path.Combine(skillRoot, "concurrent-status-probe.ps1");
            var probe = new StringBuilder();
            probe.AppendLine("param([string]$StatusPath,[string]$DataJson)");
            probe.AppendLine("$data = $DataJson | ConvertFrom-Json");
            probe.AppendLine("$i = 0");
            probe.AppendLine("while($i -lt 200){ $i++;");
            probe.AppendLine("  $tmp=$StatusPath+'.status-'+[Guid]::NewGuid().ToString('N')+'.tmp'");
            probe.AppendLine("  try{[IO.File]::WriteAllText($tmp,($data|ConvertTo-Json),[Text.UTF8Encoding]::new($false));Move-Item -LiteralPath $tmp -Destination $StatusPath -Force}");
            probe.AppendLine("  finally{if([IO.File]::Exists($tmp)){Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue}}");
            probe.AppendLine("}");
            await File.WriteAllTextAsync(probeScript, probe.ToString(), new UTF8Encoding(true));
            var probes = new List<Task<(int ExitCode, string Output)>>();
            for (var n = 0; n < 8; n++)
            {
                var captured = n;
                probes.Add(Task.Run(async () =>
                {
                    var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    start.ArgumentList.Add("-NoProfile");
                    start.ArgumentList.Add("-ExecutionPolicy");
                    start.ArgumentList.Add("Bypass");
                    start.ArgumentList.Add("-File");
                    start.ArgumentList.Add(probeScript);
                    start.ArgumentList.Add("-StatusPath");
                    start.ArgumentList.Add(concurrentStatusPath);
                    start.ArgumentList.Add("-DataJson");
                    start.ArgumentList.Add($@"{{""probe"":{captured},""ts"":{DateTime.UtcNow.Ticks}}}");
                    using var process = System.Diagnostics.Process.Start(start)!;
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    return (process.ExitCode, await stdout + await stderr);
                }));
            }
            foreach (var r in await Task.WhenAll(probes)) Assert(r.ExitCode == 0, "Concurrent status probe failed: " + r.Output);
            Assert(!Directory.EnumerateFiles(statusDir, "*.status-*.tmp").Any(), "Save-Status concurrent writes must not leave temp residuals.");
            Assert(JsonNode.Parse(await File.ReadAllTextAsync(concurrentStatusPath)) != null, "Concurrent status writes must leave a valid JSON status file.");
            Assert(service.GetPermissionMode() == ReasonixPermissionMode.Full, "Full permission mode should be persisted.");

            // ------------------------------------------------------------------
            // E2E: fake Reasonix 先输出事件、延迟创建当前项目会话、继续追加进度、
            // 最后生成报告；测试必须在进程结束前观察到会话绑定与文件增长。
            var project = Path.Combine(root, "p");
            var task = Path.Combine(project, ".codex-helper", "runs", "run-e2e");
            Directory.CreateDirectory(task);
            foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                await File.WriteAllTextAsync(Path.Combine(task, contract), contract == "manifest.json" ? "{}" : "test");
            var reasonixHome = Path.Combine(root, "rh");
            string Slug(string path) => System.Text.RegularExpressions.Regex.Replace(path.ToLowerInvariant(), @"[:\\/]+", "-");
            var sessionDirectory = Path.Combine(reasonixHome, "projects", Slug(project), "sessions");
            Directory.CreateDirectory(sessionDirectory);
            await File.WriteAllTextAsync(Path.Combine(reasonixHome, "desktop-projects.json"), "{\"projects\":[]}");
            await File.WriteAllTextAsync(Path.Combine(reasonixHome, "desktop-tabs.json"), "{\"tabs\":[],\"activeTab\":\"\"}");

            // 预建旧会话（基线内）：即使稍后 mtime 更新，也绝不能误绑到它。
            var oldSessionPath = Path.Combine(sessionDirectory, "old.jsonl");
            await File.WriteAllTextAsync(oldSessionPath, "old-session\n");
            File.SetLastWriteTimeUtc(oldSessionPath, DateTime.UtcNow.AddMinutes(-30));

            // 当前项目新会话：fake Reasonix 在事件流中间才创建，并继续追加内容。
            var newSessionPath = Path.Combine(sessionDirectory, "l.jsonl");
            var writeScript = Path.Combine(root, "we2e.ps1");
            await File.WriteAllTextAsync(writeScript, """
                param([string]$Action = 'create')
                switch ($Action) {
                  'create' { [IO.File]::WriteAllText($env:CODEX_HELPER_TEST_NEW_SESSION, '{}', [Text.UTF8Encoding]::new($false)) }
                  'grow'   { [IO.File]::AppendAllText($env:CODEX_HELPER_TEST_NEW_SESSION, 'grow' + [Environment]::NewLine, [Text.UTF8Encoding]::new($false)) }
                  'report' { [IO.File]::WriteAllText((Join-Path $env:CODEX_HELPER_TEST_TASK 'EXECUTION_REPORT.md'), 'done', [Text.UTF8Encoding]::new($false)) }
                }
                """);
            await File.WriteAllTextAsync(executable, """
                @echo off
                setlocal EnableDelayedExpansion
                echo {"kind":"turn_started"}
                echo {"kind":"reasoning","text":"plan"}
                powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Sleep -Milliseconds 700"
                powershell -NoProfile -ExecutionPolicy Bypass -File %CODEX_HELPER_TEST_WRITE_SCRIPT% create
                echo {"kind":"tool_dispatch","tool":"Bash"}
                echo {"kind":"usage","usage":{"input_tokens":500,"output_tokens":20,"cache_hit_tokens":100}}
                powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Sleep -Milliseconds 900"
                powershell -NoProfile -ExecutionPolicy Bypass -File %CODEX_HELPER_TEST_WRITE_SCRIPT% grow
                echo {"kind":"turn_started"}
                echo {"kind":"reasoning","text":"verify"}
                powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Sleep -Milliseconds 700"
                powershell -NoProfile -ExecutionPolicy Bypass -File %CODEX_HELPER_TEST_WRITE_SCRIPT% grow
                if defined CODEX_HELPER_TEST_TASK ( powershell -NoProfile -ExecutionPolicy Bypass -File %CODEX_HELPER_TEST_WRITE_SCRIPT% report )
                echo {"kind":"run_done","ok":true,"num_turns":2}
                exit /b 0
                """);

            var threadId = "019f89ac-b333-78a0-8b77-f7c5925f7052";
            var statusPath = Path.Combine(app.ReasonixTasksDirectory, "run-e2e.json");
            var runnerTask = Task.Run(() => RunPowerShellAsync(Path.Combine(skillRoot, "invoke-reasonix.ps1"), project, task, reasonixHome, newSessionPath, threadId, newSessionPath, task, writeScript));
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            ReasonixTaskStatus? liveStatus = null;
            long lengthAtBind = 0;
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
                if (File.Exists(statusPath))
                {
                    try { liveStatus = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(statusPath), options); }
                    catch { liveStatus = null; }
                    if (liveStatus is { HasBoundSession: true })
                    {
                        lengthAtBind = new FileInfo(liveStatus.ReasonixSessionPath!).Length;
                        break;
                    }
                }
            }
            Assert(liveStatus is { HasBoundSession: true }, "必须在 Reasonix（fake）进程结束前观察到 ReasonixSessionPath 被绑定：" + (liveStatus?.Message ?? "无状态"));
            Assert(!runnerTask.IsCompleted, "会话绑定时 fake Reasonix 进程必须仍在运行。");
            Assert(!string.Equals(liveStatus!.ReasonixSessionPath, oldSessionPath, StringComparison.OrdinalIgnoreCase), "绝不能误绑基线内的旧会话。");
            Assert(liveStatus.StepCount >= 1 && liveStatus.ToolCallCount >= 1 && liveStatus.ReasoningEventCount >= 1, $"实时统计应在运行中累积：steps={liveStatus.StepCount}, tools={liveStatus.ToolCallCount}, reasoning={liveStatus.ReasoningEventCount}");

            var growthObserved = false;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
                if (runnerTask.IsCompleted) break;
                if (new FileInfo(newSessionPath).Length > lengthAtBind) { growthObserved = true; break; }
            }
            Assert(growthObserved, "进程结束前应观察到会话文件长度增长（运行中持续写入）。");

            var runResult = await runnerTask;
            Assert(runResult.ExitCode == 0 && runResult.Output.Contains("same turn", StringComparison.OrdinalIgnoreCase), "The synchronous parent runner did not resume normally.");
            var taskStatus = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(statusPath), options)!;
            Assert(taskStatus.State == "completed" && taskStatus.ReturnState == "same-turn-resume" && taskStatus.CodexThreadId == threadId, $"Completion did not bind and resume the original Codex task: state={taskStatus.State}, return={taskStatus.ReturnState}, thread={taskStatus.CodexThreadId}, message={taskStatus.Message}, output={runResult.Output}");
            Assert(taskStatus.ReasonixSessionPath == newSessionPath, $"Reasonix Desktop session path was not persisted: actual={taskStatus.ReasonixSessionPath}, expected={newSessionPath}, message={taskStatus.Message}");
            Assert(taskStatus.ExecutionIntensity == "auto" && taskStatus.ExecutionProfile == "balanced" && taskStatus.StepCount >= 2 && taskStatus.ToolCallCount >= 1 && taskStatus.ReasoningEventCount >= 2 && taskStatus.TokenInput >= 500 && taskStatus.TokenOutput >= 20 && taskStatus.CacheHitTokens >= 100, $"Task host must persist strategy and live statistics: {JsonSerializer.Serialize(taskStatus)}");
            Assert((await File.ReadAllTextAsync(newSessionPath + ".jsonl.meta")).Contains("topic_id") || (await File.ReadAllTextAsync(newSessionPath + ".meta")).Contains("topic_id"), "Reasonix session metadata was not upgraded for Desktop visibility.");
            Assert((await File.ReadAllTextAsync(Path.Combine(reasonixHome, "desktop-projects.json"))).Contains(Path.GetFileName(project)), "Reasonix Desktop project index was not updated.");
            Assert((await File.ReadAllTextAsync(Path.Combine(reasonixHome, "desktop-tabs.json"))).Contains(newSessionPath.Replace("\\", "\\\\")), "Reasonix Desktop tab index was not updated.");
            Assert(!(await File.ReadAllTextAsync(Path.Combine(reasonixHome, "desktop-tabs.json"))).Contains(oldSessionPath.Replace("\\", "\\\\")), "Desktop tab index must never reference the stale baseline session.");
            service.RefreshManagedScripts();
            Assert(File.Exists(Path.Combine(skillRoot, "run-reasonix-job.ps1")), "Startup refresh must keep the managed task host current.");
            Assert((await File.ReadAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"))).Contains("same GPT turn resumes"), "Startup refresh must upgrade managed guidance, not only scripts.");

            service.SetPermissionMode(ReasonixPermissionMode.Safe);
            jobHost = await File.ReadAllTextAsync(Path.Combine(skillRoot, "run-reasonix-job.ps1"));
            Assert(jobHost.Contains("Bash(dotnet build:*)") && !jobHost.Contains("--permission-mode bypassPermissions"), "Safe mode must use the explicit build allowlist.");

            service.Disable();
            var restored = await File.ReadAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"));
            Assert(!Directory.Exists(skillRoot), "Managed Reasonix skill should be removed when disabled.");
            Assert(restored.Contains("keep-this-rule") && !restored.Contains(ReasonixIntegrationService.GuidanceStart), "Disabling must preserve unrelated user guidance.");
        });
    }

    private static Task TestReasonixExecutionPolicyAsync()
    {
        // 1) manifest 显式 Fast：balanced + low，禁止自动 review 子代理，绝不生成 delivery。
        var fast = ReasonixExecutionPolicy.Resolve(new ReasonixManifestPolicy(null, null, null, "fast", null, null, null, null, null), new string('x', 3000), "- a\n- b\n", null);
        Assert(fast.Intensity == ReasonixExecutionIntensity.Fast && fast.Profile == "balanced" && fast.Effort == "low", "Fast 应解析为 balanced + low。");
        Assert(!fast.AllowAutoReviewSubagents && fast.Source == "manifest", "Fast 不应自动启动 review 子代理。");

        // 2) manifest 显式 Strict：balanced + high，禁止自动 review（delivery 不再由任何执行强度生成）。
        var strict = ReasonixExecutionPolicy.Resolve(new ReasonixManifestPolicy(null, null, null, "strict", null, null, null, null, null), new string('x', 3000), "- a\n", null);
        Assert(strict.Profile == "balanced" && strict.Effort == "high" && !strict.AllowAutoReviewSubagents, "Strict 应映射为 balanced + high，且不得允许自动 review。");

        // 3) manifest 显式 profile=delivery 也只作为输入读取：最终仍归一化为 balanced（显式声明仅影响 source=manifest）。
        var overridden = ReasonixExecutionPolicy.Resolve(new ReasonixManifestPolicy("small", "delivery", null, null, null, null, null, null, null), new string('x', 1000), "- a\n", null);
        Assert(overridden.Complexity == "small" && overridden.Profile == "balanced" && overridden.Source == "manifest", "manifest 显式 delivery 必须归一化为 balanced，且来源仍为 manifest。");
        var overriddenEconomy = ReasonixExecutionPolicy.Resolve(new ReasonixManifestPolicy("small", "economy", null, null, null, null, null, null, null), new string('x', 1000), "- a\n", null);
        Assert(overriddenEconomy.Profile == "balanced" && overriddenEconomy.Source == "manifest", "manifest 显式 economy 也必须归一化为 balanced。");

        // 4) 非法/非正数声明安全回退，绝不抛异常。
        var bogus = ReasonixExecutionPolicy.Resolve(new ReasonixManifestPolicy(null, "bogus", "bogus", "bogus", -5, 0, null, null, null), new string('x', 3000), "- a\n- b\n- c\n", null);
        Assert(bogus.Intensity == ReasonixExecutionIntensity.Auto && bogus.Profile == "balanced" && bogus.Effort == "medium" && bogus.MaxSteps is null && bogus.BudgetSteps == 35, "非法/非正数声明应安全回退（medium 默认软预算 35）。");

        // 5) Auto + 小合同 → balanced + low（小任务不得生成 delivery）。
        var small = ReasonixExecutionPolicy.Resolve(null, new string('x', 1000), "- a\n", null);
        Assert(small.Complexity == "small" && small.Profile == "balanced" && small.Effort == "low" && small.BudgetSteps == 16, "小修复应走 balanced + low（软预算 16）。");

        // 6) Auto + 重大合同 → balanced + high（重大任务不再被降级为普通 medium，但 profile 仍是 balanced）。
        var major = ReasonixExecutionPolicy.Resolve(null, new string('x', 9500), string.Join("\n", Enumerable.Range(0, 13).Select(i => $"- item {i}")), null);
        Assert(major.Complexity == "major" && major.Profile == "balanced" && major.Effort == "high" && major.BudgetSteps == 56, "重大合同应走 balanced + high（软预算 56）。");

        // 7) 无 manifest → inferred；Helper 默认强度兜底。
        var inferred = ReasonixExecutionPolicy.Resolve(null, new string('x', 3000), "- a\n- b\n", "fast");
        Assert(inferred.Source == "inferred" && inferred.Intensity == ReasonixExecutionIntensity.Fast, "未声明时按合同推断，并用 Helper 默认强度。");

        // 8) maxSteps/budgetSteps/workerChecks 显式传递，gpt/release 拆分不影响 worker 计划。
        var checks = ReasonixExecutionPolicy.Resolve(new ReasonixManifestPolicy("medium", null, null, null, 40, 30, ["build", "test"], ["visual"], ["release"]), new string('x', 3000), "- a\n", null);
        Assert(checks.MaxSteps == 40 && checks.BudgetSteps == 30 && checks.WorkerChecks.SequenceEqual(new[] { "build", "test" }), "manifest 声明的 maxSteps/budgetSteps/workerChecks 应被采用。");

        // 9) 验收项 ≥12 触发 major 推断（不依赖 SPEC 长度），profile 仍为 balanced。
        var manyItems = ReasonixExecutionPolicy.Resolve(null, new string('x', 3000), string.Join("\n", Enumerable.Range(0, 12).Select(i => $"- item {i}")), null);
        Assert(manyItems.Complexity == "major" && manyItems.Profile == "balanced", "验收项数量多时复杂度为 major，但 profile 仍为 balanced。");

        // 10) budgetSteps=35 解析为 35；未知 estimatedSteps 不伪装为 35。
        var budget35 = ReasonixExecutionPolicy.Resolve(new ReasonixManifestPolicy(null, null, null, null, null, 35, null, null, null), new string('x', 3000), "- a\n", null);
        Assert(budget35.BudgetSteps == 35, "manifest budgetSteps=35 应解析为 35。");
        using var doc = JsonDocument.Parse("""{"estimatedSteps":35}""");
        var estimatedOnly = ReasonixManifestPolicy.FromManifest(doc.RootElement);
        Assert(estimatedOnly.BudgetSteps is null && estimatedOnly.MaxSteps is null, "estimatedSteps 不应被当作预算字段读取。");
        var estimatedResolved = ReasonixExecutionPolicy.Resolve(estimatedOnly, new string('x', 1000), "- a\n", null);
        Assert(estimatedResolved.BudgetSteps == 16, "仅 estimatedSteps 时应按合同推断预算，不得伪装为 35：" + estimatedResolved.BudgetSteps);

        // ---- workerCheck 2：所有托管强度都禁止自动 review 子代理 ----
        foreach (var intensity in new[] { "auto", "fast", "standard", "strict" })
        {
            var plan = ReasonixExecutionPolicy.Resolve(new ReasonixManifestPolicy(null, null, null, intensity, null, null, null, null, null), new string('x', 3000), "- a\n", null);
            Assert(!plan.AllowAutoReviewSubagents, $"{intensity} 必须禁止自动 review 子代理。");
        }

        // ---- workerCheck 3：worker-only 派生验收过滤——排除 GPT/release 工作，但绝不吞掉普通 build/test ----
        // 正例（明确要求视觉/GUI/发布，应移交 GPT）。
        foreach (var gpt in new[]
        {
            "Take a screenshot of the main window",
            "Verify pixel-perfect layout matches the design",
            "Check DPI scaling on a 4K monitor",
            "Verify button colors and occlusion",
            "真实 GUI 交互验收",
            "视觉验收主界面",
            "查看截图并核对像素颜色",
            "执行真实 GUI 烟测",
            "屏幕捕获验证 DPI 缩放",
            "进行视觉判断确认配色",
            "Publish the package to the release",
            "Create a zip installer and sign it",
            "打包并生成安装包",
            "发布 release 到 GitHub",
            "build-release 流程"
        })
            Assert(ReasonixAcceptanceFilter.ShouldDelegateToGpt(gpt), $"应移交 GPT 的检查被误判为 worker：{gpt}");

        // 反例（普通 build/test/source inspection 与结构/资源测试必须保留）。
        foreach (var worker in new[]
        {
            "dotnet build CodexHelper.sln -c Debug --no-restore",
            "dotnet test --no-build",
            "Release configuration build succeeds once",
            "Full test suite passes once",
            "GUI 项目构建",
            "Run the full test suite once",
            "Inspect source for unused imports",
            "build 与 test 均通过",
            "验证 XAML 布局数学",
            "验证 XML/DOM 布局结构",
            "图片资源存在性检查",
            "验证图片引用是否有效"
        })
            Assert(!ReasonixAcceptanceFilter.ShouldDelegateToGpt(worker), $"普通 worker 检查被误过滤：{worker}");

        // 否定约束说明：识别“不截图/不看图/不启动 GUI/不进行视觉判断/不发布”，不应移交 GPT，也不应被当成待执行检查。
        foreach (var constraint in new[]
        {
            "不截图、不看图、不进行视觉判断",
            "do not take screenshots",
            "不启动 GUI，只做结构测试",
            "不进行视觉判断",
            "不发布，只做构建"
        })
            Assert(!ReasonixAcceptanceFilter.ShouldDelegateToGpt(constraint), $"否定约束不应被移交给 GPT：{constraint}");

        // Partition 正确区分并剔除空项（否定约束留在 worker 侧）。
        var part = ReasonixAcceptanceFilter.Partition(new[] { "dotnet build", "take a screenshot", " ", "release 页面", "不截图，只做结构测试" });
        Assert(part.Worker.SequenceEqual(new[] { "dotnet build", "不截图，只做结构测试" }) && part.DelegatedToGpt.SequenceEqual(new[] { "take a screenshot", "release 页面" }), "Partition 应保留 worker 项并剔除空项、移交 GPT 项。");
        return Task.CompletedTask;
    }

    private static async Task TestReasonixUiTextAsync()
    {
        await WithTempDirectoryAsync("reasonix-ui-text", async root =>
        {
            // 固定事件样本：6000 条 reasoning 不得显示为 6000 个步骤。
            var running = new ReasonixTaskStatus("run-x", root, root, "running", "executing", "Full", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, 42, 6500, "working",
                StepCount: 3, ToolCallCount: 12, ReasoningEventCount: 6000, TokenInput: 12800, TokenOutput: 40, CacheHitTokens: 900);
            var activity = ReasonixUiText.ActivitySummary(running);
            Assert(activity.Contains("3 模型轮次") && activity.Contains("6,000 条推理流事件") && activity.Contains("12.8k") && activity.Contains("缓存命中 900"), "统计必须区分模型轮次/工具调用/reasoning/token：" + activity);
            // 完成态不再显示模型轮次，而是实际步骤（StepCount）。
            var doneActivity = ReasonixUiText.ActivitySummary(running with { State = "completed" });
            Assert(doneActivity.Contains("3 实际步骤"), "完成态应显示实际步骤而非模型轮次：" + doneActivity);

            // 运行中未绑定会话：明确回退自身实时事件视图，绝不宣称 Desktop 实时。
            Assert(ReasonixUiText.DesktopStateText(running).Contains("实时事件视图") && ReasonixUiText.DesktopStateText(running).Contains("任务结束后同步"), "运行中会话未落盘时必须回退自身实时事件视图。");
            Assert(ReasonixUiText.DesktopStateText(running with { ReasonixSessionPath = @"C:\sessions\x.jsonl" }) == "已同步到 Reasonix App（会话已注册）", "绑定会话后应显示已同步。");

            // 完成 + 报告存在 → 等待 GPT 独立验收。
            var report = Path.Combine(root, "EXECUTION_REPORT.md");
            await File.WriteAllTextAsync(report, "done");
            var completed = running with { State = "completed", Phase = "awaiting-gpt-review", ReturnState = "same-turn-resume" };
            Assert(ReasonixUiText.OutcomeLine(completed).Contains("等待 GPT 独立验收"), "完成状态应等待 GPT 验收。");
            Assert(ReasonixUiText.ReturnStateText(completed) == "原 GPT 任务已恢复，可开始验收", "same-turn-resume 文案不准确。");

            // 执行器退出异常但交付报告存在：不能简单显示成功，也不能丢失报告事实。
            var errorWithReport = running with { State = "failed", Phase = "awaiting-gpt-review", ReturnState = "executor-error" };
            var outcome = ReasonixUiText.OutcomeLine(errorWithReport);
            Assert(outcome.Contains("异常退出") && outcome.Contains("报告存在") && outcome.Contains("等待 GPT 验收"), "退出异常但报告存在时文案不准确：" + outcome);

            // 真正失败（无报告）与用户停止：独立任务目录，避免误读前文生成的报告。
            var noReportDir = Path.Combine(root, "no-report");
            Directory.CreateDirectory(noReportDir);
            var failed = running with { State = "failed", Phase = "failed", ReturnState = "executor-error", Message = "exit code 7; no EXECUTION_REPORT.md", TaskDirectory = noReportDir };
            Assert(ReasonixUiText.OutcomeLine(failed).Contains("执行失败"), "真正失败文案不准确。");

            // 用户停止。
            var cancelled = running with { State = "cancelled", Phase = "已停止" };
            Assert(ReasonixUiText.OutcomeLine(cancelled).Contains("用户已停止任务"), "用户停止文案不准确。");

            // 策略展示。
            var withStrategy = running with { ExecutionIntensity = "fast", ExecutionProfile = "balanced", ExecutionEffort = "low", EstimatedSteps = 25 };
            Assert(withStrategy.StrategyDisplay.Contains("fast/balanced/low") && withStrategy.StrategyDisplay.Contains("25"), "策略展示不准确：" + withStrategy.StrategyDisplay);
        });
    }

    private static Task TestCodexThreadUriAsync()
    {
        const string valid = "019f89ac-b333-78a0-8b77-f7c5925f7052";
        Assert(CodexThreadUri.Build(null, null) is null, "两者均无效应返回 null。");
        Assert(CodexThreadUri.Build("codex://threads/" + valid, null) == "codex://threads/" + valid, "合法 ReturnUri 应被采用。");
        Assert(CodexThreadUri.Build("https://evil.example/threads/" + valid, valid) == "codex://threads/" + valid, "恶意 scheme 应被忽略并用合法 CodexThreadId 重建。");
        Assert(CodexThreadUri.Build("file:///C:/threads/" + valid, null) is null, "file scheme 应拒绝。");
        Assert(CodexThreadUri.Build("codex://threads/not-a-uuid", null) is null, "无效 UUID 应拒绝。");
        Assert(CodexThreadUri.Build("codex://threads/" + valid + "/extra", null) is null, "多段路径应拒绝。");
        Assert(CodexThreadUri.Build(null, "not-a-uuid") is null, "非法 CodexThreadId 应拒绝。");
        Assert(CodexThreadUri.IsValidUuid(valid), "标准 UUID 应视为合法。");
        Assert(!CodexThreadUri.IsValidUuid(null) && !CodexThreadUri.IsValidUuid("abc"), "非法 UUID 应拒绝。");
        return Task.CompletedTask;
    }

    private static Task TestReasonixTimeAndRemainingAsync()
    {
        // ---- 纯函数 RunningRemainingPercent 边界 ----

        // 缺数据：无预算且无 workerChecks → null（“估算中”）。
        Assert(ReasonixUiText.RunningRemainingPercent(null, 0, 0, null, null) is null, "完全缺数据应返回 null（估算中）。");
        // 无预算但有 workerChecks 完成：用 workerChecks 比例。
        Assert(ReasonixUiText.RunningRemainingPercent(null, 0, 0, 1, 4) == 75, "仅 workerChecks 有效时应用其完成比例。");
        // 刚开始：无 workerChecks、有预算但尚未推进 → 无进度 → 估算中。
        Assert(ReasonixUiText.RunningRemainingPercent(16, 0, 0, null, null) is null, "刚启动且无预算进度应返回 null（估算中）。");
        // 刚开始（步骤进行中）：只有步骤预算 → 使用步骤/预算。
        Assert(ReasonixUiText.RunningRemainingPercent(16, 0, 1, null, null) == 94, "只有步骤预算时应用步骤/预算。");
        // 负数/损坏状态：负数完成数/负数总数一律视为无效并回退。
        Assert(ReasonixUiText.RunningRemainingPercent(16, 0, 1, -1, 4) == 94, "负数 workerChecks 完成数应视为无效并回退到步骤比例。");
        Assert(ReasonixUiText.RunningRemainingPercent(16, 0, 1, 2, -4) == 94, "负数总数应视为无效并回退到步骤比例。");
        Assert(ReasonixUiText.RunningRemainingPercent(null, 0, 0, -1, -4) is null, "损坏且无步骤预算应返回 null。");
        Assert(ReasonixUiText.RunningRemainingPercent(null, 0, 0, -1, 4) is null, "completed 为负（-1,total=4）且无步骤预算应返回 null，不得显示剩余 100%。");
        Assert(ReasonixUiText.RunningRemainingPercent(null, 0, 0, 0, 4) == 100, "completed=0,total=4 无步骤预算：有效完成比例为 0，剩余应为 100%。");
        // 完成数超出总数：钳制到总数（不倒退、不显示负剩余）。
        Assert(ReasonixUiText.RunningRemainingPercent(null, 0, 0, 9, 4) == 5, "完成数超总数应钳制到下限 5%。");
        // 超过软预算：步骤数 ≥ 预算仍显示 5%，不显示负数或 0%。
        Assert(ReasonixUiText.RunningRemainingPercent(16, 0, 30, null, null) == 5, "超过软预算应显示 5%。");
        Assert(ReasonixUiText.RunningRemainingPercent(16, 0, 16, null, null) == 5, "正好达到软预算应显示 5%。");

        // ---- 融合与“更可信、不倒退”（取较大完成比例） ----
        // workerChecks 完成 25%（剩余 75），步骤预算完成 1/16≈6%（剩余 94）→ 取较大完成比例 → 剩余 75。
        Assert(ReasonixUiText.RunningRemainingPercent(16, 0, 1, 1, 4) == 75, "应取较大的完成比例（workerChecks 优先融合）。");
        // workerChecks 完成 100%（剩余 5），步骤预算 1/16（剩余 94）→ 取较大完成比例 → 剩余 5。
        Assert(ReasonixUiText.RunningRemainingPercent(16, 0, 1, 4, 4) == 5, "workerChecks 全完成时剩余应为 5%。");
        // 步骤进度更靠前时融合取步骤比例：workerChecks 0%（剩余 100），步骤 1/2（剩余 50）→ 剩余 50。
        Assert(ReasonixUiText.RunningRemainingPercent(2, 0, 1, 0, 2) == 50, "步骤进度更靠前时融合应取步骤比例。");

        // ---- TimeLine 文案：运行中显示预计剩余，完成/失败/停止显示总耗时 ----
        var running = new ReasonixTaskStatus("run-t", @"C:\proj", @"C:\proj\.codex-helper\runs\run-t", "running", "executing", "Full", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, 42, 0, "working",
            EstimatedSteps: 16, StepCount: 3, ModelTurnCount: 3, CompletedChecks: 1, TotalChecks: 4);
        var runningLine = ReasonixUiText.TimeLine(running);
        Assert(runningLine.Contains("预计剩余") && !runningLine.Contains("保守 ETA"), "运行中应显示预计剩余而非保守 ETA：" + runningLine);
        var noData = ReasonixUiText.TimeLine(running with { EstimatedSteps = null, StepCount = 0, ModelTurnCount = 0, CompletedChecks = null, TotalChecks = null });
        Assert(noData.Contains("预计剩余：估算中"), "无有效进度时应显示估算中：" + noData);
        // 完成/失败/停止：显示总耗时，不伪装百分比。
        var done = running with { State = "completed", UpdatedUtc = DateTime.UtcNow };
        var doneLine = ReasonixUiText.TimeLine(done);
        Assert(doneLine.Contains("完成总耗时") && !doneLine.Contains("预计剩余"), "完成态应显示总耗时且不显示预计剩余：" + doneLine);
        var failedLine = ReasonixUiText.TimeLine(running with { State = "failed" });
        Assert(failedLine.Contains("总耗时") && !failedLine.Contains("完成总耗时") && !failedLine.Contains("预计剩余"), "失败态应显示“总耗时”且不使用“完成”措辞：" + failedLine);
        var stoppedLine = ReasonixUiText.TimeLine(running with { State = "cancelled" });
        Assert(stoppedLine.Contains("总耗时") && !stoppedLine.Contains("完成总耗时") && !stoppedLine.Contains("预计剩余"), "停止态应显示“总耗时”且不使用“完成”措辞：" + stoppedLine);
        var interruptedLine = ReasonixUiText.TimeLine(running with { State = "interrupted" });
        Assert(interruptedLine.Contains("总耗时") && !interruptedLine.Contains("完成总耗时") && !interruptedLine.Contains("预计剩余"), "中断态应显示“总耗时”且不使用“完成”措辞：" + interruptedLine);

        return Task.CompletedTask;
    }

    private static async Task TestReasonixWorkerStepsAsync()
    {
        const string c1 = "dotnet build CodexHelper.sln -c Debug --no-restore";
        const string c2 = "dotnet test --no-build";

        // ---- 步骤状态映射：完成全绿、运行中蓝+灰、失败最后一步红 ----
        var baseTask = new ReasonixTaskStatus("run-w", @"C:\proj", @"C:\proj\.codex-helper\runs\run-w", "running", "executing", "Full", DateTime.UtcNow, DateTime.UtcNow, 42, 0, "working");

        var completed = ReasonixUiText.BuildWorkerSteps(baseTask with { State = "completed" }, [c1, c2]);
        Assert(completed.Count == 2 && completed.All(step => step.State == "completed"), "完成态所有步骤应绿色 completed。" + string.Join("|", completed.Select(s => s.State)));

        var running = ReasonixUiText.BuildWorkerSteps(baseTask, [c1, c2]);
        Assert(running[0].State == "running" && running[1].State == "pending", "运行中无进度映射时第一步应蓝、其余灰：" + string.Join("|", running.Select(s => s.State)));

        // 运行中且 TotalChecks 与 workerChecks 数量一致：已完成绿、当前蓝、待执行灰。
        var runningMapped = ReasonixUiText.BuildWorkerSteps(baseTask with { CompletedChecks = 1, TotalChecks = 2 }, [c1, c2]);
        Assert(runningMapped[0].State == "completed" && runningMapped[1].State == "running", "运行中步骤 1 应绿、步骤 2 应蓝：" + string.Join("|", runningMapped.Select(s => s.State)));

        // 失败：已完成绿、失败步骤红、之后待执行灰；不把普通待执行标红。
        var failed = ReasonixUiText.BuildWorkerSteps(baseTask with { State = "failed", CompletedChecks = 1, TotalChecks = 2 }, [c1, c2]);
        Assert(failed[0].State == "completed" && failed[1].State == "failed", "失败态步骤 1 应绿、步骤 2 应红：" + string.Join("|", failed.Select(s => s.State)));
        var failedThree = ReasonixUiText.BuildWorkerSteps(baseTask with { State = "failed", CompletedChecks = 1, TotalChecks = 3 }, [c1, c2, c2]);
        Assert(failedThree[0].State == "completed" && failedThree[1].State == "failed" && failedThree[2].State == "pending", "失败态第三步应保持灰待执行：" + string.Join("|", failedThree.Select(s => s.State)));

        // 失败但无进度映射：保守全部待执行，绝不猜测失败位置而误标红。
        var failedNoMap = ReasonixUiText.BuildWorkerSteps(baseTask with { State = "failed" }, [c1, c2]);
        Assert(failedNoMap.All(step => step.State == "pending"), "失败无映射时不应标红任何步骤：" + string.Join("|", failedNoMap.Select(s => s.State)));

        // 空清单：稳定返回空列表。
        Assert(ReasonixUiText.BuildWorkerSteps(baseTask, []).Count == 0, "无 workerChecks 时不应返回任何步骤。");

        // ---- manifest 读取与损坏降级 ----
        await WithTempDirectoryAsync("reasonix-worker-steps", async root =>
        {
            var project = Path.Combine(root, "proj");
            var runs = Path.Combine(project, ".codex-helper", "runs");
            var taskDir = Path.Combine(runs, "run-ws");
            Directory.CreateDirectory(taskDir);
            var app = new AppPaths(Path.Combine(root, "app"));
            var service = new ReasonixIntegrationService(project, app);
            var status = baseTask with { ProjectRoot = project, TaskDirectory = taskDir };

            // 无 manifest：空列表且不抛异常。
            Assert(service.ReadWorkerChecks(status).Count == 0, "缺失 manifest 应返回空列表。");

            // 有效 manifest：按声明顺序返回字符串步骤。
            await File.WriteAllTextAsync(Path.Combine(taskDir, "manifest.json"),
                "{\"taskId\":\"run-ws\",\"workerChecks\":[\"" + c1 + "\",\" \", \"" + c2 + "\"]}", new System.Text.UTF8Encoding(false));
            var checks = service.ReadWorkerChecks(status);
            Assert(checks.Count == 2 && checks[0] == c1 && checks[1] == c2, "应跳过空项并按声明顺序返回步骤：" + string.Join("|", checks));

            // 损坏 manifest：空列表且不抛异常。
            await File.WriteAllTextAsync(Path.Combine(taskDir, "manifest.json"), "{ not valid json", new System.Text.UTF8Encoding(false));
            Assert(service.ReadWorkerChecks(status).Count == 0, "损坏 manifest 应安全降级为空列表。");

            // workerChecks 非数组：空列表。
            await File.WriteAllTextAsync(Path.Combine(taskDir, "manifest.json"), "{\"workerChecks\":\"nope\"}", new System.Text.UTF8Encoding(false));
            Assert(service.ReadWorkerChecks(status).Count == 0, "workerChecks 非数组应安全降级。");

            // 路径越界（TaskDirectory 不在项目 runs 下）：拒绝读取，返回空。
            var outside = baseTask with { ProjectRoot = project, TaskDirectory = Path.Combine(root, "outside") };
            Directory.CreateDirectory(Path.Combine(root, "outside"));
            await File.WriteAllTextAsync(Path.Combine(root, "outside", "manifest.json"), "{\"workerChecks\":[\"x\"]}", new System.Text.UTF8Encoding(false));
            Assert(service.ReadWorkerChecks(outside).Count == 0, "越界 TaskDirectory 应拒绝读取 manifest。");
        });
    }

    private static async Task TestReasonixLiveSessionVariantsAsync()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // ---- 场景 A：软预算提醒（超过预算不中止，任务仍完成）----
        await WithTempDirectoryAsync("reasonix-budget", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var executable = Path.Combine(root, "reasonix-cli.cmd");
            await File.WriteAllTextAsync(executable, "@echo off\r\necho {\"kind\":\"turn_started\"}\r\necho {\"kind\":\"turn_started\"}\r\necho {\"kind\":\"turn_started\"}\r\nif defined CODEX_HELPER_TEST_TASK ( echo done>\"%CODEX_HELPER_TEST_TASK%\\EXECUTION_REPORT.md\" )\r\nexit /b 0\r\n");
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            var project = Path.Combine(root, "project-budget");
            var task = Path.Combine(project, ".codex-helper", "runs", "run-budget");
            Directory.CreateDirectory(task);
            await File.WriteAllTextAsync(Path.Combine(task, "SPEC.md"), "spec");
            await File.WriteAllTextAsync(Path.Combine(task, "ACCEPTANCE.md"), "- a");
            await File.WriteAllTextAsync(Path.Combine(task, "HANDOFF.md"), "h");
            await File.WriteAllTextAsync(Path.Combine(task, "manifest.json"), """{"budgetSteps":1,"intensity":"fast"}""");
            var runResult = await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), project, task, Path.Combine(root, "reasonix-home"), "", "thread-1", taskDir: task);
            Assert(runResult.ExitCode == 0, "预算场景 runner 应正常结束：" + runResult.Output);
            var status = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-budget.json")), options)!;
            var eventsText = await File.ReadAllTextAsync(Path.Combine(task, "events.jsonl"));
            Assert(eventsText.Contains("helper_budget_notice"), "软预算提醒应写入事件流。");
            Assert(status.State == "completed" && status.StepCount == 3, $"超过软预算不得中止任务：state={status.State}, steps={status.StepCount}。");
            Assert(status.ExecutionIntensity == "fast" && status.ExecutionProfile == "balanced" && status.EstimatedSteps == 1, "manifest budgetSteps 应生效。");
            service.Disable();
        });

        // ---- 场景 B：执行器退出异常但交付报告存在 → failed/awaiting-gpt-review/executor-error ----
        await WithTempDirectoryAsync("reasonix-exit-error", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var executable = Path.Combine(root, "reasonix-cli.cmd");
            await File.WriteAllTextAsync(executable, "@echo off\r\necho {\"kind\":\"turn_started\"}\r\nif defined CODEX_HELPER_TEST_TASK ( echo done>\"%CODEX_HELPER_TEST_TASK%\\EXECUTION_REPORT.md\" )\r\nexit /b 5\r\n");
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            var project = Path.Combine(root, "project-error");
            var task = Path.Combine(project, ".codex-helper", "runs", "run-error");
            Directory.CreateDirectory(task);
            await File.WriteAllTextAsync(Path.Combine(task, "SPEC.md"), "spec");
            await File.WriteAllTextAsync(Path.Combine(task, "ACCEPTANCE.md"), "- a");
            await File.WriteAllTextAsync(Path.Combine(task, "HANDOFF.md"), "h");
            await File.WriteAllTextAsync(Path.Combine(task, "manifest.json"), "{}");
            await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), project, task, Path.Combine(root, "reasonix-home"), "", "thread-2", taskDir: task);
            var status = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-error.json")), options)!;
            Assert(status.State == "failed" && status.Phase == "awaiting-gpt-review" && status.ReturnState == "executor-error", $"退出异常但报告存在时必须给出准确状态：state={status.State}, phase={status.Phase}, return={status.ReturnState}, message={status.Message}");
            Assert(File.Exists(Path.Combine(task, "EXECUTION_REPORT.md")), "交付报告应保留。");
            Assert(ReasonixUiText.OutcomeLine(status).Contains("异常退出") && ReasonixUiText.OutcomeLine(status).Contains("等待 GPT 验收"), "退出异常但报告存在的 UI 文案不准确。");
            service.Disable();
        });

        // ---- 场景 C：两个中文路径项目不串会话，各自绑定各自会话 ----
        await WithTempDirectoryAsync("x", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var reasonixHome = Path.Combine(root, "rh");
            var executable = Path.Combine(root, "reasonix-cli.cmd");
            await File.WriteAllTextAsync(executable, "@echo off\r\nexit /b 0\r\n");
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);

            string Slug(string path) => System.Text.RegularExpressions.Regex.Replace(path.ToLowerInvariant(), @"[:\\/]+", "-");
            async Task<(ReasonixTaskStatus Status, string SessionPath)> RunIsolatedAsync(string projectName, string taskId, string sessionName)
            {
                var project = Path.Combine(root, projectName);
                var task = Path.Combine(project, ".codex-helper", "runs", taskId);
                Directory.CreateDirectory(task);
                foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                    await File.WriteAllTextAsync(Path.Combine(task, contract), contract == "manifest.json" ? "{}" : "test");
                var sessionDirectory = Path.Combine(reasonixHome, "projects", Slug(project), "sessions");
                Directory.CreateDirectory(sessionDirectory);
                var sessionPath = Path.Combine(sessionDirectory, sessionName + ".jsonl");
                var writeScript = Path.Combine(root, "w" + taskId + ".ps1");
                await File.WriteAllTextAsync(writeScript, """
                    param([string]$Action = 'create')
                    switch ($Action) {
                      'create' { [IO.File]::WriteAllText($env:CODEX_HELPER_TEST_NEW_SESSION, '{}', [Text.UTF8Encoding]::new($false)) }
                      'report' { [IO.File]::WriteAllText((Join-Path $env:CODEX_HELPER_TEST_TASK 'EXECUTION_REPORT.md'), 'done', [Text.UTF8Encoding]::new($false)) }
                    }
                    """);
                var fake = """
                    @echo off
                    setlocal EnableDelayedExpansion
                    echo {"kind":"turn_started"}
                    powershell -NoProfile -ExecutionPolicy Bypass -File %CODEX_HELPER_TEST_WRITE_SCRIPT% create
                    echo {"kind":"run_done","ok":true}
                    if defined CODEX_HELPER_TEST_TASK ( powershell -NoProfile -ExecutionPolicy Bypass -File %CODEX_HELPER_TEST_WRITE_SCRIPT% report )
                    exit /b 0
                    """;
                await File.WriteAllTextAsync(executable, fake);
                var runResult = await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), project, task, reasonixHome, "", "thread-" + taskId, sessionPath, task, writeScript);
                if (!File.Exists(sessionPath))
                    throw new InvalidOperationException($"fake 应创建会话文件：{sessionPath}。runner 输出：{runResult.Output}");
                var status = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, taskId + ".json")), options)!;
                if (status.ReasonixSessionPath != sessionPath) throw new InvalidOperationException($"绑定失败：{taskId} actual={status.ReasonixSessionPath}, expected={sessionPath}, message={status.Message}, output={runResult.Output}");
                return (status, sessionPath);
            }

            var a = await RunIsolatedAsync("项目甲", "run-a", "a");
            var b = await RunIsolatedAsync("项目乙", "run-b", "b");
            Assert(a.Status.ReasonixSessionPath == a.SessionPath, $"项目甲必须绑定自己的会话：actual={a.Status.ReasonixSessionPath}, expected={a.SessionPath}");
            Assert(b.Status.ReasonixSessionPath == b.SessionPath, $"项目乙必须绑定自己的会话：actual={b.Status.ReasonixSessionPath}, expected={b.SessionPath}");
            Assert(a.Status.State == "completed" && b.Status.State == "completed", $"两个项目任务都应正常完成：a={a.Status.State}/{a.Status.Message}, b={b.Status.State}/{b.Status.Message}");
            var projectsText = await File.ReadAllTextAsync(Path.Combine(reasonixHome, "desktop-projects.json"));
            Assert(projectsText.Contains("项目甲", StringComparison.Ordinal) && projectsText.Contains("项目乙", StringComparison.Ordinal), "两个中文路径项目都应注册到 Desktop 项目索引。");
            service.Disable();
        });

        // ---- 场景 D：final-readiness 退出码 1 → 谨慎"等待 GPT 复核"；WORKER_ACCEPTANCE.md 派生合同过滤 ----
        await WithTempDirectoryAsync("reasonix-final-readiness", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var service = new ReasonixIntegrationService(codexRoot, app);

            // D1：exit1 + final_readiness + 实际活动 + 报告 → final-readiness-blocked，且派生合同过滤视觉/打包项。
            var exe1 = Path.Combine(root, "cli-fr.cmd");
            await File.WriteAllTextAsync(exe1, "@echo off\r\necho {\"kind\":\"turn_started\"}\r\necho {\"kind\":\"tool_dispatch\",\"tool\":\"Bash\"}\r\necho {\"kind\":\"final_readiness\"}\r\nif defined CODEX_HELPER_TEST_TASK ( echo done>\"%CODEX_HELPER_TEST_TASK%\\EXECUTION_REPORT.md\" )\r\nexit /b 1\r\n");
            service.Enable(exe1, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            var project = Path.Combine(root, "p-fr");
            var task = Path.Combine(project, ".codex-helper", "runs", "run-fr");
            Directory.CreateDirectory(task);
            foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                await File.WriteAllTextAsync(Path.Combine(task, contract), contract == "manifest.json"
                    ? """{"workerChecks":["dotnet build CodexHelper.sln","take a screenshot of the UI","create the zip installer","Full test suite passes once"]}"""
                    : "test");
            await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), project, task, Path.Combine(root, "rh"), "", "thread-fr", taskDir: task);
            var status = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-fr.json")), options)!;
            Assert(status.FailureKind == "final-readiness-blocked" && status.Phase == "awaiting-gpt-review", $"final-readiness 应分类为等待 GPT 复核：kind={status.FailureKind}, phase={status.Phase}, message={status.Message}");
            Assert(ReasonixUiText.OutcomeLine(status).Contains("等待 GPT 复核") && ReasonixUiText.OutcomeLine(status).Contains("最终门禁未通过"), "final-readiness 的 UI 文案应明确等待 GPT 复核且门禁未通过。");
            var wa = await File.ReadAllTextAsync(Path.Combine(task, "WORKER_ACCEPTANCE.md"));
            var delegatedMarker = wa.IndexOf("## 已移交 GPT", StringComparison.Ordinal);
            Assert(delegatedMarker > 0, "派生合同应包含'已移交 GPT'段落。");
            var workerSection = wa[..delegatedMarker];
            Assert(workerSection.Contains("dotnet build CodexHelper.sln") && workerSection.Contains("Full test suite passes once"), "WORKER_ACCEPTANCE.md 的 worker 段应含合法 workerChecks。");
            Assert(!workerSection.Contains("take a screenshot") && !workerSection.Contains("zip installer"), "视觉/打包项不得出现在 worker 段。");
            var delegatedSection = wa[delegatedMarker..];
            Assert(delegatedSection.Contains("共 2 项", StringComparison.Ordinal) && delegatedSection.Contains("已整体移交给 GPT", StringComparison.Ordinal), "派生合同对移交项只写泛化计数说明，不披露检查正文。");
            Assert(!wa.Contains("take a screenshot") && !wa.Contains("zip installer"), "被移交 GPT 的检查正文不得以原文出现在 WORKER_ACCEPTANCE.md 任何位置。");
            Assert(!wa.Contains("gptChecks") && !wa.Contains("releaseChecks"), "派生合同不得包含 gpt/release 章节。");
            service.Disable();

            // D2：普通 exit1 + 报告但无 final_readiness → 保持 CLI 退出异常，不伪装门禁。
            var exe2 = Path.Combine(root, "cli-no-fr.cmd");
            await File.WriteAllTextAsync(exe2, "@echo off\r\necho {\"kind\":\"turn_started\"}\r\nif defined CODEX_HELPER_TEST_TASK ( echo done>\"%CODEX_HELPER_TEST_TASK%\\EXECUTION_REPORT.md\" )\r\nexit /b 1\r\n");
            service.Enable(exe2, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            var task2 = Path.Combine(project, ".codex-helper", "runs", "run-no-fr");
            Directory.CreateDirectory(task2);
            foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                await File.WriteAllTextAsync(Path.Combine(task2, contract), contract == "manifest.json" ? "{}" : "test");
            await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), project, task2, Path.Combine(root, "rh"), "", "thread-no-fr", taskDir: task2);
            var status2 = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-no-fr.json")), options)!;
            Assert(status2.FailureKind == "cli-exit" && status2.ReturnState == "executor-error", $"无 final_readiness 的 exit1 应保持 CLI 退出异常：kind={status2.FailureKind}");
            Assert(!ReasonixUiText.OutcomeLine(status2).Contains("最终门禁未通过"), "非 final-readiness 情况不得显示门禁未通过文案。");
            service.Disable();

            // D3：exit1 + final_readiness + 报告但无活动证据 → 谨慎仍为 CLI 退出异常（不伪装门禁）。
            var exe3 = Path.Combine(root, "cli-fr-noact.cmd");
            await File.WriteAllTextAsync(exe3, "@echo off\r\necho {\"kind\":\"final_readiness\"}\r\nif defined CODEX_HELPER_TEST_TASK ( echo done>\"%CODEX_HELPER_TEST_TASK%\\EXECUTION_REPORT.md\" )\r\nexit /b 1\r\n");
            service.Enable(exe3, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            var task3 = Path.Combine(project, ".codex-helper", "runs", "run-fr-noact");
            Directory.CreateDirectory(task3);
            foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                await File.WriteAllTextAsync(Path.Combine(task3, contract), contract == "manifest.json" ? "{}" : "test");
            await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), project, task3, Path.Combine(root, "rh"), "", "thread-fr-noact", taskDir: task3);
            var status3 = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-fr-noact.json")), options)!;
            Assert(status3.FailureKind == "cli-exit", $"无活动证据时 exit1+final_readiness 不得伪装为门禁未通过：kind={status3.FailureKind}");
            service.Disable();

            // D4：Reasonix 1.19.3 真实格式 {kind:"notice", code:"final_readiness"} + 活动 + 报告 + exit1 → 应识别为 final_readiness。
            var exe4 = Path.Combine(root, "cli-notice-code.cmd");
            await File.WriteAllTextAsync(exe4, "@echo off\r\necho {\"kind\":\"turn_started\"}\r\necho {\"kind\":\"tool_dispatch\",\"tool\":\"Bash\"}\r\necho {\"kind\":\"notice\",\"code\":\"final_readiness\"}\r\nif defined CODEX_HELPER_TEST_TASK ( echo done>\"%CODEX_HELPER_TEST_TASK%\\EXECUTION_REPORT.md\" )\r\nexit /b 1\r\n");
            service.Enable(exe4, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            var task4 = Path.Combine(project, ".codex-helper", "runs", "run-notice-code");
            Directory.CreateDirectory(task4);
            foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                await File.WriteAllTextAsync(Path.Combine(task4, contract), contract == "manifest.json" ? "{}" : "test");
            await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), project, task4, Path.Combine(root, "rh"), "", "thread-notice-code", taskDir: task4);
            var status4 = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-notice-code.json")), options)!;
            Assert(status4.FailureKind == "final-readiness-blocked", $"notice.code 格式也必须触发 final_readiness 门禁分类：kind={status4.FailureKind}");
            service.Disable();
        });
    }

    private static Task TestReasonixWindowsJsonAsync()
    {
        const string broken = "{\"cwd\":\"C:\\code\\demo\",\"escaped\":\"line\\nvalue\",\"slash\":\"a\\\\b\"}";
        using var document = ReasonixIntegrationService.ParseLenientWindowsJson(broken);
        Assert(document.RootElement.GetProperty("cwd").GetString() == @"C:\code\demo", "Raw Windows paths should be repaired.");
        Assert(document.RootElement.GetProperty("escaped").GetString() == "line\nvalue", "Valid JSON escapes must be preserved.");
        Assert(document.RootElement.GetProperty("slash").GetString() == @"a\b", "Escaped backslashes must be preserved.");
        Assert(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes("· 中文")) == "· 中文", "Reasonix terminal text must remain UTF-8.");
        return Task.CompletedTask;
    }

    private static string NewDoctorJson(string model = "opencode/deepseek-v4-flash") =>
        $$"""{"config":{"default_model":"{{model}}"},"providers":[{"name":"opencode","key_present":true,"models":["deepseek-v4-flash"]}]}""";

    private static string LegacyDoctorJson(string model = "opencode/old-model") =>
        $$"""{"config":{"default_model":"{{model}}"},"version":"0.53.2"}""";

    /// <summary>cmd 中输出 JSON 的辅助：JSON 不含 & | &lt; &gt; ^ % 等特殊字符时可直接 echo。</summary>
    private static string EchoLine(string text) => text.Replace("%", "%%");

    private static async Task TestReasonixCliDiscoveryAsync()
    {
        await WithTempDirectoryAsync("reasonix-cli-discovery", async root =>
        {
            var localAppData = Path.Combine(root, "local");
            var appData = Path.Combine(root, "appdata");
            var programFiles = Path.Combine(root, "pf");
            Func<Environment.SpecialFolder, string> folders = folder => folder switch
            {
                Environment.SpecialFolder.LocalApplicationData => localAppData,
                Environment.SpecialFolder.ApplicationData => appData,
                Environment.SpecialFolder.ProgramFiles => programFiles,
                Environment.SpecialFolder.ProgramFilesX86 => Path.Combine(programFiles, "x86"),
                _ => root
            };
            var noOtherSources = new ReasonixCliDiscovery
            {
                SpecialFolder = folders,
                RegistryReader = () => Array.Empty<string>(),
                RunningProcessReader = () => Array.Empty<string>(),
                PathDirectoryReader = () => Array.Empty<string>()
            };

            // 1) 仅默认 Desktop 路径
            var defaultCli = Path.Combine(localAppData, "Programs", "Reasonix", "reasonix-cli.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(defaultCli)!);
            await File.WriteAllTextAsync(defaultCli, "fake");
            var probe = new ReasonixCliProbe
            {
                ProcessRunner = (path, args) => args.Contains("--version")
                    ? new ReasonixProcessResult(0, "1.19.3", "")
                    : new ReasonixProcessResult(0, NewDoctorJson(), "")
            };
            var selection = await probe.SelectBestAsync(noOtherSources.Discover(null), null);
            Assert(selection.Best is not null && string.Equals(selection.Best.Path, defaultCli, StringComparison.OrdinalIgnoreCase)
                && selection.Best.Source == ReasonixCliSource.CommonLocation, "仅默认 Desktop 路径应被发现。");

            // 2) 自定义 D 盘注册表安装（InstallLocation 推导 + versions 版本目录）
            var dDriveCli = @"D:\reasonix\reasonix-cli.exe";
            var versionsCli = @"D:\reasonix\versions\v1.19.3\reasonix-cli.exe";
            var registryDiscovery = new ReasonixCliDiscovery
            {
                SpecialFolder = folders,
                FileExists = path => path == dDriveCli || path == versionsCli,
                RegistryReader = () => new[] { @"D:\reasonix" },
                RunningProcessReader = () => Array.Empty<string>(),
                PathDirectoryReader = () => Array.Empty<string>(),
                SubdirectoryReader = dir => string.Equals(dir, @"D:\reasonix\versions", StringComparison.OrdinalIgnoreCase)
                    ? new[] { @"D:\reasonix\versions\v1.19.3" }
                    : Array.Empty<string>()
            };
            var registrySelection = await probe.SelectBestAsync(registryDiscovery.Discover(null), null);
            Assert(registrySelection.Best is not null && string.Equals(registrySelection.Best.Path, dDriveCli, StringComparison.OrdinalIgnoreCase)
                && registrySelection.Best.Source == ReasonixCliSource.Registry, "自定义 D 盘注册表安装应被发现。");
            var versionCandidateFound = registryDiscovery.Discover(null).Any(c => string.Equals(c.Path, versionsCli, StringComparison.OrdinalIgnoreCase));
            Assert(versionCandidateFound, "注册表安装的 versions\\vX.Y.Z 版本目录应作为候选。");

            // 注册表值格式异常（引号包裹、图标 ,0 索引）不得崩溃
            var weirdRegistry = new ReasonixCliDiscovery
            {
                SpecialFolder = folders,
                FileExists = path => path == dDriveCli,
                RegistryReader = () => new[] { "\"D:\\reasonix\"" },
                RunningProcessReader = () => Array.Empty<string>(),
                PathDirectoryReader = () => Array.Empty<string>()
            };
            var weirdSelection = await probe.SelectBestAsync(weirdRegistry.Discover(null), null);
            Assert(weirdSelection.Best is not null && string.Equals(weirdSelection.Best.Path, dDriveCli, StringComparison.OrdinalIgnoreCase), "注册表值格式异常（引号）应容错。");

            // 3) Desktop 1.19.3 与 npm 0.53.2 并存时选 Desktop
            var npmPath = Path.Combine(appData, "npm", "reasonix.cmd");
            Directory.CreateDirectory(Path.GetDirectoryName(npmPath)!);
            await File.WriteAllTextAsync(npmPath, "@echo off\r\nexit /b 0\r\n");
            var mixedDiscovery = new ReasonixCliDiscovery
            {
                SpecialFolder = folders,
                FileExists = path => path == dDriveCli || File.Exists(path),
                RegistryReader = () => new[] { @"D:\reasonix" },
                RunningProcessReader = () => Array.Empty<string>(),
                PathDirectoryReader = () => Array.Empty<string>()
            };
            var mixedProbe = new ReasonixCliProbe
            {
                FileExists = path => string.Equals(path, dDriveCli, StringComparison.OrdinalIgnoreCase) || File.Exists(path),
                ProcessRunner = (path, args) =>
                {
                    var isNpm = path.EndsWith("reasonix.cmd", StringComparison.OrdinalIgnoreCase);
                    if (args.Contains("--version")) return new ReasonixProcessResult(0, isNpm ? "0.53.2" : "1.19.3", "");
                    return new ReasonixProcessResult(0, isNpm ? LegacyDoctorJson() : NewDoctorJson(), "");
                }
            };
            var mixed = await mixedProbe.SelectBestAsync(mixedDiscovery.Discover(null), null);
            Assert(mixed.Best is not null && string.Equals(mixed.Best.Path, dDriveCli, StringComparison.OrdinalIgnoreCase)
                && mixed.Best.Version == "1.19.3", "Desktop 1.19.3 与 npm 0.53.2 并存时应选 Desktop，不得选 npm 旧版。");

            // 4) 保存路径有效时优先；删除后自动恢复；保存的 npm 旧版迁移到 Desktop
            var savedSelection = await mixedProbe.SelectBestAsync(mixedDiscovery.Discover(dDriveCli), dDriveCli);
            Assert(savedSelection.Best is not null && string.Equals(savedSelection.Best.Path, dDriveCli, StringComparison.OrdinalIgnoreCase)
                && !savedSelection.SavedPathMissing, "保存路径有效时应优先。Best=" + (savedSelection.Best?.Path ?? "null") + " savedMissing=" + savedSelection.SavedPathMissing + " candidates=" + string.Join(";", savedSelection.Candidates.Select(c => c.Path)));

            var goneCli = Path.Combine(root, "gone", "reasonix-cli.exe"); // 已删除的保存路径
            var deletedProbe = new ReasonixCliProbe
            {
                FileExists = path => !string.Equals(path, goneCli, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(path, dDriveCli, StringComparison.OrdinalIgnoreCase) || File.Exists(path)),
                ProcessRunner = mixedProbe.ProcessRunner
            };
            var deletedDiscovery = new ReasonixCliDiscovery
            {
                SpecialFolder = folders,
                FileExists = path => path == dDriveCli || File.Exists(path),
                RegistryReader = () => new[] { @"D:\reasonix" },
                RunningProcessReader = () => Array.Empty<string>(),
                PathDirectoryReader = () => Array.Empty<string>()
            };
            var deleted = await deletedProbe.SelectBestAsync(deletedDiscovery.Discover(goneCli), goneCli);
            Assert(deleted.SavedPathMissing, "已保存路径被删除应标记失效。");
            Assert(deleted.Best is not null && string.Equals(deleted.Best.Path, dDriveCli, StringComparison.OrdinalIgnoreCase), "删除后应自动恢复其他可用候选。");
            Assert(deleted.DiscoveryNote is not null && deleted.DiscoveryNote.Contains("已自动重新发现", StringComparison.Ordinal), "自动恢复必须在诊断中说明。");

            var npmSaved = await mixedProbe.SelectBestAsync(mixedDiscovery.Discover(npmPath), npmPath);
            Assert(npmSaved.Best is not null && string.Equals(npmSaved.Best.Path, dDriveCli, StringComparison.OrdinalIgnoreCase), "保存的 npm 旧版应安全迁移到更兼容的 Desktop。");

            // 5) 候选重复路径去重（注册表与 PATH 指向同一路径）
            var dupDiscovery = new ReasonixCliDiscovery
            {
                SpecialFolder = folders,
                FileExists = path => path == dDriveCli,
                RegistryReader = () => new[] { @"D:\reasonix" },
                RunningProcessReader = () => Array.Empty<string>(),
                PathDirectoryReader = () => new[] { @"D:\reasonix" }
            };
            var dupCandidates = dupDiscovery.Discover(null);
            Assert(dupCandidates.Count(c => string.Equals(c.Path, dDriveCli, StringComparison.OrdinalIgnoreCase)) == 1, "重复路径应去重。");

            // 6) 安装根同时存在 CLI 与 Desktop/启动器/update-helper 时，只有 CLI 被选中，
            //    运行中 Desktop 仍能推导兄弟/版本目录的 reasonix-cli.exe，且 ProcessRunner 从不收到 Desktop 路径。
            var installRoot = Path.Combine(root, "install");
            var versionsDir = Path.Combine(installRoot, "versions", "v1.19.3");
            Directory.CreateDirectory(versionsDir);
            foreach (var name in new[] { "reasonix-cli.exe", "Reasonix.exe", "reasonix-launcher.exe", "reasonix-update-helper.exe" })
                await File.WriteAllTextAsync(Path.Combine(installRoot, name), "fake");
            await File.WriteAllTextAsync(Path.Combine(versionsDir, "reasonix-cli.exe"), "fake");
            await File.WriteAllTextAsync(Path.Combine(versionsDir, "reasonix-desktop.exe"), "fake");

            var desktopDiscovery = new ReasonixCliDiscovery
            {
                SpecialFolder = folders,
                FileExists = File.Exists,
                RegistryReader = () => Array.Empty<string>(),
                RunningProcessReader = () => new[] { Path.Combine(installRoot, "Reasonix.exe"), Path.Combine(versionsDir, "reasonix-desktop.exe") },
                PathDirectoryReader = () => Array.Empty<string>()
            };
            var desktopCandidates = desktopDiscovery.Discover(null);
            string[] forbidden = { "reasonix.exe", "reasonix-desktop.exe", "reasonix-launcher.exe", "reasonix-update-helper.exe" };
            Assert(desktopCandidates.Count > 0
                && desktopCandidates.All(c => !forbidden.Contains(Path.GetFileName(c.Path), StringComparer.OrdinalIgnoreCase)),
                "安装根同时存在 CLI 与 Desktop 启动器时，候选不得包含任何 Desktop/启动器。候选=" + string.Join(";", desktopCandidates.Select(c => c.Path)));
            Assert(desktopCandidates.Any(c => string.Equals(c.Path, Path.Combine(installRoot, "reasonix-cli.exe"), StringComparison.OrdinalIgnoreCase)),
                "运行中 Desktop 应推导安装根兄弟 reasonix-cli.exe。");
            Assert(desktopCandidates.Any(c => string.Equals(c.Path, Path.Combine(versionsDir, "reasonix-cli.exe"), StringComparison.OrdinalIgnoreCase)),
                "运行中 Desktop 应推导版本目录 reasonix-cli.exe。");

            var captured = new List<string>();
            var desktopProbe = new ReasonixCliProbe
            {
                ProcessRunner = (path, args) =>
                {
                    captured.Add(path);
                    return args.Contains("--version")
                        ? new ReasonixProcessResult(0, "1.19.3", "")
                        : new ReasonixProcessResult(0, NewDoctorJson(), "");
                }
            };
            var desktopSelection = await desktopProbe.SelectBestAsync(desktopCandidates, null);
            Assert(captured.Count > 0
                && captured.All(p => !forbidden.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)),
                "ProcessRunner 从未收到 Desktop/启动器路径，仅探测 CLI/shim。captured=" + string.Join(";", captured));
            Assert(desktopSelection.Best is not null
                && string.Equals(desktopSelection.Best.Path, Path.Combine(installRoot, "reasonix-cli.exe"), StringComparison.OrdinalIgnoreCase)
                && desktopSelection.Best.Source == ReasonixCliSource.RunningProcess,
                "应仅选中运行中 Desktop 派生的 reasonix-cli.exe。Best=" + (desktopSelection.Best?.Path ?? "null"));
        });
    }

    private static async Task TestReasonixDoctorCompatibilityAsync()
    {
        await WithTempDirectoryAsync("reasonix-doctor", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var service = new ReasonixIntegrationService(codexRoot, app);

            // 6) doctor exit 1 + stdout 有新版有效 JSON → 模型仍可用并保留警告
            var exit1Cli = Path.Combine(root, "reasonix-exit1.cmd");
            await File.WriteAllTextAsync(exit1Cli, "@echo off\r\necho " + EchoLine(NewDoctorJson()) + "\r\nexit /b 1\r\n");
            service.Enable(exit1Cli, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            var exit1Status = await service.DiagnoseAsync();
            Assert(exit1Status.Installed && exit1Status.DefaultModel == "opencode/deepseek-v4-flash" && exit1Status.CredentialReady, "exit 1 + 有效 JSON 应仍能读取状态与凭据。");
            Assert(exit1Status.DoctorWarning is not null && exit1Status.DoctorWarning.Contains("退出码 1", StringComparison.Ordinal), "应保留 doctor 失败警告。");
            var models = await service.GetAvailableModelsAsync();
            Assert(models.Any(m => m.Id == "opencode/deepseek-v4-flash"), "exit 1 + 有效 JSON 应仍返回模型列表。");

            // 7) stdout 空、stderr 空 → 错误信息仍非空且含路径/退出码
            var silentCli = Path.Combine(root, "reasonix-silent.cmd");
            await File.WriteAllTextAsync(silentCli, "@echo off\r\nif \"%1\"==\"--version\" ( echo 1.19.3 & exit /b 0 )\r\nif \"%1\"==\"doctor\" ( exit /b 1 )\r\nexit /b 0\r\n");
            service.Enable(silentCli, string.Empty, ReasonixPermissionMode.Full);
            var silentStatus = await service.DiagnoseAsync();
            Assert(silentStatus.Installed && string.Equals(silentStatus.ExecutablePath, silentCli, StringComparison.OrdinalIgnoreCase), "CLI 存在但 doctor 无输出时仍应选中它（不得伪装为未安装）。");
            Assert(!string.IsNullOrWhiteSpace(silentStatus.CredentialMessage)
                && silentStatus.CredentialMessage.Contains("退出码 1", StringComparison.Ordinal)
                && silentStatus.CredentialMessage.Contains(silentCli, StringComparison.Ordinal), "stdout/stderr 均空时错误必须非空且含路径与退出码：" + silentStatus.CredentialMessage);

            // 8) 旧版 doctor JSON 不含 providers → 明确不兼容提示
            var legacyCli = Path.Combine(root, "reasonix-legacy.cmd");
            await File.WriteAllTextAsync(legacyCli, "@echo off\r\necho " + EchoLine(LegacyDoctorJson()) + "\r\nexit /b 0\r\n");
            service.Enable(legacyCli, "opencode/old-model", ReasonixPermissionMode.Full);
            var legacyStatus = await service.DiagnoseAsync();
            Assert(legacyStatus.ProtocolCompatibility == "legacy", "旧版 JSON 应标记协议不兼容。");
            Assert(legacyStatus.CredentialMessage.Contains("不兼容", StringComparison.Ordinal), "旧版 JSON 应给出明确不兼容提示。");
            await AssertThrowsAsync<InvalidOperationException>(() => service.GetAvailableModelsAsync());

            // 9) stdout 含 BOM/ANSI/前后噪声仍可解析
            var noiseFile = Path.Combine(root, "noise.json");
            await File.WriteAllTextAsync(noiseFile, "\u001B[31m\uFEFF" + NewDoctorJson("opencode/bom-model") + "\r\nhelper log noise", new UTF8Encoding(false));
            var noisyCli = Path.Combine(root, "reasonix-noisy.cmd");
            await File.WriteAllTextAsync(noisyCli, "@echo off\r\ntype \"" + noiseFile + "\"\r\nexit /b 0\r\n");
            service.Enable(noisyCli, "opencode/bom-model", ReasonixPermissionMode.Full);
            var noisyStatus = await service.DiagnoseAsync();
            Assert(noisyStatus.Installed && noisyStatus.DefaultModel == "opencode/bom-model" && noisyStatus.CredentialReady, "BOM/ANSI/噪声输出应仍可解析 doctor JSON。");
            Assert(noisyStatus.DoctorWarning is null, "exit 0 时不应有 doctor 警告。");

            // 10) 敏感字段在诊断中脱敏
            var redacted = ReasonixIntegrationService.RedactSecrets("apiKey: \"sk-abc123def456xyz\", jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnopqrstuvwxyz, password=\"hunter2secret\", token=0123456789abcdef, Bearer ghijklmnopqrstuvwxyz123456");
            Assert(!redacted.Contains("sk-abc123def456xyz", StringComparison.Ordinal)
                && !redacted.Contains("hunter2secret", StringComparison.Ordinal)
                && !redacted.Contains("eyJhbGciOiJIUzI1NiJ9", StringComparison.Ordinal)
                && !redacted.Contains("0123456789abcdef", StringComparison.Ordinal)
                && !redacted.Contains("ghijklmnopqrstuvwxyz123456", StringComparison.Ordinal), "诊断文本中的敏感字段应被脱敏：" + redacted);
            var leakyCli = Path.Combine(root, "reasonix-leaky.cmd");
            await File.WriteAllTextAsync(leakyCli, "@echo off\r\nif \"%1\"==\"--version\" ( echo 1.19.3 & exit /b 0 )\r\necho sk-abc123def456xyz 1>&2\r\nexit /b 1\r\n");
            service.Enable(leakyCli, string.Empty, ReasonixPermissionMode.Full);
            var leakyStatus = await service.DiagnoseAsync();
            Assert(!leakyStatus.CredentialMessage.Contains("sk-abc123def456xyz", StringComparison.Ordinal), "doctor stderr 中的凭据不得出现在诊断中：" + leakyStatus.CredentialMessage);

            service.Disable();
        });
    }

    private static async Task TestReasonixManualSelectAsync()
    {
        await WithTempDirectoryAsync("reasonix-select", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var service = new ReasonixIntegrationService(codexRoot, app);

            var oldCli = Path.Combine(root, "old.cmd");
            var goodCli = Path.Combine(root, "good.cmd");
            var badCli = Path.Combine(root, "bad.cmd");
            var txtFile = Path.Combine(root, "note.txt");
            await File.WriteAllTextAsync(oldCli, "@echo off\r\nif \"%1\"==\"--version\" ( echo 1.19.3 & exit /b 0 )\r\nif \"%1\"==\"doctor\" ( echo " + EchoLine(NewDoctorJson()) + " & exit /b 0 )\r\nexit /b 0\r\n");
            await File.WriteAllTextAsync(goodCli, "@echo off\r\nif \"%1\"==\"--version\" ( echo 1.19.3 & exit /b 0 )\r\nif \"%1\"==\"doctor\" ( echo " + EchoLine(NewDoctorJson()) + " & exit /b 0 )\r\nexit /b 0\r\n");
            await File.WriteAllTextAsync(badCli, "@echo off\r\nexit /b 1\r\n");
            await File.WriteAllTextAsync(txtFile, "not a cli");

            service.Enable(oldCli, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            var skillRoot = Path.Combine(codexRoot, "skills", "reasonix-executor");
            var before = await File.ReadAllTextAsync(Path.Combine(skillRoot, "run-reasonix-job.ps1"));
            Assert(before.Contains(oldCli, StringComparison.Ordinal), "启用后托管脚本应引用当前 CLI。");

            // 非法：文件不存在
            await AssertThrowsAsync<FileNotFoundException>(() => service.SelectCliAsync(Path.Combine(root, "missing.exe")));
            // 非法：非可执行扩展名
            await AssertThrowsAsync<InvalidDataException>(() => service.SelectCliAsync(txtFile));
            // 非法：探测失败（无输出且退出非零）
            await AssertThrowsAsync<InvalidOperationException>(() => service.SelectCliAsync(badCli));
            // 失败不改状态：已保存路径仍为旧 CLI
            Assert(string.Equals(service.FindExecutable(), oldCli, StringComparison.OrdinalIgnoreCase), "选择失败不得改变已保存路径。");

            // 合法：验证成功后持久化，启用状态下托管脚本刷新到新路径
            await service.SelectCliAsync(goodCli);
            Assert(string.Equals(service.FindExecutable(), goodCli, StringComparison.OrdinalIgnoreCase), "合法选择应持久化新路径。");
            var after = await File.ReadAllTextAsync(Path.Combine(skillRoot, "run-reasonix-job.ps1"));
            Assert(after.Contains(goodCli, StringComparison.Ordinal) && !after.Contains(oldCli, StringComparison.Ordinal), "启用状态切换 CLI 后托管脚本应刷新到新路径。");
            var models = await service.GetAvailableModelsAsync();
            Assert(models.Any(m => m.Id == "opencode/deepseek-v4-flash"), "手动选择后可读取模型列表。");

            service.Disable();
        });
    }


    private static async Task TestReasonixMigrationPersistsAsync()
    {
        await WithTempDirectoryAsync("reasonix-migrate", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var statePath = Path.Combine(app.BaseDirectory, "reasonix-integration.json");
            var savedCli = Path.Combine(root, "deleted.cmd");
            var goodCli = Path.Combine(root, "good.cmd");
            Directory.CreateDirectory(app.BaseDirectory);

            // 协作已启用，但保存路径已失效；存在一个兼容新 CLI（来自运行中进程候选）。
            await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new
            {
                Enabled = true,
                ExecutablePath = savedCli,
                DefaultModel = "opencode/deepseek-v4-flash",
                PermissionMode = (int)ReasonixPermissionMode.Safe
            }, new JsonSerializerOptions { WriteIndented = true }));

            var versionCalls = 0;
            var doctorCalls = 0;
            var service = new ReasonixIntegrationService(codexRoot, app)
            {
                DiscoveryFactory = () => new ReasonixCliDiscovery
                {
                    FileExists = path => string.Equals(path, goodCli, StringComparison.OrdinalIgnoreCase),
                    SpecialFolder = _ => Path.Combine(root, "none"),
                    RegistryReader = () => Array.Empty<string>(),
                    RunningProcessReader = () => new[] { goodCli },
                    PathDirectoryReader = () => Array.Empty<string>(),
                    SubdirectoryReader = _ => Array.Empty<string>()
                },
                ProbeFactory = () => new ReasonixCliProbe
                {
                    FileExists = path => string.Equals(path, goodCli, StringComparison.OrdinalIgnoreCase),
                    ProcessRunner = (path, args) =>
                    {
                        if (args.Contains("--version")) { versionCalls++; return new ReasonixProcessResult(0, "1.19.3", ""); }
                        if (args.Contains("doctor")) { doctorCalls++; return new ReasonixProcessResult(0, NewDoctorJson(), ""); }
                        return new ReasonixProcessResult(0, "", "");
                    }
                }
            };

            var selection = await service.DiscoverBestAsync();
            Assert(selection.Best is not null && string.Equals(selection.Best.Path, goodCli, StringComparison.OrdinalIgnoreCase), "迁移后应选中新兼容 CLI。");

            var persisted = JsonSerializer.Deserialize<JsonObject>(await File.ReadAllTextAsync(statePath))!;
            Assert(string.Equals((string?)persisted["ExecutablePath"], goodCli, StringComparison.OrdinalIgnoreCase), "自动迁移必须真正写入新 ExecutablePath。");
            Assert((bool?)persisted["Enabled"] == true, "迁移必须保留 Enabled。");
            Assert((string?)persisted["DefaultModel"] == "opencode/deepseek-v4-flash", "迁移必须保留 DefaultModel。");
            Assert((int?)persisted["PermissionMode"] == (int)ReasonixPermissionMode.Safe, "迁移必须保留 PermissionMode。");

            var jobHost = await File.ReadAllTextAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "run-reasonix-job.ps1"));
            Assert(jobHost.Contains(goodCli, StringComparison.Ordinal), "启用协作时迁移后托管脚本应使用新 CLI。");

            Assert(versionCalls == 1 && doctorCalls == 1, $"迁移探测应恰好一次：version={versionCalls}, doctor={doctorCalls}。");

            service.Disable();
        });
    }

    private static async Task TestReasonixNoMigrationWhenNoCandidateAsync()
    {
        await WithTempDirectoryAsync("reasonix-nomigrate", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var statePath = Path.Combine(app.BaseDirectory, "reasonix-integration.json");
            var savedCli = Path.Combine(root, "deleted.cmd");
            var legacyCli = Path.Combine(root, "legacy.cmd");
            Directory.CreateDirectory(app.BaseDirectory);
            await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new
            {
                Enabled = false,
                ExecutablePath = savedCli,
                DefaultModel = string.Empty,
                PermissionMode = (int)ReasonixPermissionMode.Full
            }, new JsonSerializerOptions { WriteIndented = true }));

            // 只有旧协议（无 providers）候选 → 无兼容替代，不得修改旧状态。
            var legacyService = new ReasonixIntegrationService(codexRoot, app)
            {
                DiscoveryFactory = () => new ReasonixCliDiscovery
                {
                    FileExists = path => string.Equals(path, legacyCli, StringComparison.OrdinalIgnoreCase),
                    SpecialFolder = _ => Path.Combine(root, "none"),
                    RegistryReader = () => Array.Empty<string>(),
                    RunningProcessReader = () => new[] { legacyCli },
                    PathDirectoryReader = () => Array.Empty<string>(),
                    SubdirectoryReader = _ => Array.Empty<string>()
                },
                ProbeFactory = () => new ReasonixCliProbe
                {
                    FileExists = path => string.Equals(path, legacyCli, StringComparison.OrdinalIgnoreCase),
                    ProcessRunner = (path, args) =>
                        args.Contains("--version")
                            ? new ReasonixProcessResult(0, "0.53.2", "")
                            : new ReasonixProcessResult(0, LegacyDoctorJson(), "")
                }
            };
            await legacyService.DiscoverBestAsync();
            var persisted = JsonSerializer.Deserialize<JsonObject>(await File.ReadAllTextAsync(statePath))!;
            Assert(string.Equals((string?)persisted["ExecutablePath"], savedCli, StringComparison.OrdinalIgnoreCase), "无兼容候选时不得修改旧 ExecutablePath。");

            // 彻底无候选 → 同样不修改旧状态。
            var emptyService = new ReasonixIntegrationService(codexRoot, app)
            {
                DiscoveryFactory = () => new ReasonixCliDiscovery
                {
                    FileExists = _ => false,
                    SpecialFolder = _ => Path.Combine(root, "none"),
                    RegistryReader = () => Array.Empty<string>(),
                    RunningProcessReader = () => Array.Empty<string>(),
                    PathDirectoryReader = () => Array.Empty<string>(),
                    SubdirectoryReader = _ => Array.Empty<string>()
                },
                ProbeFactory = () => new ReasonixCliProbe { FileExists = _ => false }
            };
            var emptySelection = await emptyService.DiscoverBestAsync();
            Assert(emptySelection.Best is null, "无候选时不应选中任何 CLI。");
            persisted = JsonSerializer.Deserialize<JsonObject>(await File.ReadAllTextAsync(statePath))!;
            Assert(string.Equals((string?)persisted["ExecutablePath"], savedCli, StringComparison.OrdinalIgnoreCase), "彻底无候选时也不得修改旧 ExecutablePath。");
        });
    }

    private static async Task TestReasonixRefreshReuseProbeAsync()
    {
        await WithTempDirectoryAsync("reasonix-reuse", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var cli = Path.Combine(root, "cli.cmd");
            var versionCalls = 0;
            var doctorCalls = 0;
            var service = new ReasonixIntegrationService(codexRoot, app)
            {
                DiscoveryFactory = () => new ReasonixCliDiscovery
                {
                    FileExists = path => string.Equals(path, cli, StringComparison.OrdinalIgnoreCase),
                    SpecialFolder = _ => Path.Combine(root, "none"),
                    RegistryReader = () => Array.Empty<string>(),
                    RunningProcessReader = () => new[] { cli },
                    PathDirectoryReader = () => Array.Empty<string>(),
                    SubdirectoryReader = _ => Array.Empty<string>()
                },
                ProbeFactory = () => new ReasonixCliProbe
                {
                    FileExists = path => string.Equals(path, cli, StringComparison.OrdinalIgnoreCase),
                    ProcessRunner = (path, args) =>
                    {
                        if (args.Contains("--version")) { versionCalls++; return new ReasonixProcessResult(0, "1.19.3", ""); }
                        if (args.Contains("doctor")) { doctorCalls++; return new ReasonixProcessResult(0, NewDoctorJson(), ""); }
                        return new ReasonixProcessResult(0, "", "");
                    }
                }
            };

            // UI 一次刷新所用服务序列：一次候选探测 + 复用 selection 的诊断/脚本/模型读取。
            var selection = await service.DiscoverBestAsync();
            service.RefreshManagedScripts(selection.Best?.Path);
            var status = await service.DiagnoseAsync(precomputedSelection: selection);
            var models = await service.GetAvailableModelsAsync(precomputedSelection: selection);

            Assert(status.Installed && status.DefaultModel == "opencode/deepseek-v4-flash", "预计算 selection 的诊断应正常复用。");
            Assert(models.Any(m => m.Id == "opencode/deepseek-v4-flash"), "预计算 selection 的模型读取应正常复用。");
            Assert(versionCalls == 1 && doctorCalls == 1, $"单次刷新应只探测一次：version={versionCalls}, doctor={doctorCalls}。");
        });
    }

    private static async Task TestReasonixRecentTasksAsync()
    {
        await WithTempDirectoryAsync("reasonix-recent", async root =>
        {
            var app = new AppPaths(Path.Combine(root, "app"));
            var service = new ReasonixIntegrationService(Path.Combine(root, "codex"), app);
            Directory.CreateDirectory(app.ReasonixTasksDirectory);

            // 1) 真实旧样式：Windows PowerShell ConvertTo-Json 输出 \/Date(milliseconds)\/
            const long legacyMs = 1785675136894L;
            var legacyUtc = DateTimeOffset.FromUnixTimeMilliseconds(legacyMs).UtcDateTime;
            await File.WriteAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-legacy.json"), $$"""
            {
                "TaskId": "run-legacy",
                "ProjectRoot": "C:\\实用软件开发\\codex-helper",
                "TaskDirectory": "C:\\实用软件开发\\codex-helper\\.codex-helper\\runs\\run-legacy",
                "State": "running",
                "Phase": "executing",
                "PermissionMode": "Full",
                "StartedUtc": "\/Date(1785674994291)\/",
                "UpdatedUtc": "\/Date({{legacyMs}})\/",
                "HostProcessId": {{Environment.ProcessId}},
                "EventCount": 6300,
                "Message": "Processed 6300 events"
            }
            """);

            var snapshot = service.GetRecentTasks(10);
            Assert(snapshot.Diagnostics.Count == 0, "旧 /Date(ms)/ 格式应正常解析，不产生诊断。");
            Assert(snapshot.Tasks.Count == 1, "旧格式任务应被读取。");
            var legacy = snapshot.Tasks.Single();
            Assert(legacy.TaskId == "run-legacy" && legacy.IsRunning && legacy.EventCount == 6300, "旧格式任务字段不正确。");
            Assert(legacy.UpdatedUtc == legacyUtc && legacy.UpdatedUtc.Kind == DateTimeKind.Utc, "旧格式日期应解析为正确 UTC 时间。");
            Assert(legacy.ProjectRoot == @"C:\实用软件开发\codex-helper", "中文项目路径应原样保留。");

            // 2) 标准 ISO 8601 UTC
            var isoUtc = new DateTime(2026, 8, 2, 13, 30, 0, DateTimeKind.Utc).AddTicks(1234567);
            await File.WriteAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-iso.json"), $$"""
            {
                "TaskId": "run-iso",
                "ProjectRoot": "C:\\实用软件开发\\reasonix-focus-timer-test",
                "TaskDirectory": "C:\\实用软件开发\\reasonix-focus-timer-test\\.codex-helper\\runs\\run-iso",
                "State": "completed",
                "Phase": "review",
                "PermissionMode": "Full",
                "StartedUtc": "2026-08-02T12:00:00.0000000Z",
                "UpdatedUtc": "{{isoUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}}",
                "HostProcessId": 123,
                "EventCount": 42,
                "Message": "done"
            }
            """);

            // 3) 损坏文件与有效文件并存：有效任务仍显示，损坏文件给出诊断摘要
            await File.WriteAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-corrupt.json"), "{ 这不是有效 JSON ");
            snapshot = service.GetRecentTasks(10);
            Assert(snapshot.Tasks.Any(task => task.TaskId == "run-iso"), "损坏文件不得使有效任务失效。");
            Assert(snapshot.Tasks.First().TaskId == "run-iso" && snapshot.Tasks.Count == 2, "任务应按 UpdatedUtc 降序排列（ISO 更新更晚应排最前）。");
            var corrupt = snapshot.Diagnostics.SingleOrDefault(diagnostic => diagnostic.FileName == "run-corrupt.json");
            Assert(corrupt is not null && corrupt.Reason.Contains("无效", StringComparison.Ordinal), "损坏文件应给出可见诊断摘要。");

            // 即使 limit=1 且最新写入的状态文件损坏，也必须继续寻找一个可用任务。
            File.SetLastWriteTimeUtc(Path.Combine(app.ReasonixTasksDirectory, "run-corrupt.json"), DateTime.UtcNow.AddMinutes(2));
            snapshot = service.GetRecentTasks(1);
            Assert(snapshot.Tasks.Count == 1 && snapshot.Tasks.Single().TaskId == "run-iso", "损坏的最新状态文件不得遮住较早的有效任务。");
            Assert(snapshot.Diagnostics.Any(diagnostic => diagnostic.FileName == "run-corrupt.json"), "limit=1 时仍应保留最新损坏文件的诊断。");

            // 4) JSON 语法有效但日期字段损坏 → 同样计入损坏诊断，不进有效列表
            await File.WriteAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-bad-date.json"), """
            {
                "TaskId": "run-bad-date",
                "ProjectRoot": "C:\\demo",
                "TaskDirectory": "C:\\demo\\.codex-helper\\runs\\run-bad-date",
                "State": "completed",
                "Phase": "review",
                "PermissionMode": "Full",
                "StartedUtc": "not-a-date",
                "UpdatedUtc": "2026-08-02T12:00:00Z",
                "HostProcessId": 1,
                "EventCount": 0,
                "Message": ""
            }
            """);
            snapshot = service.GetRecentTasks(10);
            Assert(snapshot.Tasks.Count == 2 && snapshot.Tasks.All(task => task.TaskId is "run-iso" or "run-legacy"), "日期损坏文件不得进入有效任务列表。");
            Assert(snapshot.Diagnostics.Any(diagnostic => diagnostic.FileName == "run-bad-date.json"), "无效日期字段应计入损坏诊断。");

            // 5) TaskHost 已退出但状态仍为 running：界面不得永远显示运行中。
            var staleTaskDirectory = Path.Combine(root, "stale-task");
            Directory.CreateDirectory(staleTaskDirectory);
            await File.WriteAllTextAsync(Path.Combine(staleTaskDirectory, "EXECUTION_REPORT.md"), "ready for review");
            await File.WriteAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-stale.json"), $$"""
            {
                "TaskId": "run-stale",
                "ProjectRoot": "C:\\demo",
                "TaskDirectory": "{{staleTaskDirectory.Replace("\\", "\\\\")}}",
                "State": "running",
                "Phase": "executing",
                "PermissionMode": "Full",
                "StartedUtc": "2026-08-02T14:00:00Z",
                "UpdatedUtc": "2026-08-02T14:01:00Z",
                "HostProcessId": 2147483647,
                "EventCount": 8,
                "Message": "working"
            }
            """);
            File.SetLastWriteTimeUtc(Path.Combine(app.ReasonixTasksDirectory, "run-stale.json"), DateTime.UtcNow.AddMinutes(3));
            var stale = service.GetRecentTasks(1).Tasks.Single();
            Assert(stale.TaskId == "run-stale" && !stale.IsRunning && stale.State == "interrupted", "已退出的 TaskHost 应自动归一化为 interrupted。");
            Assert(stale.Phase == "等待验收" && stale.Message.Contains("执行报告", StringComparison.Ordinal), "存在执行报告的中断任务应提示等待验收。");
        });
    }

    private static async Task TestReasonixChineseManifestUtf8Async()
    {
        await WithTempDirectoryAsync("reasonix-utf8", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var executable = Path.Combine(root, "reasonix-cli.ps1");
            await File.WriteAllTextAsync(executable, "[IO.File]::WriteAllText((Join-Path $env:CODEX_HELPER_TEST_TASK 'argv.txt'), ($args -join [Environment]::NewLine), [Text.UTF8Encoding]::new($false))");
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);

            // 中文 projectRoot/taskDirectory + 无 BOM UTF-8 manifest（声明 Fast/profile/effort/workerChecks）。
            var project = Path.Combine(root, "中文项目");
            var task = Path.Combine(project, ".codex-helper", "runs", "run-中文manifest");
            Directory.CreateDirectory(task);
            await File.WriteAllTextAsync(Path.Combine(task, "SPEC.md"), "spec 中文");
            await File.WriteAllTextAsync(Path.Combine(task, "ACCEPTANCE.md"), "- a");
            await File.WriteAllTextAsync(Path.Combine(task, "HANDOFF.md"), "h");
            await File.WriteAllTextAsync(Path.Combine(task, "manifest.json"), """
            {
              "intensity": "fast",
              "profile": "balanced",
              "effort": "low",
              "workerChecks": ["build", "test 中文"]
            }
            """, new UTF8Encoding(false));

            var runResult = await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), project, task, Path.Combine(root, "rh"), "", "thread-utf8", taskDir: task);
            Assert(runResult.ExitCode == 0, "中文 manifest runner 应正常结束：" + runResult.Output);
            var argv = await File.ReadAllTextAsync(Path.Combine(task, "argv.txt"));
            Assert(argv.Contains("--profile" + Environment.NewLine + "balanced", StringComparison.Ordinal) && argv.Contains("--effort" + Environment.NewLine + "low", StringComparison.Ordinal), "manifest Fast 声明必须真实进入生成命令：" + argv);
            // workerChecks 现在写入派生 WORKER_ACCEPTANCE.md（UTF-8 原子写），而非直接嵌入命令行提示。
            var workerAccept = await File.ReadAllTextAsync(Path.Combine(task, "WORKER_ACCEPTANCE.md"));
            Assert(workerAccept.Contains("build") && workerAccept.Contains("test 中文", StringComparison.Ordinal), "manifest workerChecks 应进入派生 WORKER_ACCEPTANCE.md：" + workerAccept);
            Assert(!argv.Contains("test 中文", StringComparison.Ordinal), "workerChecks 不得再直接嵌入命令行提示，应通过 WORKER_ACCEPTANCE.md 传递：" + argv);
            var status = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-中文manifest.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert(status.ExecutionIntensity == "fast" && status.ExecutionProfile == "balanced" && status.ExecutionEffort == "low", "manifest 策略应持久化到状态。");
            Assert(status.ExecutionSource == "manifest", "显式声明来源应为 manifest：" + status.ExecutionSource);
            service.Disable();
        });
    }

    private static async Task TestReasonixProgressStagesAsync()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // 场景 A：fake 延迟写正常 PROGRESS.json，进程结束前应观察到阶段状态与 UI 文案。
        await WithTempDirectoryAsync("reasonix-progress", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var task = Path.Combine(root, "p", ".codex-helper", "runs", "run-progress");
            Directory.CreateDirectory(task);
            await File.WriteAllTextAsync(Path.Combine(task, "SPEC.md"), "spec");
            await File.WriteAllTextAsync(Path.Combine(task, "ACCEPTANCE.md"), "- a");
            await File.WriteAllTextAsync(Path.Combine(task, "HANDOFF.md"), "h");
            await File.WriteAllTextAsync(Path.Combine(task, "manifest.json"), "{}");
            var writeScript = Path.Combine(root, "wprogress.ps1");
            await File.WriteAllTextAsync(writeScript, """
                param([string]$Action='progress')
                switch ($Action) {
                  'progress' { [IO.File]::WriteAllText((Join-Path $env:CODEX_HELPER_TEST_TASK 'PROGRESS.json'), '{"schemaVersion":1,"taskId":"run-progress","stage":"implementing","summary":"正在实现核心逻辑","updatedUtc":"' + [DateTime]::UtcNow.ToString('o') + '","completedChecks":2,"totalChecks":6}', [Text.UTF8Encoding]::new($false)) }
                  'report' { [IO.File]::WriteAllText((Join-Path $env:CODEX_HELPER_TEST_TASK 'EXECUTION_REPORT.md'), 'done', [Text.UTF8Encoding]::new($false)) }
                }
                """, new UTF8Encoding(true));
            var executable = Path.Combine(root, "reasonix-cli.cmd");
            await File.WriteAllTextAsync(executable, """
                @echo off
                setlocal EnableDelayedExpansion
                echo {"kind":"turn_started"}
                powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Sleep -Milliseconds 500"
                powershell -NoProfile -ExecutionPolicy Bypass -File %CODEX_HELPER_TEST_WRITE_SCRIPT% progress
                echo {"kind":"turn_started"}
                powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Sleep -Milliseconds 900"
                powershell -NoProfile -ExecutionPolicy Bypass -File %CODEX_HELPER_TEST_WRITE_SCRIPT% report
                exit /b 0
                """);
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            var statusPath = Path.Combine(app.ReasonixTasksDirectory, "run-progress.json");
            var runnerTask = Task.Run(() => RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), Path.Combine(root, "p"), task, Path.Combine(root, "rh"), "", "thread-progress", taskDir: task, writeScriptPath: writeScript));
            ReasonixTaskStatus? observedBefore = null;
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
                if (File.Exists(statusPath))
                {
                    try { observedBefore = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(statusPath), options); } catch { observedBefore = null; }
                    if (observedBefore is { ProgressStage: "implementing" } && !runnerTask.IsCompleted) break;
                }
            }
            var sawBeforeCompletion = observedBefore is { ProgressStage: "implementing" } && !runnerTask.IsCompleted;
            await runnerTask; // 先等 job host 结束并释放 .reasonix.lock，再做可能失败的断言
            var final = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(statusPath), options)!;
            Assert(final.ProgressStage == "done", "成功且报告存在时最终阶段应强制 done：" + final.Message);
            Assert(sawBeforeCompletion, "进程结束前应观察到 PROGRESS 阶段：" + (observedBefore?.Message ?? "无状态"));
            Assert(final.CompletedChecks == 2 && final.TotalChecks == 6, "检查计数应保留。");
            Assert(final.ProgressSummary == "正在实现核心逻辑", "阶段摘要应持久化。");
            var ui = ReasonixUiText.SummaryText(final);
            Assert(ui.Contains("已完成", StringComparison.Ordinal) && ui.Contains("2/6", StringComparison.Ordinal), "UI 应显示最终阶段与检查计数：" + ui);
            service.Disable();
        });

        // 场景 B：损坏/过大/半写/未知 stage/taskId 不匹配/超长 summary 均安全处理，不中断任务。
        await WithTempDirectoryAsync("reasonix-progress-bad", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var executable = Path.Combine(root, "reasonix-cli.cmd");
            await File.WriteAllTextAsync(executable, "@echo off\r\nexit /b 0\r\n");
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);

            var longSummary = new string('y', 300);
            var cases = new (string Id, string ProgressText)[]
            {
                ("run-oversize", new string('x', 17000)),
                ("run-halfwrite", """{"stage":"impl"""),
                ("run-badtaskid", """{"stage":"testing","taskId":"other"}"""),
                ("run-unknownstage", """{"stage":"nonsense"}"""),
                ("run-longsummary", "{\"stage\":\"analyzing\",\"summary\":\"" + longSummary + "\"}"),
                ("run-baddate", """{"stage":"reporting","updatedUtc":"not-a-date"}"""),
                ("run-futuredate", """{"stage":"reporting","updatedUtc":"9999-12-31T00:00:00Z"}""")
            };
            foreach (var (taskId, progressText) in cases)
            {
                var task = Path.Combine(root, "p" + taskId, ".codex-helper", "runs", taskId);
                Directory.CreateDirectory(task);
                foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                    await File.WriteAllTextAsync(Path.Combine(task, contract), contract == "manifest.json" ? "{}" : "test");
                await File.WriteAllTextAsync(Path.Combine(task, "PROGRESS.json"), progressText);
                var result = await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), Path.Combine(root, "p" + taskId), task, Path.Combine(root, "rh"), "", "thread-" + taskId, taskDir: task);
                Assert(result.ExitCode == 0, $"损坏 PROGRESS 不得中断任务 {taskId}：" + result.Output);
                var status = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, taskId + ".json")), options)!;
                // 无 report 的正常终态是 failed/failed；损坏 PROGRESS 不得使 job host 崩溃进入 error。
                Assert(status.Phase != "error", $"损坏 PROGRESS 后 job host 不得崩溃进入 error {taskId}：state={status.State} phase={status.Phase}");
                if (taskId == "run-futuredate")
                {
                    Assert(status.ProgressDiagnostic != null && status.ProgressDiagnostic.Contains("晚于当前时间", StringComparison.Ordinal), "未来 updatedUtc 应产生不泄密诊断：" + (status.ProgressDiagnostic ?? "无"));
                    Assert(status.ProgressUpdatedUtc <= DateTime.UtcNow.AddSeconds(10), "未来 updatedUtc 应被夹到观察时间附近：" + status.ProgressUpdatedUtc);
                }
                // 状态文件必须可正常反序列化（不因 PROGRESS 字段不合法而整体损坏）。
            }
            service.Disable();
        });

        // 场景 C：旧式 phase 白名单兼容、steps 兜底计数、未知 phase/混合恶意 steps 安全处理、标准协议优先、成功/失败最终态。
        await WithTempDirectoryAsync("reasonix-progress-legacy", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var service = new ReasonixIntegrationService(codexRoot, app);
            // 先写好 CLI，再 Enable（Enable 会校验 CLI 存在）。
            var legacyScript = Path.Combine(root, "wlegacy.ps1");
            await File.WriteAllTextAsync(legacyScript, """
                param([string]$Action='progress')
                switch ($Action) {
                  'progress' { [IO.File]::WriteAllText((Join-Path $env:CODEX_HELPER_TEST_TASK 'PROGRESS.json'), '{"taskId":"run-legacy","phase":"implementation","steps":[{"status":"completed","name":"step-a"},{"status":"completed","name":"step-b"},{"status":"completed","name":"step-c"},{"status":"completed","name":"step-d"}]}', [Text.UTF8Encoding]::new($false)) }
                  'report' { [IO.File]::WriteAllText((Join-Path $env:CODEX_HELPER_TEST_TASK 'EXECUTION_REPORT.md'), 'done', [Text.UTF8Encoding]::new($false)) }
                }
                """, new UTF8Encoding(true));
            var legacyCli = Path.Combine(root, "reasonix-cli.cmd");
            await File.WriteAllTextAsync(legacyCli, """
                @echo off
                setlocal EnableDelayedExpansion
                echo {"kind":"turn_started"}
                powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Sleep -Milliseconds 500"
                powershell -NoProfile -ExecutionPolicy Bypass -File %CODEX_HELPER_TEST_WRITE_SCRIPT% progress
                echo {"kind":"turn_started"}
                powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Sleep -Milliseconds 700"
                powershell -NoProfile -ExecutionPolicy Bypass -File %CODEX_HELPER_TEST_WRITE_SCRIPT% report
                exit /b 0
                """);
            service.Enable(legacyCli, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);

            // C1：真实旧式样本（phase=implementation + steps）成功路径：中间态 implementing、计数 4/4、不泄露步骤名，最终强制 done。
            var taskLegacy = Path.Combine(root, "p", ".codex-helper", "runs", "run-legacy");
            Directory.CreateDirectory(taskLegacy);
            foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                await File.WriteAllTextAsync(Path.Combine(taskLegacy, contract), contract == "manifest.json" ? "{}" : "test");
            var statusLegacyPath = Path.Combine(app.ReasonixTasksDirectory, "run-legacy.json");
            var legacyRunner = Task.Run(() => RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), Path.Combine(root, "p"), taskLegacy, Path.Combine(root, "rh"), "", "thread-legacy", taskDir: taskLegacy, writeScriptPath: legacyScript));
            ReasonixTaskStatus? midLegacy = null;
            var dl = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < dl)
            {
                await Task.Delay(200);
                if (File.Exists(statusLegacyPath))
                {
                    try { midLegacy = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(statusLegacyPath), options); } catch { midLegacy = null; }
                    if (midLegacy is { ProgressStage: "implementing" } && !legacyRunner.IsCompleted) break;
                }
            }
            var sawImplementing = midLegacy is { ProgressStage: "implementing" } && !legacyRunner.IsCompleted;
            await legacyRunner;
            var finalLegacy = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(statusLegacyPath), options)!;
            Assert(sawImplementing, "旧式 phase=implementation 应映射为 implementing：" + (midLegacy?.Message ?? "无状态"));
            Assert(finalLegacy.ProgressStage == "done", "旧式样本成功且报告存在最终应强制 done：" + finalLegacy.Message);
            Assert(finalLegacy.CompletedChecks == 4 && finalLegacy.TotalChecks == 4, "旧式 steps 兜底计数应为 4/4：" + finalLegacy.Message);
            var legacyUi = ReasonixUiText.SummaryText(finalLegacy);
            Assert(!legacyUi.Contains("step-a") && !legacyUi.Contains("step-b"), "不得展示旧式步骤名称：" + legacyUi);

            // C2：失败最终态（无报告）不得伪装 done；未知 phase 忽略；混合恶意 steps 只统计对象项；标准 stage 优先于 phase。
            var failCases = new (string Id, string ProgressText, string? ExpectStage, int? ExpectCompleted, int? ExpectTotal, string? ExpectDiag)[]
            {
                ("run-unknownphase", """{"phase":"garbage"}""", null, null, null, "未知 phase"),
                ("run-mixedsteps", """{"phase":"testing","steps":[{"status":"completed","name":"leak-a"},{"status":"blocked","name":"leak-b"},"plain",42,{"status":"passed","note":"secret"}]}""", "testing", 2, 3, null),
                ("run-standardpriority", """{"stage":"implementing","phase":"completed","steps":[{"status":"completed"},{"status":"completed"}]}""", "implementing", 2, 2, null)
            };
            foreach (var (taskId, progressText, expectStage, expectCompleted, expectTotal, expectDiag) in failCases)
            {
                var task = Path.Combine(root, "f" + taskId, ".codex-helper", "runs", taskId);
                Directory.CreateDirectory(task);
                foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                    await File.WriteAllTextAsync(Path.Combine(task, contract), contract == "manifest.json" ? "{}" : "test");
                await File.WriteAllTextAsync(Path.Combine(task, "PROGRESS.json"), progressText);
                var result = await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), Path.Combine(root, "f" + taskId), task, Path.Combine(root, "rh"), "", "thread-" + taskId, taskDir: task);
                Assert(result.ExitCode == 0, $"case {taskId} 不应中断任务：" + result.Output);
                var status = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, taskId + ".json")), options)!;
                Assert(status.State == "failed" && status.ProgressStage != "done", $"无报告失败终态不得显示 done {taskId}：stage={status.ProgressStage} state={status.State}");
                if (expectStage is not null) Assert(status.ProgressStage == expectStage, $"case {taskId} 阶段应为 {expectStage}：" + status.ProgressStage);
                if (expectCompleted is not null) Assert(status.CompletedChecks == expectCompleted, $"case {taskId} completed 计数应为 {expectCompleted}：" + status.CompletedChecks);
                if (expectTotal is not null) Assert(status.TotalChecks == expectTotal, $"case {taskId} total 计数应为 {expectTotal}：" + status.TotalChecks);
                if (expectDiag is not null) Assert(status.ProgressDiagnostic != null && status.ProgressDiagnostic.Contains(expectDiag, StringComparison.Ordinal), $"case {taskId} 应产生诊断：" + (status.ProgressDiagnostic ?? "无"));
                if (taskId == "run-mixedsteps")
                {
                    var ui = ReasonixUiText.SummaryText(status);
                    Assert(!ui.Contains("leak-a") && !ui.Contains("secret"), "混合 steps 不得泄露名称或 notes：" + ui);
                }
            }
            service.Disable();
        });
    }

    private static async Task TestReasonixEffortNormalizationAsync()
    {
        // C# 层：DeepSeek 默认模型下 Fast/Standard/Strict 均不生成 medium。
        foreach (var intensity in new[] { "fast", "standard", "strict" })
        {
            var plan = ReasonixExecutionPolicy.Resolve(new ReasonixManifestPolicy(null, null, null, intensity, null, null, null, null, null), new string('x', 3000), "- a\n", null, "opencode/deepseek-v4-flash");
            Assert(plan.Effort != "medium", $"DeepSeek {intensity} 不得生成 medium：" + plan.Effort);
            Assert(plan.Effort == (intensity == "strict" ? "high" : "low"), $"DeepSeek {intensity} effort 应为 {(intensity == "strict" ? "high" : "low")}：" + plan.Effort);
        }
        Assert(ReasonixExecutionPolicy.IsDeepSeekModel("opencode/deepseek-v4-flash"), "IsDeepSeekModel 应识别 deepseek。");
        Assert(!ReasonixExecutionPolicy.IsDeepSeekModel("anthropic/claude"), "非 deepseek 不应命中。");
        Assert(ReasonixExecutionPolicy.Resolve(new ReasonixManifestPolicy(null, null, null, "standard", null, null, null, null, null), new string('x', 3000), "- a\n", null, null).Effort == "medium", "非 DeepSeek Standard 保留 medium。");
        Assert(ReasonixExecutionPolicy.Resolve(new ReasonixManifestPolicy(null, null, null, "standard", null, null, null, null, null), new string('x', 3000), "- a\n", null, "opencode/deepseek-v4-flash").Effort == "low", "DeepSeek Standard 应落到 low。");
        // Auto 推断层级：DeepSeek 默认 effort 在 Fast/Standard/Auto-small/Auto-medium 均为 low，仅 Strict 或 major 为 high。
        Assert(ReasonixExecutionPolicy.Resolve(null, new string('x', 1000), "- a\n", null, "opencode/deepseek-v4-flash").Effort == "low", "DeepSeek Auto+small 应落到 low。");
        Assert(ReasonixExecutionPolicy.Resolve(null, new string('x', 3000), "- a\n", null, "opencode/deepseek-v4-flash").Effort == "low", "DeepSeek Auto+medium 应落到 low。");
        Assert(ReasonixExecutionPolicy.Resolve(null, new string('x', 9500), string.Join("\n", Enumerable.Range(0, 13).Select(i => $"- item {i}")), null, "opencode/deepseek-v4-flash").Effort == "high", "DeepSeek Auto+major 应落到 high。");

        // 合同预检规范：manifest 显式 high/max + 普通 small/medium DeepSeek → 派生 effort 降为 low；
        // 高风险 spec / strict / major 保留 high（不修改用户合同原文件）。
        Assert(ReasonixExecutionPolicy.Resolve(new ReasonixManifestPolicy("small", null, "high", null, null, null, null, null, null), "普通小改动", "- a\n", null, "opencode/deepseek-v4-flash").Effort == "low", "普通 small DeepSeek 显式 high 应降为 low。");
        Assert(ReasonixExecutionPolicy.Resolve(new ReasonixManifestPolicy("medium", null, "max", null, null, null, null, null, null), "普通改动", "- a\n", null, "opencode/deepseek-v4-flash").Effort == "low", "普通 medium DeepSeek 显式 max 应降为 low。");
        Assert(ReasonixExecutionPolicy.Resolve(new ReasonixManifestPolicy("small", null, "high", null, null, null, null, null, null), "凭据加密与迁移任务", "- a\n", null, "opencode/deepseek-v4-flash").Effort == "high", "高风险 spec（凭据/加密/迁移）应保留 high。");
        Assert(ReasonixExecutionPolicy.Resolve(new ReasonixManifestPolicy("small", null, "high", "strict", null, null, null, null, null), "普通小改动", "- a\n", null, "opencode/deepseek-v4-flash").Effort == "high", "strict 应保留 high。");
        Assert(ReasonixExecutionPolicy.Resolve(new ReasonixManifestPolicy("major", null, "high", null, null, null, null, null, null), "普通改动", "- a\n", null, "opencode/deepseek-v4-flash").Effort == "high", "major 应保留 high。");

        // job host 层：config.toml 声明 DeepSeek 默认模型，Standard 命令不得含 --effort medium。
        await WithTempDirectoryAsync("reasonix-effort-host", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var reasonixHome = Path.Combine(root, "rh");
            Directory.CreateDirectory(reasonixHome);
            await File.WriteAllTextAsync(Path.Combine(reasonixHome, "config.toml"), "default_model = \"opencode/deepseek-v4-flash\"\n");
            var task = Path.Combine(root, "p", ".codex-helper", "runs", "run-deepseek");
            Directory.CreateDirectory(task);
            await File.WriteAllTextAsync(Path.Combine(task, "SPEC.md"), "spec");
            await File.WriteAllTextAsync(Path.Combine(task, "ACCEPTANCE.md"), "- a");
            await File.WriteAllTextAsync(Path.Combine(task, "HANDOFF.md"), "h");
            await File.WriteAllTextAsync(Path.Combine(task, "manifest.json"), """{"intensity":"standard"}""");
            var executable = Path.Combine(root, "reasonix-cli.ps1");
            await File.WriteAllTextAsync(executable, "[IO.File]::WriteAllText((Join-Path $env:CODEX_HELPER_TEST_TASK 'argv.txt'), ($args -join [Environment]::NewLine), [Text.UTF8Encoding]::new($false))");
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), Path.Combine(root, "p"), task, reasonixHome, "", "thread-deepseek", taskDir: task);
            var argv = await File.ReadAllTextAsync(Path.Combine(task, "argv.txt"));
            Assert(argv.Contains("--effort" + Environment.NewLine + "low", StringComparison.Ordinal) && !argv.Contains("--effort" + Environment.NewLine + "medium", StringComparison.Ordinal), "DeepSeek Standard 命令不得生成 medium：" + argv);
            var status = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-deepseek.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert(status.ExecutionEffort == "low", "DeepSeek Standard 状态 effort 应为 low：" + status.ExecutionEffort);
            service.Disable();
        });

        // 0 轮次启动失败：真实 stderr 必须写入任务状态而非只显示 exit code。
        await WithTempDirectoryAsync("reasonix-startfail", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var task = Path.Combine(root, "p", ".codex-helper", "runs", "run-startfail");
            Directory.CreateDirectory(task);
            await File.WriteAllTextAsync(Path.Combine(task, "SPEC.md"), "spec");
            await File.WriteAllTextAsync(Path.Combine(task, "ACCEPTANCE.md"), "- a");
            await File.WriteAllTextAsync(Path.Combine(task, "HANDOFF.md"), "h");
            await File.WriteAllTextAsync(Path.Combine(task, "manifest.json"), "{}");
            var executable = Path.Combine(root, "reasonix-cli.cmd");
            await File.WriteAllTextAsync(executable, "@echo off\r\necho unrecognized effort value for deepseek 1>&2\r\nexit /b 1\r\n");
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), Path.Combine(root, "p"), task, Path.Combine(root, "rh"), "", "thread-startfail", taskDir: task);
            var status = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-startfail.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert(status.State == "failed", "0 轮次启动失败应为 failed：" + status.State);
            Assert(status.Message.Contains("unrecognized effort value for deepseek", StringComparison.Ordinal), "启动失败必须写入真实 stderr 原因：" + status.Message);
            service.Disable();
        });
    }

    private static async Task TestReasonixPermissionArgsReachCliAsync()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        await WithTempDirectoryAsync("reasonix-perm", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var executable = Path.Combine(root, "reasonix-cli.ps1");
            await File.WriteAllTextAsync(executable, """
                if($args -contains '--version'){ '1.19.3'; exit 0 }
                if($args -contains 'doctor'){ '{"config":{"default_model":"opencode/deepseek-v4-flash"},"providers":[{"name":"opencode","key_present":true,"models":["deepseek-v4-flash"]}]}'; exit 0 }
                [IO.File]::WriteAllText((Join-Path $env:CODEX_HELPER_TEST_TASK 'argv.txt'), ($args -join [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
                """);
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);

            async Task<string> RunAndGetArgvAsync(string taskId, ReasonixPermissionMode mode)
            {
                service.SetPermissionMode(mode);
                var task = Path.Combine(root, "p" + taskId, ".codex-helper", "runs", taskId);
                Directory.CreateDirectory(task);
                foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                    await File.WriteAllTextAsync(Path.Combine(task, contract), contract == "manifest.json" ? "{}" : "test");
                var result = await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), Path.Combine(root, "p" + taskId), task, Path.Combine(root, "rh"), "", "thread-" + taskId, taskDir: task);
                Assert(result.ExitCode == 0, $"runner 应正常结束 {taskId}：" + result.Output);
                return await File.ReadAllTextAsync(Path.Combine(task, "argv.txt"));
            }

            var fullArgv = await RunAndGetArgvAsync("run-full", ReasonixPermissionMode.Full);
            Assert(fullArgv.Contains("--permission-mode" + Environment.NewLine + "auto", StringComparison.Ordinal) && !fullArgv.Contains("bypassPermissions", StringComparison.Ordinal), "Full 模式必须使用兼容的 auto 权限参数进入 CLI argv：" + fullArgv);

            var safeArgv = await RunAndGetArgvAsync("run-safe", ReasonixPermissionMode.Safe);
            Assert(safeArgv.Contains("--permission-mode" + Environment.NewLine + "acceptEdits", StringComparison.Ordinal) && safeArgv.Contains("Bash(dotnet build:*)", StringComparison.Ordinal), "Safe 模式权限参数必须真实进入 CLI argv：" + safeArgv);
            Assert(!safeArgv.Contains("bypassPermissions", StringComparison.Ordinal), "Safe 模式不得包含 bypassPermissions：" + safeArgv);
            service.Disable();
        });
    }

    private static async Task TestReasonixBudgetOverrunAsync()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        await WithTempDirectoryAsync("reasonix-budget-overrun", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var service = new ReasonixIntegrationService(codexRoot, app);

            async Task<ReasonixTaskStatus> RunCaseAsync(string id, int budget, int steps)
            {
                var executable = Path.Combine(root, "reasonix-cli-" + id + ".cmd");
                var sb = new StringBuilder("@echo off\r\n");
                for (var i = 0; i < steps; i++) sb.Append("echo {\"kind\":\"turn_started\"}\r\n");
                sb.Append($"if defined CODEX_HELPER_TEST_TASK ( echo {{\"steps\":{steps}}}>\"%CODEX_HELPER_TEST_TASK%\\metrics.json\" & echo done>\"%CODEX_HELPER_TEST_TASK%\\EXECUTION_REPORT.md\" )\r\nexit /b 0\r\n");
                await File.WriteAllTextAsync(executable, sb.ToString());
                service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
                var project = Path.Combine(root, "project-" + id);
                var task = Path.Combine(project, ".codex-helper", "runs", "run-" + id);
                Directory.CreateDirectory(task);
                await File.WriteAllTextAsync(Path.Combine(task, "SPEC.md"), "spec");
                await File.WriteAllTextAsync(Path.Combine(task, "ACCEPTANCE.md"), "- a");
                await File.WriteAllTextAsync(Path.Combine(task, "HANDOFF.md"), "h");
                await File.WriteAllTextAsync(Path.Combine(task, "manifest.json"), $$"""{"budgetSteps":{{budget}},"intensity":"fast"}""");
                var result = await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), project, task, Path.Combine(root, "rh"), "", "thread-" + id, taskDir: task);
                Assert(result.ExitCode == 0, "runner 应正常结束：" + result.Output);
                var status = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-" + id + ".json")), options)!;
                Assert(status.State == "completed", $"软预算不得终止任务：state={status.State}。");
                Assert(status.EstimatedSteps == budget, "manifest budgetSteps 应生效。");
                Assert(status.StepCount == steps, $"实际步骤应取自 metrics={steps}：{status.StepCount}。");
                return status;
            }

            // 口径1：budget12/steps29=exceeded/17（29 ≥ 1.5×12=18）。
            var over = await RunCaseAsync("over", 12, 29);
            Assert(over.BudgetState == "exceeded", $"预算达到 150% 应标记 exceeded：{over.BudgetState}。");
            Assert(over.BudgetOverrunSteps == 17, $"超支量应为 29-12=17：{over.BudgetOverrunSteps}。");
            // 口径2：budget80/steps98=warning/18（98 ∈ [80, 1.5×80=120)）。
            var warn = await RunCaseAsync("warn", 80, 98);
            Assert(warn.BudgetState == "warning", $"预算落在 1×~1.5× 之间应标记 warning：{warn.BudgetState}。");
            Assert(warn.BudgetOverrunSteps == 18, $"超支量应为 98-80=18：{warn.BudgetOverrunSteps}。");
            service.Disable();
        });
    }

    private static async Task TestReasonixFailureDiagnosisAsync()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        await WithTempDirectoryAsync("reasonix-failure", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var executable = Path.Combine(root, "reasonix-cli.cmd");
            // run_done ok=false、无 stderr、无报告。
            await File.WriteAllTextAsync(executable, "@echo off\r\necho {\"kind\":\"turn_started\"}\r\necho {\"kind\":\"run_done\",\"ok\":false}\r\nexit /b 0\r\n");
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            var project = Path.Combine(root, "project");
            var task = Path.Combine(project, ".codex-helper", "runs", "run-fail");
            Directory.CreateDirectory(task);
            await File.WriteAllTextAsync(Path.Combine(task, "SPEC.md"), "spec");
            await File.WriteAllTextAsync(Path.Combine(task, "ACCEPTANCE.md"), "- a");
            await File.WriteAllTextAsync(Path.Combine(task, "HANDOFF.md"), "h");
            await File.WriteAllTextAsync(Path.Combine(task, "manifest.json"), "{}");
            await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), project, task, Path.Combine(root, "rh"), "", "thread-fail", taskDir: task);
            var status = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-fail.json")), options)!;
            Assert(status.State == "failed", $"应识别为失败：state={status.State}。");
            Assert(status.FailureKind == "model-run-failed", $"run_done ok=false 应识别为 model-run-failed：{status.FailureKind}。");
            Assert(!string.IsNullOrWhiteSpace(status.FailureSummary), "应提供脱敏失败摘要。");
            var failureReport = Path.Combine(task, "FAILURE_REPORT.md");
            Assert(File.Exists(failureReport), "无交付报告失败时应自动生成 FAILURE_REPORT.md。");
            var reportText = await File.ReadAllTextAsync(failureReport);
            Assert(reportText.Contains("run-fail", StringComparison.Ordinal) && reportText.Contains("model-run-failed", StringComparison.Ordinal), "FAILURE_REPORT 应含任务与失败类型：" + reportText);
            Assert(!reportText.Contains("turn_started", StringComparison.Ordinal) && !reportText.Contains("thread-fail", StringComparison.Ordinal) && !reportText.Contains("reasonix-cli.cmd", StringComparison.Ordinal), "FAILURE_REPORT 不得含命令参数/正文：" + reportText);
            service.Disable();
        });
    }

    private static async Task TestReasonixSafeRetryAsync()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        await WithTempDirectoryAsync("reasonix-retry", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var executable = Path.Combine(root, "reasonix-cli.cmd");
            await File.WriteAllTextAsync(executable, "@echo off\r\nexit /b 0\r\n");
            var reasonixHome = Path.Combine(root, "rh");
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);

            var project = Path.Combine(root, "project");
            var taskDir = Path.Combine(project, ".codex-helper", "runs", "run-retry");
            Directory.CreateDirectory(taskDir);
            foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                await File.WriteAllTextAsync(Path.Combine(taskDir, contract), contract == "manifest.json" ? "{}" : "test");
            await File.WriteAllTextAsync(Path.Combine(taskDir, "events.jsonl"), "{\"kind\":\"turn_started\"}\n");
            var now = DateTime.UtcNow;
            var failed = new ReasonixTaskStatus("run-retry", project, taskDir, "failed", "failed", "Full", now, now, 0, 1, "no report",
                CodexThreadId: "thread-retry", FailureKind: "missing-report", AttemptNumber: 1, RemainingPercent: 42);
            await File.WriteAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-retry.json"), JsonSerializer.Serialize(failed, new JsonSerializerOptions { WriteIndented = true }));

            // 阻断：状态 running 不可重试。
            var running = failed with { State = "running", HostProcessId = 0 };
            Assert(service.RetryBlockReason(running) is not null, "运行中任务不得重试。");

            // 阻断：缺合同。
            var missingContract = failed with { TaskDirectory = Path.Combine(root, "other", "no-contract") };
            Directory.CreateDirectory(missingContract.TaskDirectory);
            Assert(service.RetryBlockReason(missingContract) is not null, "缺合同不得重试。");

            // 阻断：路径越界。
            Assert(service.RetryBlockReason(failed with { TaskDirectory = Path.Combine(root, "outside") }) is not null, "路径越界不得重试。");

            // 成功重试：归档 + AttemptNumber 递增 + RETRY_CONTEXT + 防双击（状态被置为 starting）。
            var original = Environment.GetEnvironmentVariable("CODEX_HELPER_REASONIX_HOME");
            Environment.SetEnvironmentVariable("CODEX_HELPER_REASONIX_HOME", reasonixHome);
            try
            {
                var result = await service.RetryTaskAsync(failed);
                Assert(result.Success, "重试应成功：" + result.Message);
                var status = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-retry.json")), options)!;
                Assert(status.AttemptNumber == 2, $"AttemptNumber 应递增为 2：{status.AttemptNumber}。");
                Assert(status.RemainingPercent is null, "新 attempt 应重新初始化剩余百分比（单调保护从新起点开始）。");
                Assert(string.Equals(status.State, "starting", StringComparison.OrdinalIgnoreCase), $"重试启动后应置为 starting（防并发）：{status.State}。");
                var attempts = Directory.GetDirectories(Path.Combine(taskDir, "attempts"));
                Assert(attempts.Length == 1 && Path.GetFileName(attempts[0]).StartsWith("attempt-1-", StringComparison.Ordinal), "应生成一个 attempt-1-* 归档目录（按被归档的旧尝试编号）。");
                Assert(File.Exists(Path.Combine(attempts[0], "events.jsonl")), "旧 events.jsonl 应归档。");
                Assert(File.Exists(Path.Combine(attempts[0], "status.json")), "应归档状态快照。");
                Assert(File.Exists(Path.Combine(taskDir, "RETRY_CONTEXT.md")), "应生成 RETRY_CONTEXT.md。");
                foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                    Assert(File.Exists(Path.Combine(taskDir, contract)), "合同文件应保持不变：" + contract);
                // 防双击：此刻状态为 starting，不得再次重试。
                var nowStarting = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-retry.json")), options)!;
                Assert(service.RetryBlockReason(nowStarting) is not null, "重试启动中不得并发再次重试。");
                // 等待后台 runner 完成，避免临时目录被占用。
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    var current = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-retry.json")), options)!;
                    if (!string.Equals(current.State, "starting", StringComparison.OrdinalIgnoreCase)) break;
                    await Task.Delay(500);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODEX_HELPER_REASONIX_HOME", original);
                service.Disable();

                // 启动失败回滚（runner 缺失）：不移动证据、不留 starting、不创建归档。
                var altDir = Path.Combine(root, ".codex-helper", "runs", "run-alt");
                Directory.CreateDirectory(altDir);
                await File.WriteAllTextAsync(Path.Combine(altDir, "events.jsonl"), "{}");
                foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                    await File.WriteAllTextAsync(Path.Combine(altDir, contract), contract == "manifest.json" ? "{}" : "test");
                var alt = failed with { TaskId = "run-alt", TaskDirectory = altDir };
                await File.WriteAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-alt.json"), JsonSerializer.Serialize(alt, new JsonSerializerOptions { WriteIndented = true }));
                var altResult = await service.RetryTaskAsync(alt);
                Assert(!altResult.Success, "runner 缺失时重试必须失败。");
                var altStatus = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-alt.json")), options)!;
                Assert(string.Equals(altStatus.State, "failed", StringComparison.OrdinalIgnoreCase), "runner 缺失失败重试不得留下 starting 假状态。");
                Assert(File.Exists(Path.Combine(altDir, "events.jsonl")), "runner 缺失失败重试不得移动运行产物。");
                Assert(!Directory.Exists(Path.Combine(altDir, "attempts")), "runner 缺失失败重试不得创建归档。");
            }
        });
    }

    private static async Task TestReasonixRetryRollbackAsync()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        await WithTempDirectoryAsync("reasonix-retry-rollback", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var executable = Path.Combine(root, "reasonix-cli.cmd");
            await File.WriteAllTextAsync(executable, "@echo off\r\nexit /b 0\r\n");
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);

            var project = Path.Combine(root, "project");
            var taskDir = Path.Combine(project, ".codex-helper", "runs", "run-rb");
            Directory.CreateDirectory(taskDir);
            foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                await File.WriteAllTextAsync(Path.Combine(taskDir, contract), contract == "manifest.json" ? "{}" : "test");
            // 本次根运行产物（会被归档再回滚）。
            await File.WriteAllTextAsync(Path.Combine(taskDir, "events.jsonl"), "{\"kind\":\"turn_started\"}\n");
            await File.WriteAllTextAsync(Path.Combine(taskDir, "metrics.json"), "{\"steps\":3}");

            // 预置一个旧历史归档（attempt-1-*），其内容与目录必须保持完全不变。
            var attemptsDir = Path.Combine(taskDir, "attempts");
            var oldAttempt = Path.Combine(attemptsDir, "attempt-1-20230101000000");
            Directory.CreateDirectory(oldAttempt);
            await File.WriteAllTextAsync(Path.Combine(oldAttempt, "events.jsonl"), "old-events\n");
            await File.WriteAllTextAsync(Path.Combine(oldAttempt, "status.json"), "{\"state\":\"failed\"}");
            await File.WriteAllTextAsync(Path.Combine(oldAttempt, "metrics.json"), "{\"steps\":1}");

            // 目录快照（相对路径|内容），用于证明回滚后旧归档完全不变。
            async Task<string> Snapshot(string dir)
            {
                var sb = new StringBuilder();
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                    sb.Append(Path.GetRelativePath(dir, file)).Append('|').Append(await File.ReadAllTextAsync(file)).Append('\n');
                return sb.ToString();
            }
            var oldSnapshot = await Snapshot(oldAttempt);

            var now = DateTime.UtcNow;
            var failed = new ReasonixTaskStatus("run-rb", project, taskDir, "failed", "failed", "Full", now, now, 0, 0, "no report",
                CodexThreadId: "thread-rb", FailureKind: "missing-report", AttemptNumber: 2);
            await File.WriteAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-rb.json"), JsonSerializer.Serialize(failed, new JsonSerializerOptions { WriteIndented = true }));

            // 已取消 token：归档创建、根产物移动、状态置 starting 后 Task.Run 抛 OCE，必须走与普通异常相同的回滚。
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var result = await service.RetryTaskAsync(failed, cts.Token);
            Assert(!result.Success, "取消场景重试必须返回失败。");
            Assert(result.Message.Contains("取消", StringComparison.Ordinal), "取消场景应提示取消：" + result.Message);

            // 原任务状态必须恢复，不得留下 starting 假状态。
            var status = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, "run-rb.json")), options)!;
            Assert(string.Equals(status.State, "failed", StringComparison.OrdinalIgnoreCase), $"取消回滚后状态应为原 failed：{status.State}。");
            Assert(status.AttemptNumber == 2, $"取消回滚后 AttemptNumber 应保持原值：{status.AttemptNumber}。");

            // 本次被移走的根运行产物必须恢复到任务根，内容原样。
            Assert(File.Exists(Path.Combine(taskDir, "events.jsonl")) && File.Exists(Path.Combine(taskDir, "metrics.json")), "取消回滚后运行产物应恢复到任务根。");
            Assert(await File.ReadAllTextAsync(Path.Combine(taskDir, "events.jsonl")) == "{\"kind\":\"turn_started\"}\n", "恢复的运行产物内容应保持原样。");

            // 旧历史归档必须完全保持不变。
            Assert(string.Equals(oldSnapshot, await Snapshot(oldAttempt), StringComparison.Ordinal), "失败回滚后旧 attempt 归档内容与目录不得改变。");

            // 本次归档被安全清理：保留 status.json 快照，已移回的运行产物不得残留，目录仍存在（可重复、安全）。
            var remaining = Directory.GetDirectories(attemptsDir, "attempt-2-*");
            Assert(remaining.Length == 1, "应仅保留一个本次 attempt-2-* 归档目录。");
            Assert(File.Exists(Path.Combine(remaining[0], "status.json")), "本次归档应保留 status.json 快照。");
            Assert(!File.Exists(Path.Combine(remaining[0], "events.jsonl")) && !File.Exists(Path.Combine(remaining[0], "metrics.json")), "本次归档不得残留已移回的运行产物。");

            service.Disable();
        });
    }

    private static async Task TestDeepSeekBackfillAsync()
    {
        await WithTempDirectoryAsync("deepseek-backfill", async root =>
        {
            var reasonixTasks = Path.Combine(root, "tasks");
            Directory.CreateDirectory(reasonixTasks);
            var reasonixHome = Path.Combine(root, "rxhome");
            var sessionsRoot = Path.Combine(reasonixHome, "projects", "sess", "sessions");
            Directory.CreateDirectory(sessionsRoot);
            var service = new DeepSeekCacheStatsService(Path.Combine(root, "codex"), reasonixTasks);
            var now = DateTime.UtcNow;
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            // task 证据路径必须位于 ProjectRoot 下 .codex-helper/runs 内。
            string RunsTask(string name) => Path.Combine(root, ".codex-helper", "runs", name);

            // 1) manifest executionModel=deepseek → 补写。
            var task1 = RunsTask("task-manifest");
            Directory.CreateDirectory(task1);
            await File.WriteAllTextAsync(Path.Combine(task1, "manifest.json"), """{"executionModel":"opencode/deepseek-v4-flash"}""");
            await File.WriteAllTextAsync(Path.Combine(reasonixTasks, "run-manifest.json"), JsonSerializer.Serialize(new ReasonixTaskStatus("run-manifest", root, task1, "completed", "review", "Full", now, now, 1, 1, "ok", TokenInput: 100, CacheHitTokens: 50)));

            // 2) REVIEW_PACKET 独立 "- Model:" 明确非 DeepSeek → 已确认但不补写。
            var task2 = RunsTask("task-packet-openai");
            Directory.CreateDirectory(task2);
            await File.WriteAllTextAsync(Path.Combine(task2, "REVIEW_PACKET.md"), "# GPT Review Packet\n- Model: opencode/gpt-4o\n");
            await File.WriteAllTextAsync(Path.Combine(reasonixTasks, "run-openai.json"), JsonSerializer.Serialize(new ReasonixTaskStatus("run-openai", root, task2, "completed", "review", "Full", now, now, 1, 1, "ok")));

            // 3) session meta model=deepseek（reasonix projects 内）→ 补写（session-meta）。
            var task3 = RunsTask("task-session");
            Directory.CreateDirectory(task3);
            var session3 = Path.Combine(sessionsRoot, "session-3.jsonl");
            await File.WriteAllTextAsync(session3 + ".jsonl.meta", """{"model":"opencode/deepseek-v4-pro"}""");
            await File.WriteAllTextAsync(Path.Combine(reasonixTasks, "run-session.json"), JsonSerializer.Serialize(new ReasonixTaskStatus("run-session", root, task3, "completed", "review", "Full", now, now, 1, 1, "ok", ReasonixSessionPath: session3)));

            // 4) 冲突：session 明确 deepseek + manifest 明确 gpt-4o → 跳过。
            var task4 = RunsTask("task-conflict");
            Directory.CreateDirectory(task4);
            await File.WriteAllTextAsync(Path.Combine(task4, "manifest.json"), """{"model":"opencode/gpt-4o"}""");
            var session4 = Path.Combine(sessionsRoot, "session-4.jsonl");
            await File.WriteAllTextAsync(session4 + ".jsonl.meta", """{"model":"opencode/deepseek-v4-flash"}""");
            await File.WriteAllTextAsync(Path.Combine(reasonixTasks, "run-conflict.json"), JsonSerializer.Serialize(new ReasonixTaskStatus("run-conflict", root, task4, "completed", "review", "Full", now, now, 1, 1, "ok", ReasonixSessionPath: session4)));

            // 5) 无任何可靠证据 → 无法确认，跳过。
            var task5 = RunsTask("task-none");
            Directory.CreateDirectory(task5);
            await File.WriteAllTextAsync(Path.Combine(task5, "REVIEW_PACKET.md"), "正文自由文本提到 DeepSeek，但无独立 Model 行，不作为证据。");
            await File.WriteAllTextAsync(Path.Combine(reasonixTasks, "run-none.json"), JsonSerializer.Serialize(new ReasonixTaskStatus("run-none", root, task5, "completed", "review", "Full", now, now, 1, 1, "ok")));

            // 6) 已是新格式 → 幂等跳过。
            var task6 = RunsTask("task-new");
            Directory.CreateDirectory(task6);
            await File.WriteAllTextAsync(Path.Combine(reasonixTasks, "run-new.json"), JsonSerializer.Serialize(new ReasonixTaskStatus("run-new", root, task6, "completed", "review", "Full", now, now, 1, 1, "ok", ExecutionModel: "opencode/deepseek-v4-flash")));

            // 7) task 目录越界：即使 manifest 有 deepseek 也不读取 → 无法确认。
            var outside = Path.Combine(root, "outside-task");
            Directory.CreateDirectory(outside);
            await File.WriteAllTextAsync(Path.Combine(outside, "manifest.json"), """{"executionModel":"opencode/deepseek-v4-flash"}""");
            await File.WriteAllTextAsync(Path.Combine(reasonixTasks, "run-outside.json"), JsonSerializer.Serialize(new ReasonixTaskStatus("run-outside", root, outside, "completed", "review", "Full", now, now, 1, 1, "ok")));

            // 8) REVIEW_PACKET 中 NotModel 行与正文中间 Model 均不得被锚定采信 → 无法确认。
            var task8 = RunsTask("task-notmodel");
            Directory.CreateDirectory(task8);
            await File.WriteAllTextAsync(Path.Combine(task8, "REVIEW_PACKET.md"), "# GPT Review Packet\nNotModel: opencode/deepseek-v4-flash\n正文提到 Model: opencode/deepseek-v4-pro 不作为证据。\n");
            await File.WriteAllTextAsync(Path.Combine(reasonixTasks, "run-notmodel.json"), JsonSerializer.Serialize(new ReasonixTaskStatus("run-notmodel", root, task8, "completed", "review", "Full", now, now, 1, 1, "ok")));

            // 9) 损坏文件 → 不计入批次失败。
            await File.WriteAllTextAsync(Path.Combine(reasonixTasks, "run-corrupt.json"), "{ 损坏 ");

            var original = Environment.GetEnvironmentVariable("CODEX_HELPER_REASONIX_HOME");
            Environment.SetEnvironmentVariable("CODEX_HELPER_REASONIX_HOME", reasonixHome);
            try
            {
                var result = service.BackfillReasonixExecutionModel();
                Assert(result.Scanned == 9, $"应扫描 9 个文件：{result.Scanned}。");
                Assert(result.Backfilled == 2, $"应补写 2 个（manifest + session-meta）：{result.Backfilled}。");
                Assert(result.AlreadyNewFormat == 1, $"已是新格式 1 个：{result.AlreadyNewFormat}。");
                Assert(result.NonDeepSeek == 1, $"明确非 DeepSeek 1 个：{result.NonDeepSeek}。");
                Assert(result.Unconfirmed == 4, $"冲突/越界/无法确认/NotModel 4 个：{result.Unconfirmed}。");
                Assert(result.CorruptOrUnreadable == 1, $"损坏 1 个：{result.CorruptOrUnreadable}。");

                var manifestStatus = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(reasonixTasks, "run-manifest.json")), options)!;
                Assert(manifestStatus.ExecutionModel is not null && manifestStatus.ExecutionModel.Contains("deepseek", StringComparison.OrdinalIgnoreCase), "manifest 证据应补写 ExecutionModel。");
                var sessionStatus = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(reasonixTasks, "run-session.json")), options)!;
                Assert(sessionStatus.ExecutionModel is not null, "session-meta 证据应补写 ExecutionModel。");
                var openaiStatus = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(reasonixTasks, "run-openai.json")), options)!;
                Assert(openaiStatus.ExecutionModel is null, "明确非 DeepSeek 不得补写。");
                var conflictStatus = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(reasonixTasks, "run-conflict.json")), options)!;
                Assert(conflictStatus.ExecutionModel is null, "证据冲突不得补写。");
                var noneStatus = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(reasonixTasks, "run-none.json")), options)!;
                Assert(noneStatus.ExecutionModel is null, "自由文本提到 DeepSeek 不得作为证据补写。");
                var outsideStatus = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(reasonixTasks, "run-outside.json")), options)!;
                Assert(outsideStatus.ExecutionModel is null, "task 目录越界不得读取 manifest 证据。");
                var notModelStatus = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(reasonixTasks, "run-notmodel.json")), options)!;
                Assert(notModelStatus.ExecutionModel is null, "NotModel/正文中间 Model 不得被锚定采信。");

                // 幂等：再次回填不新增补写。
                var second = service.BackfillReasonixExecutionModel();
                Assert(second.Backfilled == 0, $"幂等：再次回填不得补写：{second.Backfilled}。");
                Assert(second.AlreadyNewFormat == 3, $"再次回填时 3 个已是新格式：{second.AlreadyNewFormat}。");
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODEX_HELPER_REASONIX_HOME", original);
            }
        });
    }

    private static async Task TestDeepSeekCacheRangeAndCancelAsync()
    {
        await WithTempDirectoryAsync("deepseek-range-cancel", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            var sessions = Path.Combine(codexRoot, "sessions");
            Directory.CreateDirectory(sessions);
            for (var i = 0; i < 5; i++)
                await File.WriteAllTextAsync(Path.Combine(sessions, $"s{i}.jsonl"),
                    "{\"payload\":{\"model\":\"opencode/deepseek-v4-flash\"}}\n" +
                    "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":40}}}}\n");

            var service = new DeepSeekCacheStatsService(codexRoot, null);
            // 24h 范围：文件是刚写入的，应计入。
            var all = await service.ReadAsync(null);          // 全部
            var day = await service.ReadAsync(TimeSpan.FromHours(24));
            Assert(all.CodexRequests == 5 && day.CodexRequests == 5, $"范围内统计不正确：all={all.CodexRequests}, day={day.CodexRequests}。");
            Assert(day.Range == "最近 24 小时", $"范围文案不准确：{day.Range}。");
            Assert(all.Range == "全部", $"全部范围文案不准确：{all.Range}。");

            // 可取消：启动全量扫描后立即取消，应抛取消异常而非崩溃。
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var cancelled = false;
            try { await service.ReadAsync(null, cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled, "全量缓存扫描应可取消。");
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

    private static async Task CreateCredentialHelperPayloadAsync(string executablePath)
    {
        await File.WriteAllBytesAsync(executablePath, [0x4D, 0x5A, 0x01]);
        var directory = Path.GetDirectoryName(executablePath)!;
        foreach (var name in new[]
        {
            "CodexHelperCredentialHelper.dll", "CodexHelperCredentialHelper.deps.json", "CodexHelperCredentialHelper.runtimeconfig.json",
            "CodexHelper.Core.dll", "Konscious.Security.Cryptography.Argon2.dll", "Konscious.Security.Cryptography.Blake2.dll",
            "System.Security.Cryptography.ProtectedData.dll"
        })
            await File.WriteAllBytesAsync(Path.Combine(directory, name), [0x01]);
    }

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

    private static async Task AssertPowerShellParsesAsync(string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.Environment["CODEX_HELPER_SCRIPT_TO_PARSE"] = path;
        start.ArgumentList.Add("$errors=$null;$tokens=$null;[System.Management.Automation.Language.Parser]::ParseFile($env:CODEX_HELPER_SCRIPT_TO_PARSE,[ref]$tokens,[ref]$errors)|Out-Null;if($errors.Count){$errors|ForEach-Object{[Console]::WriteLine(($_.Extent.StartLineNumber.ToString()+':'+$_.Message))};exit 1}");
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert(process.ExitCode == 0, "Generated PowerShell is invalid: " + await stdout + await stderr);
    }

    private static async Task<(int ExitCode, string Output)> RunPowerShellAsync(string script, string project, string task, string reasonixHome, string sessionPath, string threadId, string? newSessionPath = null, string? taskDir = null, string? writeScriptPath = null)
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment["CODEX_HELPER_REASONIX_HOME"] = reasonixHome;
        start.Environment["CODEX_HELPER_TEST_SESSION_PATH"] = sessionPath;
        if (!string.IsNullOrWhiteSpace(newSessionPath)) start.Environment["CODEX_HELPER_TEST_NEW_SESSION"] = newSessionPath;
        if (!string.IsNullOrWhiteSpace(taskDir)) start.Environment["CODEX_HELPER_TEST_TASK"] = taskDir;
        if (!string.IsNullOrWhiteSpace(writeScriptPath)) start.Environment["CODEX_HELPER_TEST_WRITE_SCRIPT"] = writeScriptPath;
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-ProjectRoot", project, "-TaskDirectory", task, "-CodexThreadId", threadId })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
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

    // ---- Reasonix 并行调度模型（SPEC 3.4.0 合同 A） ----

    private static Task TestReasonixParallelSchedulerAsync()
    {
        // 并发槽位合法范围 1..3，默认 2，非法抛异常。
        Assert(new ReasonixParallelScheduler().MaxConcurrency == 2, "默认最大并发应为 2。");
        Assert(new ReasonixParallelScheduler(3).MaxConcurrency == 3, "最大并发 3 应合法。");
        AssertThrows<ArgumentOutOfRangeException>(() => _ = new ReasonixParallelScheduler(0));
        AssertThrows<ArgumentOutOfRangeException>(() => _ = new ReasonixParallelScheduler(4));

        // 并发 2：三个无依赖、不同文件的任务 → 前 2 个 ready，第 3 个 queued。
        var scheduler = new ReasonixParallelScheduler();
        var r1 = scheduler.Schedule([Sched("a", @"C:\proj", merge: Mergable()), Sched("b", @"C:\proj", merge: Mergable()), Sched("c", @"C:\proj", merge: Mergable())]);
        Assert(r1.Decisions[0].Status == ReasonixDecisionStatus.Ready
            && r1.Decisions[1].Status == ReasonixDecisionStatus.Ready
            && r1.Decisions[2].Status == ReasonixDecisionStatus.Queued,
            "并发 2 时前两个应 ready、第三个应 queued：" + Join(r1));
        Assert(r1.Snapshot.Running == 2 && r1.Snapshot.Queued == 1 && r1.Snapshot.Blocked == 0
            && r1.Snapshot.MaxConcurrency == 2, "并发 2 快照统计不准确：" + r1.Snapshot);

        // 依赖链：A 已完成 → B ready；B 未完成 → C waiting_dependency。
        var r2 = scheduler.Schedule([
            Sched("a", @"C:\proj", state: ReasonixTaskState.Completed),
            Sched("b", @"C:\proj", deps: ["a"], merge: Mergable()),
            Sched("c", @"C:\proj", deps: ["b"])
        ]);
        Assert(r2.Decisions[0].Status == ReasonixDecisionStatus.Completed
            && r2.Decisions[1].Status == ReasonixDecisionStatus.Ready
            && r2.Decisions[2].Status == ReasonixDecisionStatus.WaitingDependency,
            "依赖链决策不准确：" + Join(r2));
        Assert(r2.Decisions[2].Reason.Contains("b", StringComparison.Ordinal), "依赖未成功原因应指出具体依赖任务。");

        // 依赖未满足：依赖任务仍在运行 → waiting_dependency。
        var r3 = scheduler.Schedule([
            Sched("a", @"C:\proj", state: ReasonixTaskState.Running),
            Sched("b", @"C:\proj", deps: ["a"])
        ]);
        Assert(r3.Decisions[0].Status == ReasonixDecisionStatus.Running
            && r3.Decisions[1].Status == ReasonixDecisionStatus.WaitingDependency,
            "依赖运行中应为 waiting_dependency：" + Join(r3));

        // 同文件：同项目写相同文件 → 后者 waiting_conflict，且给出冲突任务 ID。
        var r4 = scheduler.Schedule([Sched("a", @"C:\proj", files: ["out.txt"], merge: Mergable()), Sched("b", @"C:\proj", files: ["out.txt"], merge: Mergable())]);
        Assert(r4.Decisions[0].Status == ReasonixDecisionStatus.Ready
            && r4.Decisions[1].Status == ReasonixDecisionStatus.WaitingConflict
            && r4.Decisions[1].ConflictTaskId == "a",
            "同文件应冲突并指出冲突任务：" + Join(r4));

        // 目录覆盖：写目录 src 的任务与写其子文件 src/foo.cs 的任务冲突。
        var r5 = scheduler.Schedule([Sched("a", @"C:\proj", files: ["src"], merge: Mergable()), Sched("b", @"C:\proj", files: [@"src\foo.cs"], merge: Mergable())]);
        Assert(r5.Decisions[0].Status == ReasonixDecisionStatus.Ready
            && r5.Decisions[1].Status == ReasonixDecisionStatus.WaitingConflict,
            "目录所有权应覆盖其子文件：" + Join(r5));

        // 大小写：Windows 下路径比较大小写不敏感，SRC\Foo.cs 与 src\foo.cs 冲突。
        var r6 = scheduler.Schedule([Sched("a", @"C:\proj", files: [@"SRC\Foo.cs"], merge: Mergable()), Sched("b", @"C:\proj", files: [@"src\foo.cs"], merge: Mergable())]);
        Assert(r6.Decisions[0].Status == ReasonixDecisionStatus.Ready
            && r6.Decisions[1].Status == ReasonixDecisionStatus.WaitingConflict,
            "大小写不同应视为同路径冲突：" + Join(r6));

        // 不同项目：不同 projectRoot 写同名文件 → 都 ready，互不冲突。
        var r7 = scheduler.Schedule([Sched("a", @"C:\proj1", files: ["same.txt"], merge: Mergable()), Sched("b", @"C:\proj2", files: ["same.txt"], merge: Mergable())]);
        Assert(r7.Decisions[0].Status == ReasonixDecisionStatus.Ready
            && r7.Decisions[1].Status == ReasonixDecisionStatus.Ready,
            "不同项目写同名文件不应冲突：" + Join(r7));

        // 失败依赖：A 失败，B 依赖 A → B waiting_dependency。
        var r8 = scheduler.Schedule([
            Sched("a", @"C:\proj", state: ReasonixTaskState.Failed),
            Sched("b", @"C:\proj", deps: ["a"])
        ]);
        Assert(r8.Decisions[0].Status == ReasonixDecisionStatus.Failed
            && r8.Decisions[1].Status == ReasonixDecisionStatus.WaitingDependency,
            "失败依赖应让下游 waiting_dependency：" + Join(r8));

        // 槽位占满：2 个 running 输入占满并发 2，第三个 queued。
        var s2 = new ReasonixParallelScheduler(2);
        var r9 = s2.Schedule([
            Sched("a", @"C:\proj", state: ReasonixTaskState.Running, merge: Mergable()),
            Sched("b", @"C:\proj", state: ReasonixTaskState.Running, merge: Mergable()),
            Sched("c", @"C:\proj", merge: Mergable())
        ]);
        Assert(r9.Decisions[0].Status == ReasonixDecisionStatus.Running
            && r9.Decisions[1].Status == ReasonixDecisionStatus.Running
            && r9.Decisions[2].Status == ReasonixDecisionStatus.Queued,
            "槽位占满时第三个应 queued：" + Join(r9));
        Assert(r9.Snapshot.Running == 2 && r9.Snapshot.Queued == 1, "槽位占满快照统计不准确：" + r9.Snapshot);

        // 通配符保守冲突：无法规范化的范围与任何范围都冲突。
        var r10 = scheduler.Schedule([Sched("a", @"C:\proj", files: ["a.txt"], merge: Mergable()), Sched("b", @"C:\proj", files: ["*.cs"], merge: Mergable())]);
        Assert(r10.Decisions[0].Status == ReasonixDecisionStatus.Ready
            && r10.Decisions[1].Status == ReasonixDecisionStatus.WaitingConflict,
            "通配符范围应保守视为冲突：" + Join(r10));

        // 快照统计：同时包含 waiting_dependency 与 waiting_conflict → blocked 计数正确。
        var r11 = scheduler.Schedule([
            Sched("a", @"C:\proj", files: ["f.txt"], merge: Mergable()),
            Sched("b", @"C:\proj", files: ["f.txt"], deps: ["missing"], merge: Mergable()),
            Sched("c", @"C:\proj", files: ["f.txt"], merge: Mergable())
        ]);
        Assert(r11.Snapshot.Running == 1 && r11.Snapshot.Blocked == 2 && r11.Snapshot.Queued == 0
            && r11.Snapshot.Completed == 0 && r11.Snapshot.Failed == 0,
            "blocked 快照统计不准确：" + r11.Snapshot);

        // SPEC 3.4.0 新增：候选组内未声明 / 需二次接线 / 接口未冻结 → 保守 WaitingMerge（串行）。
        var w1 = scheduler.Schedule([Sched("x1", @"C:\proj", merge: null), Sched("x2", @"C:\proj", merge: null)]);
        Assert(w1.Decisions.All(d => d.Status == ReasonixDecisionStatus.WaitingMerge),
            "未声明可机械合并应 WaitingMerge：" + Join(w1));

        var w2 = scheduler.Schedule([
            Sched("x3", @"C:\proj", merge: new ReasonixMergeStrategy(CanMergeMechanically: true, InterfaceFrozen: false)),
            Sched("x4", @"C:\proj", merge: new ReasonixMergeStrategy(CanMergeMechanically: true, InterfaceFrozen: false))
        ]);
        Assert(w2.Decisions.All(d => d.Status == ReasonixDecisionStatus.WaitingMerge),
            "接口未冻结应 WaitingMerge：" + Join(w2));

        var w3 = scheduler.Schedule([
            Sched("x5", @"C:\proj", merge: new ReasonixMergeStrategy(InterfaceFrozen: true, CanMergeMechanically: true, NeedsRewiring: true)),
            Sched("x6", @"C:\proj", merge: new ReasonixMergeStrategy(InterfaceFrozen: true, CanMergeMechanically: true, NeedsRewiring: true))
        ]);
        Assert(w3.Decisions.All(d => d.Status == ReasonixDecisionStatus.WaitingMerge),
            "需二次接线应 WaitingMerge：" + Join(w3));

        // 单一待执行任务（非并行拆分候选组）不受合并门槛阻断，保持可直接执行。
        var w4 = scheduler.Schedule([Sched("solo", @"C:\proj", merge: null)]);
        Assert(w4.Decisions[0].Status == ReasonixDecisionStatus.Ready,
            "单一待执行任务即使未声明合并策略也应可直接执行：" + Join(w4));

        return Task.CompletedTask;
    }

    private static ReasonixSchedulerTask Sched(
        string id,
        string root,
        IReadOnlyList<string>? files = null,
        IReadOnlyList<string>? deps = null,
        ReasonixTaskState state = ReasonixTaskState.Pending,
        ReasonixMergeStrategy? merge = null)
        => new(id, "任务-" + id, Path.Combine(root, ".codex-helper", "runs", "run-" + id), root,
            files ?? ["write-" + id + ".txt"], deps ?? [], state, merge);

    /// <summary>可机械合并策略：接口已冻结、声明可由 Git/Helper 机械合并。</summary>
    private static ReasonixMergeStrategy Mergable()
        => new(InterfaceFrozen: true, CanMergeMechanically: true);

    private static string Join(ReasonixScheduleResult result)
        => string.Join("|", result.Decisions.Select(d => $"{d.TaskId}:{d.Status}{(d.ConflictTaskId is null ? "" : ":" + d.ConflictTaskId)}"));

    private static ReasonixWorktreePreparationRequest Wt(
        string taskId, string projectRoot, string worktreeRoot, IReadOnlyList<string>? files = null, bool depends = false)
        => new(taskId, projectRoot, worktreeRoot, files ?? ["out.txt"], depends);

    private static Task TestReasonixWorktreePreparationAsync()
    {
        const string proj = @"C:\proj";
        const string root = @"C:\worktrees";

        // 非 Git → 转串行。
        var nonGit = new ReasonixWorktreePreparationService(new FakeProbe(isRepo: false));
        Assert(nonGit.Prepare(Wt("t1", proj, root), true).Status == ReasonixWorktreeStatus.SerialFallback, "非 Git 仓库应转串行。");

        // 无 HEAD → 转串行。
        var noHead = new ReasonixWorktreePreparationService(new FakeProbe(head: null));
        Assert(noHead.Prepare(Wt("t2", proj, root), true).Status == ReasonixWorktreeStatus.SerialFallback, "无 HEAD 应转串行。");

        // 干净仓库 → parallelReady，路径限定在配置根内、branch/ref 正确。
        var clean = new ReasonixWorktreePreparationService(new FakeProbe(head: "deadbeef"));
        var ok = clean.Prepare(Wt("task-a", proj, root, files: [@"src\foo.cs"]), true);
        Assert(ok.Status == ReasonixWorktreeStatus.ParallelReady, "干净仓库应 parallelReady：" + ok.Reason);
        Assert(ok.WorktreePath == @"C:\worktrees\reasonix\task-a", "worktree 路径应在配置根下：" + ok.WorktreePath);
        Assert(ok.Branch == "reasonix/task-a" && ok.Ref == "deadbeef", "branch/ref 不正确：" + ok.Branch + "/" + ok.Ref);

        // 重复目标唯一化：首个目录已存在 → 追加 -2。
        var unique = new ReasonixWorktreePreparationService(new FakeProbe(
            head: "h", exists: p => p == Path.GetFullPath(@"C:\worktrees\reasonix\task-b")));
        var u = unique.Prepare(Wt("task-b", proj, root), true);
        Assert(u.Status == ReasonixWorktreeStatus.ParallelReady && u.WorktreePath == @"C:\worktrees\reasonix\task-b-2",
            "目标已存在应唯一化到 -2：" + u.WorktreePath);

        // 重复目标全部存在 → blocked。
        var full = new ReasonixWorktreePreparationService(new FakeProbe(head: "h", exists: _ => true));
        var b = full.Prepare(Wt("task-c", proj, root), true);
        Assert(b.Status == ReasonixWorktreeStatus.Blocked && b.Reason.Contains("已存在", StringComparison.Ordinal),
            "全部重复目标应阻断：" + b.Reason);

        // 触及脏文件 → 必须串行。
        var dirtyHit = new ReasonixWorktreePreparationService(new FakeProbe(head: "h", dirty: [@"src\foo.cs"]));
        var dh = dirtyHit.Prepare(Wt("task-d", proj, root, files: ["src"]), true);
        Assert(dh.Status == ReasonixWorktreeStatus.SerialFallback && dh.Reason.Contains("必须串行", StringComparison.Ordinal),
            "触及脏文件必须串行：" + dh.Reason);

        // 不触及脏文件 → parallelReady。
        var dirtyMiss = new ReasonixWorktreePreparationService(new FakeProbe(head: "h", dirty: ["a.txt"]));
        var dm = dirtyMiss.Prepare(Wt("task-e", proj, root, files: [@"src\foo.cs"]), true);
        Assert(dm.Status == ReasonixWorktreeStatus.ParallelReady, "不触及脏文件应 parallelReady：" + dm.Reason);

        // 任务写范围覆盖未跟踪文件 → 转串行。
        var utDep = new ReasonixWorktreePreparationService(new FakeProbe(head: "h", untracked: [@"generated\x.txt"]));
        var ud = utDep.Prepare(Wt("task-f", proj, root, files: ["generated"]), true);
        Assert(ud.Status == ReasonixWorktreeStatus.SerialFallback && ud.Reason.Contains("未跟踪", StringComparison.Ordinal),
            "依赖未跟踪文件应转串行：" + ud.Reason);

        // 显式依赖未跟踪 → 转串行。
        var utFlag = new ReasonixWorktreePreparationService(new FakeProbe(head: "h"));
        var uf = utFlag.Prepare(Wt("task-g", proj, root, depends: true), true);
        Assert(uf.Status == ReasonixWorktreeStatus.SerialFallback, "显式依赖未跟踪应转串行：" + uf.Reason);

        // 未跟踪但任务不依赖 → parallelReady。
        var utMiss = new ReasonixWorktreePreparationService(new FakeProbe(head: "h", untracked: ["x.txt"]));
        var um = utMiss.Prepare(Wt("task-h", proj, root, files: [@"src\foo.cs"]), true);
        Assert(um.Status == ReasonixWorktreeStatus.ParallelReady, "不依赖未跟踪应 parallelReady：" + um.Reason);

        // 绝对写路径越界 → blocked。
        var escape = new ReasonixWorktreePreparationService(new FakeProbe(head: "h"));
        var es = escape.Prepare(new ReasonixWorktreePreparationRequest("task-i", proj, root, [@"C:\outside\out.txt"]), true);
        Assert(es.Status == ReasonixWorktreeStatus.Blocked && es.Reason.Contains("越界", StringComparison.Ordinal),
            "越界写路径应阻断：" + es.Reason);

        // 相对 .. 越界 → blocked。
        var relEscape = new ReasonixWorktreePreparationService(new FakeProbe(head: "h"));
        var re = relEscape.Prepare(new ReasonixWorktreePreparationRequest("task-j", proj, root, [Path.Combine("..", "out.txt")]), true);
        Assert(re.Status == ReasonixWorktreeStatus.Blocked, "相对 .. 越界应阻断：" + re.Reason);

        // 宽泛通配符 → blocked。
        var wild = new ReasonixWorktreePreparationService(new FakeProbe(head: "h"));
        var w = wild.Prepare(new ReasonixWorktreePreparationRequest("task-k", proj, root, ["*.cs"]), true);
        Assert(w.Status == ReasonixWorktreeStatus.Blocked && w.Reason.Contains("通配符", StringComparison.Ordinal),
            "宽泛通配符应阻断：" + w.Reason);

        // 自动 worktree 关闭 → 转串行。
        var off = new ReasonixWorktreePreparationService(new FakeProbe(head: "h"));
        var o = off.Prepare(Wt("task-l", proj, root), false);
        Assert(o.Status == ReasonixWorktreeStatus.SerialFallback, "自动 worktree 关闭应转串行：" + o.Reason);

        // 只读 cleanup plan：根内路径列出、根外路径不纳入。
        var planSvc = new ReasonixWorktreePreparationService(new FakeProbe(head: "h"));
        var plan = planSvc.GetCleanupPlan(new ReasonixWorktreePreparationRequest("task-m", proj, root, ["src"]), ok.WorktreePath);
        Assert(plan.Items.Count == 1 && plan.Items[0].WorktreePath == ok.WorktreePath, "cleanup 应列出准备目录。");
        var outPlan = planSvc.GetCleanupPlan(new ReasonixWorktreePreparationRequest("task-m", proj, root, ["src"]), @"C:\outside\evil");
        Assert(outPlan.Items.Count == 0, "根外路径不应纳入清理。");

        return Task.CompletedTask;
    }

    private static Task TestReasonixWorktreeSettingsAsync()
    {
        // 新设置字段默认开启，最大并发默认 2。
        var defaults = new AppSettings();
        Assert(defaults.AutoSplitEnabled && defaults.ParallelIndependentEnabled
            && defaults.MaxConcurrency == 2 && defaults.AutoWorktreeEnabled
            && defaults.ConvergeOnBudgetOverrunEnabled, "新设置字段应默认开启、最大并发默认 2。");

        // 旧配置反序列化：JSON 缺失新字段时回退默认值。
        var legacy = JsonStore.Deserialize<AppSettings>("""{"schemaVersion":1,"codexRoot":"C:\\x"}"""u8);
        Assert(legacy.AutoSplitEnabled && legacy.ParallelIndependentEnabled
            && legacy.MaxConcurrency == 2 && legacy.AutoWorktreeEnabled
            && legacy.ConvergeOnBudgetOverrunEnabled, "旧配置缺失新字段应回退默认值。");

        // 并发配置边界：SettingsService 保存时 clamp 到 1..3。
        var root = Path.Combine(Path.GetTempPath(), "codex-helper-worktree-settings");
        var svc = new SettingsService(new AppPaths(root));
        svc.Save(new AppSettings { MaxConcurrency = 0 });
        Assert(svc.Load().MaxConcurrency == 1, "并发低于下限应 clamp 到 1。");
        svc.Save(new AppSettings { MaxConcurrency = 9 });
        Assert(svc.Load().MaxConcurrency == 3, "并发高于上限应 clamp 到 3。");
        return Task.CompletedTask;
    }

    private static Task TestReasonixTaskRoutingAsync()
    {
        // 1) 微任务：2 文件 / 80 行 / 低风险 → GPT 直接实现。
        Assert(ReasonixTaskRouter.Decide(new() { EstimatedFileCount = 2, EstimatedChangedLines = 80, HighRisk = false }).Kind == ReasonixRoutingKind.GptDirect, "2 文件 80 行低风险应为 GPT 直接实现。");
        // 2) 3 文件 / 81 行 → Reasonix 单合同。
        Assert(ReasonixTaskRouter.Decide(new() { EstimatedFileCount = 3, EstimatedChangedLines = 81, HighRisk = false }).Kind == ReasonixRoutingKind.ReasonixSingle, "3 文件 81 行应为 Reasonix 单合同。");
        // 3) 高风险单文件 → Reasonix 单合同（文件数不达标但高风险）。
        Assert(ReasonixTaskRouter.Decide(new() { EstimatedFileCount = 1, EstimatedChangedLines = 20, HighRisk = true }).Kind == ReasonixRoutingKind.ReasonixSingle, "高风险单文件应为 Reasonix 单合同。");
        // 4) 用户指定 Reasonix → Reasonix 单合同（即使很小）。
        Assert(ReasonixTaskRouter.Decide(new() { EstimatedFileCount = 1, EstimatedChangedLines = 5, HighRisk = false, UserRequestedReasonix = true }).Kind == ReasonixRoutingKind.ReasonixSingle, "用户指定 Reasonix 应为 Reasonix 单合同。");
        // 5) 验收微修 → GPT 直接修，不启动新 Reasonix。
        Assert(ReasonixTaskRouter.Decide(new() { EstimatedFileCount = 2, EstimatedChangedLines = 50, HighRisk = false, AcceptanceMicroFix = true }).Kind == ReasonixRoutingKind.GptDirect, "验收微修应为 GPT 直接修复。");
        // 6) 可机械合并并行候选：多独立模块、接口冻结无需接线 → 并行候选。
        Assert(ReasonixTaskRouter.Decide(new() { EstimatedFileCount = 4, EstimatedChangedLines = 200, HighRisk = false, HasIndependentModules = true, RequiresWiring = false }).Kind == ReasonixRoutingKind.ReasonixParallelCandidate, "可机械合并的独立模块应为并行候选。");
        // 7) 需接线退回单合同：独立模块但需二次接线 → 不并行。
        Assert(ReasonixTaskRouter.Decide(new() { EstimatedFileCount = 4, EstimatedChangedLines = 200, HighRisk = false, HasIndependentModules = true, RequiresWiring = true }).Kind == ReasonixRoutingKind.ReasonixSingle, "需接线应退回 Reasonix 单合同。");
        // 额外：微任务但含跨模块接口 → 非 GPT 直接实现（Reasonix 单合同）。
        Assert(ReasonixTaskRouter.Decide(new() { EstimatedFileCount = 2, EstimatedChangedLines = 40, HighRisk = false, RequiresWiring = true }).Kind == ReasonixRoutingKind.ReasonixSingle, "微任务含跨模块接口应转为 Reasonix 单合同。");
        return Task.CompletedTask;
    }

    private static async Task TestReasonixAutoRecoveryAsync()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        await WithTempDirectoryAsync("reasonix-auto-recovery", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var executable = Path.Combine(root, "reasonix-cli.cmd");

            async Task<ReasonixTaskStatus> RunCaseAsync(string id, string cliBody, string? progressJson, bool expectRecovered, string expectedFailureKind)
            {
                await File.WriteAllTextAsync(executable, cliBody);
                var service = new ReasonixIntegrationService(codexRoot, app);
                service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
                var project = Path.Combine(root, "p" + id);
                var taskDir = Path.Combine(project, ".codex-helper", "runs", id);
                Directory.CreateDirectory(taskDir);
                foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                    await File.WriteAllTextAsync(Path.Combine(taskDir, contract), contract == "manifest.json" ? "{}" : "test");
                if (progressJson is not null)
                    await File.WriteAllTextAsync(Path.Combine(taskDir, "PROGRESS.json"), progressJson, new UTF8Encoding(false));
                var result = await RunPowerShellAsync(Path.Combine(codexRoot, "skills", "reasonix-executor", "invoke-reasonix.ps1"), project, taskDir, Path.Combine(root, "rh"), "", "thread-" + id, taskDir: taskDir);
                Assert(result.ExitCode == 0, $"case {id} runner 不应中断：" + result.Output);
                var raw = JsonSerializer.Deserialize<ReasonixTaskStatus>(await File.ReadAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, id + ".json")), options)!;
                Assert(string.Equals(raw.FailureKind, expectedFailureKind, StringComparison.OrdinalIgnoreCase), $"case {id} 失败类型应为 {expectedFailureKind}：" + raw.FailureKind);
                // Helper 刷新触发自动恢复（GetRecentTasks 统一入口，恢复幂等）。
                var snapshot = service.GetRecentTasks(50);
                var after = snapshot.Tasks.FirstOrDefault(t => t.TaskId == id);
                Assert(after is not null, $"case {id} 应出现在任务快照中。");
                if (expectRecovered)
                {
                    Assert(string.Equals(after!.State, "completed", StringComparison.OrdinalIgnoreCase) && after.Phase == "awaiting-gpt-review", $"case {id} 应自动恢复为等待验收完成态：" + after.State + "/" + after.Phase);
                    Assert(after.RemainingPercent == 0 && after.ProgressStage == "done", $"case {id} 自动恢复后剩余应为 0%、阶段 done。");
                    var report = await File.ReadAllTextAsync(Path.Combine(taskDir, "EXECUTION_REPORT.md"));
                    Assert(report.Contains("自动恢复", StringComparison.Ordinal) && report.Contains("未伪造", StringComparison.Ordinal), $"case {id} 自动恢复报告必须明确标注自动恢复且不伪造测试：" + report);
                    Assert(report.Contains("Reasonix 自报通过", StringComparison.Ordinal) && report.Contains("尚未经 GPT 复核", StringComparison.Ordinal), $"case {id} 自动恢复报告必须区分 worker 自报与 GPT 独立验收：" + report);
                    var packet = await File.ReadAllTextAsync(Path.Combine(taskDir, "REVIEW_PACKET.md"));
                    Assert(packet.Contains("自动恢复", StringComparison.Ordinal)
                        && packet.Contains("Worker-reported passed checks", StringComparison.Ordinal)
                        && packet.Contains("not yet verified by GPT", StringComparison.Ordinal),
                        $"case {id} 自动恢复 Review Packet 应标注自动恢复，并区分 worker 自报与 GPT 验收。");
                    Assert(File.Exists(Path.Combine(taskDir, "auto-recovery-evidence.json")), $"case {id} runner 应持久化自动恢复证据。");
                }
                else
                {
                    Assert(string.Equals(after!.State, "failed", StringComparison.OrdinalIgnoreCase), $"case {id} 不得误判自动恢复：" + after.State);
                    Assert(!File.Exists(Path.Combine(taskDir, "EXECUTION_REPORT.md")), $"case {id} 不得生成自动恢复报告。");
                }
                service.Disable();
                return after;
            }

            // 真阳性：有模型活动（turn_started）+ PROGRESS 记录已通过 workerCheck（build）+ exit 0 无报告。
            var positive = await RunCaseAsync("run-pos", "@echo off\r\necho {\"kind\":\"turn_started\"}\r\nexit /b 0\r\n",
                """{"stage":"testing","checks":[{"name":"build","status":"passed"},{"name":"test","status":"running"}]}""",
                expectRecovered: true, expectedFailureKind: "missing-report");
            Assert(positive.FailureKind == "missing-report", "自动恢复后应保留 FailureKind=missing-report 供审计。");
            Assert(positive.HasExecutionActivity, "自动恢复任务应保留实际活动证据。");

            // 无活动 + 无证据：不得恢复（真阴性）。
            await RunCaseAsync("run-inactive", "@echo off\r\nexit /b 0\r\n", null, expectRecovered: false, expectedFailureKind: "missing-report");

            // 有活动但无 diff 无 passedChecks：无法证明，保守不恢复。
            await RunCaseAsync("run-noevidence", "@echo off\r\necho {\"kind\":\"turn_started\"}\r\nexit /b 0\r\n", null, expectRecovered: false, expectedFailureKind: "missing-report");

            // 模型失败（run_done ok=false）：即使有 passedChecks 也不得恢复。
            await RunCaseAsync("run-modelfail", "@echo off\r\necho {\"kind\":\"turn_started\"}\r\necho {\"kind\":\"run_done\",\"ok\":false}\r\nexit /b 0\r\n",
                """{"stage":"testing","checks":[{"name":"build","status":"passed"}]}""", expectRecovered: false, expectedFailureKind: "model-run-failed");

            // 非零退出（cli-exit）：不得恢复。
            await RunCaseAsync("run-nonzero", "@echo off\r\necho {\"kind\":\"turn_started\"}\r\nexit /b 1\r\n",
                """{"stage":"testing","checks":[{"name":"build","status":"passed"}]}""", expectRecovered: false, expectedFailureKind: "cli-exit");
        });
    }

    private static async Task TestReasonixCircuitBreakerAsync()
    {
        await WithTempDirectoryAsync("reasonix-circuit-breaker", async root =>
        {
            var codexRoot = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexRoot);
            await File.WriteAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"), "keep\n");
            var app = new AppPaths(Path.Combine(root, "app"));
            var executable = Path.Combine(root, "reasonix-cli.cmd");
            await File.WriteAllTextAsync(executable, "@echo off\r\nexit /b 0\r\n");
            var service = new ReasonixIntegrationService(codexRoot, app);
            service.Enable(executable, "opencode/deepseek-v4-flash", ReasonixPermissionMode.Full);
            var now = DateTime.UtcNow;

            // 独立任务夹具：每个 (id) 有独立项目/任务目录，避免归档互相干扰。
            async Task<(string Project, string TaskDir, ReasonixTaskStatus Status)> MakeTaskAsync(string id, string failureKind, int attempt)
            {
                var project = Path.Combine(root, "project-" + id);
                var taskDir = Path.Combine(project, ".codex-helper", "runs", id);
                Directory.CreateDirectory(taskDir);
                foreach (var contract in new[] { "SPEC.md", "ACCEPTANCE.md", "HANDOFF.md", "manifest.json" })
                    await File.WriteAllTextAsync(Path.Combine(taskDir, contract), contract == "manifest.json" ? "{}" : "test");
                var status = new ReasonixTaskStatus(id, project, taskDir, "failed", "failed", "Full", now, now, 0, 1, "no report", FailureKind: failureKind, AttemptNumber: attempt);
                await File.WriteAllTextAsync(Path.Combine(app.ReasonixTasksDirectory, id + ".json"), JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
                return (project, taskDir, status);
            }

            // 单次 missing-report（无归档）：可重试（未到熔断阈值）。
            var (_, taskSingle, single) = await MakeTaskAsync("run-cb-single", "missing-report", 1);
            Assert(service.CountCircuitBreakerFailures(single) == 1, "单次 missing-report 计数应为 1。");
            Assert(service.RetryBlockReason(single) is null, "单次 missing-report 不应熔断，允许安全原地重试。");

            // 归档一次 missing-report：累计 2 次 → 熔断。
            var (_, taskTwice, twice) = await MakeTaskAsync("run-cb-twice", "missing-report", 2);
            var archiveDir = Path.Combine(taskTwice, "attempts", "attempt-1-20260101000000");
            Directory.CreateDirectory(archiveDir);
            ReasonixStatusJson.WriteStatus(Path.Combine(archiveDir, "status.json"), twice with { AttemptNumber = 1 });
            Assert(service.CountCircuitBreakerFailures(twice) == 2, "当前 + 归档一次应为 2 次。");
            var block = service.RetryBlockReason(twice);
            Assert(block is not null && block.Contains("熔断", StringComparison.Ordinal), "连续 2 次 missing-report 必须熔断：" + (block ?? "无"));

            // model-run-failed 同样计入熔断（当前 model-run-failed + 归档 model-run-failed = 2）。
            var (_, taskModel, modelFail) = await MakeTaskAsync("run-cb-model", "model-run-failed", 2);
            var modelArchive = Path.Combine(taskModel, "attempts", "attempt-1-20260101000000");
            Directory.CreateDirectory(modelArchive);
            ReasonixStatusJson.WriteStatus(Path.Combine(modelArchive, "status.json"), modelFail with { FailureKind = "model-run-failed", AttemptNumber = 1 });
            Assert(service.CountCircuitBreakerFailures(modelFail) == 2, "model-run-failed 应计入熔断计数。");
            Assert(service.RetryBlockReason(modelFail) is not null && service.RetryBlockReason(modelFail)!.Contains("熔断", StringComparison.Ordinal), "连续 model-run-failed 应熔断。");

            // 用户主动停止不计入：当前 cancelled/user-stopped + 归档 user-stopped = 0，绝不熔断。
            var (_, taskStop, stopped) = await MakeTaskAsync("run-cb-stop", "user-stopped", 2);
            var stopArchive = Path.Combine(taskStop, "attempts", "attempt-1-20260101000000");
            Directory.CreateDirectory(stopArchive);
            ReasonixStatusJson.WriteStatus(Path.Combine(stopArchive, "status.json"), stopped with { FailureKind = "user-stopped", AttemptNumber = 1 });
            var stoppedCurrent = stopped with { State = "cancelled", Phase = "已停止", FailureKind = "user-stopped" };
            Assert(service.CountCircuitBreakerFailures(stoppedCurrent) == 0, "用户主动停止不得计入熔断。");
            Assert(service.RetryBlockReason(stoppedCurrent) is null, "用户停止任务不得因熔断被额外阻断（本身可重试时）。");

            // 混合：归档 user-stopped + 当前 missing-report = 1，不熔断（停止不计入）。
            var (_, taskMixed, mixed) = await MakeTaskAsync("run-cb-mixed", "missing-report", 2);
            var mixedArchive = Path.Combine(taskMixed, "attempts", "attempt-1-20260101000000");
            Directory.CreateDirectory(mixedArchive);
            ReasonixStatusJson.WriteStatus(Path.Combine(mixedArchive, "status.json"), mixed with { FailureKind = "user-stopped", AttemptNumber = 1 });
            Assert(service.CountCircuitBreakerFailures(mixed) == 1, "user-stopped 归档 + 当前 missing-report 只计 1 次（停止不计入）。");

            // 自动恢复后的任务（completed/missing-report）不计当前；归档计数仍有效但已完成态不可重试。
            var (_, taskRecovered, recovered) = await MakeTaskAsync("run-cb-recovered", "missing-report", 1);
            var recoveredStatus = recovered with { State = "completed", Phase = "awaiting-gpt-review", FailureKind = "missing-report" };
            Assert(service.CountCircuitBreakerFailures(recoveredStatus) == 1, "自动恢复任务当前状态为 completed，只计归档。");
            Assert(service.RetryBlockReason(recoveredStatus) is not null, "自动恢复完成态不得重试。");
            service.Disable();
        });
    }

    private static Task TestReasonixStateNormalizationAsync()
    {
        var now = DateTime.UtcNow;
        var running = new ReasonixTaskStatus("run-n", @"C:\p", @"C:\p\.codex-helper\runs\run-n", "running", "executing", "Full", now, now, 42, 1, "w",
            EstimatedSteps: 16, StepCount: 3, RemainingPercent: 42, ProgressStage: "implementing");

        // 运行中保持 5~100 与阶段不变。
        var stillRunning = ReasonixStatusJson.NormalizeTerminalState(running);
        Assert(stillRunning.RemainingPercent == 42 && stillRunning.ProgressStage == "implementing", "运行中不得归一化剩余/阶段。");

        // 完成（含等待 GPT 验收）：剩余 0%、阶段 done。
        var completed = ReasonixStatusJson.NormalizeTerminalState(running with { State = "completed", Phase = "awaiting-gpt-review", RemainingPercent = 15, ProgressStage = "testing" });
        Assert(completed.RemainingPercent == 0 && completed.ProgressStage == "done", $"完成态应为 0%/done：{completed.RemainingPercent}/{completed.ProgressStage}");

        // 失败：剩余 0%，阶段保留失败发生阶段（不伪装 done，也不丢弃阶段信息）。
        var failed = ReasonixStatusJson.NormalizeTerminalState(running with { State = "failed", Phase = "failed", RemainingPercent = 8, ProgressStage = "implementing" });
        Assert(failed.RemainingPercent == 0 && failed.ProgressStage == "implementing", $"失败态剩余应归零、阶段应保留：{failed.RemainingPercent}/{failed.ProgressStage}");

        // 取消 / 中断：剩余归零，阶段保留。
        var cancelled = ReasonixStatusJson.NormalizeTerminalState(running with { State = "cancelled", Phase = "已停止", RemainingPercent = 30, ProgressStage = "testing" });
        Assert(cancelled.RemainingPercent == 0 && cancelled.ProgressStage == "testing", "取消态剩余应归零、阶段保留。");
        var interrupted = ReasonixStatusJson.NormalizeTerminalState(running with { State = "interrupted", Phase = "意外停止", RemainingPercent = 25, ProgressStage = "analyzing" });
        Assert(interrupted.RemainingPercent == 0 && interrupted.ProgressStage == "analyzing", "中断态剩余应归零、阶段保留。");

        // 等待 GPT 验收（completed + awaiting-gpt-review）：剩余 0%、阶段 done。
        var awaiting = ReasonixStatusJson.NormalizeTerminalState(running with { State = "completed", Phase = "awaiting-gpt-review", RemainingPercent = 12, ProgressStage = "reporting" });
        Assert(awaiting.RemainingPercent == 0 && awaiting.ProgressStage == "done", "等待 GPT 验收应为 0%/done。");

        // 无 RemainingPercent 记录：保持 null（不显示），不引入虚假 0 语义破坏。
        var noRemaining = ReasonixStatusJson.NormalizeTerminalState(running with { State = "failed", RemainingPercent = null, ProgressStage = null });
        Assert(noRemaining.RemainingPercent is null && noRemaining.ProgressStage is null, "无记录时保持 null。");
        return Task.CompletedTask;
    }

    private static Task TestReasonixBudgetHistoryAsync()
    {
        // 样本不足回退默认值。
        Assert(ReasonixBudgetHistory.Calibrate(35, null) == 35, "无样本应回退默认 35。");
        Assert(ReasonixBudgetHistory.Calibrate(35, [10, 20]) == 35, "样本 <3 应回退默认。");
        Assert(ReasonixBudgetHistory.Calibrate(16, []) == 16, "空样本应回退默认 16。");

        // 异常值截尾：排序去首尾后取平均。
        Assert(ReasonixBudgetHistory.Calibrate(35, [30, 32, 34, 9999]) == 33, "截尾平均应为 (32+34)/2=33。");
        Assert(ReasonixBudgetHistory.Calibrate(35, [1, 40, 42, 44, 1000]) == 42, "截尾平均应为 (40+42+44)/3=42。");

        // 合理上下限：不因极端样本失控。
        Assert(ReasonixBudgetHistory.Calibrate(16, [1000, 1000, 1000]) == 32, "上限应钳制到 min(200, default*2)=32。");
        Assert(ReasonixBudgetHistory.Calibrate(56, [1, 1, 1]) == 28, "下限应钳制到 max(8, default/2)=28。");
        Assert(ReasonixBudgetHistory.Calibrate(56, [10000, 10000, 10000]) == 112, "major 上限应钳制到 min(200, 112)=112。");

        // 持久化：Record/Load 往返、键隔离、保留最近上限、损坏文件安全重建。
        return WithTempDirectoryAsync("reasonix-budget-history", async root =>
        {
            var path = Path.Combine(root, "reasonix-budget-history.json");
            ReasonixBudgetHistory.Record(path, "c:/proj/a", "major", 60);
            ReasonixBudgetHistory.Record(path, "c:/proj/a", "major", 70);
            ReasonixBudgetHistory.Record(path, "c:/proj/b", "major", 90);
            var samplesA = ReasonixBudgetHistory.LoadSamples(path, "c:/proj/a", "major");
            Assert(samplesA.SequenceEqual(new[] { 60, 70 }), "Record/Load 应往返无损：" + string.Join(",", samplesA));
            var samplesB = ReasonixBudgetHistory.LoadSamples(path, "c:/proj/b", "major");
            Assert(samplesB.SequenceEqual(new[] { 90 }), "不同项目键应隔离。");
            Assert(ReasonixBudgetHistory.LoadSamples(path, "c:/proj/a", "small").Count == 0, "不同复杂度键应隔离。");
            // 保留最近 20 条。
            for (var i = 0; i < 25; i++) ReasonixBudgetHistory.Record(path, "c:/proj/a", "major", 100 + i);
            var trimmed = ReasonixBudgetHistory.LoadSamples(path, "c:/proj/a", "major");
            Assert(trimmed.Count == 20 && trimmed[^1] == 124, $"样本应保留最近 20 条：{trimmed.Count} 条，末位 {trimmed[^1]}。");
            // 损坏历史文件安全重建。
            await File.WriteAllTextAsync(path, "{broken");
            ReasonixBudgetHistory.Record(path, "c:/proj/a", "major", 50);
            Assert(ReasonixBudgetHistory.LoadSamples(path, "c:/proj/a", "major").SequenceEqual(new[] { 50 }), "损坏历史文件应安全重建。");
        });
    }

    private sealed class FakeProbe : IReasonixGitProbe
    {
        private readonly bool isRepo;
        private readonly string? head;
        private readonly IReadOnlyList<string> dirty;
        private readonly IReadOnlyList<string> untracked;
        private readonly Func<string, bool>? exists;

        public FakeProbe(
            bool isRepo = true,
            string? head = "head",
            IReadOnlyList<string>? dirty = null,
            IReadOnlyList<string>? untracked = null,
            Func<string, bool>? exists = null)
        {
            this.isRepo = isRepo;
            this.head = head;
            this.dirty = dirty ?? [];
            this.untracked = untracked ?? [];
            this.exists = exists;
        }

        public bool IsRepository(string projectRoot) => isRepo;
        public string? GetHead(string projectRoot) => head;
        public IReadOnlyList<string> GetDirtyFiles(string projectRoot) => dirty;
        public IReadOnlyList<string> GetUntrackedFiles(string projectRoot) => untracked;
        public bool DirectoryExists(string path) => exists?.Invoke(Path.GetFullPath(path)) ?? false;
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

    // ---- DeepSeek Harness 双执行器：设置迁移 / Node 版本规则 / 能力降级 / 路由 ----

    private static async Task TestCollaborationModeMigrationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-helper-collab-mode-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 旧设置无 CollaborationMode 且 Reasonix 已启用 → 迁移为 Reasonix。
            var app = new AppPaths(Path.Combine(root, "app-a"));
            Directory.CreateDirectory(app.BaseDirectory);
            await File.WriteAllTextAsync(Path.Combine(app.BaseDirectory, "reasonix-integration.json"), """{"Enabled":true}""");
            // 旧设置 JSON 不含 CollaborationMode 字段（模拟旧版升级前）。
            await File.WriteAllTextAsync(app.SettingsPath, System.Text.Json.JsonSerializer.Serialize(new { SchemaVersion = 1, CodexRoot = Path.Combine(root, "codex-a") }));
            var settings = new SettingsService(app).Load();
            Assert(settings.CollaborationMode == "Reasonix", "旧版已启用 Reasonix 应迁移为 Reasonix，实际：" + settings.CollaborationMode);
            var saved = JsonStore.Deserialize<AppSettings>(await File.ReadAllBytesAsync(app.SettingsPath));
            Assert(saved is not null && saved.CollaborationMode == "Reasonix", "迁移应写回 settings.json，实际：" + (saved?.CollaborationMode));

            // 旧设置未启用 Reasonix → 迁移为 Off。
            var app2 = new AppPaths(Path.Combine(root, "app-b"));
            Directory.CreateDirectory(app2.BaseDirectory);
            await File.WriteAllTextAsync(app2.SettingsPath, System.Text.Json.JsonSerializer.Serialize(new { SchemaVersion = 1, CodexRoot = Path.Combine(root, "codex-b") }));
            var settings2 = new SettingsService(app2).Load();
            Assert(settings2.CollaborationMode == "Off", "未启用 Reasonix 应迁移为 Off，实际：" + settings2.CollaborationMode);

            // 已在文件中的显式模式不迁移覆盖。
            var app3 = new AppPaths(Path.Combine(root, "app-c"));
            Directory.CreateDirectory(app3.BaseDirectory);
            await File.WriteAllTextAsync(app3.SettingsPath, System.Text.Json.JsonSerializer.Serialize(new { SchemaVersion = 1, CollaborationMode = "Harness", CodexRoot = Path.Combine(root, "codex-c") }));
            var settings3 = new SettingsService(app3).Load();
            Assert(settings3.CollaborationMode == "Harness", "已显式选择 Harness 不应被迁移覆盖，实际：" + settings3.CollaborationMode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static Task TestHarnessVersionRuleAsync()
    {
        Assert(DeepSeekHarnessProbe.IsNodeVersionSupported("v22.19.0"), "22.19.0 应受支持");
        Assert(DeepSeekHarnessProbe.IsNodeVersionSupported("v22.19.5"), "22.19.x 应受支持");
        Assert(DeepSeekHarnessProbe.IsNodeVersionSupported("v24.0.0"), "24.0.0 应受支持");
        Assert(DeepSeekHarnessProbe.IsNodeVersionSupported("v24.19.0"), "24.19.0 应受支持");
        Assert(DeepSeekHarnessProbe.IsNodeVersionSupported("v25.0.0"), "24 以上的未来主版本应受支持");
        Assert(!DeepSeekHarnessProbe.IsNodeVersionSupported("v22.18.0"), "22.18.0 不应受支持");
        Assert(!DeepSeekHarnessProbe.IsNodeVersionSupported("v23.0.0"), "23.0.0 不在白名单");
        Assert(!DeepSeekHarnessProbe.IsNodeVersionSupported("v20.0.0"), "20.x 不支持");
        Assert(!DeepSeekHarnessProbe.IsNodeVersionSupported("garbage"), "非法版本不支持");
        Assert(DeepSeekHarnessVersions.FixedVersion == "0.1.0-rc.5", "固定 Harness 版本应为 0.1.0-rc.5，实际：" + DeepSeekHarnessVersions.FixedVersion);
        Assert(!DeepSeekHarnessVersions.FixedVersion.Contains("latest", StringComparison.OrdinalIgnoreCase), "禁止静默 latest");
        Assert(DeepSeekHarnessProbe.IsDshVersionSupported("0.1.0-rc.5"), "rc.5 应受支持");
        Assert(DeepSeekHarnessProbe.IsDshVersionSupported("0.1.0-rc.6"), "已安装 rc.6 应识别为可探测，不得判为缺失");
        Assert(DeepSeekHarnessProbe.IsDshVersionSupported(" 0.1.0-rc.6 "), "带空白 rc.6 应可探测");
        Assert(DeepSeekHarnessProbe.IsDshVersionSupported("0.1.0-rc.7"), "未来 rc.7 可探测，不因版本被拒绝");
        Assert(DeepSeekHarnessProbe.IsDshVersionSupported("0.1.0"), "0.1.0 正式版可探测");
        Assert(DeepSeekHarnessProbe.IsDshVersionSupported("0.2.0"), "0.2.0 跨 minor 可探测");
        Assert(DeepSeekHarnessProbe.IsDshVersionSupported("1.0.0"), "1.0.0 跨主版本可探测");
        Assert(!DeepSeekHarnessProbe.IsDshVersionSupported("latest"), "禁止静默 latest");
        Assert(!DeepSeekHarnessProbe.IsDshVersionSupported(""), "空版本不可探测");
        Assert(!DeepSeekHarnessProbe.IsDshVersionSupported("garbage"), "损坏版本不可探测");

        // SemVer 解析（含 prerelease/build metadata）。
        Assert(DeepSeekHarnessSemVer.Parse("0.1.0-rc.5") is not null, "rc.5 应可解析");
        var rc5 = DeepSeekHarnessSemVer.Parse("0.1.0-rc.5")!;
        Assert(rc5.Major == 0 && rc5.Minor == 1 && rc5.Patch == 0 && rc5.PreRelease == "rc.5", "prerelease 解析错误：" + rc5);
        Assert(DeepSeekHarnessSemVer.Parse("0.1.0-rc.5+build.7")!.Build == "build.7", "build metadata 解析错误");
        Assert(DeepSeekHarnessSemVer.Parse("0.1.0")!.PreRelease == "" && DeepSeekHarnessSemVer.Parse("0.1.0")!.Build == "", "无 prerelease/build 应为空");
        Assert(DeepSeekHarnessSemVer.Parse("v0.2.0")!.Minor == 2, "可选 v 前缀应解析");
        Assert(DeepSeekHarnessSemVer.Parse("latest") is null, "latest 不得作为实际版本");
        Assert(DeepSeekHarnessSemVer.Parse("garbage") is null, "非法版本不得解析");
        Assert(DeepSeekHarnessSemVer.Parse("") is null, "空版本不得解析");

        // 风险分级：已知基线 / 同系列新版本 / 跨 minor / 跨主版本 / 非法损坏。
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("0.1.0-rc.5") == HarnessVersionRiskLevel.KnownBaseline, "基线应识别为已知基线");
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("0.1.0-rc.6") == HarnessVersionRiskLevel.SameSeries, "rc.6 应识别为同系列新版本");
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("0.1.0-rc.7") == HarnessVersionRiskLevel.SameSeries, "rc.7 应识别为同系列新版本");
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("0.1.0") == HarnessVersionRiskLevel.SameSeries, "0.1.0 应为同系列新版本");
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("0.2.0") == HarnessVersionRiskLevel.NewMinor, "0.2.0 应为跨 minor 未验证");
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("1.0.0") == HarnessVersionRiskLevel.CrossMajor, "1.0.0 应为跨主版本");
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("latest") == HarnessVersionRiskLevel.Invalid, "latest 应视为非法");
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("garbage") == HarnessVersionRiskLevel.Invalid, "损坏应视为非法");
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("") == HarnessVersionRiskLevel.Invalid, "空版本应视为非法");
        return Task.CompletedTask;
    }

    private static async Task TestHarnessCapabilityDiagnoseAsync()
    {
        // 默认中继探测未确认 → 诚实降级，不得声称可实时协作。
        var relay = new DeepSeekHarnessRelayProbe();
        var cap = await relay.ProbeCapabilitiesAsync();
        Assert(!cap.Confirmed && !cap.CanLiveCollaborate, "预览协议未确认时应不可实时协作");

        // 注入确认中继 → 三项能力齐全时可实时协作。
        var confirmed = new ConfirmedHarnessRelay();
        var cap2 = await confirmed.ProbeCapabilitiesAsync();
        Assert(cap2.Confirmed && cap2.CanLiveCollaborate, "三项能力确认后可实时协作");

        // 无 Node 场景：诊断不崩溃、不误报成功、提供官方下载入口。
        var root = Path.Combine(Path.GetTempPath(), "codex-helper-harness-diag-" + Guid.NewGuid().ToString("N"));
        try
        {
            var app = new AppPaths(Path.Combine(root, "app"));
            var discovery = new DeepSeekHarnessDiscovery { FileExists = _ => false, PathDirectoryReader = () => Array.Empty<string>() };
            var service = new DeepSeekHarnessService(app)
            {
                DiscoveryFactory = () => discovery,
                WebHostPortProbe = (_, _, _) => Task.FromResult(false),
                RelayFactory = () => relay
            };
            var status = await service.DiagnoseAsync();
            Assert(!status.NodeFound && !status.EnableAllowed, "无 Node 时不得允许开启 Harness");
            Assert(status.DownloadUrl == DeepSeekHarnessVersions.NodeDownloadUrl, "应提供官方下载入口");
            Assert(status.NodeMessage.Contains("nodejs.org", StringComparison.OrdinalIgnoreCase), "缺 Node 诊断应解释缺少什么并提供下载入口");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task TestHarnessDiscoveryOldPathAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-helper-harness-oldpath-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 模拟本机典型场景：当前进程 PATH 无 node/dsh，但常见目录中二者完整。
            var programFiles = Path.Combine(root, "Program Files");
            var appData = Path.Combine(root, "AppData", "Roaming");
            var localAppData = Path.Combine(root, "AppData", "Local");
            var nodeDir = Path.Combine(programFiles, "nodejs");
            var nodePath = Path.Combine(nodeDir, "node.exe");
            var dshPkg = Path.Combine(appData, "npm", "node_modules", "@deepseek-ai", "dsh");
            var dshEntry = Path.Combine(dshPkg, "lib", "bin.js");
            Directory.CreateDirectory(nodeDir);
            Directory.CreateDirectory(Path.Combine(dshPkg, "lib"));
            await File.WriteAllTextAsync(nodePath, "fake node");
            await File.WriteAllTextAsync(dshEntry, "fake bin.js");
            await File.WriteAllTextAsync(Path.Combine(dshPkg, "package.json"), "{\"name\":\"@deepseek-ai/dsh\",\"version\":\"0.1.0-rc.6\"}");

            var discovery = new DeepSeekHarnessDiscovery
            {
                SpecialFolder = folder => folder switch
                {
                    Environment.SpecialFolder.ProgramFiles => programFiles,
                    Environment.SpecialFolder.ProgramFilesX86 => programFiles,
                    Environment.SpecialFolder.ApplicationData => appData,
                    Environment.SpecialFolder.LocalApplicationData => localAppData,
                    _ => root
                },
                // 旧 PATH：不包含 node 或 dsh 的任何目录。
                PathDirectoryReader = () => Array.Empty<string>()
            };

            // 直接发现：node 与 dsh 入口都应从常见目录找到。
            var nodes = discovery.Discover(null);
            Assert(nodes.Count == 1, "常见目录应发现唯一 node，实际：" + nodes.Count);
            Assert(string.Equals(nodes[0].Path, nodePath, StringComparison.OrdinalIgnoreCase), "应发现绝对 node.exe：" + nodes[0].Path);
            Assert(nodes[0].Source == HarnessNodeSource.CommonLocation, "node 来源应为常见位置");

            var dsh = discovery.DiscoverDsh(null);
            Assert(dsh.Count == 1, "常见全局 npm 根应发现唯一 dsh 入口，实际：" + dsh.Count);
            Assert(string.Equals(dsh[0].EntryPath, dshEntry, StringComparison.OrdinalIgnoreCase), "应发现绝对 dsh 入口：" + dsh[0].EntryPath);
            Assert(string.Equals(dsh[0].PackageRoot, dshPkg, StringComparison.OrdinalIgnoreCase), "包根应正确解析");
            Assert(dsh[0].Source == HarnessDshSource.CommonLocation, "dsh 来源应为常见全局 npm 根");

            // 服务级诊断：Node 与 dsh 都应识别，rc.6 视为受支持，允许开启。
            var app = new AppPaths(Path.Combine(root, "app"));
            var service = new DeepSeekHarnessService(app)
            {
                DiscoveryFactory = () => discovery,
                NodeVersionReader = _ => "v24.19.0",
                WebProfileReader = (_, _) => "Usage: dsh <command> ...\nCommands:\n  web  Start Harness web UI",
                WebHostPortProbe = (_, _, _) => Task.FromResult(false),
                RelayFactory = () => new DeepSeekHarnessRelayProbe()
            };
            var status = await service.DiagnoseAsync();
            Assert(status.NodeFound && status.NodeVersionSupported, "旧 PATH 下应发现并支持 Node");
            Assert(status.DshFound && status.DshVersion == "0.1.0-rc.6", "应识别已安装 rc.6，不得判为缺失，实际：" + status.DshVersion);
            Assert(status.DshVersionSupported, "rc.6 应受支持");
            Assert(string.Equals(status.DshEntryPath, dshEntry, StringComparison.OrdinalIgnoreCase), "诊断应返回绝对 dsh 入口");
            Assert(string.Equals(status.NodePath, nodePath, StringComparison.OrdinalIgnoreCase), "诊断应返回绝对 node");
            Assert(status.EnableAllowed, "Node 与 dsh 均就绪时应允许开启");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task TestHarnessRoutingAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-helper-collab-route-" + Guid.NewGuid().ToString("N"));
        try
        {
            var codexRoot = Path.Combine(root, "codex");
            var app = new AppPaths(Path.Combine(root, "app"));
            Directory.CreateDirectory(codexRoot);
            Directory.CreateDirectory(app.BaseDirectory);

            var collab = new CollaborationService(codexRoot, app);
            var settings = new AppSettings { CodexRoot = codexRoot, CollaborationMode = "Harness" };

            // Harness → 写全局指导与 executor skill。
            collab.Synchronize(settings);
            var guidance = await File.ReadAllTextAsync(Path.Combine(codexRoot, "AGENTS.md"));
            Assert(guidance.Contains(CollaborationService.HarnessGuidanceStart, StringComparison.Ordinal), "Harness 模式应写全局指导");
            Assert(File.Exists(Path.Combine(codexRoot, "skills", "harness-executor", "SKILL.md")), "Harness 模式应写 executor skill");
            Assert(guidance.Contains("known compatibility baseline", StringComparison.Ordinal), "指导应把 rc.5 作为兼容基线而非硬固定版本");
            Assert(guidance.Contains("newer valid semantic versions", StringComparison.Ordinal), "指导应允许通过能力探测的新版本");

            // 切到 Off → 移除 Harness 规则。
            settings.CollaborationMode = "Off";
            collab.Synchronize(settings);
            Assert(!File.Exists(Path.Combine(codexRoot, "skills", "harness-executor", "SKILL.md")), "Off 应移除 Harness skill");
            // Off 后 AGENTS.md 可能被删除（无其他规则时删除空文件），读取前先判存在。
            var agentsAfterOff = Path.Combine(codexRoot, "AGENTS.md");
            var afterOff = File.Exists(agentsAfterOff) ? await File.ReadAllTextAsync(agentsAfterOff) : string.Empty;
            Assert(!afterOff.Contains(CollaborationService.HarnessGuidanceStart, StringComparison.Ordinal), "Off 应移除 Harness 指导");

            // 切到 Reasonix → 移除 Harness 规则，不写 Harness。
            settings.CollaborationMode = "Reasonix";
            collab.Synchronize(settings);
            Assert(!File.Exists(Path.Combine(codexRoot, "skills", "harness-executor", "SKILL.md")), "Reasonix 模式不应残留 Harness skill");

            // 再切回 Harness → 再次写入（互斥路由）。
            settings.CollaborationMode = "Harness";
            collab.Synchronize(settings);
            Assert(File.Exists(Path.Combine(codexRoot, "skills", "harness-executor", "SKILL.md")), "切回 Harness 应写规则");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static Task TestHarnessSemVerRiskAsync()
    {
        Assert(DeepSeekHarnessSemVer.Parse("0.1.0-rc.5") is not null, "rc.5 应可解析");
        var rc5 = DeepSeekHarnessSemVer.Parse("0.1.0-rc.5")!;
        Assert(rc5.Major == 0 && rc5.Minor == 1 && rc5.Patch == 0 && rc5.PreRelease == "rc.5", "prerelease 解析错误：" + rc5);
        Assert(DeepSeekHarnessSemVer.Parse("0.1.0-rc.5+build.7")!.Build == "build.7", "build metadata 解析错误");
        Assert(DeepSeekHarnessSemVer.Parse("0.1.0")!.PreRelease == "" && DeepSeekHarnessSemVer.Parse("0.1.0")!.Build == "", "无 prerelease/build 应为空");
        Assert(DeepSeekHarnessSemVer.Parse("v0.2.0")!.Minor == 2, "可选 v 前缀应解析");
        Assert(DeepSeekHarnessSemVer.Parse("latest") is null, "latest 不得作为实际版本");
        Assert(DeepSeekHarnessSemVer.Parse("garbage") is null, "非法版本不得解析");
        Assert(DeepSeekHarnessSemVer.Parse("") is null, "空版本不得解析");

        Assert(DeepSeekHarnessSemVer.EvaluateRisk("0.1.0-rc.5") == HarnessVersionRiskLevel.KnownBaseline, "基线应识别为已知基线");
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("0.1.0-rc.6") == HarnessVersionRiskLevel.SameSeries, "rc.6 应为同系列新版本");
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("0.1.0-rc.7") == HarnessVersionRiskLevel.SameSeries, "rc.7 应为同系列新版本");
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("0.1.0") == HarnessVersionRiskLevel.SameSeries, "0.1.0 应为同系列新版本");
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("0.2.0") == HarnessVersionRiskLevel.NewMinor, "0.2.0 应为跨 minor 未验证");
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("1.0.0") == HarnessVersionRiskLevel.CrossMajor, "1.0.0 应为跨主版本");
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("latest") == HarnessVersionRiskLevel.Invalid, "latest 应视为非法");
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("garbage") == HarnessVersionRiskLevel.Invalid, "损坏应视为非法");
        Assert(DeepSeekHarnessSemVer.EvaluateRisk("") == HarnessVersionRiskLevel.Invalid, "空版本应视为非法");
        return Task.CompletedTask;
    }

    private static async Task TestHarnessCapabilityGateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-helper-harness-gate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var app = new AppPaths(Path.Combine(root, "app"));
            var programFiles = Path.Combine(root, "Program Files");
            var appData = Path.Combine(root, "AppData", "Roaming");
            var localAppData = Path.Combine(root, "AppData", "Local");
            var nodePath = Path.Combine(programFiles, "nodejs", "node.exe");
            var dshEntry = Path.Combine(appData, "npm", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            var dshPkg = Path.GetDirectoryName(Path.GetDirectoryName(dshEntry))!;
            Directory.CreateDirectory(Path.GetDirectoryName(nodePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(dshEntry)!);
            await File.WriteAllTextAsync(nodePath, "fake node");
            await File.WriteAllTextAsync(dshEntry, "fake bin.js");
            await File.WriteAllTextAsync(Path.Combine(dshPkg, "package.json"), "{\"version\":\"0.1.0-rc.7\"}");

            var discovery = new DeepSeekHarnessDiscovery
            {
                SpecialFolder = folder => folder switch
                {
                    Environment.SpecialFolder.ProgramFiles => programFiles,
                    Environment.SpecialFolder.ProgramFilesX86 => programFiles,
                    Environment.SpecialFolder.ApplicationData => appData,
                    Environment.SpecialFolder.LocalApplicationData => localAppData,
                    _ => root
                },
                PathDirectoryReader = () => Array.Empty<string>()
            };

            // 新版本 rc.7 + 入口完整 + 中继未确认 → 可打开 Web 但自动中继未确认，版本不拒绝启用。
            var service = new DeepSeekHarnessService(app)
            {
                DiscoveryFactory = () => discovery,
                NodeVersionReader = _ => "v24.19.0",
                DshVersionReader = _ => "0.1.0-rc.7",
                WebProfileReader = (_, _) => "Usage: dsh <command> ...\nCommands:\n  web  Start Harness web UI",
                WebHostPortProbe = (_, _, _) => Task.FromResult(false),
                RelayFactory = () => new DeepSeekHarnessRelayProbe()
            };
            var s1 = await service.DiagnoseAsync();
            Assert(s1.DshVersion == "0.1.0-rc.7", "应识别 rc.7，实际：" + s1.DshVersion);
            Assert(s1.DshRisk == HarnessVersionRiskLevel.SameSeries, "rc.7 应为同系列新版本，实际：" + s1.DshRisk);
            Assert(s1.CapabilityCli && s1.CapabilityWebProfile, "入口应完整");
            Assert(s1.EnableAllowed, "入口完整且能力通过时新版本不应被版本拒绝");
            Assert(s1.StatusKind == HarnessStatusKind.WebOnly, "中继未确认应显示可打开 Web 但自动中继未确认，实际：" + s1.StatusKind);

            // 跨主版本 1.0.0 + 入口完整 + 中继确认 → 新版本已验证，可实时协作。
            var service2 = new DeepSeekHarnessService(app)
            {
                DiscoveryFactory = () => discovery,
                NodeVersionReader = _ => "v24.19.0",
                DshVersionReader = _ => "1.0.0",
                WebProfileReader = (_, _) => "Usage: dsh ... web ...",
                WebHostPortProbe = (_, _, _) => Task.FromResult(true),
                RelayFactory = () => new ConfirmedHarnessRelay()
            };
            var s2 = await service2.DiagnoseAsync();
            Assert(s2.DshRisk == HarnessVersionRiskLevel.CrossMajor, "1.0.0 应为跨主版本，实际：" + s2.DshRisk);
            Assert(s2.StatusKind == HarnessStatusKind.NewVersionVerified, "跨主版本能力通过应显示新版本已验证，实际：" + s2.StatusKind);
            Assert(s2.EnableAllowed && s2.RelayCapable, "跨主版本能力通过不因版本被拒，且中继确认后可实时协作");

            // 新版本但入口不完整（web profile 未含 web）→ 新版本未通过，不得启用。
            var service3 = new DeepSeekHarnessService(app)
            {
                DiscoveryFactory = () => discovery,
                NodeVersionReader = _ => "v24.19.0",
                DshVersionReader = _ => "0.1.0-rc.7",
                WebProfileReader = (_, _) => "Usage: dsh <command> ... run, help",
                WebHostPortProbe = (_, _, _) => Task.FromResult(false),
                RelayFactory = () => new DeepSeekHarnessRelayProbe(),
                CapabilityCacheFactory = () => new DeepSeekHarnessCapabilityCache(Path.Combine(root, "cap-s3.json"))
            };
            var s3 = await service3.DiagnoseAsync();
            Assert(!s3.EnableAllowed, "入口不完整时新版本不得启用");
            Assert(s3.StatusKind == HarnessStatusKind.NewVersionFailed, "新版本入口不完整应显示新版本未通过，实际：" + s3.StatusKind);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task TestHarnessCapabilityCacheAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-helper-harness-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var programFiles = Path.Combine(root, "Program Files");
            var appData = Path.Combine(root, "AppData", "Roaming");
            var localAppData = Path.Combine(root, "AppData", "Local");
            var nodePath = Path.Combine(programFiles, "nodejs", "node.exe");
            var dshEntry = Path.Combine(appData, "npm", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            var dshPkg = Path.GetDirectoryName(Path.GetDirectoryName(dshEntry))!;
            Directory.CreateDirectory(Path.GetDirectoryName(nodePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(dshEntry)!);
            await File.WriteAllTextAsync(nodePath, "fake node");
            await File.WriteAllTextAsync(dshEntry, "fake bin.js");
            await File.WriteAllTextAsync(Path.Combine(dshPkg, "package.json"), "{\"version\":\"0.1.0-rc.7\"}");
            var app = new AppPaths(Path.Combine(root, "app"));
            var discovery = new DeepSeekHarnessDiscovery
            {
                SpecialFolder = folder => folder switch
                {
                    Environment.SpecialFolder.ProgramFiles => programFiles,
                    Environment.SpecialFolder.ProgramFilesX86 => programFiles,
                    Environment.SpecialFolder.ApplicationData => appData,
                    Environment.SpecialFolder.LocalApplicationData => localAppData,
                    _ => root
                },
                PathDirectoryReader = () => Array.Empty<string>()
            };
            var cache = new DeepSeekHarnessCapabilityCache(app);
            var probeCount = 0;
            var service = new DeepSeekHarnessService(app)
            {
                DiscoveryFactory = () => discovery,
                NodeVersionReader = _ => "v24.19.0",
                DshVersionReader = _ => "0.1.0-rc.7",
                WebProfileReader = (_, _) => { probeCount++; return "Usage: dsh ... web ..."; },
                WebHostPortProbe = (_, _, _) => Task.FromResult(false),
                RelayFactory = () => new DeepSeekHarnessRelayProbe(),
                CapabilityCacheFactory = () => cache
            };

            await service.DiagnoseAsync();
            Assert(probeCount == 1, "首次应探测入口，probeCount=" + probeCount);
            await service.DiagnoseAsync();
            Assert(probeCount == 1, "缓存命中不应再次探测入口，probeCount=" + probeCount);
            await service.DiagnoseAsync(forceRefresh: true);
            Assert(probeCount == 2, "强制刷新应绕过缓存再次探测入口，probeCount=" + probeCount);

            // 缓存损坏 → 安全重建（视为未命中）。
            var capPath = Path.Combine(app.BaseDirectory, "harness-capability.json");
            Assert(File.Exists(capPath), "缓存文件应存在");
            var (key, _, _) = cache.BuildFingerprint(nodePath, dshEntry, "0.1.0-rc.7", "v24.19.0");
            await File.WriteAllTextAsync(capPath, "{ not valid json");
            Assert(!cache.TryLoad(key, out _), "损坏缓存应视为未命中并安全重建");
            cache.Store(key, new HarnessCapabilityResult(true, "cli", true, "web", false, "off", false, false, false, false, "relay", DateTime.UtcNow, "n", "d"));
            Assert(cache.TryLoad(key, out _), "重建后可写读");

            // 文件变化 → 缓存键失效。
            await File.WriteAllTextAsync(nodePath, "fake node changed longer");
            var (key2, _, _) = cache.BuildFingerprint(nodePath, dshEntry, "0.1.0-rc.7", "v24.19.0");
            Assert(key != key2, "node 文件变化应使缓存键失效");

            // 敏感数据不得写入缓存：无环境变量键名、无任务正文。
            var capText = await File.ReadAllTextAsync(capPath);
            Assert(!capText.Contains("DEEPSEEK_API_KEY", StringComparison.OrdinalIgnoreCase), "缓存不得含环境变量键名");
            Assert(!capText.Contains("SPEC.md", StringComparison.OrdinalIgnoreCase), "缓存不得含任务正文");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class ConfirmedHarnessRelay : IDeepSeekHarnessRelay
    {
        public Task<HarnessRelayCapability> ProbeCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new HarnessRelayCapability(true, true, true, true, "能力已由运行时探测确认。"));
    }
}
