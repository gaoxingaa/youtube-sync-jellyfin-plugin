namespace Jellyfin.Plugin.YoutubeSync.Configuration;

public class YoutubeChannelConfig
{
    public string YoutubeUrl { get; set; }

    public int Episodes { get; set; }

    public bool AutoDeletePlayed { get; set; }
}
