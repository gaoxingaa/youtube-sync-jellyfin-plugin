using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.YoutubeSync.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YoutubeSync;

/// <summary>
/// Scheduled task to sync YouTube content into Jellyfin.
/// </summary>
public class YoutubeSyncTask : IScheduledTask
{
    private readonly ILogger<YoutubeSyncTask> _logger;

    public YoutubeSyncTask(ILogger<YoutubeSyncTask> logger)
    {
        _logger = logger;
        _logger.LogInformation("YouTubeSyncTask init.");
    }

    /// <inheritdoc />
    public string Name => "YouTube Sync Task";

    /// <inheritdoc />
    public string Description => "Syncs YouTube data into Jellyfin";

    /// <inheritdoc />
    public string Category => "YouTube";

    /// <inheritdoc />
    public string Key => "YoutubeSync";

    /// <summary>
    /// tststt.
    /// </summary>
    /// <param name="progress">The first name to join.</param>
    /// <param name="cancellationToken">The last name to join.</param>
    /// <returns>IEnumerable.</returns>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("YouTube Sync Task started.");

        var config = YoutubeSyncPlugin.Instance?.Configuration;

        if (config == null)
        {
            _logger.LogError("Plugin configuration is missing.");
            return;
        }

        string videoLocation = config.VideoLocation;

        string channelIds = config.ChannelIds;
        int episodes = config.Episodes;
        bool autoDeletePlayed = config.AutoDeletePlayed;

        string[] channels = channelIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.Trim())
            .ToArray();
        var channelCount = channels.Length;
        for (int i = 0; i < channelCount; i++)
        {
            var channelId = channels[i];
            _logger.LogInformation("Syncing to: {VideoLocation}, for ChannelId: {ChannelId}, Episodes: {Episodes}, AutoDelete: {AutoDeletePlayed}", videoLocation, channelId, episodes, autoDeletePlayed);

            try
            {
                using var httpClient = new HttpClient();

                _logger.LogInformation("Extract channel ID from handle page: {ChannelId}", channelId);
                string feedUrl = $"https://www.youtube.com/feeds/videos.xml?channel_id={channelId}";

                string xml = await httpClient.GetStringAsync(feedUrl);

                var doc = XDocument.Parse(xml);
                var ns = doc.Root.GetDefaultNamespace();
                string channelName = doc.Root.Element(ns + "title").Value;
                _logger.LogInformation("Get channel name: {ChannelName}", channelName);
                // Create folder for the title
                var safeChannelName = NormalizeYtDlpTitle(channelName);
                string downloadFolder = Path.Combine(config.VideoLocation, safeChannelName);

                if (!Directory.Exists(downloadFolder))
                {
                    Directory.CreateDirectory(downloadFolder);
                    _logger.LogInformation("Created download folder: {Path}", downloadFolder);
                }

                var entries = doc.Descendants(ns + "entry")
                                .Where(e =>
                                {
                                    var url = e.Element(ns + "link")?.Attribute("href")?.Value ?? string.Empty;
                                    return !url.Contains("shorts", StringComparison.OrdinalIgnoreCase);
                                })
                                .Take(episodes);

                foreach (var entry in entries)
                {
                    string title = entry.Element(ns + "title")?.Value;
                    string videoUrl = entry.Element(ns + "link")?.Attribute("href")?.Value;

                    _logger.LogInformation("Found video: {Title} - {Url}", title, videoUrl);
                    _logger.LogInformation("Check if video {Title} exists in {Url}", title, downloadFolder);
                    // Check if a file with this title already exists in the download folder
                    string normalizedTitle = NormalizeYtDlpTitle(title);

                    bool existing = Directory.EnumerateFiles(downloadFolder, "*.mp4")
                                            .Any(file => Path.GetFileNameWithoutExtension(file)
                                            .Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase));
                    if (existing)
                    {
                        _logger.LogInformation("Skipping download, file already exists: {Title}", title);
                        continue;
                    }

                    _logger.LogInformation("Downloading file: {Title} ............", title);

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "yt-dlp",  // because it's already in PATH
                        Arguments = $"\"{videoUrl}\"",
                        WorkingDirectory = downloadFolder,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = new Process { StartInfo = startInfo };
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) _logger.LogDebug("[yt-dlp] {Line}", e.Data); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) _logger.LogInformation("[yt-dlp:stderr] {Line}", e.Data); };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    await process.WaitForExitAsync(cancellationToken);

                    if (process.ExitCode == 0)
                    {
                        _logger.LogInformation("Downloaded successfully: {Title}", title);
                    }
                    else
                    {
                        _logger.LogError("yt-dlp exited with code {Code} for video: {Title}", process.ExitCode, title);
                    }
                }

                if (autoDeletePlayed)
                {
                    _logger.LogInformation("Removing watched episode");
                    var directoryInfo = new DirectoryInfo(downloadFolder);
                    var videoFiles = directoryInfo.EnumerateFiles("*.mp4")
                        .OrderBy(f => f.CreationTimeUtc)
                        .ToList();

                    var remaining = videoFiles.Count;
                    foreach (var file in videoFiles)
                    {
                        if (remaining <= episodes)
                        {
                            break;
                        }

                        if (IsWatched(file))
                        {
                            try
                            {
                                _logger.LogInformation("Removing watched episode {File}", file.Name);

                                var baseName = Path.GetFileNameWithoutExtension(file.Name);
                                var dir = file.DirectoryName ?? string.Empty;

                                foreach (var associatedPath in Directory.EnumerateFiles(dir, baseName + ".*"))
                                {
                                    try
                                    {
                                        File.Delete(associatedPath);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex, "Failed to delete associated file {File}", associatedPath);
                                    }
                                }

                                remaining--;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to delete file {File}", file.FullName);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "YouTube Sync Task failed.");
                throw;
            }

            progress.Report(100.0 * (i + 1) / channelCount);
        }

        progress.Report(100.0);
        _logger.LogInformation("YouTube Sync Task completed successfully.");
    }

    /// <summary>
    /// Determines whether the episode has been watched.
    /// </summary>
    /// <param name="file">The video file to check.</param>
    /// <returns><c>true</c> if the accompanying .nfo file contains &lt;watched&gt;true&lt;/watched&gt;; otherwise, <c>false</c>.</returns>
    private static bool IsWatched(FileInfo file)
    {
        // Jellyfin writes a .nfo file next to each video. The watched state is
        // stored in a &lt;watched&gt; element under the &lt;episodedetails&gt; root.
        var nfoPath = Path.ChangeExtension(file.FullName, ".nfo");

        if (!File.Exists(nfoPath))
        {
            return false;
        }

        try
        {
            var doc = XDocument.Load(nfoPath);
            var watchedValue = doc.Root?.Element("watched")?.Value;
            return string.Equals(watchedValue, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // If the file cannot be parsed, assume it is not watched.
            return false;
        }
    }

    /// <summary>
    /// tststt.
    /// </summary>
    /// <returns>IEnumerable.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromHours(1).Ticks
            }
        };
    }

    private string NormalizeYtDlpTitle(string input)
    {
        return input
            .Replace('|', '｜')// Fullwidth vertical bar
            .Replace(':', '：')// Colon to dash ：
            .Replace('/', '_')// Slashes to underscores
            .Replace('\\', '_')
            .Replace('"', '“')// Quotes to single quote“
            .Replace('<', '_')
            .Replace('>', '_')
            .Replace('*', '_')
            .Replace('?', '？')
            .Trim();
    }
}
