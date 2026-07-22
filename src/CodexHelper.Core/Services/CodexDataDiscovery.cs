using CodexHelper.Core.Models;

namespace CodexHelper.Core.Services;

public sealed class CodexDataDiscovery
{
    public Task<IReadOnlyList<DataInventoryItem>> DiscoverAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default) => Task.Run(() => Discover(settings, cancellationToken), cancellationToken);

    private static IReadOnlyList<DataInventoryItem> Discover(AppSettings settings, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(settings.CodexRoot);
        var definitions = new[]
        {
            new Definition("config", "全局配置与规则", new[] { "config.toml", "AGENTS.md", "hooks.json" }, ProtectionKind.Critical, true, "Codex 全局配置、指导和生命周期钩子"),
            new Definition("profiles", "配置 Profiles", Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.config.toml", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OfType<string>().ToArray() : Array.Empty<string>(), ProtectionKind.Critical, true, "独立 Codex 配置 Profile"),
            new Definition("skills", "个人 Skills", new[] { "skills" }, ProtectionKind.Critical, true, "自定义工作流；系统自带 Skills 将单独排除"),
            new Definition("sessions", "任务与会话", new[] { "sessions", "archived_sessions", "session_index.jsonl" }, ProtectionKind.Recommended, settings.IncludeSessions, "Codex 任务正文和归档索引"),
            new Definition("state", "状态与记忆", new[] { "sqlite", "state_5.sqlite", "memories_1.sqlite", "goals_1.sqlite", ".codex-global-state.json" }, ProtectionKind.Recommended, true, "需要在安全状态下创建一致性快照"),
            new Definition("attachments", "附件与上传", new[] { "attachments", "web-uploads" }, ProtectionKind.Optional, settings.IncludeAttachments, "任务附件和上传副本"),
            new Definition("generated", "生成图片", new[] { "generated_images" }, ProtectionKind.Optional, settings.IncludeGeneratedImages, "Codex 生成的图片资产"),
            new Definition("plugin-inventory", "插件安装信息", new[] { "plugins" }, ProtectionKind.Reinstallable, false, "默认仅保存安装清单，不复制下载缓存"),
            new Definition("runtime", "缓存、临时文件与日志", new[] { ".tmp", "cache", "tmp", "logs_2.sqlite" }, ProtectionKind.Runtime, false, "可重建运行时数据，默认排除")
        };

        var results = new List<DataInventoryItem>();
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = definition.RelativePaths
                .Select(relative => Path.Combine(root, relative))
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .ToList();
            var (size, count) = Measure(existing, definition.Id, cancellationToken);
            results.Add(new DataInventoryItem(
                definition.Id,
                definition.Name,
                existing.Count == 1 ? existing[0] : root,
                definition.Kind,
                definition.Included,
                size,
                count,
                definition.Description,
                existing.Count > 0));
        }
        return results;
    }

    private static (long Size, int Count) Measure(IEnumerable<string> paths, string id, CancellationToken cancellationToken)
    {
        long size = 0;
        var count = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                try { size += new FileInfo(path).Length; count++; } catch { }
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (id == "skills" && file.Contains($"{Path.DirectorySeparatorChar}.system{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
                    if (id == "plugin-inventory" && file.Contains($"{Path.DirectorySeparatorChar}cache{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
                    try { size += new FileInfo(file).Length; count++; } catch { }
                }
            }
            catch { }
        }
        return (size, count);
    }

    private sealed record Definition(string Id, string Name, string[] RelativePaths, ProtectionKind Kind, bool Included, string Description);
}

