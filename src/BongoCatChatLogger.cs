using System;
using System.IO;
using System.Reflection;
using System.Text;

[assembly: AssemblyVersion("1.0.0.0")]

public static class BongoCatChatLogger
{
    private static readonly object Sync = new object();
    private static DateTime NextOutboxCheck = DateTime.MinValue;

    public static void Log(string senderName, string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            string directory = GetLogDirectory();
            Directory.CreateDirectory(directory);

            string sender = Clean(senderName);
            if (string.IsNullOrWhiteSpace(sender))
                sender = "未知玩家";
            string cleanMessage = Clean(message);
            string line = string.Format(
                "[{0:yyyy-MM-dd HH:mm:ss}] {1}：{2}{3}",
                DateTime.Now, sender, cleanMessage, Environment.NewLine);
            string path = Path.Combine(directory, "聊天记录-" + DateTime.Now.ToString("yyyy-MM-dd") + ".txt");

            lock (Sync)
            {
                // Unity's Mono profile does not expose File.AppendAllText with an
                // Encoding argument, nor StreamWriter's three-argument overload.
                // Its four-argument StreamWriter constructor is present.
                using (var writer = new StreamWriter(path, true, new UTF8Encoding(true), 1024))
                    writer.Write(line);
            }
        }
        catch
        {
            // Chat display must never fail just because the local history file is unavailable.
        }
    }

    public static string TryTakeOutgoing()
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            if (now < NextOutboxCheck)
                return null;
            NextOutboxCheck = now.AddMilliseconds(250);

            string outbox = Path.Combine(GetLogDirectory(), "待发送");
            if (!Directory.Exists(outbox))
                return null;
            string[] files = Directory.GetFiles(outbox, "*.msg");
            if (files.Length == 0)
                return null;
            Array.Sort(files, StringComparer.Ordinal);

            foreach (string file in files)
            {
                try
                {
                    string message = File.ReadAllText(file);
                    File.Delete(file);
                    message = Clean(message);
                    if (!string.IsNullOrWhiteSpace(message))
                        return message;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static string GetLogDirectory()
    {
        string directory = Environment.GetEnvironmentVariable("BONGOCAT_CHAT_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
            return directory;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "BongoCat聊天记录");
    }

    private static string Clean(string value)
    {
        return (value ?? "").Replace("\r", "").Replace("\n", " ↵ ").Trim();
    }
}
