using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using EutherDrive.Core;

namespace EutherDrive.AsciiViewer;

/// <summary>
/// Live ASCII art framebuffer viewer for terminal
/// Renders the framebuffer as colored ASCII characters with real-time updates
/// </summary>
class Program
{
    // ASCII character ramp (from dark to light)
    private static string Chars = " .:-=+*#%@";

    // File path for IPC (must match adapter)
    private const string DefaultFilePath = "/tmp/eutherdrive_ascii_fb.dat";
    private static string _filePath = DefaultFilePath;

    // ANSI escape codes
    private const string Esc = "\x1b[";
    private const string Home = Esc + "H";
    private const string ClearScreen = Esc + "2J";
    private const string HideCursor = Esc + "?25l";
    private const string ShowCursor = Esc + "?25h";
    private const string ResetColor = Esc + "0m";
    private const string EnterAlternateScreen = Esc + "?1049h";
    private const string ExitAlternateScreen = Esc + "?1049l";
    private const string EraseLine = Esc + "2K";

    // 256-color foreground
    private static string Foreground256(byte r, byte g, byte b) =>
        Esc + "38;2;" + r + ";" + g + ";" + b + "m";

    // Default RGB values
    private static int _targetWidth = 80;
    private static int _targetHeight = 40;
    private static bool _useTrueColor = true;
    private static int _sampleEveryNFrames = 1;
    private static int _pollIntervalMs = 16; // ~60fps

    // Debug log file
    private static string _debugLogPath = "/tmp/eutherdrive_ascii_viewer.log";
    private static StreamWriter? _debugLog;
    private static readonly object _debugLogLock = new();

    private static void DebugLog(string message)
    {
        lock (_debugLogLock)
        {
            try
            {
                if (_debugLog == null)
                {
                    _debugLog = new StreamWriter(_debugLogPath, false) { AutoFlush = true };
                }
                _debugLog.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            }
            catch
            {
                // Ignore log errors
            }
        }
    }

    static int Main(string[] args)
    {
        ParseArgs(args);

        Console.WriteLine($"[AsciiViewer] Starting ASCII framebuffer viewer");
        Console.WriteLine($"[AsciiViewer] Resolution: {_targetWidth}x{_targetHeight}");
        Console.WriteLine($"[AsciiViewer] Color mode: {(_useTrueColor ? "TrueColor (24-bit)" : "8-bit")}");
        Console.WriteLine($"[AsciiViewer] File: {_filePath}");
        Console.WriteLine($"[AsciiViewer] Poll interval: {_pollIntervalMs}ms");

        return RunFileMode();
    }

    private static void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--width":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int w))
                    {
                        _targetWidth = Math.Max(20, Math.Min(160, w));
                        i++;
                    }
                    break;
                case "--height":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int h))
                    {
                        _targetHeight = Math.Max(20, Math.Min(60, h));
                        i++;
                    }
                    break;
                case "--chars":
                    if (i + 1 < args.Length)
                    {
                        Chars = args[i + 1];
                        i++;
                    }
                    break;
                case "--8bit":
                    _useTrueColor = false;
                    break;
                case "--sample":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int s))
                    {
                        _sampleEveryNFrames = Math.Max(1, s);
                        i++;
                    }
                    break;
                case "--file":
                    if (i + 1 < args.Length)
                    {
                        _filePath = args[i + 1];
                        i++;
                    }
                    break;
                case "--poll":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
                    {
                        _pollIntervalMs = Math.Max(1, p);
                        i++;
                    }
                    break;
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"
EutherDrive ASCII Framebuffer Viewer
=====================================

Usage: dotnet run --project EutherDrive.AsciiViewer -- [options]

Options:
  --width N        Set output width (default: 80, range: 20-160)
  --height N       Set output height (default: 40, range: 20-60)
  --chars STRING   Set ASCII character ramp (default: ' .:-=+*#%@')
  --8bit           Use 8-bit colors instead of TrueColor (24-bit)
  --sample N       Sample every Nth frame (default: 1)
  --file PATH      Path to framebuffer file (default: /tmp/eutherdrive_ascii_fb.dat)
  --poll N         Poll interval in ms (default: 16, ~60fps)
  --help           Show this help

