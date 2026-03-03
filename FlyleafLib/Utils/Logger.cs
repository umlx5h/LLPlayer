namespace FlyleafLib;

public static class Logger
{
    public static bool CanError => Engine.Config.LogLevel >= LogLevel.Error;
    public static bool CanWarn  => Engine.Config.LogLevel >= LogLevel.Warn;
    public static bool CanInfo  => Engine.Config.LogLevel >= LogLevel.Info;
    public static bool CanDebug => Engine.Config.LogLevel >= LogLevel.Debug;
    public static bool CanTrace => Engine.Config.LogLevel >= LogLevel.Trace;


    public   static Action<string> CustomOutput = DevNullPtr;
    internal static Action<string> Output       = DevNullPtr;

    static ConcurrentQueue<byte[]>
                        fileData = [];
    static bool         fileTaskRunning;
    static FileStream   fileStream;
    static object       lockFileStream = new();
    static Dictionary<LogLevel, string>
                        logLevels = [];

    static Logger()
    {
        foreach (LogLevel loglevel in Enum.GetValues(typeof(LogLevel)))
            logLevels.Add(loglevel, loglevel.ToString().PadRight(5, ' '));

        // Flush File Data on Application Exit
        System.Windows.Application.Current.Exit += (o, e) =>
        {
            lock (lockFileStream)
            {
                if (fileStream != null)
                {
                    while (fileData.TryDequeue(out byte[] data))
                        fileStream.Write(data, 0, data.Length);
                    fileStream.Dispose();
                }
            }
        };
    }

    internal static void SetOutput()
    {
        string output = Engine.Config.LogOutput;

        if (string.IsNullOrEmpty(output))
            Output = DevNullPtr;
        else if (output.StartsWith(':'))
        {
            if (output == ":console")
                Output = Console.WriteLine;
            else if (output == ":debug")
                Output = DebugPtr;
            else if (output == ":custom")
                Output = CustomOutput;
            else
                throw new Exception("Invalid log output");
        }
        else
        {
            lock (lockFileStream)
            {
                if (fileStream != null)
                {   // Flush File Data on Previously Opened File Stream
                    while (fileData.TryDequeue(out byte[] data))
                        fileStream.Write(data, 0, data.Length);
                    fileStream.Dispose();
                }

                string dir = Path.GetDirectoryName(output);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                if (Engine.Config.LogAppend)
                {
                    fileStream = new FileStream(output, FileMode.Append, FileAccess.Write);
                    Output = FilePtr;
                }
                    
                else if (Engine.Config.LogRollMaxFiles > 0 && Engine.Config.LogRollMaxFileSize > 0)
                {
                    RollLogFiles(); // If we have rolling log enables and do not append, then we need to roll the log files first
                    fileStream = new FileStream(Engine.Config.LogOutput, FileMode.Create, FileAccess.Write);
                    Output = FileRollPtr;
                }
                else
                {
                    fileStream = new FileStream(output, FileMode.Create, FileAccess.Write);
                    Output = FilePtr;
                }
            }
        }
    }
    static void DebugPtr(string msg) => System.Diagnostics.Debug.WriteLine(msg);
    static void DevNullPtr(string msg) { }
    static void FilePtr(string msg)
    {
        fileData.Enqueue(Encoding.UTF8.GetBytes($"{msg}\r\n"));

        if (!fileTaskRunning && fileData.Count > Engine.Config.LogCachedLines)
            FlushFileData();
    }

    static void FileRollPtr(string msg)
    {
        fileData.Enqueue(Encoding.UTF8.GetBytes($"{msg}\r\n"));

        if (!fileTaskRunning && fileData.Count > Engine.Config.LogCachedLines)
        {
            if (fileStream.Length >= Engine.Config.LogRollMaxFileSize)
            {
                while (fileTaskRunning) Thread.Sleep(10);

                lock (lockFileStream)
                {
                    while (fileData.TryDequeue(out byte[] data))
                        fileStream.Write(data, 0, data.Length);

                    fileStream.Flush();
                }

                HandleLogFileRolling();
            }
            else
                FlushFileData();
        }
    }

    static void RollLogFiles()
    {
        string name = Engine.Config.LogOutput;

        for (long i = Engine.Config.LogRollMaxFiles; i > 0; i--)
        {
            string logFile = $"{name}.{i}";
            string nextLogFile = $"{name}.{i + 1}";
            if (File.Exists(logFile))
            {
                if (i == Engine.Config.LogRollMaxFiles)
                    File.Delete(logFile);
                else
                    File.Move(logFile, nextLogFile);
            }
        }

        if (File.Exists(name))
            File.Move(name, $"{name}.{1}");
    }

    static void HandleLogFileRolling()
    {
        fileTaskRunning = true;

        Task.Run(() =>
        {
            lock (lockFileStream)
            {
                fileStream.Dispose();
                RollLogFiles();
                fileStream = new FileStream(Engine.Config.LogOutput, FileMode.Create, FileAccess.Write);
            }

            fileTaskRunning = false;
        });
    }

    static void FlushFileData()
    {
        fileTaskRunning = true;

        Task.Run(() =>
        {
            lock (lockFileStream)
            {
                while (fileData.TryDequeue(out byte[] data))
                    fileStream.Write(data, 0, data.Length);

                fileStream.Flush();
            }

            fileTaskRunning = false;
        });
    }

    /// <summary>
    /// Forces cached file data to be written to the file
    /// </summary>
    public static void ForceFlush()
    {
        if (!fileTaskRunning && fileStream != null)
            FlushFileData();
    }

    internal static void Log(string msg, LogLevel logLevel)
    {
        if (logLevel <= Engine.Config.LogLevel)
            Output($"{DateTime.Now.ToString(Engine.Config.LogDateTimeFormat)} | {logLevels[logLevel]} | {msg}");
    }
}

public class LogHandler
{
    public string Prefix;

    public LogHandler(string prefix = "")
        => Prefix = prefix;

    public void Error(string msg)   => Log($"{Prefix}{msg}", LogLevel.Error);
    public void Info(string msg)    => Log($"{Prefix}{msg}", LogLevel.Info);
    public void Warn(string msg)    => Log($"{Prefix}{msg}", LogLevel.Warn);
    public void Debug(string msg)   => Log($"{Prefix}{msg}", LogLevel.Debug);
    public void Trace(string msg)   => Log($"{Prefix}{msg}", LogLevel.Trace);
}

public enum LogLevel
{
    Quiet   = 0x00,
    Error   = 0x10,
    Warn    = 0x20,
    Info    = 0x30,
    Debug   = 0x40,
    Trace   = 0x50
}
