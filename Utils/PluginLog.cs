using System;
using System.IO;

namespace Cwseo.NINA.Focuser {
    internal static class PluginLog {
        private static readonly object _lock = new();

        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "Plugins", "Cwseo.NINA.Focuser", "Logs");

        private static string FilePath =>
            Path.Combine(Dir, $"FocuserPlugin-{DateTime.Now:yyyyMMdd}.log");

        public static void Info(string msg) => Write("INFO", msg);
        public static void Error(string msg, Exception ex) => Write("ERROR", $"{msg} | {ex}");

        private static void Write(string level, string msg) {
            lock (_lock) {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:HH:mm:ss.fff}|{level}|{msg}{Environment.NewLine}");
            }
        }
    }
}