Examples:
  # Standard view (80x40)
  dotnet run --project EutherDrive.AsciiViewer -- --width 100 --height 50

  # Low resolution retro look
  dotnet run --project EutherDrive.AsciiViewer -- --width 40 --height 25 --chars ' .,-+*#'

  # Higher resolution
  dotnet run --project EutherDrive.AsciiViewer -- --width 120 --height 50

Requirements:
  - Terminal must support TrueColor (24-bit color)
  - Run from a terminal that understands ANSI escape codes
  - Font should be monospaced

Controls:
  Press Ctrl+C to exit
");
    }

    private static int RunFileMode()
    {
        Console.WriteLine($"[AsciiViewer] Starting... file: {_filePath}");

        // Wait for file to appear
        int waitCount = 0;
        while (!File.Exists(_filePath) && waitCount < 100)
        {
            DebugLog($"Waiting for file... ({waitCount}/100)");
            Thread.Sleep(100);
            waitCount++;
        }

        if (!File.Exists(_filePath))
        {
            DebugLog($"Timeout waiting for file: {_filePath}");
            return 1;
        }

        Console.WriteLine("[AsciiViewer] File found, starting viewer...");

        // Enter alternate screen mode (prevents scrolling)
        Console.Out.Write(EnterAlternateScreen);
        Console.Out.Write(ClearScreen);
        Console.Out.Write(HideCursor);
        Console.Out.Flush();

        try
        {
            var buffer = new byte[16]; // header: width(4) + height(4) + size(4) + frame(4)
            var frameBuffer = new byte[320 * 224 * 4]; // Max size
            int frameCount = 0;
            int lastFrameNumber = -1;

            int loopCount = 0;
            while (true)
            {
                if (++loopCount % 60 == 0)
                {
                    try
                    {
                        var fi = new FileInfo(_filePath);
                        DebugLog($"Loop alive, count={loopCount}, lastFrame={lastFrameNumber}, fileLen={fi.Length}");
                    }
                    catch { DebugLog($"Loop alive, count={loopCount}, lastFrame={lastFrameNumber}"); }
                }

                // Re-open file each iteration to avoid stale handle issues
                FileStream? fs = null;
                try
                {
                    fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                }
                catch (Exception ex)
                {
                    DebugLog($"Failed to open file: {ex.Message}");
                    Thread.Sleep(_pollIntervalMs);
                    continue;
                }

                try
                {
                    // Read header
                    int bytesRead = fs.Read(buffer, 0, 16);
                    if (bytesRead < 16)
                    {
                        DebugLog($"Short header read: {bytesRead} bytes");
                        fs.Dispose();
                        Thread.Sleep(_pollIntervalMs);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    DebugLog($"Header read error: {ex.Message}");
                    fs.Dispose();
                    Thread.Sleep(_pollIntervalMs);
                    continue;
                }

                int width = BitConverter.ToInt32(buffer, 0);
                int height = BitConverter.ToInt32(buffer, 4);
                int size = BitConverter.ToInt32(buffer, 8);
                int frameNumber = BitConverter.ToInt32(buffer, 12);

                if (width <= 0 || width > 320 || height <= 0 || height > 224 || size > frameBuffer.Length)
                {
                    fs?.Dispose();
                    if (frameCount % 60 == 1)
                        DebugLog($"Invalid header: w={width} h={height} size={size}");
                    Thread.Sleep(_pollIntervalMs);
                    continue;
                }

                // Skip if same frame
                if (frameNumber == lastFrameNumber)
                {
                    fs?.Dispose();
                    // Debug on first occurrence of a repeated frame
                    if (frameCount % 60 == 0)
                    {
                        try
                        {
                            var fi = new FileInfo(_filePath);
                            DebugLog($"Same frame {frameNumber} for 60+ loops, fileLen={fi.Length}");
                        }
                        catch { DebugLog($"Same frame {frameNumber} for 60+ loops"); }
                    }
                    Thread.Sleep(_pollIntervalMs);
                    continue;
                }

                // Detect frame number jumps (possible mode change)
                if (lastFrameNumber > 0 && frameNumber < lastFrameNumber)
                {
                    DebugLog($"Frame number RESET: {lastFrameNumber} -> {frameNumber}");
                }

                // Read framebuffer
                try
                {
                    fs.Seek(16, SeekOrigin.Begin);
                    int bytesRead = 0;
                    int readTimeout = 0;
                    while (bytesRead < size)
                    {
                        // Check if file was truncated (smaller than expected)
                        if (fs.Length < 16 + size)
                        {
                            if (readTimeout++ < 5)
                            {
                                Thread.Sleep(_pollIntervalMs);
                                continue;
                            }
                            DebugLog($"File truncated! len={fs.Length} expected={16+size}");
                            break;
                        }

                        int read = fs.Read(frameBuffer, bytesRead, size - bytesRead);
                        if (read <= 0)
                        {
                            if (readTimeout++ > 10)
                            {
                                DebugLog($"Read timeout at {bytesRead}/{size}");
                                break;
                            }
                            Thread.Sleep(_pollIntervalMs);
                            continue;
                        }
                        bytesRead += read;
                        readTimeout = 0;
                    }
                    fs.Dispose();
                    fs = null;
                }
                catch (Exception ex)
                {
                    fs?.Dispose();
                    DebugLog($"Read error: {ex.Message}");
                    Thread.Sleep(_pollIntervalMs);
                    continue;
                }

                lastFrameNumber = frameNumber;
                frameCount++;

                DebugLog($"Rendered frame {frameCount} (stream {frameNumber}) {width}x{height}");

                // Print frame info occasionally
                if (frameCount % 60 == 1)
                {
                    // Show info at the bottom
                    Console.Out.Write(Home);
                    for (int i = 0; i < _targetHeight + 2; i++)
                    {
                        Console.Out.Write(EraseLine); Console.Out.Write("\r\n");
                    }
                    Console.Out.Write(Home);
                    Console.Out.Write(ResetColor);
                    Console.Out.Write($"[AsciiViewer] Frame {frameCount} (stream {frameNumber}) {width}x{height}");
                    Console.Out.Flush();
                }

                RenderFrame(frameBuffer, width, height);

                // Throttle to avoid using 100% CPU and to match ~60fps
                Thread.Sleep(_pollIntervalMs);
            }
        }
        catch (Exception ex)
        {
            Console.Out.Write(ExitAlternateScreen);
            Console.Out.Write(ShowCursor);
            Console.Out.Flush();
            Console.WriteLine($"[AsciiViewer] Error: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.Out.Write(ExitAlternateScreen);
            Console.Out.Write(ShowCursor);
            Console.Out.Write(ResetColor);
            Console.Out.Flush();
        }
    }

    private static void RenderFrame(byte[] fb, int width, int height)
    {
        // Calculate sampling rate
        int sampleX = Math.Max(1, width / _targetWidth);
        int sampleY = Math.Max(1, height / _targetHeight);

        // Render to string builder - use VT100 line erase to avoid scrolling
        var sb = new StringBuilder();

        // Position cursor at home without clearing (we'll erase lines)
        sb.Append(Home);

        int charIdx = 0;

        for (int y = 0; y < _targetHeight && y * sampleY < height; y++)
        {
            int srcY = Math.Min((y * sampleY), height - 1);

            // Erase current line before writing
            sb.Append(Esc); sb.Append("2K");  // Erase entire line
            sb.Append('\r');  // Return to start of line

            for (int x = 0; x < _targetWidth && x * sampleX < width; x++)
            {
                int srcX = Math.Min((x * sampleX), width - 1);
                int offset = srcY * width * 4 + srcX * 4;

                byte b = fb[offset + 0];
                byte g = fb[offset + 1];
                byte r = fb[offset + 2];
                byte a = fb[offset + 3];

                // Calculate brightness
                int brightness = (r + g + b) / 3;
                int charIndex = (brightness * (Chars.Length - 1)) / 255;
                charIndex = Math.Max(0, Math.Min(Chars.Length - 1, charIndex));

                // Get character
                char c = Chars[charIndex];

                // Output with color
                if (_useTrueColor)
                {
                    sb.Append(Foreground256(r, g, b));
                }
                else
                {
                    // 8-bit color approximation
                    int color = (r > 128 ? 1 : 0) | (g > 128 ? 2 : 0) | (b > 128 ? 4 : 0);
                    sb.Append(Esc + "3" + color + "m");
                }

                sb.Append(c);
            }
            sb.Append('\n');
        }

        sb.Append(ResetColor);

        // Write to console
        Console.Out.Write(sb.ToString());
        Console.Out.Flush();
    }
}
