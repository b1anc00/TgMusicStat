namespace TGMusicStat.Media;

public class TrackInfo
{
    public string Title { get; }
    public string Artist { get; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Artist)
            ? Title
            : $"{Artist} - {Title}";

    public TrackInfo(string title, string artist)
    {
        Title = title;
        Artist = artist;
    }
}