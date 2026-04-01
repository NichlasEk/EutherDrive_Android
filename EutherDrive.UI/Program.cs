using Avalonia;
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;

namespace EutherDrive.UI;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        ConfigureConsoleLogging();
        if (IsEnvEnabled("EUTHERDRIVE_TRACE_APP_START"))
            Console.Error.WriteLine("[EutherDrive.UI] Program.Main reached");
        var builder = BuildAvaloniaApp();
        // Store args in a static field that App can access
        App.CommandLineArgs = args;
        builder.StartWithClassicDesktopLifetime(args);
    }

    private static void ConfigureConsoleLogging()
    {
        if (ShouldSilenceConsole())
        {
            Console.SetOut(TextWriter.Null);
            // Keep stderr open for FB analyzer
            Trace.Listeners.Clear();
            Trace.AutoFlush = false;
        }
    }

    private static bool ShouldSilenceConsole()
    {
        // If any trace flag is set, enable all console output
        if (IsEnvEnabled("EUTHERDRIVE_LOG_VERBOSE"))
        {
            return false;
        }

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key)
            {
                continue;
            }

            if (key.StartsWith("EUTHERDRIVE_TRACE_", StringComparison.OrdinalIgnoreCase)
                && IsEnvEnabled(key))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEnvEnabled(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        return value == "1"
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => RenderBackendConfig.Configure(
            AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont())
            .LogToTrace();
}
