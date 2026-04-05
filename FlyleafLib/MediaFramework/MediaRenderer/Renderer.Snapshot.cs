using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media.Imaging;

using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

using ID3D11Device      = Vortice.Direct3D11.ID3D11Device;
using ID3D11Texture2D   = Vortice.Direct3D11.ID3D11Texture2D;
using ID3D11VideoDevice = Vortice.Direct3D11.ID3D11VideoDevice;

using FlyleafLib.MediaFramework.MediaFrame;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{
    Snapshot snapshot;

    Snapshot GetSnapshot(uint width, uint height)
    {
        if (snapshot != null)
        {
            if (snapshot.Width == width && snapshot.Height == height)
                return snapshot;

            snapshot.Dispose();
        }

        return snapshot = new(device, vd, ve, width, height);
    }

    public Bitmap TakeSnapshot(uint width = 0, uint height = 0)
    {
        try
        {
            if (width == 0 && height == 0)
            {
                width  = VisibleWidth;
                height = VisibleHeight;
            }
            else if (width != 0 && height == 0)
                height = (uint)(width / curRatio);
            else if (height != 0 && width == 0)
                width  = (uint)(height * curRatio);

            var snapshot = GetSnapshot(width, height);

            lock (lockRenderLoops)
            {
                var rFrame = Frames.RendererFrame;

                if (rFrame == null)
                    return null;

                if (VideoProcessor == VideoProcessors.D3D11)
                {
                    vc.VideoProcessorGetStreamDestRect  (vp, 0, out _, out var d3destOld);
                    vc.VideoProcessorGetOutputTargetRect(vp,    out _, out var d3outOld);
                    D3Render(rFrame.VPIV, snapshot.d3rtv, snapshot.d3view);
                    vc.VideoProcessorSetStreamDestRect  (vp, 0, true, d3destOld);
                    vc.VideoProcessorSetOutputTargetRect(vp,    true, d3outOld);
                }
                else
                {
                    FLRender(rFrame.SRV, snapshot.rtv, snapshot.view);
                    context.RSSetViewport(Viewport);
                }
            }
            
            context.CopyResource(snapshot.txtStage, snapshot.txt);

            return GetBitmap(snapshot.txtStage);
        }
        catch { return null; }   
    }

    #region Bitmap Helpers
    public Bitmap GetBitmap(VideoFrame frame, uint width = 0, uint height = 0)
    {   // Extractor example (should be used separate from Player-OnScreen rendering)
        // TBR: Consider using pre-configured textures for better performance (possible caller's responsibility)

        if (width == 0 && height == 0)
        {
            width  = VisibleWidth;
            height = VisibleHeight;
        }
        else if (width != 0 && height == 0)
            height = (uint)(width / curRatio);
        else if (height != 0 && width == 0)
            width  = (uint)(height * curRatio);

        var snapshot = GetSnapshot(width, height);

        if (VideoProcessor == VideoProcessors.D3D11)
            D3Render(frame.VPIV, snapshot.d3rtv, snapshot.d3view);
        else
            FLRender(frame.SRV, snapshot.rtv, snapshot.view);
            
        context.CopyResource(snapshot.txtStage, snapshot.txt);

        return GetBitmap(snapshot.txtStage);
    }
    public Bitmap GetBitmap(ID3D11Texture2D txtStage)
    {
        var bmp     = new Bitmap((int)txtStage.Description.Width, (int)txtStage.Description.Height);
        var db      = context.Map(txtStage, 0);
        var bmpData = bmp.LockBits(new(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        if (db.RowPitch == bmpData.Stride)
            MemoryHelpers.CopyMemory(bmpData.Scan0, db.DataPointer, bmp.Width * bmp.Height * 4);
        else
        {
            var srcPtr  = db.DataPointer;
            var dstPtr  = bmpData.Scan0;

            for (int y = 0; y < bmp.Height; y++)
            {
                MemoryHelpers.CopyMemory(dstPtr, srcPtr, bmp.Width * 4);

                srcPtr  = nint.Add(srcPtr, (int)db.RowPitch);
                dstPtr  = nint.Add(dstPtr, bmpData.Stride);
            }
        }

        bmp.UnlockBits(bmpData);
        context.Unmap(txtStage, 0);

        return bmp;
    }
    public BitmapSource GetBitmapSource(ID3D11Texture2D txtStage)
    {
        var bmp = new WriteableBitmap((int)txtStage.Description.Width, (int)txtStage.Description.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var db  = context.Map(txtStage, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        bmp.Lock();

        if (db.RowPitch == bmp.BackBufferStride)
            MemoryHelpers.CopyMemory(bmp.BackBuffer, db.DataPointer, bmp.PixelWidth * bmp.PixelHeight * 4);
        else
        {
            var srcPtr  = db.DataPointer;
            var dstPtr  = bmp.BackBuffer;

            for (int y = 0; y < bmp.Height; y++)
            {
                MemoryHelpers.CopyMemory(dstPtr, srcPtr, bmp.PixelWidth * 4);

                srcPtr = nint.Add(srcPtr, (int)db.RowPitch);
                dstPtr = nint.Add(dstPtr, bmp.BackBufferStride);
            }
        }

        bmp.Unlock();
        context.Unmap(txtStage, 0);

        // Freezing animated wpf assets improves performance
        bmp.Freeze();

        return bmp;
    }
    #endregion
}

class Snapshot
{
    public uint Width    { get; set; }
    public uint Height   { get; set; }

    public ID3D11Texture2D                 txt, txtStage;
    public ID3D11RenderTargetView          rtv;
    public ID3D11VideoProcessorOutputView  d3rtv;
    public Viewport                        view;
    public RawRect                         d3view;

    public Snapshot(ID3D11Device device, ID3D11VideoDevice vd, ID3D11VideoProcessorEnumerator ve, uint width, uint height)
    {
        Width       = width;
        Height      = height;
        view        = new(Width, Height);

        var txtStageDesc= new Texture2DDescription()
        {
            Usage               = ResourceUsage.Staging,
            CPUAccessFlags      = CpuAccessFlags.Read,
            Format              = Format.B8G8R8A8_UNorm,
            ArraySize           = 1,
            MipLevels           = 1,
            BindFlags           = BindFlags.None,
            SampleDescription   = new(1, 0),
            Width               = Width,
            Height              = Height
        };

        var txtDesc     = new Texture2DDescription()
        {
            Usage               = ResourceUsage.Default,
            Format              = Format.B8G8R8A8_UNorm,
            ArraySize           = 1,
            MipLevels           = 1,
            BindFlags           = BindFlags.RenderTarget | BindFlags.ShaderResource,
            SampleDescription   = new(1, 0),
            Width               = Width,
            Height              = Height
        };

        txtStage    = device.CreateTexture2D        (txtStageDesc);
        txt         = device.CreateTexture2D        (txtDesc);
        rtv         = device.CreateRenderTargetView (txt);

        if (vd != null)
        {
            d3view  = new(0, 0, (int)Width, (int)Height);
            d3rtv   = vd.CreateVideoProcessorOutputView(txt, ve, new() { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
        }
    }

    public void Dispose()
    {
        d3rtv?.     Dispose();
        rtv.        Dispose();
        txtStage.   Dispose();
        txt.        Dispose();
    }
}
