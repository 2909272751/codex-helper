using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Core.Services;

/// <summary>
/// Keeps persisted Codex conversation metadata aligned with the active model
/// provider. Every database and JSONL file is backed up before mutation and can
/// be rolled back if the surrounding configuration switch fails.
/// </summary>
internal sealed class CodexConversationSynchronizer : IDisposable
{
    private readonly string codexRoot;
    private readonly string transactionDirectory;
    private readonly List<DatabaseBackup> databases = new();
    private readonly List<SessionBackup> sessions = new();
    private bool completed;
    private bool rolledBack;

    private CodexConversationSynchronizer(string root, string recoveryRoot)
    {
        codexRoot = Path.GetFullPath(root);
        transactionDirectory = Path.Combine(recoveryRoot, "conversation-switches", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"));
    }

    public static CodexConversationSynchronizer BeginAndApply(string root, string recoveryRoot, string targetProvider)
    {
        if (targetProvider is not ("openai" or "custom" or "sub2api")) throw new InvalidOperationException("不支持的会话 provider：" + targetProvider);
        var transaction = new CodexConversationSynchronizer(root, recoveryRoot);
        try
        {
            transaction.Apply(targetProvider);
            return transaction;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void Complete() => completed = true;

    public void Rollback()
    {
        if (rolledBack) return;
        var errors = new List<Exception>();
        foreach (var database in databases.AsEnumerable().Reverse())
        {
            try { File.Copy(database.BackupPath, database.Path, overwrite: true); }
            catch (Exception ex) { errors.Add(ex); }
        }
        foreach (var session in sessions.AsEnumerable().Reverse())
        {
            try { File.Copy(session.BackupPath, session.Path, overwrite: true); }
            catch (Exception ex) { errors.Add(ex); }
        }
        rolledBack = true;
        if (errors.Count > 0) throw new AggregateException("会话 provider 回滚未完全成功，请保留恢复目录并手动检查。", errors);
    }

    public void Dispose()
    {
        if (!completed && !rolledBack) Rollback();
    }

    private void Apply(string provider)
    {
        var statePaths = new[]
        {
            Path.Combine(codexRoot, "sqlite", "state_5.sqlite"),
            Path.Combine(codexRoot, "state_5.sqlite")
        }.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (statePaths.Count == 0) return;

        Directory.CreateDirectory(transactionDirectory);
        var rolloutPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < statePaths.Count; index++)
        {
            var path = statePaths[index];
            try { using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read); }
            catch (IOException ex) { throw new InvalidOperationException("Codex 状态数据库仍被占用：" + path, ex); }
            var backup = Path.Combine(transactionDirectory, $"state-{index}.sqlite.bak");
            NativeSqlite.Backup(path, backup);
            databases.Add(new DatabaseBackup(path, backup));
            using var database = NativeSqlite.Open(path);
            EnsureThreadColumns(database);
            foreach (var rolloutPath in database.QueryTextColumn("select rollout_path from threads where first_user_message <> '' and source in ('vscode','cli') order by rollout_path"))
                if (!string.IsNullOrWhiteSpace(rolloutPath)) rolloutPaths.Add(RemoveExtendedPrefix(rolloutPath));
        }

        var sessionIndex = 0;
        foreach (var rolloutPath in rolloutPaths)
        {
            var fullPath = Path.GetFullPath(rolloutPath);
            if (!PathSafety.IsWithin(fullPath, codexRoot)) throw new InvalidOperationException("会话文件位于 Codex 根目录之外：" + fullPath);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("会话 JSONL 不存在。", fullPath);
            if (!TryCreateUpdatedFirstLine(fullPath, provider, out var updated)) continue;
            try
            {
                var backup = Path.Combine(transactionDirectory, $"session-{sessionIndex++}.jsonl.bak");
                File.Copy(fullPath, backup, overwrite: false);
                sessions.Add(new SessionBackup(fullPath, backup));
                AtomicFile.WriteAllBytes(fullPath, updated);
            }
            finally { CryptographicOperations.ZeroMemory(updated); }
        }

        var escaped = provider.Replace("'", "''", StringComparison.Ordinal);
        foreach (var item in databases)
        {
            using var database = NativeSqlite.Open(item.Path);
            database.Execute("begin immediate");
            try
            {
                database.Execute("update threads set model_provider = '" + escaped + "', has_user_event = 1 where first_user_message <> '' and source in ('vscode','cli')");
                var remaining = database.ScalarInt("select count(*) from threads where first_user_message <> '' and source in ('vscode','cli') and model_provider <> '" + escaped + "'");
                if (remaining != 0) throw new InvalidOperationException($"状态数据库仍有 {remaining} 条会话未同步 provider。");
                var integrity = database.ScalarText("pragma integrity_check");
                if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SQLite 完整性检查失败：" + integrity);
                database.Execute("commit");
            }
            catch
            {
                try { database.Execute("rollback"); } catch { }
                throw;
            }
        }
    }

    private static bool TryCreateUpdatedFirstLine(string path, string provider, out byte[] updated)
    {
        var original = File.ReadAllBytes(path);
        try
        {
            var newline = Array.IndexOf(original, (byte)'\n');
            var firstLineLength = newline >= 0 ? newline : original.Length;
            if (firstLineLength > 0 && original[firstLineLength - 1] == (byte)'\r') firstLineLength--;
            var firstLine = Encoding.UTF8.GetString(original, 0, firstLineLength);
            var document = JsonNode.Parse(firstLine) as JsonObject ?? throw new InvalidDataException("会话 JSONL 首行不是 JSON 对象：" + path);
            var payload = document["payload"] as JsonObject ?? throw new InvalidDataException("会话 JSONL 首行缺少 payload：" + path);
            if (string.Equals(payload["model_provider"]?.GetValue<string>(), provider, StringComparison.Ordinal))
            {
                updated = Array.Empty<byte>();
                return false;
            }
            payload["model_provider"] = provider;
            var replacement = Encoding.UTF8.GetBytes(document.ToJsonString());
            try
            {
                var remainderOffset = newline >= 0 ? newline : original.Length;
                updated = new byte[replacement.Length + original.Length - remainderOffset];
                replacement.CopyTo(updated, 0);
                original.AsSpan(remainderOffset).CopyTo(updated.AsSpan(replacement.Length));
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(replacement); }
        }
        finally { CryptographicOperations.ZeroMemory(original); }
    }

    private static void EnsureThreadColumns(NativeSqlite database)
    {
        foreach (var column in new[] { "source", "first_user_message", "has_user_event", "model_provider", "rollout_path" })
        {
            if (database.ScalarInt("select count(*) from pragma_table_info('threads') where name = '" + column + "'") == 0)
                throw new InvalidDataException("threads 表缺少必要字段：" + column);
        }
    }

    private static string RemoveExtendedPrefix(string path) => path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path[4..] : path;
    private sealed record DatabaseBackup(string Path, string BackupPath);
    private sealed record SessionBackup(string Path, string BackupPath);

    private sealed class NativeSqlite : IDisposable
    {
        private const int Ok = 0;
        private const int Row = 100;
        private const int Done = 101;
        private IntPtr handle;

        private NativeSqlite(IntPtr value) => handle = value;

        public static NativeSqlite Open(string path)
        {
            var result = sqlite3_open16(path, out var database);
            if (result != Ok)
            {
                var message = GetError(database, result);
                if (database != IntPtr.Zero) sqlite3_close(database);
                throw new InvalidOperationException("无法打开 SQLite 数据库：" + message);
            }
            sqlite3_busy_timeout(database, 30_000);
            return new NativeSqlite(database);
        }

        public static void Backup(string sourcePath, string destinationPath)
        {
            using var source = Open(sourcePath);
            using var destination = Open(destinationPath);
            var backup = sqlite3_backup_init(destination.handle, "main", source.handle, "main");
            if (backup == IntPtr.Zero) throw new InvalidOperationException("无法初始化 SQLite 备份：" + GetError(destination.handle, sqlite3_errcode(destination.handle)));
            int step;
            try { step = sqlite3_backup_step(backup, -1); }
            finally
            {
                var finish = sqlite3_backup_finish(backup);
                if (finish != Ok) throw new InvalidOperationException("无法完成 SQLite 备份：" + GetError(destination.handle, finish));
            }
            if (step != Done) throw new InvalidOperationException("SQLite 备份失败：" + GetError(destination.handle, step));
        }

        public void Execute(string sql)
        {
            var result = sqlite3_exec(handle, sql, IntPtr.Zero, IntPtr.Zero, out var error);
            if (result == Ok) return;
            var message = error == IntPtr.Zero ? GetError(handle, result) : Marshal.PtrToStringAnsi(error) ?? "未知错误";
            if (error != IntPtr.Zero) sqlite3_free(error);
            throw new InvalidOperationException("SQLite 命令失败：" + message);
        }

        public int ScalarInt(string sql)
        {
            var statement = Prepare(sql);
            try
            {
                var result = sqlite3_step(statement);
                if (result != Row) throw new InvalidOperationException("SQLite 查询未返回数据：" + GetError(handle, result));
                return sqlite3_column_int(statement, 0);
            }
            finally { sqlite3_finalize(statement); }
        }

        public string ScalarText(string sql)
        {
            var statement = Prepare(sql);
            try
            {
                var result = sqlite3_step(statement);
                if (result != Row) throw new InvalidOperationException("SQLite 查询未返回数据：" + GetError(handle, result));
                var value = sqlite3_column_text16(statement, 0);
                return value == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(value) ?? string.Empty;
            }
            finally { sqlite3_finalize(statement); }
        }

        public List<string> QueryTextColumn(string sql)
        {
            var statement = Prepare(sql);
            var values = new List<string>();
            try
            {
                while (true)
                {
                    var result = sqlite3_step(statement);
                    if (result == Done) return values;
                    if (result != Row) throw new InvalidOperationException("SQLite 查询失败：" + GetError(handle, result));
                    var value = sqlite3_column_text16(statement, 0);
                    values.Add(value == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(value) ?? string.Empty);
                }
            }
            finally { sqlite3_finalize(statement); }
        }

        private IntPtr Prepare(string sql)
        {
            var result = sqlite3_prepare16_v2(handle, sql, -1, out var statement, IntPtr.Zero);
            if (result != Ok) throw new InvalidOperationException("无法准备 SQLite 查询：" + GetError(handle, result));
            return statement;
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            sqlite3_close(handle);
            handle = IntPtr.Zero;
        }

        private static string GetError(IntPtr database, int code)
        {
            if (database == IntPtr.Zero) return "SQLite error " + code;
            var pointer = sqlite3_errmsg16(database);
            return pointer == IntPtr.Zero ? "SQLite error " + code : Marshal.PtrToStringUni(pointer) ?? "SQLite error " + code;
        }

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open16([MarshalAs(UnmanagedType.LPWStr)] string filename, out IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private static extern int sqlite3_exec(IntPtr database, string sql, IntPtr callback, IntPtr callbackArgument, out IntPtr errorMessage);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_free(IntPtr pointer);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare16_v2(IntPtr database, [MarshalAs(UnmanagedType.LPWStr)] string sql, int byteCount, out IntPtr statement, IntPtr remainingSql);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_int(IntPtr statement, int columnIndex);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text16(IntPtr statement, int columnIndex);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg16(IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_errcode(IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private static extern IntPtr sqlite3_backup_init(IntPtr destinationDatabase, string destinationName, IntPtr sourceDatabase, string sourceName);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_backup_step(IntPtr backup, int pageCount);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_backup_finish(IntPtr backup);
    }
}
