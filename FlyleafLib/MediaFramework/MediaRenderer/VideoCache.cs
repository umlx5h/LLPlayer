using FlyleafLib.MediaFramework.MediaFrame;

using static FlyleafLib.Config;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public class VideoCache
{
    public long         Count       { get { lock (this) return Last == null ? 0 : Last.Id + 1 - First.Id; } }
    //public long         CountFront  => Current == null ? 0 : Last.Id - Current.Id;
    public bool         IsEmpty     => Count == 0;
    public VideoFrame   Next        => Current?.Next;

    DecoderConfig       dcfg;
    internal VideoFrame RendererFrame, First, Last, Current;
    long                curId;
    bool                isCurrentNext;
    bool                disposeRenderFrame;

    internal VideoCache(DecoderConfig dcfg)
        => this.dcfg = dcfg;

    public void Enqueue(VideoFrame frame, bool used = false)
    {   // Called by Decoder & ShowFrame (used = true to force next as current)
        lock (this)
        {
            frame.Id = curId++;

            if (Last == null)
            {
                isCurrentNext = !used;
                First = Current = Last = frame;
                //Log($"E: [{First.Id} {Last.Id}] {(Current != null ? Current.Id : "-")}");
            }
            else
            {
                frame.Prev  = Last;
                Last.Next   = frame;
                Last        = frame;

                if (Current == null || used)
                {
                    isCurrentNext = !used;
                    Current = Last;

                    while (Current.Id - First.Id > dcfg.MaxVideoFramesPrev)
                        { Shrink(); if (First == null) break; }

                    //if (First != null) Log($"E: [{First.Id} {Last.Id}] {(Current != null ? Current.Id : "-")} *");
                }
                //else
                //    Log($"E: [{First.Id} {Last.Id}] {(Current != null ? Current.Id : "-")}");
            }
        }
    }

    public void PushCurrentToLast()
    {   // For Enqueues that don't check MaxVideoFrames (forces shrink)
        lock (this)
        {
            Current = null;
            while (Last != null && Last.Id - First.Id > dcfg.MaxVideoFramesPrev - 1)
                Shrink();
        }
    }

    public bool TryDequeue(out VideoFrame frame)
    {
        lock (this)
        {
            if (Current == null)
            {
                frame = null;
                return false;
            }

            if (isCurrentNext)
            {
                //Log($"D: [{First.Id} {Last.Id}] {(Current != null ? Current.Id : "-")} *");

                frame = Current;
                isCurrentNext = false;
                return true;
            }

            var id  = Current.Id;   // we might dispose/null it during shrink
            Current = Current.Next;

            while (id - First.Id > dcfg.MaxVideoFramesPrev - 1)
                { Shrink(); if (First == null) break; }

            //if (First != null) Log($"D: [{First.Id} {Last.Id}] {(Current != null ? Current.Id : "-")}");

            if (Current != null)
            {
                frame = Current;
                return true;
            }

            frame = null;
            return false;
        }
    }

    void Shrink()
    {
        var first   = First;
        First       = First.Next;

        if (First == null)
            Last = null;
        else
            First.Prev = null;

        if (first  != RendererFrame)
            first.Dispose();
        else
            disposeRenderFrame = true;
    }

    public void SetRendererFrame(VideoFrame frame)
    {
        lock (this)
        {
            if (RendererFrame != null && disposeRenderFrame)
                RendererFrame.Dispose();

            disposeRenderFrame = false;
            RendererFrame = frame;
        }
    }

    public void Reset()
    {
        lock (this)
        {
            var cur = First;
            First = Current = Last = null;

            while (cur != null)
            {
                var next = cur.Next;
                if (cur != RendererFrame)
                    cur.Dispose();
                else
                    disposeRenderFrame = true;
                cur = next;
            }
        }
    }

    public void Dispose()
    {
        lock (this)
        {
            var cur = First;
            First = Current = Last = null;

            while (cur != null)
            {
                var next = cur.Next;
                cur.Dispose();
                cur = next;
            }

            if (RendererFrame != null)
            {
                RendererFrame.Dispose();
                RendererFrame = null;
            }

            disposeRenderFrame = false;
        }
    }
}


