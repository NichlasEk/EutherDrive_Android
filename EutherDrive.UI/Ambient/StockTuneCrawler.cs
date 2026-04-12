using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EutherDrive.UI.Ambient;

internal sealed record AmbientTrackInfo(string Title, string Mp3Url, string CoverUrl, string DownloadPath);

internal static class StockTuneCrawler
{
    private sealed record BrowserCandidate(string ExecutablePath, bool IsChromiumBased);

    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    private static readonly Regex s_buttonTagRegex = new(
        @"<button\b[^>]*\bdata-mp3=""[^""]+""[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex s_attributeRegex = new(
        @"\b(?<name>data-mp3|data-download|data-cover)=""(?<value>[^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_scriptFallbackRegex = new(
        @"\""src\"":\""(?<mp3>https://tunes\.stocktune\.com/[^\""]+\.mp3)\"",\""download_src\"":\""(?<download>/free-music/[^\""]+)\"",\""cover\"":\""(?<cover>https://covers\.stocktune\.com/[^\""]+\.(?:jpg|jpeg|png|webp))\""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] s_linuxBrowserNames =
    [
        "chromium",
        "chromium-browser",
        "google-chrome",
        "google-chrome-stable",
        "microsoft-edge",
        "microsoft-edge-stable",
        "firefox"
    ];

    private static readonly string[] s_windowsBrowserPaths =
    [
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Mozilla Firefox\firefox.exe",
        @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe",
        "chrome",
        "msedge",
        "firefox"
    ];

    private static readonly string[] s_macBrowserPaths =
    [
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
        "/Applications/Firefox.app/Contents/MacOS/firefox",
        "google-chrome",
        "microsoft-edge",
        "firefox"
    ];

    private const string CategoryUrl = "https://stocktune.com/free-songs/cyberpunk-ambient";
    private const string BrowserUserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36";
    private static readonly TimeSpan BrowserTimeout = TimeSpan.FromSeconds(30);

    public static async Task<IReadOnlyList<AmbientTrackInfo>> CrawlCyberpunkAmbientAsync(Action<string>? reportStatus, CancellationToken cancellationToken)
    {
        reportStatus?.Invoke("Crawling StockTune...");
        string? html = await TryFetchCategoryHtmlAsync(cancellationToken).ConfigureAwait(false);
        List<AmbientTrackInfo> tracks = ParseTracks(html);
        if (tracks.Count > 0)
            return tracks;

        reportStatus?.Invoke("StockTune needs browser render, crawling DOM...");
        string dom = await DumpCategoryDomAsync(cancellationToken).ConfigureAwait(false);
        tracks = ParseTracks(dom);
        if (tracks.Count > 0)
            return tracks;

        throw new InvalidOperationException("Could not find any ambient tracks on StockTune.");
    }

    private static async Task<string?> TryFetchCategoryHtmlAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CategoryUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.Referrer = new Uri("https://stocktune.com/");

        try
        {
            using HttpResponseMessage response = await s_httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return LooksLikeChallengePage(html) ? null : html;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeChallengePage(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return true;

        return html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Performing security verification", StringComparison.OrdinalIgnoreCase)
            || html.Contains("__cf_chl", StringComparison.OrdinalIgnoreCase);
    }

    private static List<AmbientTrackInfo> ParseTracks(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return [];

        var tracksByDownload = new Dictionary<string, AmbientTrackInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in s_buttonTagRegex.Matches(html))
        {
            string tag = match.Value;
            string? mp3 = GetAttributeValue(tag, "data-mp3");
            string? download = GetAttributeValue(tag, "data-download");
            string? cover = GetAttributeValue(tag, "data-cover");
            AmbientTrackInfo? track = TryCreateTrack(mp3, cover, download);
            if (track != null)
                tracksByDownload[track.DownloadPath] = track;
        }

        if (tracksByDownload.Count > 0)
            return [.. tracksByDownload.Values];

        foreach (Match match in s_scriptFallbackRegex.Matches(html))
        {
            AmbientTrackInfo? track = TryCreateTrack(
                WebUtility.HtmlDecode(match.Groups["mp3"].Value),
                WebUtility.HtmlDecode(match.Groups["cover"].Value),
                WebUtility.HtmlDecode(match.Groups["download"].Value));
            if (track != null)
                tracksByDownload[track.DownloadPath] = track;
        }

        return [.. tracksByDownload.Values];
    }

    private static string? GetAttributeValue(string tag, string attributeName)
    {
        foreach (Match match in s_attributeRegex.Matches(tag))
        {
            if (string.Equals(match.Groups["name"].Value, attributeName, StringComparison.OrdinalIgnoreCase))
                return WebUtility.HtmlDecode(match.Groups["value"].Value);
        }

        return null;
    }

    private static AmbientTrackInfo? TryCreateTrack(string? mp3Url, string? coverUrl, string? downloadPath)
    {
        if (string.IsNullOrWhiteSpace(mp3Url)
            || string.IsNullOrWhiteSpace(coverUrl)
            || string.IsNullOrWhiteSpace(downloadPath))
        {
            return null;
        }

        if (!mp3Url.StartsWith("https://tunes.stocktune.com/", StringComparison.OrdinalIgnoreCase)
            || !coverUrl.StartsWith("https://covers.stocktune.com/", StringComparison.OrdinalIgnoreCase)
            || !downloadPath.StartsWith("/free-music/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string title = Path.GetFileNameWithoutExtension(mp3Url) ?? downloadPath;
        title = title.Replace("-stocktune", string.Empty, StringComparison.OrdinalIgnoreCase);
        title = title.Replace('-', ' ').Trim();
        if (title.Length == 0)
            title = downloadPath.Trim('/').Split('/').LastOrDefault() ?? "ambient track";

        return new AmbientTrackInfo(title, mp3Url, coverUrl, downloadPath);
    }

    private static async Task<string> DumpCategoryDomAsync(CancellationToken cancellationToken)
    {
        foreach (BrowserCandidate candidate in EnumerateBrowserCandidates())
        {
            string? dom = await TryDumpDomWithBrowserAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(dom) && ParseTracks(dom).Count > 0)
                return dom;
        }

        throw new InvalidOperationException("No compatible browser was available to crawl StockTune.");
    }

    private static IEnumerable<BrowserCandidate> EnumerateBrowserCandidates()
    {
        string? overrideBrowser = Environment.GetEnvironmentVariable("EUTHERDRIVE_AMBIENT_BROWSER");
        if (!string.IsNullOrWhiteSpace(overrideBrowser))
            yield return new BrowserCandidate(overrideBrowser.Trim(), IsChromiumLike(overrideBrowser));

        IEnumerable<string> paths = OperatingSystem.IsWindows()
            ? s_windowsBrowserPaths
            : OperatingSystem.IsMacOS()
                ? s_macBrowserPaths
                : s_linuxBrowserNames;

        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !yielded.Add(path))
                continue;

            if (Path.IsPathRooted(path) && !File.Exists(path))
                continue;

            yield return new BrowserCandidate(path, IsChromiumLike(path));
        }
    }

    private static bool IsChromiumLike(string pathOrName)
    {
        string value = pathOrName.ToLowerInvariant();
        return value.Contains("chrome", StringComparison.Ordinal)
            || value.Contains("chromium", StringComparison.Ordinal)
            || value.Contains("edge", StringComparison.Ordinal)
            || value.Contains("msedge", StringComparison.Ordinal);
    }

    private static async Task<string?> TryDumpDomWithBrowserAsync(BrowserCandidate candidate, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = BuildBrowserStartInfo(candidate)
        };

        try
        {
            if (!process.Start())
                return null;
        }
        catch
        {
            return null;
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(BrowserTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            return null;
        }
        catch
        {
            TryKillProcess(process);
            return null;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            return null;

        return stdout;
    }

    private static ProcessStartInfo BuildBrowserStartInfo(BrowserCandidate candidate)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = candidate.ExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (candidate.IsChromiumBased)
        {
            startInfo.ArgumentList.Add("--headless=new");
            startInfo.ArgumentList.Add("--disable-gpu");
            startInfo.ArgumentList.Add("--no-first-run");
            startInfo.ArgumentList.Add("--no-default-browser-check");
            startInfo.ArgumentList.Add("--disable-blink-features=AutomationControlled");
            startInfo.ArgumentList.Add("--virtual-time-budget=15000");
            startInfo.ArgumentList.Add($"--user-agent={BrowserUserAgent}");
            startInfo.ArgumentList.Add("--dump-dom");
            startInfo.ArgumentList.Add(CategoryUrl);
        }
        else
        {
            startInfo.ArgumentList.Add("--headless");
            startInfo.ArgumentList.Add("--dump-dom");
            startInfo.ArgumentList.Add(CategoryUrl);
        }

        return startInfo;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Ignore browser cleanup failures.
        }
    }
}
