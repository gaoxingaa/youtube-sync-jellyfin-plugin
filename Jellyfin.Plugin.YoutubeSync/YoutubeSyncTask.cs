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
