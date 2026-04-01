using System;
using System.IO;
using Avalonia;

namespace EutherDrive.Android;

internal enum AndroidRenderBackendMode
{
    VulkanBitmap,
    VulkanOpenGl,
    OpenGl
}

internal static class AndroidRenderBackendConfig
{
    public const string SettingsFileName = "android-settings.toml";
    private const string RenderBackendEnvKey = "EUTHERDRIVE_ANDROID_RENDERER";

    public static AndroidRenderBackendMode StartupMode { get; private set; } = AndroidRenderBackendMode.VulkanBitmap;

    public static AppBuilder Configure(AppBuilder builder)
    {
        StartupMode = ResolvePreferredMode();

        AndroidRenderingMode[] modes = StartupMode switch
        {
            AndroidRenderBackendMode.OpenGl => new[]
            {
                AndroidRenderingMode.Egl,
                AndroidRenderingMode.Software
            },
            _ => new[]
            {
                AndroidRenderingMode.Vulkan,
                AndroidRenderingMode.Egl,
                AndroidRenderingMode.Software
            }
        };

        return builder.With(new AndroidPlatformOptions
        {
            RenderingMode = modes
        });
    }

    public static AndroidRenderBackendMode ResolvePreferredMode()
    {
        if (TryGetEnvironmentOverride(out AndroidRenderBackendMode envMode))
            return envMode;

        if (TryReadSavedMode(out AndroidRenderBackendMode savedMode))
            return savedMode;

        return AndroidRenderBackendMode.VulkanBitmap;
    }

    public static bool TryGetEnvironmentOverride(out AndroidRenderBackendMode mode)
    {
        string? raw = Environment.GetEnvironmentVariable(RenderBackendEnvKey);
        return TryParse(raw, out mode);
    }

    public static AndroidRenderBackendMode Parse(string? raw)
        => TryParse(raw, out AndroidRenderBackendMode mode) ? mode : AndroidRenderBackendMode.VulkanBitmap;

    public static string GetDisplayName(AndroidRenderBackendMode mode)
        => mode switch
        {
            AndroidRenderBackendMode.VulkanOpenGl => "Vulkan OpenGL",
            AndroidRenderBackendMode.OpenGl => "OpenGL",
            _ => "Vulkan Bitmap"
        };

    public static bool TryReadSavedMode(out AndroidRenderBackendMode mode)
    {
        mode = AndroidRenderBackendMode.VulkanBitmap;

        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SettingsFileName);

        if (!File.Exists(settingsPath))
            return false;

        try
        {
            foreach (string rawLine in File.ReadLines(settingsPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                if (!line.StartsWith("AndroidRenderBackendMode", StringComparison.OrdinalIgnoreCase)
                    && !line.StartsWith("android_render_backend_mode", StringComparison.OrdinalIgnoreCase)
                    && !line.StartsWith("RenderBackendMode", StringComparison.OrdinalIgnoreCase)
                    && !line.StartsWith("render_backend_mode", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int equalsIndex = line.IndexOf('=');
                if (equalsIndex < 0)
                    continue;

                string value = TrimTomlValue(line[(equalsIndex + 1)..].Trim());
                return TryParse(value, out mode);
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryParse(string? raw, out AndroidRenderBackendMode mode)
    {
        if (string.Equals(raw, "vulkan-bitmap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "vulkan_bitmap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "vk-bitmap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "vk_bitmap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "vulkan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "vk", StringComparison.OrdinalIgnoreCase))
        {
            mode = AndroidRenderBackendMode.VulkanBitmap;
            return true;
        }

        if (string.Equals(raw, "vulkan-opengl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "vulkan_opengl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "vk-opengl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "vk_opengl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "vulkangl", StringComparison.OrdinalIgnoreCase))
        {
            mode = AndroidRenderBackendMode.VulkanOpenGl;
            return true;
        }

        if (string.Equals(raw, "opengl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "gl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "egl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "OpenGl", StringComparison.OrdinalIgnoreCase))
        {
            mode = AndroidRenderBackendMode.OpenGl;
            return true;
        }

        mode = AndroidRenderBackendMode.VulkanBitmap;
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
