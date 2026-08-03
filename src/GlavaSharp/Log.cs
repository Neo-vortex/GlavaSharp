using System;

namespace GlavaSharp;

/// <summary>Minimum severity a message needs to actually get printed. Set via <c>--log-level</c> (see Program.cs).</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

/// <summary>
///     Minimal leveled, colorized console logger -- deliberately hand-rolled
///     rather than pulling in a logging framework (Microsoft.Extensions.Logging,
///     Serilog, ...): this project already avoids dependencies where a small
///     from-scratch piece does the job (see e.g. <see cref="Shaders.CpuFft" />),
///     and all this needs is "leveled, colored, goes to stdout/stderr."
///     <see cref="Debug" />/<see cref="Info" />/<see cref="Warn" />/
///     <see cref="Error" /> replace the ad-hoc <c>Console.WriteLine($"[GlavaSharp] ...")</c>
///     calls that used to be scattered through Program.cs/AppWindow.cs/ShaderModule.cs.
/// </summary>
public static class Log
{
    /// <summary>Messages below this level are silently dropped. Default <see cref="LogLevel.Info" /> matches the pre-Log.cs behavior (everything that used to always print, still does).</summary>
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    // Console.IsOutputRedirected/IsErrorRedirected: skip ANSI color codes when
    // stdout/stderr isn't a terminal (piped to a file, `| tee`, CI logs, ...)
    // -- otherwise every redirected log fills up with escape-code noise.
    // NO_COLOR (https://no-color.org/) is honored the same way most CLI
    // tools do: any non-empty value disables color, full stop.
    private static readonly bool ColorEnabled =
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    private static void Write(LogLevel level, string message)
    {
        if (level < MinLevel) return;

        // Errors and warnings go to stderr (matches the Console.Error.WriteLine
        // calls this replaces) so redirecting stdout alone doesn't swallow
        // them; everything else goes to stdout.
        var writer = level >= LogLevel.Warn ? Console.Error : Console.Out;
        var useColor = ColorEnabled && !(level >= LogLevel.Warn ? Console.IsErrorRedirected : Console.IsOutputRedirected);

        var (tag, color) = level switch
        {
            LogLevel.Debug => ("DEBUG", ConsoleColor.DarkGray),
            LogLevel.Info => ("INFO ", ConsoleColor.Cyan),
            LogLevel.Warn => ("WARN ", ConsoleColor.Yellow),
            LogLevel.Error => ("ERROR", ConsoleColor.Red),
            _ => ("?????", ConsoleColor.White)
        };

        if (!useColor)
        {
            writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{tag}] {message}");
            return;
        }

        var prevColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        writer.Write(DateTime.Now.ToString("HH:mm:ss.fff"));
        writer.Write(' ');
        Console.ForegroundColor = color;
        writer.Write('[');
        writer.Write(tag);
        writer.Write(']');
        Console.ForegroundColor = prevColor;
        writer.Write(' ');
        writer.WriteLine(message);
    }
}
