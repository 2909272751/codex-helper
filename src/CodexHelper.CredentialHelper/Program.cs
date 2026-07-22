using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Services;

namespace CodexHelper.CredentialHelper;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = Parse(args);
            var root = Require(options, "--root");
            var profile = Require(options, "--profile");
            var service = new ApiProviderService(root, new AppPaths(), new CodexProcessService());
            Console.Out.Write(service.EmitSecret(profile));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) continue;
            if (index + 1 >= args.Length) throw new InvalidOperationException("参数缺少值：" + args[index]);
            result[args[index]] = args[++index];
        }
        return result;
    }

    private static string Require(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException("缺少参数：" + key);
}

