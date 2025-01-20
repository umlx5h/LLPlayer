using System.Linq;

namespace FlyleafLib.MediaFramework.MediaStream;

public class ExternalSubtitlesStream : ExternalStream, ISubtitlesStream
{
    public SelectedSubMethod[] SelectedSubMethods
    {
        get
        {
            var methods = (SelectSubMethod[])Enum.GetValues(typeof(SelectSubMethod));

            return methods.
                Select(m => new SelectedSubMethod(this, m)).ToArray();
        }
    }

    public bool     IsBitmap        { get; set; }
    public bool     Downloaded      { get; set; }
    public Language Language        { get; set; } = Language.Unknown;
    public bool     LanguageDetected{ get; set; }
    public float    Rating          { get; set; } // 1.0-10.0 (0: not set)
    // TODO: Add confidence rating (maybe result is for other movie/episode) | Add Weight calculated based on rating/downloaded/confidence (and lang?) which can be used from suggesters
    public string   Title           { get; set; }

    public string   DisplayMember =>
        $"({Language}) {Title} ({(IsBitmap ? "BMP" : "TXT")})";
}
