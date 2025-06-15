using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
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

    private static readonly string[] VideoExtensions =
    [
        ".mp4",
        ".mkv",
        ".webm",
        ".flv",
        ".avi",
        ".mov"
    ];

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

        string youtubeUrl = config.YoutubeUrl;
        int episodes = config.Episodes;
        bool autoDeletePlayed = config.AutoDeletePlayed;

        string[] channels = youtubeUrl.Split(',');
        foreach (string channelId in channels)
        {
            _logger.LogInformation("Syncing from {YoutubeUrl}, Episodes: {Episodes}, AutoDelete: {AutoDelete}", channelId, episodes, autoDeletePlayed);

            _logger.LogInformation("Saving to: {VideoLocation}, Episodes: {Episodes}, AutoDelete: {AutoDeletePlayed}", videoLocation, episodes, autoDeletePlayed);

            try
            {
                using var httpClient = new HttpClient();

                _logger.LogInformation("Extract channel ID from handle page: {ChannelId}", channelId);
                string feedUrl = $"https://www.youtube.com/feeds/videos.xml?channel_id={channelId}";

                string xml = await httpClient.GetStringAsync(feedUrl);

                var doc = XDocument.Parse(xml);
                var ns = doc.Root.GetDefaultNamespace();
                string channelName = doc.Root.Element(ns + "title").Value;

                progress.Report(10);

                // Create folder for the title

                string downloadFolder = Path.Combine(config.VideoLocation, $"{channelName}");

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
                    _logger.LogInformation(".........entry.........");

                    string title = entry.Element(ns + "title")?.Value;
                    string videoUrl = entry.Element(ns + "link")?.Attribute("href")?.Value;

                    _logger.LogInformation("Found video: {Title} - {Url}", title, videoUrl);

                    // Check if a file with this title already exists in the download folder
                    bool existing = VideoExtensions
                        .Select(ext => Path.Combine(downloadFolder, $"{title}{ext}"))
                        .Any(File.Exists);

                    if (existing)
                    {
                        _logger.LogInformation("Skipping download, file already exists: {Title}", title);
                        continue;
                    }

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
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) _logger.LogInformation("[yt-dlp] {Line}", e.Data); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) _logger.LogWarning("[yt-dlp:stderr] {Line}", e.Data); };

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
                    var directoryInfo = new DirectoryInfo(downloadFolder);
                    var videoFiles = directoryInfo.EnumerateFiles()
                        .Where(f => VideoExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
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

                _logger.LogInformation("YouTube Sync Task completed successfully.");
                progress.Report(100);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "YouTube Sync Task failed.");
                throw;
            }
        }
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
}
