namespace FlyleafLib.MediaFramework.MediaStream;

public class ExternalVideoStream : ExternalStream
{
    public double   FPS             { get; set; }
    public int      Height          { get; set; }
    public int      Width           { get; set; }

    public bool     HasAudio        { get; set; }

    public string   DisplayMember =>
        $"{Width}x{Height} @{Math.Round(FPS, 2, MidpointRounding.AwayFromZero)} ({Codec}) [{Protocol}]{(HasAudio ? "" : " [NA]")}";
}
