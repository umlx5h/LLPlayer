using System.IO;

namespace FlyleafLib.MediaFramework.MediaDemuxer;

public unsafe class CustomIOContext
{
    AVIOContext*    avioCtx;
    public Stream   stream;
    Demuxer         demuxer;

    public CustomIOContext(Demuxer demuxer)
    {
        this.demuxer = demuxer;
    }

    public void Initialize(Stream stream)
    {
        this.stream = stream;
        //this.stream.Seek(0, SeekOrigin.Begin);

        ioread = IORead;
        ioseek = IOSeek;
        avioCtx = avio_alloc_context((byte*)av_malloc((nuint)demuxer.Config.IOStreamBufferSize), demuxer.Config.IOStreamBufferSize, 0, null, ioread, null, ioseek);
        demuxer.FormatContext->pb     = avioCtx;
        demuxer.FormatContext->flags |= FmtFlags2.CustomIo;
    }

    public void Dispose()
    {
        if (avioCtx != null)
        {
            av_free(avioCtx->buffer);
            fixed (AVIOContext** ptr = &avioCtx) avio_context_free(ptr);
        }
        avioCtx = null;
        stream = null;
        ioread = null;
        ioseek = null;
    }

    avio_alloc_context_read_packet  ioread;
    avio_alloc_context_seek         ioseek;

    int IORead(void* opaque, byte* buffer, int bufferSize)
    {
        int ret;

        if (demuxer.Interrupter.ShouldInterrupt(null) != 0) return AVERROR_EXIT;

        ret = demuxer.CustomIOContext.stream.Read(new Span<byte>(buffer, bufferSize));

        if (ret > 0)
            return ret;

        if (ret == 0)
            return AVERROR_EOF;

        demuxer.Log.Warn("CustomIOContext Interrupted");

        return AVERROR_EXIT;
    }

    long IOSeek(void* opaque, long offset, IOSeekFlags whence)
    {
        //System.Diagnostics.Debug.WriteLine($"** S | {decCtx.demuxer.fmtCtx->pb->pos} - {decCtx.demuxer.ioStream.Position}");

        return whence == IOSeekFlags.Size
            ? demuxer.CustomIOContext.stream.Length
            : demuxer.CustomIOContext.stream.Seek(offset, (SeekOrigin) whence);
    }
}
