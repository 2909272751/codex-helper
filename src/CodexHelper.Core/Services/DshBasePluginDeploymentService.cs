using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// DSH 基础插件部署服务：把随 Helper 一起发布的基础插件实体（Core 程序集嵌入资源）
/// 解包到受管的稳定本地目录，并通过 DSH 原生 profile 声明（package.json 的
/// <c>dsh.profile.bundles</c> + <c>dependencies</c>）与 <c>node_modules</c> 目录链接加载。
/// 不调用 npm、不联网、不安装任何依赖包。
/// <para>安全边界：</para>
/// <list type="bullet">
/// <item>只补缺失项：已存在的 bundle 条目、依赖声明与 <c>node_modules</c> 条目一律保持原样，
/// 不替换用户安装的同名实体，也不改禁用状态。</item>
/// <item>package.json 只增字段：未知字段、字段顺序、缩进与 Helper 无关的依赖全部保留。</item>
/// <item>写入受管目录前做目录越界与 reparse point 防护；package.json 经临时文件 + 替换原子写入；
/// 任一步失败回滚本次全部写入；重复启动无重复写入。</item>
/// <item>不读任何凭据、会话或远程地址，不修改 cordis.patch.yml。</item>
/// </list>
/// </summary>
public sealed class DshBasePluginDeploymentService
{
    /// <summary>嵌入资源名前缀（与 CodexHelper.Core.csproj 的 LogicalName 一致）。</summary>
    public const string ResourcePrefix = "CodexHelper.BasePlugins.";

    /// <summary>受管基础插件实体目录（profile 内相对路径，Helper 独占管理）。</summary>
    public const string ManagedRootRelativePath = ".helper-bundles";

    private readonly string dshHome;
    private readonly IReadOnlyList<DshBasePlugin> plugins;
    private readonly Func<string, string, string?> nodeModulesLinkCreator;
    private readonly Func<string, string, string?> packageJsonWriter;

    /// <param name="configuredHome">显式 DSH Home（null 时按 DSH_HOME 与 ~/.dsh 解析，与 DshProfilePatchLocator 一致）。</param>
    public DshBasePluginDeploymentService(string? configuredHome = null)
        : this(configuredHome, null, null, null)
    {
    }

    /// <param name="configuredHome">显式 DSH Home。</param>
    /// <param name="nodeModulesLinkCreator">node_modules 链接创建注入点（默认目录链接；返回 null 表示成功，否则返回脱敏诊断）。</param>
    /// <param name="packageJsonWriter">package.json 原子写入注入点（默认 <see cref="AtomicFile"/>；返回 null 表示成功）。</param>
    /// <param name="plugins">插件目录清单（默认取嵌入资源）。</param>
    public DshBasePluginDeploymentService(
        string? configuredHome,
        Func<string, string, string?>? nodeModulesLinkCreator,
        Func<string, string, string?>? packageJsonWriter = null,
        IReadOnlyList<DshBasePlugin>? plugins = null)
    {
        dshHome = DshProfilePatchLocator.ResolveDshHome(configuredHome);
        this.plugins = plugins ?? LoadEmbeddedPlugins();
        this.nodeModulesLinkCreator = nodeModulesLinkCreator ?? CreateDirectoryLink;
        this.packageJsonWriter = packageJsonWriter ?? (static (path, json) =>
        {
            try { AtomicFile.WriteAllText(path, json); return null; }
            catch (Exception ex) { return Sanitize(ex.Message); }
        });
    }

    /// <summary>当前解析出的 DSH Home（规范化绝对路径）。</summary>
    public string DshHome => dshHome;

    /// <summary>受部署的基础插件包名（稳定顺序）。</summary>
    public IReadOnlyList<string> BasePluginNames => plugins.Select(plugin => plugin.Name).ToArray();

    /// <summary>目标 profile 根目录（<c>$DSH_HOME/profiles/&lt;profile&gt;</c>）。</summary>
    public string ResolveProfileDirectory(string profileName = "web")
        => Path.GetFullPath(Path.Combine(dshHome, "profiles", NormalizeProfileName(profileName)));

