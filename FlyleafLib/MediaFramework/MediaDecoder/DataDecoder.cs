using System.Runtime.InteropServices;

using FlyleafLib.MediaFramework.MediaFrame;
using FlyleafLib.MediaFramework.MediaStream;

namespace FlyleafLib.MediaFramework.MediaDecoder;
public unsafe class DataDecoder : DecoderBase
{
    public DataStream DataStream => (DataStream)Stream;

    public ConcurrentQueue<DataFrame>
                            Frames              { get; protected set; } = [];

    public DataDecoder(Config config, int uniqueId = -1) : base(config, uniqueId) { }


    protected override unsafe bool Setup() => true;
    protected override void DisposeInternal()
        => Frames = [];

    public void Flush()
    {
        lock (lockActions)
            lock (lockCodecCtx)
            {
                if (Disposed)
                    return;

                if (Status == Status.Ended)
                    Status = Status.Stopped;
                else if (Status == Status.Draining)
                    Status = Status.Stopping;

                DisposeFrames();
            }
    }

    protected override void RunInternal()
    {
        int allowedErrors = Config.Decoder.MaxErrors;
        AVPacket *packet;

        do
        {
            // Wait until Queue not Full or Stopped
            if (Frames.Count >= Config.Decoder.MaxDataFrames)
            {
                lock (lockStatus)
                    if (Status == Status.Running)
                        Status = Status.QueueFull;

                while (Frames.Count >= Config.Decoder.MaxDataFrames && Status == Status.QueueFull)
                    Thread.Sleep(20);

                lock (lockStatus)
                {
                    if (Status != Status.QueueFull)
                        break;
                    Status = Status.Running;
                }
            }

            // While Packets Queue Empty (Ended | Quit if Demuxer stopped | Wait until we get packets)
            if (demuxer.DataPackets.Count == 0)
            {
                CriticalArea = true;

                lock (lockStatus)
                    if (Status == Status.Running)
                        Status = Status.QueueEmpty;

                while (demuxer.DataPackets.Count == 0 && Status == Status.QueueEmpty)
                {
                    if (demuxer.Status == Status.Ended)
                    {
                        Status = Status.Draining;
                        break;
                    }
                    else if (!demuxer.IsRunning)
                    {
                        int retries = 5;

                        while (retries > 0)
                        {
                            retries--;
                            Thread.Sleep(10);
                            if (demuxer.IsRunning)
                                break;
                        }

                        lock (demuxer.lockStatus)
                            lock (lockStatus)
                            {
                                if (demuxer.Status == Status.Pausing || demuxer.Status == Status.Paused)
                                    Status = Status.Pausing;
                                else if (demuxer.Status == Status.QueueFull)
                                    Status = Status.Draining;
                                else if (demuxer.Status == Status.Running)
                                    continue;
                                else if (demuxer.Status != Status.Ended)
                                    Status = Status.Stopping;
                                else
                                    continue;
                            }

                        break;
                    }

                    Thread.Sleep(20);
                }

                lock (lockStatus)
                {
                    CriticalArea = false;
                    if (Status != Status.QueueEmpty && Status != Status.Draining)
                        break;
                    if (Status != Status.Draining)
                        Status = Status.Running;
                }
            }

            lock (lockCodecCtx)
            {
                if (Status == Status.Stopped || demuxer.DataPackets.Count == 0)
                    continue;
                packet = demuxer.DataPackets.Dequeue();

                if (packet->size > 0 && packet->data != null)
                {
                    var mFrame = ProcessDataFrame(packet);
                    Frames.Enqueue(mFrame);
                }

                av_packet_free(&packet);
            }
        } while (Status == Status.Running);

        if (Status == Status.Draining) Status = Status.Ended;
    }

    private DataFrame ProcessDataFrame(AVPacket* packet)
    {
        IntPtr ptr = new(packet->data);
        byte[] dataFrame = new byte[packet->size];
        Marshal.Copy(ptr, dataFrame, 0, packet->size); // for performance/in case of large data we could just keep the avpacket data directly (DataFrame instead of byte[] Data just use AVPacket* and move ref?)

        DataFrame mFrame = new()
        {
            Timestamp   = (long)(packet->pts * DataStream.Timebase) - demuxer.StartTime,
            DataCodecId = DataStream.CodecID,
            Data        = dataFrame
        };

        return mFrame;
    }

    public void DisposeFrames()
        => Frames = [];
}
