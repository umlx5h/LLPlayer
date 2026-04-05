global using System;
global using System.Collections.Concurrent;
global using System.Collections.Generic;
global using System.Collections.ObjectModel;
global using System.IO;
global using System.Text;
global using System.Threading;
global using System.Threading.Tasks;

global using Flyleaf.FFmpeg;

global using static Flyleaf.FFmpeg.Raw;
global using static FlyleafLib.Logger;
global using static FlyleafLib.Utils;

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Vortice.DXGI;

namespace FlyleafLib;

public enum MediaType
{
    Audio,
    Video,
    Subs,
    Data
}
public enum InputType
{
    File    = 0,
    UNC     = 1,
    Torrent = 2,
    Web     = 3,
    Unknown = 4
}
public enum HDRtoSDRMethod : int
{
    None        = 0,
    Aces        = 1,
    Hable       = 2,
    Reinhard    = 3
}

public enum DeInterlace // Must match with VideoFrameFormat
{
    Progressive,
    TopField,
    BottomField,
    Auto
}
public enum VideoProcessors
{
    Auto,
    D3D11,
    Flyleaf,
    SwsScale
}
public enum SplitFrameAlphaPosition
{
    None,
    Top,
    Left,
    Bottom,
    Right
}
[Flags]
public enum Cropping
{
    None,
    Stream  = 1 << 0,
    Codec   = 1 << 1,
    Texture = 1 << 2
}
public enum ColorSpace : int
{
    None        = 0,
    Bt601       = 1,
    Bt709       = 2,
    Bt2020      = 3
}
public enum ColorRange : int
{
    None        = 0,
    Full        = 1,
    Limited     = 2
}
public enum ColorType
{
    YUV,
    RGB,
    Gray
}
public enum HDRFormat : int
{
    None        = 0,
    DolbyVision = 1,
    HDR         = 2,
    HDRPlus     = 3,
    HLG         = 4,

}

public enum SubOCREngineType
{
    Tesseract,
    MicrosoftOCR
}

public enum SubASREngineType
{
    [Description("whisper.cpp")]
    WhisperCpp,
    [Description("faster-whisper (Recommended)")]
    FasterWhisper
}
public enum UIRefreshType
{
    PerFrame,
    PerFrameSecond,
    PerUIRefreshInterval,
    PerUISecond
}

public enum SwapChainFormat : uint
{
    BGRA        = Vortice.DXGI.Format.B8G8R8A8_UNorm,
    RGBA        = Vortice.DXGI.Format.R8G8B8A8_UNorm,
    RGBA10bit   = Vortice.DXGI.Format.R10G10B10A2_UNorm
}

public enum FLFilters
{
    Brightness,
    Contrast,
    Hue,
    Saturation
}

public class GPUOutput
{
    public nint             Hwnd            { get; internal set; }
    public string           DeviceName      { get; internal set; }
    public int              Left            { get; internal set; }
    public int              Top             { get; internal set; }
    public int              Right           { get; internal set; }
    public int              Bottom          { get; internal set; }
    public int              Width           => Right- Left;
    public int              Height          => Bottom- Top;
    public bool             IsAttached      { get; internal set; }
    public ModeRotation     Rotation        { get; internal set; }
    public float            MaxLuminance    { get; internal set; }
    //public int              RefreshRate     { get; internal set; } // Currently not used

    public override string ToString()
    {
        int gcd = GCD(Width, Height);
        return $"{DeviceName,-20} [Top: {Top,-4}, Left: {Left,-4}, Width: {Width,-4}, Height: {Height,-4}, Ratio: " + (gcd > 0 ? $"{Width / gcd}:{Height / gcd}]" : "]");
    }
}

public class GPUAdapter
{
    public nuint            SystemMemory    { get; internal set; }
    public nuint            VideoMemory     { get; internal set; }
    public nuint            SharedMemory    { get; internal set; }

    public uint             Id              { get; internal set; }
    public GPUVendor        Vendor          { get; internal set; }
    public string           Description     { get; internal set; }
    public long             Luid            { get; internal set; }

    internal IDXGIAdapter   dxgiAdapter;

    public List<GPUOutput>  GetGPUOutputs()    => Engine.Video.GetGPUOutputs(dxgiAdapter);

    public override string  ToString()
        => (Vendor + " " + Description).PadRight(40) + $"[ID: {Id,-6}, LUID: {Luid,-6}, DVM: {GetBytesReadable(VideoMemory),-8}, DSM: {GetBytesReadable(SystemMemory),-8}, SSM: {GetBytesReadable(SharedMemory)}]";
}

public enum GPUVendor : uint
{
    Unknown,
    ATI         = 0x1002,
    Intel       = 0x8086,
    Nvidia      = 0x10DE,
    Qualcomm    = 0x4D4F4351,
    S3Graphics  = 0x5333,
    VIA         = 0x1106,
}

public struct AspectRatio : IEquatable<AspectRatio>
{
    public static readonly AspectRatio Keep     = new(-1,   1);
    public static readonly AspectRatio Fill     = new(-2,   1);
    public static readonly AspectRatio Custom   = new(-3,   1);
    public static readonly AspectRatio Invalid  = new(-999, 1);

    public static readonly List<AspectRatio> AspectRatios =
    [
        Keep,
        Fill,
        Custom,
        new(1,      1),
        new(4,      3),
        new(16,     9),
        new(16,     10),
        new(2.35f,  1),
    ];

    public static implicit operator AspectRatio(string value) => new(value);

    public double Num { get; set; }
    public double Den { get; set; }

