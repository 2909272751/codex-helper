using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;
using CodexHelper.Core.Services;

namespace CodexHelper.Core.Tests;

internal static partial class Program
{
    private static async Task Test4413QueueThenContinueAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "helper-queue-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = Path.Combine(root, "project");
            var first = Path.Combine(project, ".codex-helper", "runs", "run-first");
            var second = Path.Combine(project, ".codex-helper", "runs", "run-second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            File.WriteAllText(Path.Combine(first, "SPEC.md"), "先前合同");
            File.WriteAllText(Path.Combine(second, "SPEC.md"), "新合同，只写自己的报告");
            File.WriteAllText(Path.Combine(second, "manifest.json"), """{"rootCauseKey":"different-workflow"}""");
            WriteValidReport(second, "run-second", TestFingerprint(second));
            var running = true;
            var promptWhileBusy = false;
            await using var host = new FakeHarnessHost
            {
                Respond = (method, _) =>
                {
                    if (method == "session.prompt") { promptWhileBusy |= running; return new JsonObject { ["accepted"] = true }; }
                    return method switch
                    {
                        "session.list" => new JsonObject { ["items"] = new JsonArray(new JsonObject { ["sessionId"] = "same-session", ["running"] = running }) },
                        "session.history" => new JsonObject { ["projections"] = new JsonObject { ["asOfSeq"] = 20L } },
                        _ => new JsonObject()
                    };
                },
                WsScripts = [new Queue<string>([
                    WsFrame("session/subscribed", "same-session"),
                    "@wait:session.prompt",
                    WsFrame("session/event", "same-session", "turn/end", "completed", seq: 21)])]
            };
            await host.StartAsync();
            var runner = new DeepSeekHarnessRunner(new AppPaths(Path.Combine(root, "app")))
            {
                WebUrl = host.BaseUrl, RelayProbe = new ConfirmedHarnessRelay(),
                HostReadyEnsurer = _ => Task.FromResult(ReadyResult("ready")), ProjectLeaseWaitLimitSeconds = 8
            };
            var prior = new HarnessTaskStatus("run-first", project, first, "failed", "前轮报告失败",
                DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(-1), 0, host.BaseUrl,
                "same-session", RootCauseKey: "first-workflow", ContractFingerprint: TestFingerprint(first));
            File.WriteAllText(runner.TaskDirectoryFor("run-first"), JsonSerializer.Serialize(prior));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var pending = runner.StartAsync(project, second, deadline.Token);
            await WaitUntilAsync(() => runner.TryRead("run-second")?.SessionState == "waiting-session", message: "must queue");
            Assert(!host.Calls.Any(x => x.Method is "session.create" or "session.prompt"), "排队不得提交或新建");
            Assert(runner.TryRead("run-second")?.SessionId is null, "排队不冒用前轮会话 ID");
            running = false;
            var result = await pending;
            Assert(result.State == "awaiting-gpt" && result.SessionId == "same-session", "排队后在原会话完成自己合同：" + result.Message);
            Assert(!promptWhileBusy && host.Calls.Count(x => x.Method == "session.prompt") == 1
                && !host.Calls.Any(x => x.Method == "session.create"), "只在前轮结束后提交一次，零新会话");
            var context = File.ReadAllText(Path.Combine(second, "CONTINUITY_CONTEXT.md"));
            Assert(context.Contains("未通过") && !context.Contains("已通过"), "失败报告不得被说成前轮成功");

            var third = Path.Combine(project, ".codex-helper", "runs", "run-cancel-queue");
            Directory.CreateDirectory(third);
            File.WriteAllText(Path.Combine(third, "SPEC.md"), "取消排队测试");
            running = true;
            using var cancelled = new CancellationTokenSource();
            var waiting = runner.StartAsync(project, third, cancelled.Token);
            await WaitUntilAsync(() => runner.TryRead("run-cancel-queue")?.SessionState == "waiting-session", message: "must queue before cancel");
            cancelled.Cancel();
            try { await waiting; } catch (OperationCanceledException) { }
            Assert(!host.Calls.Any(x => x.Method == "session.cancel"), "取消排队不得取消前序 DSH");
        }
        finally { TryDeleteDirectory(root); }
    }

    private static async Task Test4413GatewayTerminalAsync()
    {
        foreach (var (kind, expected) in new[] { ("user", "aborted"), ("error", "error") })
        {
            var frame = """{"type":"item","streamId":"__STREAM_ID__","value":{"type":"snapshot","cursor":9,"records":[{"type":"event","event":{"seq":9,"type":"turn/end","data":{"reason":{"kind":"aborted","reason":{"kind":"REASON","message":"PRIVATE"}}}}}]}}""".Replace("REASON", kind);
            await using var host = new FakeHarnessHost
            {
                Respond = (method, _) => method == "session/list"
                    ? new JsonObject { ["items"] = new JsonArray() } : new JsonObject(),
                WsScripts = [new Queue<string>([frame]), new Queue<string>([frame])]
            };
            await host.StartAsync();
            using var rpc = new HarnessRpcClient(host.BaseUrl);
            var terminal = await rpc.GetSessionHistoryAsync("stopped");
            Assert(terminal.Success && terminal.Value?["events"]?[0]?["event"]?["data"]?["reason"]?["kind"]?.ToString() == expected,
                "新版用户停止/错误必须保留真实终态");
            Assert(!terminal.Value!.ToJsonString().Contains("PRIVATE") && !host.Calls.Any(x => x.Method == "session.history"),
                "新接口只返最小终态元数据，不读取旧接口、不泄露错误正文");
            using var stream = new DeepSeekHarnessEventStream(host.BaseUrl) { UseGatewayProtocol = true, SessionIdFilter = "stopped" };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var seen = false;
            await foreach (var item in stream.ListenAsync(deadline.Token))
            {
                if (item.EventType != "turn/end") continue;
                Assert(item.TurnEndKind == expected && item.Seq == 9, "Gateway 原生事件流保留终态与序号");
                seen = true;
                break;
            }
            Assert(seen, "原生事件流必须收到真实终态");
        }
    }

    private static async Task Test4413ActiveFrpAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "helper-frp-" + Guid.NewGuid().ToString("N"));
        var oldProbe = FrpRuntimeAuthorityResolver.ActiveProbe;
        var oldRoots = FrpRuntimeAuthorityResolver.StaticInstanceRootsResolver;
        var oldEnabled = FrpRuntimeAuthorityResolver.ActiveProbeEnabled;
        try
        {
            string Config(string name, int remote, int local = 3080)
            {
                var dir = Path.Combine(root, name); Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "runtime-frpc.toml");
                File.WriteAllText(path, $"serverAddr = '203.0.113.9'\n[[proxies]]\ntype = 'tcp'\nlocalIP = '127.0.0.1'\nlocalPort = {local}\nremotePort = {remote}\n");
                return path;
            }
            var stale = Config("old", 50001);
            var active = Config("中文服务", 50002);
            var other = Config("other", 50003, 18888);
            FrpRuntimeAuthorityResolver.ActiveProbeEnabled = true;
            FrpRuntimeAuthorityResolver.StaticInstanceRootsResolver = () => new[] { root };
            FrpRuntimeAuthorityResolver.ActiveProbe = _ => Task.FromResult(new ProbeResult(true, true, new[] { active, other }));
            var result = await FrpRuntimeAuthorityResolver.ResolveDshWebAuthoritiesAsync();
            Assert(result.Authorities.SequenceEqual(new[] { "203.0.113.9:50002" }), "活动服务配置优先，忽略 stale 和其他服务端口");
            FrpRuntimeAuthorityResolver.ActiveProbe = _ => Task.FromResult(new ProbeResult(true, true, new[] { active, stale }));
            Assert((await FrpRuntimeAuthorityResolver.ResolveDshWebAuthoritiesAsync()).Authorities.Count == 2, "多个活动入口必须保留歧义");
            File.WriteAllText(active, "serverAddr =");
            FrpRuntimeAuthorityResolver.ActiveProbe = _ => Task.FromResult(new ProbeResult(true, true, new[] { active }));
            Assert((await FrpRuntimeAuthorityResolver.ResolveDshWebAuthoritiesAsync()).Authorities.Count == 0, "活动配置暂坏不得回退旧 IP");
        }
        finally
        {
            FrpRuntimeAuthorityResolver.ActiveProbe = oldProbe;
            FrpRuntimeAuthorityResolver.StaticInstanceRootsResolver = oldRoots;
            FrpRuntimeAuthorityResolver.ActiveProbeEnabled = oldEnabled;
            TryDeleteDirectory(root);
        }
    }

    private static async Task Test4413BasePluginsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "helper-plugins-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = Path.Combine(root, "profiles", "web"); Directory.CreateDirectory(profile);
            var package = Path.Combine(profile, "package.json");
            var original = """{"name":"test-profile","private":true,"unknown":{"keep":1},"dsh":{"profile":{"bundles":[]}},"dependencies":{}}""";
            File.WriteAllText(package, original);
            var service = new DshBasePluginDeploymentService(root);
            Assert(service.BasePluginNames.Count == 3, "三项基础资源必须随程序集内置");
            var deployed = await service.EnsureDeployedAsync();
            Assert(deployed.Succeeded && deployed.Installed.Count == 3, "新设备部署基础插件：" + deployed.Message);
            var after = File.ReadAllText(package);
            Assert(JsonNode.Parse(after)?["unknown"]?["keep"]?.ToString() == "1", "保留未知字段");
            Assert(!after.Contains("dsh-open-web-access"), "新电脑不自动取消公网认证");
            var second = await service.EnsureDeployedAsync();
            Assert(second.Succeeded && second.Installed.Count == 0 && after == File.ReadAllText(package), "二次部署幂等");
            var fresh = Path.Combine(root, "rollback");
            var freshProfile = Path.Combine(fresh, "profiles", "web"); Directory.CreateDirectory(freshProfile);
            File.WriteAllText(Path.Combine(freshProfile, "package.json"), original);
            var failed = await new DshBasePluginDeploymentService(fresh, null, (_, _) => "injected failure").EnsureDeployedAsync();
            Assert(!failed.Succeeded && File.ReadAllText(Path.Combine(freshProfile, "package.json")) == original, "声明写失败必须回滚");
        }
        finally { TryDeleteDirectory(root); }
    }

    private static Task Test4413PresetCompatibilityAsync()
    {
        const string source = """
            - id: persona
              name: "@deepseek-ai/dsh-persona"
              config:
                prefix: |
                  This is the official standard persona with sufficient text.
                suffix: |
                  KEEP THE SUFFIX
            - id: other
              name: "@deepseek-ai/tool"
              config: {}
            """;
        var rewrite = HarnessContractPresetCompatibility.RewritePersonaPrefix(source, "contract instructions");
        Assert(rewrite.Supported && rewrite.Rewritten!.Contains("KEEP THE SUFFIX")
            && rewrite.Rewritten.Contains("@deepseek-ai/tool") && !rewrite.Rewritten.Contains("official standard persona"),
            "新版 prefix 仅替换 persona，保留 suffix 和其他插件：" + rewrite.Reason);
        var root = Path.Combine(Path.GetTempPath(), "helper-preset-root");
        Assert(HarnessContractPresetCompatibility.AnyReparsePointOnPath(root, root + "-outside/file"), "路径前缀相同不等于在根内");
        return Task.CompletedTask;
    }
}
