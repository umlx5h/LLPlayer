using Vortice.Direct3D11;

using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace FlyleafLib.MediaFramework.MediaFrame;

public unsafe class VideoFrame : FrameBase
{
    public ID3D11Texture2D[]                Texture;    // Planes (we just keep them alive for SRVs - not used anywhere*)
    public ID3D11ShaderResourceView[]       SRV;        // Views (FlyleafVP)
    public ID3D11VideoProcessorInputView    VPIV;       // Views (D3D11VP)
    public AVFrame* AVFrame;                            // HW Decoded only - to keep the extra ref alive

    public VideoFrame Prev, Next;
    public long Id;

    public void Dispose()
    {   // Manually dipose only when not in VC
        Prev = Next = null; // Could null Next.Prev here

        DisposeTexture();

        if (AVFrame != null)
        {
            fixed(AVFrame** ptr = &AVFrame) av_frame_free(ptr);
            AVFrame = null;
        }
            
    }

    public void DisposeTexture()
    {
        if (Texture != null)
        {
            for (int i = 0; i < Texture.Length; i++)
                Texture[i].Dispose();

            Texture = null;
        }

        if (SRV != null)
        {
            for (int i = 0; i < SRV.Length; i++)
                SRV[i].Dispose();

            SRV = null;
        }

        if (VPIV != null)
        {
            VPIV.Dispose();
            VPIV = null;
        }
    }
}
