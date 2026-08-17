using System.Collections.Concurrent;

namespace CodexHelper.Core.Infrastructure;

public static class AtomicFile
{
    // 同一进程内可能由任务主线程与事件监听线程同时写同一状态文件。每个目标路径
    // 独占一次“写临时文件 → Replace/Move”事务，避免两个临时文件交叉替换而把
    // 正常的实时状态更新误报为 IO 失败。跨进程仍由任务租约与文件系统原子操作保护。
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> content)
    {
        var fullPath = Path.GetFullPath(path);
        var gate = Gates.GetOrAdd(fullPath, static _ => new object());
        lock (gate)
        {
            WriteAllBytesCore(fullPath, content);
        }
    }

    private static void WriteAllBytesCore(string fullPath, ReadOnlySpan<byte> content)
    {
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("目标文件没有父目录。");
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                var backup = temporary + ".bak";
                File.Replace(temporary, fullPath, backup, ignoreMetadataErrors: true);
                File.Delete(backup);
            }
            else
            {
                File.Move(temporary, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static void WriteAllText(string path, string content) =>
        WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(content));
}
