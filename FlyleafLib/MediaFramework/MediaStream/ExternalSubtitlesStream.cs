using System.IO;
using System.Linq;

namespace FlyleafLib.MediaFramework.MediaStream;

public class ExternalSubtitlesStream : ExternalStream, ISubtitlesStream
{
    public SelectedSubMethod[] SelectedSubMethods
    {
        get
        {
            var methods = (SelectSubMethod[])Enum.GetValues(typeof(SelectSubMethod));

            if (!IsBitmap)
            {
                // delete OCR if text sub
                methods = methods.Where(m => m != SelectSubMethod.OCR).ToArray();
            }

            return methods.
                Select(m => new SelectedSubMethod(this, m)).ToArray();
        }
    }

    public bool     IsBitmap        { get; set; }
    public bool     ManualDownloaded{ get; set; }
    public bool     Downloaded      { get; set; }
    public Language Language        { get; set; } = Language.Unknown;
    public bool     LanguageDetected{ get; set; }
    public float    Rating          { get; set; } // 1.0-10.0 (0: not set)
    // TODO: Add confidence rating (maybe result is for other movie/episode) | Add Weight calculated based on rating/downloaded/confidence (and lang?) which can be used from suggesters
    public string   Title           { get; set; }
    public string   FileName => Path.GetFileName(Url);

    public string   DisplayMember =>
        $"({Language}){(ManualDownloaded ? " (DL)" : "")} {Utils.TruncateString(FileName, 60)} ({(IsBitmap ? "BMP" : "TXT")})";
}