    public double Value
    {
        readonly get => Den == 0 ? 0 : Num / Den;
        set { Num = value; Den = 1; }
    }

    public string ValueStr
    {
        readonly get => ToString();
        set => FromString(value);
    }

    public AspectRatio(double value) : this(value, 1) { }
    public AspectRatio(double num, double den) { Num = num; Den = den; }
    public AspectRatio(string value) { Num = Invalid.Num; Den = Invalid.Den; FromString(value); }

    public readonly bool Equals(AspectRatio other) => Num == other.Num && Den == other.Den;
    public override readonly bool Equals(object obj) => obj is AspectRatio o && Equals(o);
    public override readonly int GetHashCode() => HashCode.Combine(Num, Den);
    public static bool operator ==(AspectRatio a, AspectRatio b) => a.Equals(b);
    public static bool operator !=(AspectRatio a, AspectRatio b) => !(a == b);

    public void FromString(string value)
    {
        if (value == "Keep")
            { Num = Keep.Num;       Den = Keep.Den;     return; }
        else if (value == "Fill")
            { Num = Fill.Num;       Den = Fill.Den;     return; }
        else if (value == "Custom")
            { Num = Custom.Num;     Den = Custom.Den;   return; }
        else if (value == "Invalid")
            { Num = Invalid.Num;    Den = Invalid.Den;  return; }

        string newvalue = value.ToString().Replace(',', '.');

        if (Regex.IsMatch(newvalue.ToString(), @"^\s*[0-9\.]+\s*[:/]\s*[0-9\.]+\s*$"))
        {
            string[] values = newvalue.ToString().Split(':');
            if (values.Length < 2)
                        values = newvalue.ToString().Split('/');

            Num = double.Parse(values[0], NumberStyles.Any, CultureInfo.InvariantCulture);
            Den = double.Parse(values[1], NumberStyles.Any, CultureInfo.InvariantCulture);
        }

        else if (double.TryParse(newvalue.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
            { Num = result; Den = 1; }

        else
            { Num = Invalid.Num; Den = Invalid.Den; }
    }
    public override readonly string ToString() => this == Keep ? "Keep" : (this == Fill ? "Fill" : (this == Custom ? "Custom" : (this == Invalid ? "Invalid" : $"{Num}:{Den}")));
}

public struct CropRect(uint top = 0, uint left= 0, uint bottom= 0, uint right= 0) : IEquatable<CropRect>
{
    public static readonly CropRect Empty;

    public uint             Top     = top;
    public uint             Left    = left;
    public uint             Bottom  = bottom;
    public uint             Right   = right;

    public readonly uint    Width   => Right  + Left;
    public readonly uint    Height  => Bottom + Top;
    public readonly bool    IsEmpty => Top == 0 && Left == 0 && Bottom == 0 && Right == 0;

    public static CropRect operator +(CropRect a, CropRect b)
        => new(a.Top + b.Top, a.Left + b.Left, a.Bottom + b.Bottom, a.Right + b.Right);
    public static CropRect operator -(CropRect a, CropRect b)
        => new(a.Top - b.Top, a.Left - b.Left, a.Bottom - b.Bottom, a.Right - b.Right);
    public static bool operator ==(CropRect a, CropRect b)
        => a.Top == b.Top && a.Bottom == b.Bottom && a.Left == b.Left && a.Right == b.Right;
    public static bool operator !=(CropRect left, CropRect right)
        => !(left == right);
    public readonly bool Equals(CropRect other)
        => this == other;
    public override readonly bool Equals(object obj)
        => obj is CropRect other && this == other;
    public override readonly int GetHashCode()
        => HashCode.Combine(Top, Bottom, Left, Right);
    public override readonly string ToString()
        => $"[Top: {Top}, Left: {Left}, Bottom: {Bottom}, Right: {Right}]";
}

class PlayerStats
{
    public long TotalBytes      { get; set; }
    public long VideoBytes      { get; set; }
    public long AudioBytes      { get; set; }
    public long FramesDisplayed { get; set; }
}

public class NotifyPropertyChanged : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    //public bool DisableNotifications { get; set; }

    //private static bool IsUI() => System.Threading.Thread.CurrentThread.ManagedThreadId == System.Windows.Application.Current.Dispatcher.Thread.ManagedThreadId;

    protected bool Set<T>(ref T field, T value, bool check = true, [CallerMemberName] string propertyName = "")
    {
        //Log($"[===| {propertyName} |===] | Set | {IsUI()}");

        if (!check || !EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;

            //if (!DisableNotifications)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

            return true;
        }

        return false;
    }

    protected bool SetUI<T>(ref T field, T value, bool check = true, [CallerMemberName] string propertyName = "")
    {
        //Log($"[===| {propertyName} |===] | SetUI | {IsUI()}");

        if (!check || !EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;

            //if (!DisableNotifications)
            UI(() => PropertyChanged?.Invoke(this, new(propertyName)));

            return true;
        }

        return false;
    }
    protected void Raise([CallerMemberName] string propertyName = "")
    {
        //Log($"[===| {propertyName} |===] | Raise | {IsUI()}");

        //if (!DisableNotifications)
        PropertyChanged?.Invoke(this, new(propertyName));
    }


    protected void RaiseUI([CallerMemberName] string propertyName = "")
    {
        //Log($"[===| {propertyName} |===] | RaiseUI | {IsUI()}");

        //if (!DisableNotifications)
        UI(() => PropertyChanged?.Invoke(this, new(propertyName)));
    }
}
