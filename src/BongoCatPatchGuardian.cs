using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal static class BongoCatPatchGuardian
{
    private static readonly string InstallRoot = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private static readonly string InstanceSuffix = Environment.GetEnvironmentVariable("BONGOCAT_PATCH_INSTANCE") ?? "";
    private static readonly string GuardianMutexName = "Local\\BongoCatPatchGuardian_SingleInstance" + InstanceSuffix;
    private static readonly string GuardianStopEventName = "Local\\BongoCatPatchGuardian_Stop" + InstanceSuffix;
    private static readonly string LogPath = Path.Combine(InstallRoot, "guardian.log");
    private static readonly string StatusPath = Path.Combine(InstallRoot, "status.txt");
    private static readonly string StatePath = Path.Combine(InstallRoot, "active-patch.txt");
    private static readonly string IncompatiblePath = Path.Combine(InstallRoot, "incompatible-build.txt");
    private static DateTime _lastTrayAttemptUtc = DateTime.MinValue;
    private static string _lastStatus = "";
    private static string GameRoot;
    private static string AssemblyPath;
    private static string ManagedPath;

    private sealed class PatchState
    {
        public string OriginalHash;
        public string PatchedHash;
        public string BackupPath;
    }

    private static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--stop", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using (var stop = EventWaitHandle.OpenExisting(GuardianStopEventName))
                    stop.Set();
                return 0;
            }
            catch { return 3; }
        }

        try
        {
            ConfigurePaths();
        }
        catch (Exception ex)
        {
            SetStatus("读取游戏位置失败：" + ex.Message);
            return 2;
        }

        bool once = args.Any(a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase));
        bool createdNew;
        using (var mutex = new Mutex(true, GuardianMutexName, out createdNew))
        {
            if (!createdNew)
                return 0;

            bool createdStop;
            using (var stop = new EventWaitHandle(false, EventResetMode.AutoReset, GuardianStopEventName, out createdStop))
            {
                Log("Guardian started" + (once ? " (one-shot)" : ""));
                do
                {
                    try
                    {
                        RepairIfSafe();
                        EnsureTrayForRunningGame();
                    }
                    catch (Exception ex)
                    {
                        SetStatus("守护程序异常：" + ex.Message);
                        Log(ex.ToString());
                    }
                    if (once)
                        break;
                }
                while (!stop.WaitOne(1000));
                Log("Guardian stopped");
            }
        }
        return 0;
    }

    private static void ConfigurePaths()
    {
        string configured = Environment.GetEnvironmentVariable("BONGOCAT_PATCH_GAME_ROOT");
        if (string.IsNullOrWhiteSpace(configured))
        {
            string pathFile = Path.Combine(InstallRoot, "game-path.txt");
            if (!File.Exists(pathFile))
                throw new FileNotFoundException("缺少 game-path.txt", pathFile);
            configured = File.ReadAllText(pathFile, Encoding.UTF8).Trim();
        }
        configured = configured.Trim().Trim('"');
        GameRoot = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar);
        ManagedPath = Path.Combine(GameRoot, @"BongoCat_Data\Managed");
        AssemblyPath = Path.Combine(ManagedPath, "Assembly-CSharp.dll");
        if (!File.Exists(Path.Combine(GameRoot, "BongoCat.exe")) || !File.Exists(AssemblyPath))
            throw new DirectoryNotFoundException("配置的目录不是有效的 Bongo Cat 安装目录：" + GameRoot);
    }

    private static void RepairIfSafe()
    {
        if (!File.Exists(AssemblyPath))
        {
            SetStatus("未找到游戏主文件：" + AssemblyPath);
            return;
        }

        string currentHash = Sha256(AssemblyPath);
        PatchState state = ReadState();
        if (state != null && EqualsHash(currentHash, state.PatchedHash))
        {
            if (!IsGameRunning())
                EnsurePayload();
            SetStatus("补丁正常，当前文件：" + currentHash);
            return;
        }

        if (IsGameRunning())
        {
            SetStatus("检测到补丁被覆盖；将在 Bongo Cat 退出后自动修复。当前文件：" + currentHash);
            return;
        }

        if (File.Exists(IncompatiblePath))
        {
            string failed = File.ReadAllText(IncompatiblePath, Encoding.UTF8);
            if (failed.StartsWith(currentHash, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("当前新构建与现有补丁结构不兼容，已保留官方文件，等待补丁更新。SHA256=" + currentHash);
                return;
            }
        }

        ApplyPatch(currentHash);
    }

    private static void ApplyPatch(string originalHash)
    {
        string patcher = Path.Combine(InstallRoot, "BongoCatAdaptivePatcher.exe");
        if (!File.Exists(patcher))
            throw new FileNotFoundException("缺少补丁程序", patcher);

        string backupDirectory = Path.Combine(
            InstallRoot,
            "backups",
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + originalHash.Substring(0, 12));
        Directory.CreateDirectory(backupDirectory);
        string backupPath = Path.Combine(backupDirectory, "Assembly-CSharp.dll");
        File.Copy(AssemblyPath, backupPath, false);
        if (!EqualsHash(Sha256(backupPath), originalHash))
            throw new IOException("更新前备份校验失败。");

        string temporaryOutput = Path.Combine(ManagedPath, "Assembly-CSharp.dll.guardian-new");
        string rollbackPath = Path.Combine(ManagedPath, "Assembly-CSharp.dll.guardian-rollback");
        DeleteExactFile(temporaryOutput);
        DeleteExactFile(rollbackPath);

        var start = new ProcessStartInfo
        {
            FileName = patcher,
            Arguments = Quote(AssemblyPath) + " " + Quote(temporaryOutput),
            WorkingDirectory = InstallRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        string stdout;
        string stderr;
        int exitCode;
        using (var process = Process.Start(start))
        {
            stdout = process.StandardOutput.ReadToEnd();
            stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30000))
            {
                process.Kill();
                throw new TimeoutException("补丁程序运行超过 30 秒。");
            }
            exitCode = process.ExitCode;
        }

        if (exitCode != 0 || !File.Exists(temporaryOutput))
        {
            DeleteExactFile(temporaryOutput);
            string failure = originalHash + Environment.NewLine + DateTime.Now.ToString("O") + Environment.NewLine +
                             "ExitCode=" + exitCode + Environment.NewLine + stdout + Environment.NewLine + stderr;
            File.WriteAllText(IncompatiblePath, failure, new UTF8Encoding(false));
            SetStatus("新构建不兼容，未修改官方文件；原文件已备份。SHA256=" + originalHash);
            Log("Patch refused for " + originalHash + Environment.NewLine + stdout + Environment.NewLine + stderr);
            return;
        }

        string patchedHash = Sha256(temporaryOutput);
        if (EqualsHash(patchedHash, originalHash))
            throw new InvalidOperationException("补丁输出与输入相同，拒绝替换。");
        if (!EqualsHash(Sha256(AssemblyPath), originalHash))
        {
            DeleteExactFile(temporaryOutput);
            SetStatus("Steam 在补丁生成期间又更新了文件，本轮未替换，将自动重试。");
            return;
        }

        File.Replace(temporaryOutput, AssemblyPath, rollbackPath, true);
        if (!EqualsHash(Sha256(AssemblyPath), patchedHash))
        {
            if (File.Exists(rollbackPath))
                File.Replace(rollbackPath, AssemblyPath, null, true);
            throw new IOException("安装后哈希校验失败，已尝试回滚。");
        }
        DeleteExactFile(rollbackPath);
        DeleteExactFile(IncompatiblePath);
        WriteState(originalHash, patchedHash, backupPath);
        EnsurePayload();
        SetStatus("已自动恢复补丁。原版=" + originalHash + "，补丁版=" + patchedHash);
        Log("Patch installed. Original=" + originalHash + " Patched=" + patchedHash + " Backup=" + backupPath);
    }

    private static void EnsurePayload()
    {
        CopyVerified(
            Path.Combine(InstallRoot, @"payload\BongoCatChatLogger.dll"),
            Path.Combine(ManagedPath, "BongoCatChatLogger.dll"));
        CopyVerified(
            Path.Combine(InstallRoot, @"payload\BongoCatTray.exe"),
            Path.Combine(GameRoot, "BongoCatTray.exe"));
    }

    private static void CopyVerified(string source, string destination)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException("缺少守护程序组件", source);
        string sourceHash = Sha256(source);
        if (File.Exists(destination) && EqualsHash(Sha256(destination), sourceHash))
            return;
        File.Copy(source, destination, true);
        if (!EqualsHash(Sha256(destination), sourceHash))
            throw new IOException("组件复制校验失败：" + destination);
        Log("Restored component: " + destination);
    }

    private static void EnsureTrayForRunningGame()
    {
        if (!IsGameRunning() || Process.GetProcessesByName("BongoCatTray").Any(p => !p.HasExited))
            return;
        if ((DateTime.UtcNow - _lastTrayAttemptUtc).TotalSeconds < 5)
            return;
        _lastTrayAttemptUtc = DateTime.UtcNow;
        string tray = Path.Combine(GameRoot, "BongoCatTray.exe");
        if (!File.Exists(tray))
            return;
        Process.Start(new ProcessStartInfo { FileName = tray, WorkingDirectory = GameRoot, UseShellExecute = true });
        Log("Started tray helper for running game.");
    }

    private static bool IsGameRunning()
    {
        try { return Process.GetProcessesByName("BongoCat").Any(p => !p.HasExited); }
        catch { return false; }
    }

    private static PatchState ReadState()
    {
        if (!File.Exists(StatePath))
            return null;
        var result = new PatchState();
        foreach (string line in File.ReadAllLines(StatePath, Encoding.UTF8))
        {
            int equals = line.IndexOf('=');
            if (equals <= 0) continue;
            string key = line.Substring(0, equals);
            string value = line.Substring(equals + 1).Trim();
            if (key == "OriginalHash") result.OriginalHash = value;
            if (key == "PatchedHash") result.PatchedHash = value;
            if (key == "BackupPath") result.BackupPath = value;
        }
        return string.IsNullOrEmpty(result.PatchedHash) ? null : result;
    }

    private static void WriteState(string originalHash, string patchedHash, string backupPath)
    {
        string text = "OriginalHash=" + originalHash + Environment.NewLine +
                      "PatchedHash=" + patchedHash + Environment.NewLine +
                      "BackupPath=" + backupPath + Environment.NewLine +
                      "Updated=" + DateTime.Now.ToString("O") + Environment.NewLine;
        File.WriteAllText(StatePath, text, new UTF8Encoding(false));
    }

    private static string Sha256(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }

    private static bool EqualsHash(string left, string right)
    {
        return !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) &&
               string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static void DeleteExactFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void SetStatus(string message)
    {
        if (message == _lastStatus)
            return;
        _lastStatus = message;
        File.WriteAllText(StatusPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + message, new UTF8Encoding(false));
        Log(message);
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(InstallRoot);
            File.AppendAllText(LogPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }
}
