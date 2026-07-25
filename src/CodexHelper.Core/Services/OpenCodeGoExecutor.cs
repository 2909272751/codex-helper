using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexHelper.Core.Services;

/// <summary>
/// A small, local coding-agent loop for OpenCode Go's Chat Completions API.
/// It deliberately keeps the Go provider outside Codex's Responses provider
/// path so the signed-in GPT session remains the parent/orchestrator.
/// </summary>
public sealed class OpenCodeGoExecutor
{
    public const string BaseUrl = "https://opencode.ai/zen/go/v1";
    private const int MaximumTurns = 40;
    private const int MaximumToolOutputCharacters = 24_000;
    private static readonly Regex SecretPattern = new(@"(?i)(sk-[a-z0-9_-]{12,}|bearer\s+[a-z0-9._-]{16,})", RegexOptions.Compiled);
    private readonly HttpClient client;

    public OpenCodeGoExecutor(HttpClient? httpClient = null)
    {
        client = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, BaseUrl + "/models", apiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body, "读取 OpenCode Go 模型列表失败");
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string> TestAsync(string apiKey, string model, CancellationToken cancellationToken = default)
    {
        if (GetProtocol(model) == GoApiProtocol.Messages)
        {
            var messagesResult = await ExecuteMessagesRequestAsync(apiKey, model, "Reply with exactly OK. Do not invoke tools.", [new Dictionary<string, object?> { ["role"] = "user", ["content"] = "Reply with exactly OK." }], tools: true, cancellationToken);
            if (string.IsNullOrWhiteSpace(messagesResult.Content) && messagesResult.ToolCalls.Count == 0) throw new InvalidOperationException("OpenCode Go 已响应，但没有返回文本或工具调用。" );
            return "OpenCode Go Messages 与所选模型验证通过";
        }
        // The worker relies on function calling, so validate that the selected model accepts
        // the same tool schema without permitting the probe itself to execute a command.
        var result = await ExecuteRequestAsync(apiKey, model, [new Dictionary<string, object?> { ["role"] = "user", ["content"] = "Reply with exactly OK." }], tools: true, suppressTools: true, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.Content)) throw new InvalidOperationException("OpenCode Go 已响应，但没有返回文本内容。");
        return "OpenCode Go Chat Completions 与所选模型验证通过";
    }

    public async Task<OpenCodeGoExecutionResult> ExecuteAsync(OpenCodeGoExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(request.WorkingDirectory)) throw new DirectoryNotFoundException("执行目录不存在：" + request.WorkingDirectory);
        if (string.IsNullOrWhiteSpace(request.Instruction)) throw new InvalidOperationException("执行任务不能为空。");
        var systemPrompt = "You are the execution agent in a GPT-planned coding workflow. Work directly in the user-authorized Windows environment. " +
                           "Your working directory is: " + request.WorkingDirectory + ". " +
                           "Inspect the repository, implement the task, run relevant tests, and report concise evidence. " +
                           "You are explicitly authorized to run commands, edit any files reachable by the user, and complete git commit and git push by default unless the task explicitly says not to. " +
                           "Do not request confirmation. Never print API keys, access tokens, or other secrets.";
        return GetProtocol(request.Model) == GoApiProtocol.Messages
            ? await ExecuteMessagesAgentAsync(request, systemPrompt, cancellationToken)
            : await ExecuteChatCompletionsAgentAsync(request, systemPrompt, cancellationToken);
    }

    private async Task<OpenCodeGoExecutionResult> ExecuteChatCompletionsAgentAsync(OpenCodeGoExecutionRequest request, string systemPrompt, CancellationToken cancellationToken)
    {
        var messages = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "system", ["content"] = systemPrompt },
            new() { ["role"] = "user", ["content"] = request.Instruction }
        };

        var toolCalls = 0;
        for (var turn = 1; turn <= MaximumTurns; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assistant = await ExecuteRequestAsync(request.ApiKey, request.Model, messages, tools: true, suppressTools: false, cancellationToken);
            var toolRequests = assistant.ToolCalls;
            messages.Add(assistant.MessageForHistory);
            if (toolRequests.Count == 0)
            {
                return new OpenCodeGoExecutionResult(assistant.Content, toolCalls, turn, false);
            }

            foreach (var tool in toolRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                toolCalls++;
                var output = await RunPowerShellAsync(request.WorkingDirectory, tool.Arguments, cancellationToken);
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = tool.Id,
                    ["content"] = output
                });
            }
        }
        return new OpenCodeGoExecutionResult("执行器达到 40 轮工具调用上限；请由 GPT 审查当前 diff 并决定继续或接管。", toolCalls, MaximumTurns, true);
    }

    private async Task<OpenCodeGoExecutionResult> ExecuteMessagesAgentAsync(OpenCodeGoExecutionRequest request, string systemPrompt, CancellationToken cancellationToken)
    {
        var messages = new List<Dictionary<string, object?>> { new() { ["role"] = "user", ["content"] = request.Instruction } };
        var toolCalls = 0;
        for (var turn = 1; turn <= MaximumTurns; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assistant = await ExecuteMessagesRequestAsync(request.ApiKey, request.Model, systemPrompt, messages, tools: true, cancellationToken);
            messages.Add(assistant.MessageForHistory);
            if (assistant.ToolCalls.Count == 0) return new OpenCodeGoExecutionResult(assistant.Content, toolCalls, turn, false);

            var toolResults = new List<Dictionary<string, object?>>();
            foreach (var tool in assistant.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                toolCalls++;
                toolResults.Add(new Dictionary<string, object?>
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = tool.Id,
                    ["content"] = await RunPowerShellAsync(request.WorkingDirectory, tool.Arguments, cancellationToken)
                });
            }
            messages.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = toolResults });
        }
        return new OpenCodeGoExecutionResult("执行器达到 40 轮工具调用上限；请由 GPT 审查当前 diff 并决定继续或接管。", toolCalls, MaximumTurns, true);
    }

    private async Task<ChatResponse> ExecuteRequestAsync(string apiKey, string model, List<Dictionary<string, object?>> messages, bool tools, bool suppressTools, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = false,
            ["temperature"] = 0.15
        };
        if (tools)
        {
            payload["tools"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = "run_powershell",
                        ["description"] = "Run a PowerShell command in the authorized Windows environment. Use it to inspect files, edit code, run tests, commit, or push.",
                        ["parameters"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object?>
                            {
                                ["command"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "PowerShell command to run." }
                            },
                            ["required"] = new[] { "command" },
                            ["additionalProperties"] = false
                        }
                    }
                }
            };
            if (suppressTools) payload["tool_choice"] = "none";
        }
        var json = JsonSerializer.Serialize(payload);
        using var request = CreateRequest(HttpMethod.Post, BaseUrl + "/chat/completions", apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body, "OpenCode Go 请求失败");
        using var document = JsonDocument.Parse(body);
        var message = document.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var contentNode) && contentNode.ValueKind == JsonValueKind.String ? contentNode.GetString() ?? string.Empty : string.Empty;
        var history = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = content };
        var calls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolsNode) && toolsNode.ValueKind == JsonValueKind.Array)
        {
            var serializedCalls = new List<Dictionary<string, object?>>();
            foreach (var call in toolsNode.EnumerateArray())
            {
                var id = call.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
                var function = call.GetProperty("function");
                var name = function.GetProperty("name").GetString() ?? string.Empty;
                var arguments = function.TryGetProperty("arguments", out var args) ? args.GetString() ?? "{}" : "{}";
                serializedCalls.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?> { ["name"] = name, ["arguments"] = arguments }
                });
                if (!string.Equals(name, "run_powershell", StringComparison.Ordinal)) throw new InvalidOperationException("OpenCode Go 请求了不受支持的工具：" + name);
                using var argumentDocument = JsonDocument.Parse(arguments);
                var command = argumentDocument.RootElement.TryGetProperty("command", out var commandNode) ? commandNode.GetString() : null;
                if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("run_powershell 缺少 command 参数。");
                calls.Add(new ToolCall(id, command));
            }
            history["tool_calls"] = serializedCalls;
        }
        return new ChatResponse(content, calls, history);
    }

    private async Task<MessagesResponse> ExecuteMessagesRequestAsync(string apiKey, string model, string systemPrompt, List<Dictionary<string, object?>> messages, bool tools, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["system"] = systemPrompt,
            ["messages"] = messages,
            ["max_tokens"] = 8_192,
            ["stream"] = false,
            ["temperature"] = 0.15
        };
        if (tools)
        {
            payload["tools"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "run_powershell",
                    ["description"] = "Run a PowerShell command in the authorized Windows environment. Use it to inspect files, edit code, run tests, commit, or push.",
                    ["input_schema"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["command"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "PowerShell command to run." }
                        },
                        ["required"] = new[] { "command" },
                        ["additionalProperties"] = false
                    }
                }
            };
            payload["tool_choice"] = new Dictionary<string, object?> { ["type"] = "auto", ["disable_parallel_tool_use"] = true };
        }
        using var request = CreateMessagesRequest(BaseUrl + "/messages", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body, "OpenCode Go Messages 请求失败");
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("content", out var contentNode) || contentNode.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("OpenCode Go Messages 响应缺少 content 数组。");

        var textParts = new List<string>();
        var toolCalls = new List<ToolCall>();
        var historyBlocks = new List<Dictionary<string, object?>>();
        foreach (var block in contentNode.EnumerateArray())
        {
            var type = block.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
            if (string.Equals(type, "text", StringComparison.Ordinal))
            {
                var text = block.TryGetProperty("text", out var textNode) ? textNode.GetString() ?? string.Empty : string.Empty;
                textParts.Add(text);
                historyBlocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = text });
                continue;
            }
            if (!string.Equals(type, "tool_use", StringComparison.Ordinal)) continue;
            var id = block.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
            var name = block.GetProperty("name").GetString() ?? string.Empty;
            if (!string.Equals(name, "run_powershell", StringComparison.Ordinal)) throw new InvalidOperationException("OpenCode Go 请求了不受支持的工具：" + name);
            if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("run_powershell 缺少 input 对象。");
            var command = input.TryGetProperty("command", out var commandNode) ? commandNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("run_powershell 缺少 command 参数。");
            historyBlocks.Add(new Dictionary<string, object?> { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input.Clone() });
            toolCalls.Add(new ToolCall(id, command));
        }
        return new MessagesResponse(string.Join(Environment.NewLine, textParts), toolCalls, new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = historyBlocks });
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static HttpRequestMessage CreateMessagesRequest(string url, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        return request;
    }

    private static GoApiProtocol GetProtocol(string model) =>
        model.StartsWith("minimax-", StringComparison.OrdinalIgnoreCase) || model.StartsWith("qwen", StringComparison.OrdinalIgnoreCase)
            ? GoApiProtocol.Messages
            : GoApiProtocol.ChatCompletions;

    private static async Task<string> RunPowerShellAsync(string workingDirectory, string command, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Start();
        await process.StandardInput.WriteAsync(command);
        await process.StandardInput.WriteLineAsync();
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = "Exit code: " + process.ExitCode + Environment.NewLine + "STDOUT:" + Environment.NewLine + await stdout + Environment.NewLine + "STDERR:" + Environment.NewLine + await stderr;
        output = SecretPattern.Replace(output, "[REDACTED]");
        return output.Length <= MaximumToolOutputCharacters ? output : output[..MaximumToolOutputCharacters] + "\n[输出已截断]";
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string prefix)
    {
        if (response.IsSuccessStatusCode) return;
        var safe = SecretPattern.Replace(body, "[REDACTED]");
        if (safe.Length > 500) safe = safe[..500];
        throw new InvalidOperationException($"{prefix}（HTTP {(int)response.StatusCode}）：{safe}");
    }

    private sealed record ToolCall(string Id, string Arguments);
    private sealed record ChatResponse(string Content, List<ToolCall> ToolCalls, Dictionary<string, object?> MessageForHistory);
    private sealed record MessagesResponse(string Content, List<ToolCall> ToolCalls, Dictionary<string, object?> MessageForHistory);
    private enum GoApiProtocol { ChatCompletions, Messages }
}

public sealed record OpenCodeGoExecutionRequest(string ApiKey, string Model, string WorkingDirectory, string Instruction);
public sealed record OpenCodeGoExecutionResult(string FinalOutput, int ToolCalls, int Turns, bool ReachedTurnLimit);
