using System.Text;
using N_m3u8DL_RE.Common.Log;
using Spectre.Console;

namespace M3U8Box.App.Core;

/// <summary>
/// Routes the upstream engine's console output to Android's logcat.
/// </summary>
/// <remarks>
/// <para>
/// The engine logs through Spectre.Console, which renders ANSI escape sequences
/// and assumes a terminal with dimensions. Android has neither, and none of
/// this is visible at compile time: the code builds cleanly and only fails when
/// a download starts. Everything the engine prints goes through
/// <see cref="CustomAnsiConsole.Console"/>, so redirecting the writer that
/// console writes to captures all of it.
/// </para>
/// <para>
/// Two mechanisms are combined, and the order matters:
/// </para>
/// <list type="number">
/// <item>
/// <c>Console.SetOut</c> replaces the process-wide stdout. This catches
/// <see cref="Console.Write(string)"/> calls, which is where
/// <c>NonAnsiWriter</c> (the engine's own stripped writer) funnels.
/// </item>
/// <item>
/// An <see cref="AnsiConsoleOutput"/> over a Logcat writer is installed on the
/// Spectre console, catching everything Spectre renders itself, including
/// progress frames.
/// </item>
/// </list>
/// <para>
/// The engine's <c>Logger.IsWriteFile</c> path is left off: it resolves its
/// directory from <c>Environment.ProcessPath</c>, which is null on Android, and
/// logging is handled here instead.
/// </para>
/// </remarks>
public static class AndroidLog
{
    private const string Tag = "M3U8Box";

    /// <summary>True once <see cref="Initialize"/> has run.</summary>
    public static bool Initialized { get; private set; }

    /// <summary>
    /// Install the logcat bridge. Safe to call more than once, and never throws.
    /// </summary>
    /// <param name="minLevel">
    /// Suppresses engine output below this level. Defaults to INFO because the
    /// engine emits a lot of debug chatter that would drown the useful lines.
    /// </param>
    /// <remarks>
    /// Failures here are recorded in <see cref="InitError"/> instead of
    /// propagating. This runs before <c>SetContentView</c>, and it asks
    /// Spectre.Console to configure itself on a platform with no terminal at
    /// all; an exception from that would abort the activity constructor and
    /// leave a blank screen. Degraded logging beats a blank UI, and the engine
    /// still works with its own default writer.
    /// </remarks>
    public static void Initialize(LogLevel minLevel = LogLevel.INFO)
    {
        if (Initialized)
        {
            return;
        }

        try
        {
            // 1. Process-wide stdout. NonAnsiWriter calls Console.Write, so this
            //    is where stripped engine output lands.
            Console.SetOut(new LogcatWriter());

            // 2. Force a re-init of the Spectre console now that stdout is
            //    ours, so the AnsiConsoleOutput it builds wraps the bridge
            //    rather than the original stdout. forceAnsi=false and
            //    noAnsiColor=true: no terminal, and escape sequences would be
            //    noise in logcat.
            CustomAnsiConsole.InitConsole(forceAnsi: false, noAnsiColor: true);

            // A redirected stream reports no screen size, and the engine's
            // progress renderer indexes into the profile height, so pin both to
            // something large rather than letting it read as 0 or -1.
            // Guarded on its own: a non-interactive profile may reject writes,
            // and that must not take the UI down with it.
            try
            {
                CustomAnsiConsole.Console.Profile.Width = int.MaxValue;
                CustomAnsiConsole.Console.Profile.Height = int.MaxValue;
            }
            catch (Exception profileEx)
            {
                UserWarn($"控制台尺寸设置失败: {profileEx.Message}");
            }

            // 3. Engine logging levels. These must be set after the console is
            //    rebuilt, because InitConsole replaces the Console instance.
            //
            //    IsWriteFile is off: the engine's file logger resolves its
            //    directory from Environment.ProcessPath, which is null on
            //    Android, and Path.GetDirectoryName(null) throws. Logcat
            //    already covers it.
            Logger.IsWriteFile = false;
            Logger.LogFilePath = null;
            Logger.LogLevel = minLevel;

            Initialized = true;
        }
        catch (Exception ex)
        {
            InitError = ex;
            UserError("日志桥接初始化失败，引擎将使用默认输出", ex);
        }
    }

    /// <summary>
    /// Why the logcat bridge failed to install, or null when it succeeded.
    /// </summary>
    public static Exception? InitError { get; private set; }

    /// <summary>
    /// Write a message at a level chosen for user-visible output, independent
    /// of the engine's own log level.
    /// </summary>
    public static void User(string message) =>
        Android.Util.Log.Info(Tag, message);

    public static void UserWarn(string message) =>
        Android.Util.Log.Warn(Tag, message);

    public static void UserError(string message, Exception? ex = null) =>
        Android.Util.Log.Error(Tag, ex is null ? message : $"{message}: {ex}");

    /// <summary>
    /// A <see cref="TextWriter"/> that forwards to logcat, one line per call.
    /// </summary>
    /// <remarks>
    /// The engine writes progress frames without trailing newlines and relies on
    /// ANSI cursor movement to redraw in place. Logcat has no cursor, so a frame
    /// would otherwise be emitted as one enormous line. Buffering until a
    /// newline, with a cap, keeps the log readable and bounded.
    /// </remarks>
    private sealed class LogcatWriter : TextWriter
    {
        private const int MaxBuffer = 4096;
        private readonly StringBuilder _buffer = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                FlushLine();
                return;
            }

            if (_buffer.Length >= MaxBuffer)
            {
                // Drop the frame rather than growing without bound; progress
                // output is the only thing that can reach this size.
                _buffer.Clear();
                return;
            }

            _buffer.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            foreach (var c in value)
            {
                Write(c);
            }
        }

        public override void Flush()
        {
            // A partial frame is dropped on purpose: emitting it would split one
            // logical line across two log entries.
        }

        private void FlushLine()
        {
            if (_buffer.Length == 0)
            {
                return;
            }

            var line = _buffer.ToString().TrimEnd('\r');
            _buffer.Clear();

            // Strip any escape sequences the engine emitted despite the
            // non-colour path; logcat renders them as literal garbage.
            line = EscapeStripper.Replace(line, string.Empty);
            if (line.Length > 0)
            {
                Android.Util.Log.Info(Tag, line);
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex EscapeStripper =
        new(@"\x1B\[[0-9;?]*[A-Za-z]|\x1B\][^\x07]*(\x07|\x1B\\)",
            System.Text.RegularExpressions.RegexOptions.Compiled);
}
