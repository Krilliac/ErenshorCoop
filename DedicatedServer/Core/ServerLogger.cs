using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace ErenshorDedicatedServer.Core
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        Fatal = 4
    }

    public static class ServerLogger
    {
        private static LogLevel _minConsoleLevel = LogLevel.Info;
        private static LogLevel _minFileLevel = LogLevel.Debug;
        private static StreamWriter _fileWriter;
        private static readonly object _fileLock = new object();
        private static readonly ConcurrentQueue<LogEntry> _logQueue = new ConcurrentQueue<LogEntry>();
        private static string _logDirectory = "logs";
        private static string _currentLogFile;
        private static long _maxFileSize = 10 * 1024 * 1024; // 10MB
        private static int _maxLogFiles = 10;
        private static bool _initialized;
        private static Timer _flushTimer;

        private struct LogEntry
        {
            public DateTime Timestamp;
            public LogLevel Level;
            public string Message;
            public string Category;
        }

        public static void Initialize(string logDirectory = "logs", LogLevel consoleLevel = LogLevel.Info, LogLevel fileLevel = LogLevel.Debug)
        {
            _logDirectory = logDirectory;
            _minConsoleLevel = consoleLevel;
            _minFileLevel = fileLevel;

            try
            {
                if (!Directory.Exists(_logDirectory))
                    Directory.CreateDirectory(_logDirectory);

                RotateLogs();

                _currentLogFile = Path.Combine(_logDirectory, $"server_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
                _fileWriter = new StreamWriter(_currentLogFile, true) { AutoFlush = false };

                _flushTimer = new Timer(_ => FlushLogQueue(), null, 500, 500);
                _initialized = true;
            }
            catch (Exception ex)
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine($"[LOGGER] Failed to initialize file logging: {ex.Message}");
                System.Console.ResetColor();
                _initialized = true; // Still allow console logging
            }
        }

        public static void SetConsoleLevel(LogLevel level) => _minConsoleLevel = level;
        public static void SetFileLevel(LogLevel level) => _minFileLevel = level;

        public static void Debug(string message, string category = null) => Log(LogLevel.Debug, message, category);
        public static void Info(string message, string category = null) => Log(LogLevel.Info, message, category);
        public static void Warning(string message, string category = null) => Log(LogLevel.Warning, message, category);
        public static void Error(string message, string category = null) => Log(LogLevel.Error, message, category);
        public static void Fatal(string message, string category = null) => Log(LogLevel.Fatal, message, category);

        public static void Log(LogLevel level, string message, string category = null)
        {
            if (!_initialized)
            {
                // Fallback before initialization
                WriteToConsole(level, message, category, DateTime.UtcNow);
                return;
            }

            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = level,
                Message = message ?? "(null)",
                Category = category
            };

            if (level >= _minConsoleLevel)
                WriteToConsole(entry.Level, entry.Message, entry.Category, entry.Timestamp);

            if (level >= _minFileLevel)
                _logQueue.Enqueue(entry);
        }

        private static void WriteToConsole(LogLevel level, string message, string category, DateTime timestamp)
        {
            var prevColor = System.Console.ForegroundColor;

            // Timestamp
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.Write($"[{timestamp:HH:mm:ss}] ");

            // Level tag
            System.Console.ForegroundColor = level switch
            {
                LogLevel.Debug => ConsoleColor.Gray,
                LogLevel.Info => ConsoleColor.Cyan,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Fatal => ConsoleColor.DarkRed,
                _ => ConsoleColor.White
            };

            var levelTag = level switch
            {
                LogLevel.Debug => "DBG",
                LogLevel.Info => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Fatal => "FTL",
                _ => "???"
            };
            System.Console.Write($"[{levelTag}]");

            // Category
            if (!string.IsNullOrEmpty(category))
            {
                System.Console.ForegroundColor = ConsoleColor.DarkYellow;
                System.Console.Write($"[{category}]");
            }

            System.Console.Write(" ");

            // Message
            System.Console.ForegroundColor = level switch
            {
                LogLevel.Debug => ConsoleColor.DarkGray,
                LogLevel.Info => ConsoleColor.White,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Fatal => ConsoleColor.DarkRed,
                _ => ConsoleColor.White
            };
            System.Console.WriteLine(message);

            System.Console.ForegroundColor = prevColor;
        }

        private static void FlushLogQueue()
        {
            if (_fileWriter == null) return;

            lock (_fileLock)
            {
                try
                {
                    var count = 0;
                    while (_logQueue.TryDequeue(out var entry) && count < 1000)
                    {
                        var categoryPart = string.IsNullOrEmpty(entry.Category) ? "" : $"[{entry.Category}]";
                        var line = $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level}]{categoryPart} {entry.Message}";
                        _fileWriter.WriteLine(line);
                        count++;
                    }
                    _fileWriter.Flush();

                    // Check file size for rotation
                    if (_currentLogFile != null && File.Exists(_currentLogFile))
                    {
                        var fileInfo = new FileInfo(_currentLogFile);
                        if (fileInfo.Length > _maxFileSize)
                        {
                            _fileWriter.Close();
                            _currentLogFile = Path.Combine(_logDirectory, $"server_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
                            _fileWriter = new StreamWriter(_currentLogFile, true) { AutoFlush = false };
                            RotateLogs();
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Console.ForegroundColor = ConsoleColor.Red;
                    System.Console.WriteLine($"[LOGGER] Flush error: {ex.Message}");
                    System.Console.ResetColor();
                }
            }
        }

        private static void RotateLogs()
        {
            try
            {
                if (!Directory.Exists(_logDirectory)) return;

                var logFiles = Directory.GetFiles(_logDirectory, "server_*.log");
                if (logFiles.Length <= _maxLogFiles) return;

                Array.Sort(logFiles);
                var toDelete = logFiles.Length - _maxLogFiles;
                for (var i = 0; i < toDelete; i++)
                {
                    try { File.Delete(logFiles[i]); }
                    catch { /* best effort */ }
                }
            }
            catch { /* best effort */ }
        }

        public static void Shutdown()
        {
            _flushTimer?.Dispose();
            FlushLogQueue();

            lock (_fileLock)
            {
                try
                {
                    _fileWriter?.Flush();
                    _fileWriter?.Close();
                    _fileWriter?.Dispose();
                    _fileWriter = null;
                }
                catch { /* shutting down */ }
            }
        }
    }
}