    /// <summary>
    /// 部署基础插件。绝不对调用方抛异常（失败以诊断返回），使启动路径永远不被部署缺陷拖死。
    /// web profile 的 package.json 不存在时标记 <see cref="DshBasePluginDeploymentResult.Deferred"/>，
    /// 只写一个待办标记，等 DSH 初始化出 profile 后由下一次启动补上。
    /// </summary>
    public async Task<DshBasePluginDeploymentResult> EnsureDeployedAsync(
        string profileName = "web",
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(() => Deploy(profileName, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(false, false, [], [], [],
                "基础插件部署已取消：DSH 仍可正常启动，下次启动会重试。");
        }
        catch (Exception ex)
        {
            return new(false, false, [], [], [],
                "基础插件部署失败（不影响 DSH 启动，下次启动会重试）：" + Sanitize(ex.Message));
        }
    }

    private DshBasePluginDeploymentResult Deploy(string profileName, CancellationToken cancellationToken)
    {
        var profileDirectory = ResolveProfileDirectory(profileName);
        var packageJsonPath = Path.Combine(profileDirectory, "package.json");
        var profilesRoot = Path.Combine(dshHome, "profiles");
        var pendingMarkerPath = Path.Combine(profilesRoot, ".helper-base-plugins.pending.json");

        if (!File.Exists(packageJsonPath))
        {
            var reason = !Directory.Exists(profileDirectory)
                ? $"DSH profile 目录尚未初始化（{profileDirectory}）"
                : "DSH profile 缺少 package.json";
            // 写待办标记同样是"写入"：必须先核对从 DSH_HOME 到标记文件的整条链（含 DSH_HOME 自身），
            // 再创建任何目录 —— 绝不"先创建再检查"。
            var markerProblem = CheckPathChain(dshHome, [profilesRoot, pendingMarkerPath]);
            if (markerProblem is not null)
                return new(false, false, [], [], [], "基础插件未部署：" + markerProblem);
            WritePendingMarker(profilesRoot, profileDirectory, reason);
            return new(false, true, [], [], [],
                $"基础插件未就绪：{reason}；DSH 初始化后下次启动会自动补齐。");
        }

        var managedRoot = Path.Combine(profileDirectory, ManagedRootRelativePath);
        var pluginsRoot = Path.Combine(managedRoot, "plugins");
        var nodeModulesRoot = Path.Combine(profileDirectory, "node_modules");
        var lockPath = Path.Combine(managedRoot, ".deploy.lock");
        var backupPath = packageJsonPath + ".helper-original.bak";

        // 创建任何东西之前先核对从 DSH_HOME（含其自身）到目标的整条链：任何已有祖先或目标本身是
        // reparse point（目录联接/符号链接）都拒绝，绝不"先创建再检查"。所有写入目标都要覆盖：
        // profile、受管目录、plugins、单个实体目录、锁文件、持久备份、package.json、node_modules。
        var targets = new List<string>
        {
            profileDirectory, managedRoot, pluginsRoot, packageJsonPath, backupPath, nodeModulesRoot, lockPath
        };
        foreach (var plugin in plugins) targets.Add(Path.Combine(pluginsRoot, plugin.Name));
        var chainProblem = CheckPathChain(dshHome, targets);
        if (chainProblem is not null)
            return new(false, false, [], [], [], "基础插件未部署：" + chainProblem);

        try { Directory.CreateDirectory(managedRoot); }
        catch (Exception ex)
        {
            return new(false, false, [], [], [],
                "基础插件未部署：无法创建受管目录（" + Sanitize(ex.Message) + "）。");
        }
        chainProblem = CheckPathChain(dshHome, [managedRoot]);
        if (chainProblem is not null)
            return new(false, false, [], [], [], "基础插件未部署：" + chainProblem);

        // 跨进程单飞：拿不到锁就不写（Deferred），等下一次启动再补，绝不并发写入同一 profile。
        DshBasePluginDeploymentResult result;
        using (var deploymentLock = TryAcquireLock(lockPath))
        {
            if (deploymentLock is null)
                return new(false, true, [], [], [],
                    "基础插件未部署：同 profile 正在被另一个 Helper 进程部署（未取得 .deploy.lock），本次不写入，下次启动重试。");

            // 锁内读取：只有持锁后才读取 pristine 内容，避免读到"锁外被并发修改"的中间态。
            string pristineJson;
            try { pristineJson = File.ReadAllText(packageJsonPath, Encoding.UTF8); }
            catch (Exception ex)
            {
                return new(false, false, [], [], [],
                    "基础插件未部署：无法读取 DSH profile package.json（" + Sanitize(ex.Message) + "）。");
            }

            var journal = new DeploymentJournal(dshHome, profileDirectory, managedRoot, nodeModulesRoot, packageJsonPath, pristineJson);
            try
            {
                result = journal.Transaction(() => DeployLocked(
                    packageJsonPath, managedRoot, nodeModulesRoot, pristineJson, journal, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                // 取消也走同一套回滚：journal.Transaction 已在失败路径统一回滚本次真正写入的内容。
                result = new(false, false, [], [], [],
                    "基础插件部署已取消：本次写入已回滚，DSH 仍可正常启动，下次启动会重试。");
            }
            catch (Exception ex)
            {
                result = new(false, false, [], [], [],
                    "基础插件部署失败（不影响 DSH 启动，下次启动会重试）：" + Sanitize(ex.Message));
            }
        }

        // 锁释放后再清理空壳：持锁期间租约文件占位，无法判定"空"。
        TryRemoveEmptyManagedRoot(managedRoot);
        return result;
    }

    /// <summary>受管目录为空时移除（绝不删除任何非空目录）。</summary>
    private static void TryRemoveEmptyManagedRoot(string managedRoot)
    {
        try
        {
            if (Directory.Exists(managedRoot) && !Directory.EnumerateFileSystemEntries(managedRoot).Any())
                Directory.Delete(managedRoot);
        }
        catch { /* 空壳残留不影响正确性 */ }
    }

    /// <summary>持锁后的部署主体；不在此处做回滚（由 <see cref="DeploymentJournal.Transaction"/> 统一负责）。</summary>
    private DshBasePluginDeploymentResult DeployLocked(
        string packageJsonPath,
        string managedRoot,
        string nodeModulesRoot,
        string pristineJson,
        DeploymentJournal journal,
        CancellationToken cancellationToken)
    {
        JsonObject root;
        try { root = ParsePackageObject(pristineJson); }
        catch (Exception ex)
        {
            return new(false, false, [], [], [],
                "基础插件未部署：DSH profile package.json 结构不可识别（" + Sanitize(ex.Message) + "），已保持原样。");
        }

        var installed = new List<string>();
        var preserved = new List<string>();
        var conflicts = new List<string>();
        var declarations = new List<(string Name, string Dependency)>();

        // 结构校验：存在但类型不符（或 bundles 含非字符串）时直接诊断并保留原文，
        // 绝不把用户的既有配置改成数组、也绝不丢失未知项。
        var structure = ValidateStructure(root);
        if (structure is not null)
            return new(false, false, [], [], [], "基础插件未部署：" + structure + "，已保持原样。");

        // 只读：不创建任何字段（没有新增插件时 package.json 必须逐字不变）。
        var bundles = ReadBundleNames(root);

        // 写入类错误（实体写入/链接创建/package.json 写入/备份失败）：整轮回滚，绝不留下半成品。
        DshBasePluginDeploymentResult Fail(string reason)
            => new(false, false, [], preserved.ToArray(), conflicts.ToArray(), "基础插件未部署：" + reason);

        foreach (var plugin in plugins)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bundleDeclared = bundles.Contains(plugin.Name, StringComparer.OrdinalIgnoreCase);
            var dependencyDeclared = HasDependency(root, plugin.Name);
            var linkPath = Path.Combine(nodeModulesRoot, plugin.Name);
            var linkExists = Directory.Exists(linkPath) || File.Exists(linkPath);
            if (bundleDeclared || dependencyDeclared || linkExists)
            {
                // 用户已有同名声明或实体（任一）：一律不修改、不自动启用，报告为 preserved/AlreadyPresent。
                // 这是"用户既有安装被保留"，不是会导致其他缺失基础插件一起失败的冲突。
                preserved.Add(plugin.Name);
                continue;
            }

            var managedCopy = Path.Combine(managedRoot, "plugins", plugin.Name);
            if (!PathSafety.IsWithin(managedCopy, managedRoot))
            {
                conflicts.Add(plugin.Name + "（受管路径越界，已拒绝）");
                continue;
            }

            // 已存在的受管实体：本轮绝不更新/覆盖（无受管升级功能）。只在内容与随包实体
            // 逐字节完全相同才引用它；不相同则原样不动并直接诊断。
            if (Directory.Exists(managedCopy) && !IsIdenticalManagedCopy(managedCopy, plugin))
            {
                conflicts.Add(plugin.Name + "（受管实体与随包实体内容不一致，保留原样未修改）");
                continue;
            }

            // 1) 只在缺失时解包到受管稳定目录；创建/移动成功即记账（后续 link 失败或取消都能准确移除）。
            if (!Directory.Exists(managedCopy) && !WriteManagedCopy(managedCopy, plugin, out var writeError, journal))
                return Fail("写入受管实体失败（" + plugin.Name + "：" + writeError + "）。");

            // 2) 通过 DSH 原生 node_modules 解析加载（目录链接，离线创建，不调用 npm）。
            if (!linkExists)
            {
                if (!Directory.Exists(nodeModulesRoot))
                {
                    try { Directory.CreateDirectory(nodeModulesRoot); }
                    catch (Exception ex)
                    {
                        return Fail("创建 node_modules 目录失败（" + plugin.Name + "：" + Sanitize(ex.Message) + "）。");
                    }
                    // 只记录本次真正新建的目录：用户既有的 node_modules 绝不在回滚范围内。
                    journal.RecordCreatedNodeModulesRoot(nodeModulesRoot);
                }
                var linkError = nodeModulesLinkCreator(managedCopy, linkPath);
                if (linkError is not null)
                    return Fail("创建 node_modules 链接失败（" + plugin.Name + "：" + linkError + "）。");
                journal.RecordLinkedPath(linkPath);
            }

            // 3) profile 声明：只在缺失时追加，既有条目/顺序/禁用状态不动。
            declarations.Add((plugin.Name, ToFileDependency(managedCopy)));
            installed.Add(plugin.Name);
        }

        // 没有任何新增插件：绝不 Serialize、绝不写 package.json（连字段都不创建），原文件逐字不变。
        if (installed.Count == 0)
        {
            var text = preserved.Count == 0
                ? "基础插件无需部署（清单为空），未写入任何内容。"
                : $"基础插件已就绪（{string.Join("、", preserved)}），未重复写入，package.json 逐字未变。";
            if (conflicts.Count > 0) text += " 冲突项：" + string.Join("；", conflicts) + "。";
            return new(true, false, [], preserved, conflicts, text);
        }

        // 确有新增：此时才创建缺失字段（只增字段，既有结构绝不覆盖）。
        if (root["dsh"] is not JsonObject dshNode) root["dsh"] = dshNode = new JsonObject();
        if (dshNode["profile"] is not JsonObject profileNode) dshNode["profile"] = profileNode = new JsonObject();
        if (profileNode["bundles"] is not JsonArray bundlesArray) profileNode["bundles"] = bundlesArray = new JsonArray();
        if (root["dependencies"] is not JsonObject dependenciesNode) root["dependencies"] = dependenciesNode = new JsonObject();
        foreach (var (name, dependency) in declarations)
        {
            dependenciesNode[name] = dependency;
            if (!bundles.Contains(name, StringComparer.OrdinalIgnoreCase)) bundlesArray.Add(name);
        }

        var json = Serialize(root);
        if (!string.Equals(json, pristineJson, StringComparison.Ordinal))
        {
            // 写前核对：内容必须仍是锁内读到的原件，防止外部在本次部署期间改写后被我方覆盖。
            string current;
            try { current = File.ReadAllText(packageJsonPath, Encoding.UTF8); }
            catch (Exception ex)
            {
                return Fail("写前无法复核 profile package.json（" + Sanitize(ex.Message) + "），本次不写入。");
            }
            if (!string.Equals(current, pristineJson, StringComparison.Ordinal))
                return Fail("profile package.json 在本次部署期间被外部修改，冲突不覆盖，已保持原样。");

            // 首次写入前为用户的原始 package.json 留一份持久备份（CreateNew 避免竞态；已存在则保持不动）。
            var backupError = EnsurePersistentBackup(packageJsonPath, pristineJson);
            if (backupError is not null)
                return Fail("无法保留 package.json 原始备份（" + backupError + "），本次不写入。");

            var writeError = packageJsonWriter(packageJsonPath, json);
            if (writeError is not null)
                return Fail("package.json 写入失败（" + writeError + "）。");
            journal.MarkPackageJsonWritten();
        }

        var summary = new StringBuilder();
        summary.Append($"已部署基础插件：{string.Join("、", installed)}（实际生效需重启 DSH Host）。");
        if (preserved.Count > 0)
            summary.Append(summary.Length > 0 ? " " : string.Empty).Append($"已存在保持原样：{string.Join("、", preserved)}。");
        if (conflicts.Count > 0)
            summary.Append(summary.Length > 0 ? " " : string.Empty).Append("冲突项：" + string.Join("；", conflicts) + "。");
        return new(true, false, installed, preserved, conflicts, summary.ToString().Trim());
    }

    /// <summary>
    /// 在 profile 的 <c>node_modules</c> 里建立指向受管实体的目录链接（DSH 原生解析即可加载）。
    /// 先试 .NET 目录符号链接（需要开发者模式/管理员），失败则退回 NTFS 目录联接
    /// （<c>mklink /J</c> 不需要额外特权，也不联网）；两者都不可用时返回脱敏诊断。
    /// </summary>
    private static string? CreateDirectoryLink(string target, string linkPath)
    {
        var parent = Path.GetDirectoryName(linkPath);
        if (parent is not null) Directory.CreateDirectory(parent);
        if (Directory.Exists(linkPath) || File.Exists(linkPath)) return null;

        string? symlinkError = null;
        var method = typeof(Directory).GetMethod(
            "CreateSymbolicLink",
            BindingFlags.Public | BindingFlags.Static,
            [typeof(string), typeof(string)]);
        if (method is not null)
        {
            try
            {
                method.Invoke(null, [linkPath, target]);
                return null;
            }
            catch (TargetInvocationException ex) { symlinkError = Sanitize(ex.InnerException?.Message ?? ex.Message); }
            catch (Exception ex) { symlinkError = Sanitize(ex.Message); }
        }

        var junctionError = TryCreateDirectoryJunction(target, linkPath);
        if (junctionError is null) return null;
        return symlinkError is null
            ? junctionError
            : $"符号链接失败（{symlinkError}），目录联接也失败（{junctionError}）";
    }

    /// <summary>NTFS 目录联接（<c>mklink /J</c>）：Windows 上无需管理员，也不依赖开发者模式。</summary>
    private static string? TryCreateDirectoryJunction(string target, string linkPath)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(linkPath);
            start.ArgumentList.Add(target);
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return "无法启动 cmd.exe 创建目录联接。";
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "创建目录联接超时。";
            }
            if (process.ExitCode == 0 && Directory.Exists(linkPath)) return null;
            var detail = (error.GetAwaiter().GetResult() + " " + output.GetAwaiter().GetResult()).Trim();
            return Sanitize(detail.Length == 0 ? $"mklink 退出码 {process.ExitCode}" : detail);
        }
        catch (Exception ex) { return Sanitize(ex.Message); }
    }

    /// <summary>把<b>缺失</b>的受管实体写入受管目录：先写同卷临时目录，再整体移入；失败不留下半成品。</summary>
    private static bool WriteManagedCopy(string managedCopy, DshBasePlugin plugin, out string error, DeploymentJournal journal)
    {
        error = string.Empty;
        var parent = Path.GetDirectoryName(managedCopy)!;
        var staging = Path.Combine(parent, "." + plugin.Name + ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (!PathSafety.IsWithin(staging, journal.ManagedRoot))
            {
                error = "临时目录越界。";
                return false;
            }
            // 临时目录写入前重复核对整条链（含 DSH_HOME 自身与受管目录各级）。
            var chain = CheckPathChain(journal.DshHome, [journal.ManagedRoot, staging]);
            if (chain is not null) { error = chain; return false; }
            if (!Directory.Exists(parent)) journal.RecordCreatedDirectory(parent);
            Directory.CreateDirectory(staging);
            // 一创建成功就记账：即使后面文件写入失败或取消，回滚也能准确移除。
            journal.RecordCreatedDirectory(staging);
            foreach (var file in plugin.Files)
            {
                var relative = file.RelativePath.Replace('\\', '/');
                if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                {
                    error = "插件相对路径不合法：" + relative;
                    return false;
                }
                var destination = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!PathSafety.IsWithin(destination, staging))
                {
                    error = "插件文件越界：" + relative;
                    return false;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, file.Bytes);
            }
            // 受管实体缺失才走到这里：直接移动，绝不覆盖既有实体。
            Directory.Move(staging, managedCopy);
            journal.RecordCreatedDirectory(managedCopy);
            return true;
        }
        catch (Exception ex)
        {
            error = Sanitize(ex.Message);
            return false;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    /// <summary>已存在的受管实体是否与本轮随包实体<b>逐字节完全相同</b>（完全相同才允许引用，否则原样不动）。</summary>
    private static bool IsIdenticalManagedCopy(string managedCopy, DshBasePlugin plugin)
    {
        try
        {
            if (!Directory.Exists(managedCopy)) return false;
            foreach (var file in plugin.Files)
            {
                var path = Path.Combine(managedCopy, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) return false;
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(file.Bytes)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    // ---- package.json 结构（只增字段） ----

    private static JsonObject ParsePackageObject(string text)
    {
        if (JsonNode.Parse(text) is not JsonObject root)
            throw new InvalidDataException("package.json 根节点不是 JSON 对象");
        return root;
    }

    /// <summary>序列化 profile package.json（缩进 + 中文不转义）；只用于“确实需要新增字段”的写入。</summary>
    private static string Serialize(JsonObject root)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            // 保持非 ASCII 原样（package.json 里可能有中文描述），不做 \uXXXX 转义。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
        }))
        {
            root.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// 读取 <c>dsh.profile.bundles</c> 字符串列表（缺失时返回空表）。调用前必须已通过
    /// <see cref="ValidateStructure"/>；这里<b>只读</b>，绝不创建任何字段。
    /// </summary>
    private static List<string> ReadBundleNames(JsonObject root)
    {
        if (root["dsh"] is not JsonObject dsh) return [];
        if (dsh["profile"] is not JsonObject profile) return [];
        if (profile["bundles"] is not JsonArray bundles) return [];
        return bundles.OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var name) ? name : null)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToList();
    }

    /// <summary>是否已声明同名依赖（只读；不创建 dependencies 字段）。</summary>
    private static bool HasDependency(JsonObject root, string name)
        => root["dependencies"] is JsonObject dependencies
        && dependencies.Any(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// package.json 相关结构校验：存在但类型不符、或 bundles 含非字符串时返回可读诊断，调用方必须保持原文不动。
    /// 缺失的字段由部署按需创建（只增字段），不在此处判错。
    /// </summary>
    internal static string? ValidateStructure(JsonObject root)
    {
        if (root.ContainsKey("dsh") && root["dsh"] is not JsonObject)
            return "dsh 字段类型不符（应为对象）";
        if (root["dsh"] is JsonObject dsh)
        {
            if (dsh.ContainsKey("profile") && dsh["profile"] is not JsonObject)
                return "dsh.profile 字段类型不符（应为对象）";
            if (dsh["profile"] is JsonObject profile)
            {
                if (profile.ContainsKey("bundles") && profile["bundles"] is not JsonArray)
                    return "dsh.profile.bundles 字段类型不符（应为数组）";
                if (profile["bundles"] is JsonArray bundles)
                {
                    foreach (var node in bundles)
                        if (node is not JsonValue value || !value.TryGetValue<string>(out _))
                            return "dsh.profile.bundles 含有非字符串条目";
                }
            }
        }
        if (root.ContainsKey("dependencies") && root["dependencies"] is not JsonObject)
            return "dependencies 字段类型不符（应为对象）";
        return null;
    }

    /// <summary>
    /// 检查从 DSH Home 到每个目标的整条路径链：<b>DSH Home 自身</b>或任一已存在的祖先/目标本身是
    /// reparse point（目录联接/符号链接）即拒绝。只在创建目录之前调用，绝不"先创建再检查"。
    /// 路径先做尾斜杠归一化，避免 <c>...\web\</c> 被判成越界或漏检。
    /// </summary>
    internal static string? CheckPathChain(string root, IReadOnlyList<string> targets)
    {
        string fullRoot;
        try { fullRoot = NormalizeForCheck(root); }
        catch (Exception ex) { return "DSH Home 路径无效（" + Sanitize(ex.Message) + "）"; }

        // 根本身（DSH_HOME）也必须检查：它若是联接/符号链接，所有"写入"都会落到别处。
        if (IsReparsePoint(fullRoot)) return "DSH Home 自身是 reparse point（已拒绝写入）：" + fullRoot;

        foreach (var rawTarget in targets)
        {
            if (string.IsNullOrWhiteSpace(rawTarget)) continue;
            string target;
            try { target = NormalizeForCheck(rawTarget); }
            catch (Exception ex) { return "目标路径无效（" + Sanitize(ex.Message) + "）"; }
            if (!PathSafety.IsWithin(target, fullRoot) && !string.Equals(target, fullRoot, StringComparison.OrdinalIgnoreCase))
                return "目标路径越界（不在 DSH Home 内）";

            // 从 DSH Home 到目标逐级检查已存在的组件（含目标自身）。
            var relative = target.Length > fullRoot.Length ? target[(fullRoot.Length + 1)..] : string.Empty;
            var current = fullRoot;
            foreach (var segment in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current) && !File.Exists(current)) break;
                if (IsReparsePoint(current)) return "路径链存在 reparse point（已拒绝写入）：" + segment;
            }
        }
        return null;
    }

    /// <summary>路径归一化：绝对化并去掉尾部分隔符（根路径如 <c>C:\</c> 保持不变）。</summary>
    internal static string NormalizeForCheck(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        return trimmed.Length == 0 ? full : trimmed;
    }

    /// <summary>首次写入前为用户原始 package.json 留持久备份：CreateNew 避免竞态；已存在则保持不动。</summary>
    private string? EnsurePersistentBackup(string packageJsonPath, string pristineJson)
    {
        var backup = packageJsonPath + ".helper-original.bak";
        try
        {
            if (File.Exists(backup)) return null;
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(pristineJson);
            using var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
            return null;
        }
        catch (IOException) when (File.Exists(backup))
        {
            // 并发下另一进程刚创建了备份：内容保持不动，视为成功。
            return null;
        }
        catch (Exception ex) { return Sanitize(ex.Message); }
    }

    /// <summary>把受管实体绝对路径写成 DSH/pnpm 可解析的 <c>file:</c> 依赖（正斜杠，不改动盘符）。</summary>
    internal static string ToFileDependency(string absolutePath)
        => "file:" + Path.GetFullPath(absolutePath).Replace('\\', '/');

    // ---- 写入日记（任一步失败回滚本次全部写入） ----

    private sealed class DeploymentJournal
    {
        private readonly string packageJsonPath;
        private readonly string pristineJson;
        private readonly List<string> linkedPaths = [];
        private readonly List<string> createdDirectories = [];
        private string? createdNodeModulesRoot;
        private bool packageJsonWritten;
        private bool rolledBack;

        public DeploymentJournal(
            string dshHome,
            string profileDirectory,
            string managedRoot,
            string nodeModulesRoot,
            string packageJsonPath,
            string pristineJson)
        {
            DshHome = dshHome;
            ProfileDirectory = profileDirectory;
            this.packageJsonPath = packageJsonPath;
            this.pristineJson = pristineJson;
            ManagedRoot = managedRoot;
            NodeModulesRoot = nodeModulesRoot;
        }

        public string DshHome { get; }

        public string ProfileDirectory { get; }

        public string ManagedRoot { get; }

        public string NodeModulesRoot { get; }

        /// <summary>
        /// 事务包一层：只有<b>真正成功</b>才提交；<b>任何失败返回（Succeeded=false）或异常/取消</b>
        /// 都走同一条唯一回滚路径，且只回滚一次。失败结果绝不报告 Installed 仍已部署。
        /// </summary>
        public DshBasePluginDeploymentResult Transaction(Func<DshBasePluginDeploymentResult> work)
        {
            DshBasePluginDeploymentResult result;
            try
            {
                result = work();
            }
            catch
            {
                Rollback();
                throw;
            }

            if (result.Succeeded)
            {
                Commit();
                return result;
            }

            var problems = Rollback();
            var message = result.Message.TrimEnd('。') + "；本次写入已回滚"
                + (problems is null ? "。" : "（" + problems + "）。");
            return result with { Installed = Array.Empty<string>(), Message = message };
        }

        /// <summary>记录本次真正新建的目录（临时目录与受管实体）：创建/移动一成功即记账，取消后也能准确移除。</summary>
        public void RecordCreatedDirectory(string path)
        {
            if (!createdDirectories.Contains(path, StringComparer.OrdinalIgnoreCase)) createdDirectories.Add(path);
        }

        public void RecordLinkedPath(string path)
        {
            if (!linkedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) linkedPaths.Add(path);
        }

        /// <summary>记录本次由 Helper 新建的 node_modules 目录（回滚时若已空则一并清理）。</summary>
        public void RecordCreatedNodeModulesRoot(string path) => createdNodeModulesRoot = path;

        public void MarkPackageJsonWritten() => packageJsonWritten = true;

        /// <summary>提交：没有任何替换/备份需要清理（本轮只新建，不覆盖既有实体）。</summary>
        public void Commit()
        {
        }

        /// <summary>
        /// 唯一回滚路径：只移除本次真正新建的目录/链接、还原本次真正写入的 package.json。
        /// 幂等（重复调用不再删除任何东西），且绝不触碰用户既有实体或已恢复的旧包。
        /// 清理前重复核对路径链，链上有 reparse point 时跳过该目标并如实记入问题。
        /// </summary>
        public string? Rollback()
        {
            if (rolledBack) return null;
            rolledBack = true;
            var problems = new List<string>();

            // 先移除链接（联接/符号链接本身），再移除实体目录：顺序反转可避免先删目标后留下悬空链接。
            foreach (var path in linkedPaths)
            {
                try
                {
                    if (!PathSafety.IsWithin(path, NodeModulesRoot)) continue;
                    if (CheckPathChain(DshHome, [NodeModulesRoot]) is { } chain)
                    {
                        problems.Add("跳过链接清理（" + chain + "）");
                        continue;
                    }
                    if (Directory.Exists(path) || File.Exists(path)) Directory.Delete(path, recursive: false);
                }
                catch (Exception ex) { problems.Add("移除 node_modules 链接失败：" + Sanitize(ex.Message)); }
            }

            foreach (var path in createdDirectories)
            {
                try
                {
                    if (!PathSafety.IsWithin(path, ManagedRoot)) continue;
                    if (CheckPathChain(DshHome, [path]) is { } chain)
                    {
                        problems.Add("跳过实体清理（" + chain + "）");
                        continue;
                    }
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                }
                catch (Exception ex) { problems.Add("移除受管实体失败：" + Sanitize(ex.Message)); }
            }

            if (packageJsonWritten)
            {
                try { AtomicFile.WriteAllText(packageJsonPath, pristineJson); }
                catch (Exception ex) { problems.Add("还原 package.json 失败：" + Sanitize(ex.Message)); }
            }

            if (createdNodeModulesRoot is { } nodeModulesRoot
                && Directory.Exists(nodeModulesRoot)
                && !Directory.EnumerateFileSystemEntries(nodeModulesRoot).Any())
            {
                try { Directory.Delete(nodeModulesRoot); }
                catch { /* 空目录残留不影响正确性 */ }
            }

            // 受管目录空壳由 Deploy 在锁释放后统一清理（持锁期间租约文件占位，这里判定必然为"非空"）。
            return problems.Count == 0 ? null : string.Join("；", problems);
        }
    }

    private void WritePendingMarker(string profilesRoot, string profileDirectory, string reason)
    {
        try
        {
            // profiles 目录是 DSH 官方惯例目录；不存在时按需创建以留下待办标记（不创建 profile 本身）。
            // 调用方已在写入前核对整条链（含 DSH_HOME 自身），这里不再"先创建再检查"。
            if (!Directory.Exists(profilesRoot)) Directory.CreateDirectory(profilesRoot);
            var markerPath = Path.Combine(profilesRoot, ".helper-base-plugins.pending.json");
            var payload = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["profile"] = Path.GetFileName(profileDirectory),
                ["reason"] = reason,
                ["recordedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["note"] = "DSH 初始化出 profile 后，Helper 下次启动会补齐基础插件；不需要联网或 npm。"
            };
            AtomicFile.WriteAllText(markerPath, Serialize(payload));
        }
        catch { /* 标记写入失败不影响 DSH 启动 */ }
    }

    // ---- 基础设施 ----

    private static bool IsReparsePoint(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch { return false; }
    }

    /// <summary>同 profile 部署单飞（跨进程租约文件）；拿不到也继续，最坏情况只是重复一次幂等写入。</summary>
    private static IDisposable? TryAcquireLock(string lockPath)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.DeleteOnClose);
            }
            catch { Thread.Sleep(150); }
        }
        return null;
    }

    private static string NormalizeProfileName(string profileName)
    {
        var name = string.IsNullOrWhiteSpace(profileName) ? "web" : profileName.Trim();
        if (name.Contains("..", StringComparison.Ordinal) || name.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new InvalidDataException("profile 名称不合法。");
        return name;
    }

    internal static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "未知错误。";
        var value = new StringBuilder(text.Length);
        foreach (var ch in text) value.Append(ch is '\r' or '\n' or '\t' ? ' ' : ch);
        var trimmed = value.ToString().Trim();
        return trimmed.Length <= 240 ? trimmed : trimmed[..240] + "…";
    }

    // ---- 嵌入资源 ----

    /// <summary>读取 Core 程序集嵌入的基础插件实体（离线、不依赖开发目录）。</summary>
    public static IReadOnlyList<DshBasePlugin> LoadEmbeddedPlugins()
    {
        var assembly = typeof(DshBasePluginDeploymentService).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var drafts = new List<(string Name, List<DshBasePluginFile> Files)>();
        foreach (var name in resources)
        {
            var rest = name[ResourcePrefix.Length..];
            var separator = rest.IndexOf('.');
            if (separator <= 0) continue;
            var pluginName = rest[..separator];
            var fileName = rest[(separator + 1)..];
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var index = drafts.FindIndex(draft => string.Equals(draft.Name, pluginName, StringComparison.Ordinal));
            if (index < 0)
            {
                drafts.Add((pluginName, [new DshBasePluginFile(fileName, buffer.ToArray())]));
            }
            else
            {
                drafts[index].Files.Add(new DshBasePluginFile(fileName, buffer.ToArray()));
            }
        }
        return drafts
            .Select(draft => new DshBasePlugin(draft.Name, ReadDeclaredVersion(draft.Files), draft.Files))
            .OrderBy(plugin => plugin.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ReadDeclaredVersion(IReadOnlyList<DshBasePluginFile> files)
    {
        try
        {
            var packageJson = files.FirstOrDefault(file => string.Equals(file.RelativePath, "package.json", StringComparison.OrdinalIgnoreCase));
            if (packageJson is null) return "0.0.0";
            using var document = JsonDocument.Parse(packageJson.Bytes);
            return document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                ? version.GetString() ?? "0.0.0"
                : "0.0.0";
        }
        catch { return "0.0.0"; }
    }
}

/// <summary>一个基础插件实体（包名 + 声明版本 + 随包文件）。</summary>
public sealed record DshBasePlugin(string Name, string Version, IReadOnlyList<DshBasePluginFile> Files)
{
    public override string ToString() => Name + "@" + Version;
}

/// <summary>基础插件实体内的单个文件（包内相对路径 + 原始字节）。</summary>
public sealed record DshBasePluginFile(string RelativePath, byte[] Bytes);

/// <summary>一次基础插件部署的结果。成功 = 本轮没有发生需要回滚的写入错误（失败结果已回滚且 Installed 为空）。</summary>
public sealed record DshBasePluginDeploymentResult(
    bool Succeeded,
    bool Deferred,
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> AlreadyPresent,
    IReadOnlyList<string> Conflicts,
    string Message)
{
    /// <summary>本次有新写入的插件；实际生效需要 DSH Host 重启。</summary>
    public bool NeedsRestartToTakeEffect => Installed.Count > 0;
}
