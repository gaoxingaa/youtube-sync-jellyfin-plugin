using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YoutubeSync.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        // set default options here
        VideoLocation = "c:\\video";
        ChannelIds = string.Empty;
        Episodes = 3;
        AutoDeletePlayed = false;
    }

    /// <summary>
    /// Gets or sets a value indicating whether AutoDeletePlayed setting is enabled..
    /// </summary>
    public bool AutoDeletePlayed { get; set; }

    /// <summary>
    /// Gets or sets Episodes setting.
    /// </summary>
    public int Episodes { get; set; }

    /// <summary>
    /// Gets or sets ChannelIds setting.
    /// </summary>
    public string ChannelIds { get; set; }

    /// <summary>
    /// Gets or sets VideoLocation setting.
    /// </summary>
    public string VideoLocation { get; set; }
}
