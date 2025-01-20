using FlyleafLib.MediaFramework.MediaDemuxer;

namespace FlyleafLib.MediaFramework.MediaStream;

public unsafe class DataStream : StreamBase
{

    public DataStream() { }
    public DataStream(Demuxer demuxer, AVStream* st) : base(demuxer, st)
    {
        Demuxer = demuxer;
        AVStream = st;
        Refresh();
    }

    public override void Refresh()
    {
        base.Refresh();
    }

    public override string GetDump()
        => $"[{Type} #{StreamIndex}] {CodecID}";
}
