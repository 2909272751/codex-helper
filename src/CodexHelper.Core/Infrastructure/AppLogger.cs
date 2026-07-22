using System.Text;
using System.Text.RegularExpressions;

namespace CodexHelper.Core.Infrastructure;

public sealed class AppLogger
{
    private readonly AppPaths paths;

    public AppLogger(AppPaths paths)
    {
        this.paths = paths;
        paths.EnsureCreated();
    }

    public string WriteError(string operation, Exception exception)
    {
        var id = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")[..8];
        var path = Path.Combine(paths.LogsDirectory, DateTime.UtcNow.ToString("yyyy-MM") + ".log");
        var message = Redact(exception.ToString());
        var block = $"[{DateTime.UtcNow:O}] ERROR {id} {Redact(operation)}{Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}";
        File.AppendAllText(path, block, new UTF8Encoding(false));
        return id;
    }

    private static string Redact(string value)
    {
        var result = Regex.Replace(value, @"(?i)(authorization\s*[:=]\s*bearer\s+)[^\s,;]+", "$1[REDACTED]");
        result = Regex.Replace(result, @"(?i)((?:api[_-]?key|access[_-]?token|refresh[_-]?token)\s*[:=]\s*[\""']?)[^\s,;\""']+", "$1[REDACTED]");
        result = Regex.Replace(result, @"\bsk-[A-Za-z0-9_-]{8,}\b", "[REDACTED]");
        return result;
    }
}
