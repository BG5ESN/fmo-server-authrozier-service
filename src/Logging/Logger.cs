using System.Threading.Channels;

namespace Sas.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

public static class Logger
{
    private static volatile LogLevel _level = LogLevel.Info;

    private static readonly Channel<(string Line, TextWriter Writer)> _channel =
        Channel.CreateUnbounded<(string, TextWriter)>(new UnboundedChannelOptions
        {
            SingleReader = true
        });

    private static readonly Task _consumer = Task.Run(ConsumeAsync);

    public static void SetLevel(LogLevel level) => _level = level;

    public static void SetLevel(string level)
    {
        SetLevel(level.ToUpperInvariant() switch
        {
            "DEBUG" => LogLevel.Debug,
            "INFO" => LogLevel.Info,
            "WARN" => LogLevel.Warn,
            "ERROR" => LogLevel.Error,
            _ => LogLevel.Info
        });
    }

    public static void Debug(string message)
    {
        if (_level <= LogLevel.Debug)
            Write("DEBUG", message, Console.Out);
    }

    public static void Info(string message)
    {
        if (_level <= LogLevel.Info)
            Write("INFO ", message, Console.Out);
    }

    public static void Warn(string message)
    {
        if (_level <= LogLevel.Warn)
            Write("WARN ", message, Console.Error);
    }

    public static void Error(string message)
    {
        if (_level <= LogLevel.Error)
            Write("ERROR", message, Console.Error);
    }

    public static void Flush()
    {
        _channel.Writer.TryComplete();
        _consumer.Wait(TimeSpan.FromSeconds(5));
    }

    private static void Write(string level, string message, TextWriter writer)
    {
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{ts}] [{level}] {message}";
        if (!_channel.Writer.TryWrite((line, writer)))
            writer.WriteLine(line);
    }

    private static async Task ConsumeAsync()
    {
        var reader = _channel.Reader;
        await foreach (var (line, writer) in reader.ReadAllAsync())
        {
            try
            {
                writer.WriteLine(line);
                writer.Flush();
            }
            catch
            {
            }
        }
    }
}
