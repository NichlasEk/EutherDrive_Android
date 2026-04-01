using System;
using System.IO;
using Avalonia;
using Avalonia.Native;
using Avalonia.Win32;
using Avalonia.X11;

namespace EutherDrive.UI;

internal enum RenderBackendMode
{
    Bitmap,
    OpenGl,
    Vulkan
}

internal static class RenderBackendConfig
{
    public const string SettingsFileName = "eutherdrive_settings.toml";
    private const string RenderBackendEnvKey = "EUTHERDRIVE_RENDERER";
    public static RenderBackendMode StartupMode { get; private set; } = RenderBackendMode.Bitmap;
    public static bool SupportsVulkanPlatform => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static AppBuilder Configure(AppBuilder builder)
    {
        StartupMode = ResolvePreferredMode();

        builder = builder.With(new AvaloniaNativePlatformOptions
        {
            RenderingMode = new[]
            {
                AvaloniaNativeRenderingMode.OpenGl,
                AvaloniaNativeRenderingMode.Software
            }
        });

        if (StartupMode == RenderBackendMode.Vulkan)
        {
            builder = builder
                .With(new Win32PlatformOptions
                {
                    RenderingMode = new[]
                    {
                        Win32RenderingMode.Vulkan,
                        Win32RenderingMode.AngleEgl,
                        Win32RenderingMode.Wgl,
                        Win32RenderingMode.Software
                    }
                })
                .With(new X11PlatformOptions
                {
                    RenderingMode = new[]
                    {
                        X11RenderingMode.Vulkan,
                        X11RenderingMode.Glx,
                        X11RenderingMode.Egl,
                        X11RenderingMode.Software
                    }
                });
        }

        return builder;
    }

    public static RenderBackendMode ResolvePreferredMode()
    {
        if (TryGetEnvironmentOverride(out RenderBackendMode envMode))
            return envMode;

        if (TryReadSavedMode(out RenderBackendMode savedMode))
            return savedMode;

        return RenderBackendMode.Bitmap;
    }

    public static bool TryGetEnvironmentOverride(out RenderBackendMode mode)
    {
        string? raw = Environment.GetEnvironmentVariable(RenderBackendEnvKey);
        return TryParse(raw, out mode);
    }

    public static RenderBackendMode Parse(string? raw)
        => TryParse(raw, out RenderBackendMode mode) ? mode : RenderBackendMode.Bitmap;

    public static string GetDisplayName(RenderBackendMode mode)
        => mode switch
        {
            RenderBackendMode.OpenGl => "OpenGL",
            RenderBackendMode.Vulkan => "Vulkan",
            _ => "Bitmap"
        };

    public static bool TryReadSavedMode(out RenderBackendMode mode)
    {
        mode = RenderBackendMode.Bitmap;
        string path = Path.Combine(Directory.GetCurrentDirectory(), SettingsFileName);
        if (!File.Exists(path))
            return false;

        try
        {
            foreach (string rawLine in File.ReadLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                if (!line.StartsWith("RenderBackendMode", StringComparison.OrdinalIgnoreCase))
                    continue;

                int equalsIndex = line.IndexOf('=');
                if (equalsIndex < 0)
                    continue;

                string value = line[(equalsIndex + 1)..].Trim();
                value = TrimTomlValue(value);
                return TryParse(value, out mode);
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryParse(string? raw, out RenderBackendMode mode)
    {
        if (string.Equals(raw, "opengl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "gl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "OpenGl", StringComparison.OrdinalIgnoreCase))
        {
            mode = RenderBackendMode.OpenGl;
            return true;
        }

        if (string.Equals(raw, "vulkan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "vk", StringComparison.OrdinalIgnoreCase))
        {
            mode = RenderBackendMode.Vulkan;
            return true;
        }

        if (string.Equals(raw, "bitmap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "software", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "sw", StringComparison.OrdinalIgnoreCase))
        {
            mode = RenderBackendMode.Bitmap;
            return true;
        }

        mode = RenderBackendMode.Bitmap;
        return false;
    }

    private static string TrimTomlValue(string raw)
    {
        if (raw.Length >= 2)
        {
            char first = raw[0];
            char last = raw[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
                return raw[1..^1].Trim();
        }

        return raw;
    }
}
