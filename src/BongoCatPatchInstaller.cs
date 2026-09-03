using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class BongoCatPatchInstaller
{
    private const string RunValueName = "BongoCatPatchGuardian";
    private static readonly string PackageRoot = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    private static readonly string ComponentRoot = Path.Combine(PackageRoot, "components");
    private static readonly bool TestMode = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BONGOCAT_PATCH_TEST_GAME_ROOT"));
    private static readonly string InstallRoot = GetInstallRoot();

    private sealed class PatchState
    {
        public string OriginalHash;
        public string PatchedHash;
        public string BackupPath;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        bool silent = args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));
        bool restore = args.Any(a => string.Equals(a, "--restore", StringComparison.OrdinalIgnoreCase)) ||
                       Path.GetFileNameWithoutExtension(Application.ExecutablePath).Contains("恢复原版");
        try
        {
            string result = restore ? RestoreOriginal(silent) : Install(silent);
            if (!silent)
                MessageBox.Show(result, restore ? "Bongo Cat 恢复完成" : "Bongo Cat 补丁安装完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex)
        {
            string message = ex.Message;
            try
            {
                string errorPath = Path.Combine(Path.GetTempPath(), "BongoCatPatchInstaller-error.txt");
                File.WriteAllText(errorPath, ex.ToString(), new UTF8Encoding(false));
                message += Environment.NewLine + Environment.NewLine + "详细日志：" + errorPath;
            }
            catch { }
            if (!silent)
                MessageBox.Show(message, "Bongo Cat 补丁安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string Install(bool silent)
    {
        string gameRoot = LocateGame(silent);
        string managed = Path.Combine(gameRoot, @"BongoCat_Data\Managed");
        string active = Path.Combine(managed, "Assembly-CSharp.dll");
        EnsureGameStopped(silent);
        VerifyPackageComponents();

        Directory.CreateDirectory(InstallRoot);
        Directory.CreateDirectory(Path.Combine(InstallRoot, "payload"));
        Directory.CreateDirectory(Path.Combine(InstallRoot, "backups"));
        StopInstalledGuardian();

        CopyVerified(Path.Combine(ComponentRoot, "BongoCatAdaptivePatcher.exe"), Path.Combine(InstallRoot, "BongoCatAdaptivePatcher.exe"));
        CopyVerified(Path.Combine(ComponentRoot, "Mono.Cecil.dll"), Path.Combine(InstallRoot, "Mono.Cecil.dll"));
        CopyVerified(Path.Combine(ComponentRoot, "BongoCatPatchGuardian.exe"), Path.Combine(InstallRoot, "BongoCatPatchGuardian.exe"));
        CopyVerified(Path.Combine(ComponentRoot, "BongoCatChatLogger.dll"), Path.Combine(InstallRoot, @"payload\BongoCatChatLogger.dll"));
        CopyVerified(Path.Combine(ComponentRoot, "BongoCatTray.exe"), Path.Combine(InstallRoot, @"payload\BongoCatTray.exe"));
        File.WriteAllText(Path.Combine(InstallRoot, "game-path.txt"), gameRoot, new UTF8Encoding(false));

        string currentHash = Sha256(active);
        PatchState previous = ReadState(Path.Combine(InstallRoot, "active-patch.txt"));
        string patchedHash;
        string backupPath;
        if (previous != null && EqualsHash(currentHash, previous.PatchedHash))
        {
            patchedHash = currentHash;
            backupPath = previous.BackupPath;
        }
        else
        {
            string backupDirectory = Path.Combine(InstallRoot, "backups",
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + currentHash.Substring(0, 12));
            if (Directory.Exists(backupDirectory))
                backupDirectory += "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            Directory.CreateDirectory(backupDirectory);
            backupPath = Path.Combine(backupDirectory, "Assembly-CSharp.dll");
            File.Copy(active, backupPath, false);
            if (!EqualsHash(Sha256(backupPath), currentHash))
                throw new IOException("安装前的原版备份校验失败，未修改游戏。");

            string output = Path.Combine(managed, "Assembly-CSharp.dll.installer-new");
            string rollback = Path.Combine(managed, "Assembly-CSharp.dll.installer-rollback");
            DeleteExactFile(output);
            DeleteExactFile(rollback);
            RunPatcher(active, output);
            patchedHash = Sha256(output);
            if (EqualsHash(currentHash, patchedHash))
                throw new InvalidOperationException("补丁输出与原文件相同，拒绝安装。");
            if (!EqualsHash(Sha256(active), currentHash))
            {
                DeleteExactFile(output);
                throw new IOException("Steam 在安装期间更新了游戏文件，请重新运行安装器。");
            }

            File.Replace(output, active, rollback, true);
            if (!EqualsHash(Sha256(active), patchedHash))
            {
                if (File.Exists(rollback))
                    File.Replace(rollback, active, null, true);
                throw new IOException("安装后校验失败，已尝试恢复原版。");
            }
            DeleteExactFile(rollback);
            WriteState(Path.Combine(InstallRoot, "active-patch.txt"), currentHash, patchedHash, backupPath);
        }

        CopyVerified(Path.Combine(InstallRoot, @"payload\BongoCatChatLogger.dll"), Path.Combine(managed, "BongoCatChatLogger.dll"));
        CopyVerified(Path.Combine(InstallRoot, @"payload\BongoCatTray.exe"), Path.Combine(gameRoot, "BongoCatTray.exe"));
        if (!TestMode)
        {
            using (RegistryKey run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                run.SetValue(RunValueName, Quote(Path.Combine(InstallRoot, "BongoCatPatchGuardian.exe")), RegistryValueKind.String);
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(InstallRoot, "BongoCatPatchGuardian.exe"),
                WorkingDirectory = InstallRoot,
                UseShellExecute = true
            });
        }
        else
        {
            RunGuardianOnce();
        }

        return "安装成功。" + Environment.NewLine + Environment.NewLine +
               "游戏目录：" + gameRoot + Environment.NewLine +
               "原版备份：" + backupPath + Environment.NewLine +
               "补丁 SHA256：" + patchedHash + Environment.NewLine + Environment.NewLine +
               "以后 Steam 更新覆盖补丁时，守护程序会在游戏退出后自动备份并尝试重新适配。";
    }

    private static string RestoreOriginal(bool silent)
    {
        if (!Directory.Exists(InstallRoot))
            throw new DirectoryNotFoundException("没有找到已安装的补丁守护程序。");
        StopInstalledGuardian();
        EnsureGameStopped(silent);

        string gamePathFile = Path.Combine(InstallRoot, "game-path.txt");
        string gameRoot = File.Exists(gamePathFile)
            ? File.ReadAllText(gamePathFile, Encoding.UTF8).Trim()
            : LocateGame(silent);
        string active = Path.Combine(gameRoot, @"BongoCat_Data\Managed\Assembly-CSharp.dll");
        PatchState state = ReadState(Path.Combine(InstallRoot, "active-patch.txt"));
        if (state == null)
            throw new InvalidOperationException("缺少补丁状态文件，未修改游戏。");

        string currentHash = Sha256(active);
        if (EqualsHash(currentHash, state.PatchedHash))
        {
            if (string.IsNullOrWhiteSpace(state.BackupPath) || !File.Exists(state.BackupPath))
                throw new FileNotFoundException("找不到对应的原版备份，未修改游戏。", state.BackupPath);
            if (!EqualsHash(Sha256(state.BackupPath), state.OriginalHash))
                throw new IOException("原版备份哈希不匹配，未修改游戏。");
            File.Copy(state.BackupPath, active, true);
            if (!EqualsHash(Sha256(active), state.OriginalHash))
                throw new IOException("恢复后哈希校验失败。");
        }
        else if (!EqualsHash(currentHash, state.OriginalHash))
        {
            throw new InvalidOperationException("当前游戏 DLL 既不是记录中的补丁版也不是原版；为避免覆盖其他修改，已停止恢复。");
        }

        if (!TestMode)
        {
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                if (run != null) run.DeleteValue(RunValueName, false);
        }
        return "已经恢复原版并停止自动修复。备份和日志仍保留在：" + InstallRoot;
    }

    private static string LocateGame(bool silent)
    {
        string forced = Environment.GetEnvironmentVariable("BONGOCAT_PATCH_TEST_GAME_ROOT");
        if (!string.IsNullOrWhiteSpace(forced))
            return ValidateGameRoot(forced);

        var libraries = new List<string>();
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
        {
            if (key != null)
            {
                object steamPath = key.GetValue("SteamPath");
                if (steamPath != null) libraries.Add(steamPath.ToString());
            }
        }
        libraries.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));

        foreach (string steamRoot in libraries.ToArray())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            string folders = Path.Combine(steamRoot.Replace('/', Path.DirectorySeparatorChar), @"steamapps\libraryfolders.vdf");
            if (!File.Exists(folders)) continue;
            string text = File.ReadAllText(folders, Encoding.UTF8);
            foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
        }

        foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string steamApps = Path.Combine(library.Replace('/', Path.DirectorySeparatorChar), "steamapps");
                string manifest = Path.Combine(steamApps, "appmanifest_3419430.acf");
                if (!File.Exists(manifest)) continue;
                Match match = Regex.Match(File.ReadAllText(manifest, Encoding.UTF8), "\\\"installdir\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                return ValidateGameRoot(Path.Combine(steamApps, "common", match.Groups[1].Value));
            }
            catch { }
        }

        if (silent)
            throw new DirectoryNotFoundException("没有自动找到 Steam 版 Bongo Cat（AppID 3419430）。");
        using (var picker = new FolderBrowserDialog())
        {
            picker.Description = "请选择包含 BongoCat.exe 的游戏文件夹";
            if (picker.ShowDialog() != DialogResult.OK)
                throw new OperationCanceledException("用户取消了目录选择。");
            return ValidateGameRoot(picker.SelectedPath);
        }
    }

    private static string ValidateGameRoot(string path)
    {
        string root = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar);
        if (!File.Exists(Path.Combine(root, "BongoCat.exe")) ||
            !File.Exists(Path.Combine(root, @"BongoCat_Data\Managed\Assembly-CSharp.dll")))
            throw new DirectoryNotFoundException("不是有效的 Steam Bongo Cat 游戏目录：" + root);
        return root;
    }

    private static void EnsureGameStopped(bool silent)
    {
        Process[] games = Process.GetProcessesByName("BongoCat").Where(p => !p.HasExited).ToArray();
        if (games.Length == 0) return;
        if (silent)
            throw new InvalidOperationException("Bongo Cat 正在运行，请先退出游戏。");
        DialogResult answer = MessageBox.Show("检测到 Bongo Cat 正在运行。是否现在发送正常退出请求？",
            "需要退出游戏", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
            throw new OperationCanceledException("请退出 Bongo Cat 后重新运行安装器。");
        foreach (Process game in games)
        {
            try { game.CloseMainWindow(); }
            catch { }
        }
        DateTime deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline && Process.GetProcessesByName("BongoCat").Any())
            Thread.Sleep(250);
        if (Process.GetProcessesByName("BongoCat").Any())
            throw new InvalidOperationException("游戏没有正常退出；未强制结束，请手动退出后重试。");
    }

    private static void RunPatcher(string input, string output)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(InstallRoot, "BongoCatAdaptivePatcher.exe"),
            Arguments = Quote(input) + " " + Quote(output),
            WorkingDirectory = InstallRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        string stdout;
        string stderr;
        int exitCode;
        using (Process process = Process.Start(start))
        {
            stdout = process.StandardOutput.ReadToEnd();
            stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30000))
            {
                process.Kill();
                throw new TimeoutException("补丁结构校验超过 30 秒，未修改游戏。");
            }
            exitCode = process.ExitCode;
        }
        if (exitCode != 0 || !File.Exists(output))
        {
            DeleteExactFile(output);
            throw new InvalidOperationException("当前游戏版本与补丁不兼容，已保留原版。" + Environment.NewLine + stderr + Environment.NewLine + stdout);
        }
    }

    private static void RunGuardianOnce()
    {
        using (Process process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(InstallRoot, "BongoCatPatchGuardian.exe"),
            Arguments = "--once",
            WorkingDirectory = InstallRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        }))
        {
            if (!process.WaitForExit(30000) || process.ExitCode != 0)
                throw new InvalidOperationException("守护程序单次校验失败。");
        }
    }

    private static void StopInstalledGuardian()
    {
        string guardian = Path.Combine(InstallRoot, "BongoCatPatchGuardian.exe");
        if (!File.Exists(guardian)) return;
        try
        {
            using (Process process = Process.Start(new ProcessStartInfo
            {
                FileName = guardian,
                Arguments = "--stop",
                WorkingDirectory = InstallRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            })) process.WaitForExit(5000);
            Thread.Sleep(1000);
        }
        catch { }
    }

    private static void VerifyPackageComponents()
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "BongoCatAdaptivePatcher.exe", "EB9D031761C8453926D625A95782CEA279A6E041D885C00BB7CCF3181C5BE299" },
            { "BongoCatPatchGuardian.exe", "7920F7C662E1A69737C4381942316CCB4CA130CC8AE94075C676C37986068755" },
            { "Mono.Cecil.dll", "C41BDB9FFD3C5F6E17D2382C1012D73703E035E3F1100245FDD4E08C8DC6EB5B" },
            { "BongoCatChatLogger.dll", "E3470566A58BB14342097C7EEBA83092A05DA781151F1CDA285246868D006292" },
            { "BongoCatTray.exe", "009DB485529CD6AAF470DADC1CB7C001F9F70CB9B7F580D23557A533028D9538" }
        };
        foreach (KeyValuePair<string, string> component in expected)
        {
            string path = Path.Combine(ComponentRoot, component.Key);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new FileNotFoundException("安装包组件缺失", path);
            if (!EqualsHash(Sha256(path), component.Value))
                throw new IOException("安装包组件哈希不匹配，拒绝安装：" + component.Key);
        }
    }

    private static PatchState ReadState(string path)
    {
        if (!File.Exists(path)) return null;
        var state = new PatchState();
        foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
        {
            int equals = line.IndexOf('=');
            if (equals <= 0) continue;
            string key = line.Substring(0, equals);
            string value = line.Substring(equals + 1).Trim();
            if (key == "OriginalHash") state.OriginalHash = value;
            if (key == "PatchedHash") state.PatchedHash = value;
            if (key == "BackupPath") state.BackupPath = value;
        }
        return string.IsNullOrEmpty(state.PatchedHash) ? null : state;
    }

    private static void WriteState(string path, string originalHash, string patchedHash, string backupPath)
    {
        string text = "OriginalHash=" + originalHash + Environment.NewLine +
                      "PatchedHash=" + patchedHash + Environment.NewLine +
                      "BackupPath=" + backupPath + Environment.NewLine +
                      "Updated=" + DateTime.Now.ToString("O") + Environment.NewLine;
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static void CopyVerified(string source, string destination)
    {
        File.Copy(source, destination, true);
        if (!EqualsHash(Sha256(source), Sha256(destination)))
            throw new IOException("文件复制校验失败：" + destination);
    }

    private static string GetInstallRoot()
    {
        string test = Environment.GetEnvironmentVariable("BONGOCAT_PATCH_TEST_INSTALL_ROOT");
        return string.IsNullOrWhiteSpace(test)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BongoCatPatchGuardian")
            : Path.GetFullPath(test);
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
        if (File.Exists(path)) File.Delete(path);
    }
}
