using Vortice.Direct3D11;
using Vortice.DXGI;

using ID3D11Texture2D   = Vortice.Direct3D11.ID3D11Texture2D;
using MapFlags          = Vortice.Direct3D11.MapFlags;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{
    const string dHDRDetect = "dHDRDetect";

    const int   hdrBuffers          = 3;
    const int   hdrHistogramBins    = 4096;
    const int   hdrMaxPixels        = 256 * 1024;

    const float hdrBlackCutoff      = 0.01f;  // PQ 1%
    const float hdrBlackPercentile  = 0.50f;
    const float hdrBlackMaxAdvance  = 0.049f; // PQ 4.9%
    const float hdrPeakPercentile   = 99.995f;
    const float hdrSmoothingPeriod  = 20.0f;
    const float hdrSceneThresholdLow= 0.01f;  // PQ 1%
    const float hdrSceneThresholdHigh=0.03f;  // PQ 3%

    ID3D11PixelShader       psHdr;
    ID3D11Texture2D         txtHdr;
    ID3D11RenderTargetView  rtvHdr;
    ID3D11Texture2D[]       txtStageHdr         = new ID3D11Texture2D[hdrBuffers];
    ID3D11Query[]           queryHdr            = new ID3D11Query[hdrBuffers];
    bool[]                  hdrPending          = new bool[hdrBuffers];
    int[]                   hdrPendingGen       = new int[hdrBuffers];
    uint[]                  hdrHistogram        = new uint[hdrHistogramBins];

    int                     hdrWidth;
    int                     hdrHeight;
    int                     hdrSourceWidth;
    int                     hdrSourceHeight;
    int                     hdrWriteIndex;
    int                     hdrReadIndex;
    int                     hdrGeneration;
    bool                    hdrHasStats;
    bool                    hdrSyncNext;

    internal HDRLumiStats   hdrFrameStats;
    internal HDRLumiStats   hdrStats;

    internal struct HDRLumiStats
    {
        public float MinPQ;
        public float AvgPQ;
        public float PeakPQ;

        public HDRLumiStats(float minPQ, float avgPQ, float peakPQ)
        {
            MinPQ  = MathF.Min(minPQ, avgPQ);
            AvgPQ  = Math.Clamp(avgPQ, MinPQ, peakPQ);
            PeakPQ = MathF.Max(peakPQ, AvgPQ);
        }
    }

    void FLHDRSetPS(ReadOnlySpan<char> sampleHLSL, List<string> defines)
    {
        psHdr = null;

        if (defines == null || !defines.Contains(dPQSpline))
            return;

        string detectId = psId + "D";

        if (!psShader.TryGetValue(detectId, out psHdr))
        {
            psHdr = ShaderCompiler.CompilePS(device, detectId, sampleHLSL, [.. defines, dHDRDetect]);
            psShader[detectId] = psHdr;
        }
    }

    internal void FLHDRDetectReset()
    {
        hdrGeneration++;
        hdrHasStats     = false;
        hdrSyncNext     = true;
        hdrFrameStats   = default;
        hdrStats        = default;
    }

    void FLHDRDetect()
    {
        if (psHdr == null)
            return;

        FLHDRSetup();

        if (hdrSyncNext)
        {
            FLHDRSync();
            return;
        }

        FLHDRRead();

        if (hdrPending[hdrWriteIndex])
            return;

        int index = hdrWriteIndex;

        FLHDRSubmit(index);
        hdrWriteIndex = (hdrWriteIndex + 1) % hdrBuffers;
        FLHDRRestore();
    }

    void FLHDRSubmit(int index)
    {
        context.OMSetRenderTargets(rtvHdr);
        context.RSSetViewport(0, 0, hdrWidth, hdrHeight);
        context.VSSetShader(vsMain);
        context.PSSetShader(psHdr);
        context.Draw(6, 0);

        context.CopyResource(txtStageHdr[index], txtHdr);
        context.End(queryHdr[index]);

        hdrPending[index]   = true;
        hdrPendingGen[index]= hdrGeneration;
    }

    void FLHDRRestore()
    {
        context.OMSetRenderTargets(SwapChain.BackBufferRtv);
        context.RSSetViewport(Viewport);
        //context.VSSetShader(vsMain);
        context.PSSetShader(psShader[psIdPrev]);
    }

    void FLHDRSync()
    {
        FLHDRDiscardPending();

        int index = 0;

        FLHDRSubmit(index);
        context.Flush();

        while (!context.IsDataAvailable(queryHdr[index], AsyncGetDataFlags.DoNotFlush))
            Thread.Yield();

        var db = context.Map(txtStageHdr[index], 0, MapMode.Read, MapFlags.None);
        FLHDRRead(db);
        context.Unmap(txtStageHdr[index], 0);

        hdrPending[index] = false;
        hdrWriteIndex     = 0;
        hdrReadIndex      = 0;
        hdrSyncNext       = false;

        FLHDRRestore();
    }

    void FLHDRDiscardPending()
    {
        bool flushed = false;

        for (int i = 0; i < hdrBuffers; i++)
        {
            if (!hdrPending[i])
                continue;

            if (!flushed)
            {
                context.Flush();
                flushed = true;
            }

            while (!context.IsDataAvailable(queryHdr[i], AsyncGetDataFlags.DoNotFlush))
                Thread.Yield();

            hdrPending[i] = false;
        }

        hdrWriteIndex = 0;
        hdrReadIndex  = 0;
    }

    void FLHDRSetup()
    {
        int sourceWidth  = (int)scfg.txtWidth;
        int sourceHeight = (int)scfg.txtHeight;

        // Source dimensions are stable for the configured stream. Avoid the
        // sqrt/scale calculation on every rendered HDR frame.
        if (txtHdr != null &&
            hdrSourceWidth == sourceWidth && hdrSourceHeight == sourceHeight)
            return;

        if (sourceWidth < 1 || sourceHeight < 1)
            return;

        double scale = Math.Min(1.0,
            Math.Sqrt(hdrMaxPixels / ((double)sourceWidth * sourceHeight)));

        int width  = Math.Max(1, (int)Math.Round(sourceWidth  * scale));
        int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));

        FLHDRDisposeResources();

        hdrSourceWidth  = sourceWidth;
        hdrSourceHeight = sourceHeight;
        hdrWidth        = width;
        hdrHeight       = height;

        Texture2DDescription desc = new()
        {
            Width               = (uint)width,
            Height              = (uint)height,
            MipLevels           = 1,
            ArraySize           = 1,
            Format              = Format.R16_Float,
            SampleDescription   = new(1, 0),
            Usage               = ResourceUsage.Default,
            BindFlags           = BindFlags.RenderTarget,
            CPUAccessFlags      = CpuAccessFlags.None
        };

        txtHdr  = device.CreateTexture2D(desc);
        rtvHdr  = device.CreateRenderTargetView(txtHdr);

        desc.Usage          = ResourceUsage.Staging;
        desc.BindFlags      = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;

        for (int i = 0; i < hdrBuffers; i++)
        {
            txtStageHdr[i] = device.CreateTexture2D(desc);
            queryHdr[i] = device.CreateQuery(QueryType.Event);
        }

        hdrSyncNext = true; // "1st" frame always synced
    }

    void FLHDRRead()
    {
        // Latest-wins: when more than one asynchronous readback completed, only
        // scan the newest one. Older histograms are stale and would just add CPU
        // work plus extra latency to the dynamic mapper.
        int newest = -1;

        while (hdrPending[hdrReadIndex])
        {
            int index = hdrReadIndex;

            if (!context.IsDataAvailable(queryHdr[index], AsyncGetDataFlags.DoNotFlush))
                break;

            if (hdrPendingGen[index] == hdrGeneration)
                newest = index;

            hdrPending[index] = false;
            hdrReadIndex      = (hdrReadIndex + 1) % hdrBuffers;
        }

        if (newest < 0)
            return;

        var db = context.Map(txtStageHdr[newest], 0, MapMode.Read, MapFlags.None);
        FLHDRRead(db);
        context.Unmap(txtStageHdr[newest], 0);
    }

    void FLHDRRead(MappedSubresource db)
    {
        Array.Clear(hdrHistogram);

        double sumPQ = 0;
        long   count = 0;
        float  maxPQ = 0;

        for (int y = 0; y < hdrHeight; y++)
        {
            Half* src = (Half*)nint.Add(db.DataPointer, y * (int)db.RowPitch);

            for (int x = 0; x < hdrWidth; x++)
            {
                float pq = (float)src[x];

                if (!float.IsFinite(pq))
                    continue;

                pq = Math.Clamp(pq, 0, 1);

                if (pq < hdrBlackCutoff)
                {
                    float t = pq / hdrBlackCutoff;
                    pq *= t * t * (3.0f - 2.0f * t);
                }

                if (pq <= 0)
                    continue;

                int bin = Math.Min((int)(pq * hdrHistogramBins), hdrHistogramBins - 1);
                hdrHistogram[bin]++;

                sumPQ += pq;
                count++;
                maxPQ = MathF.Max(maxPQ, pq);
            }
        }

        if (count == 0)
        {
            FLHDRUpdate(default);
            return;
        }

        float avgPQ = (float)(sumPQ / count);
        float lowPQ = FLHDRBlack(count, avgPQ);
        float peakPQ= hdrPeakPercentile <= 0 || hdrPeakPercentile >= 100 ? maxPQ : FLHDRPercentile(count, hdrPeakPercentile);

        FLHDRUpdate(new(lowPQ, avgPQ, peakPQ));
    }

    float FLHDRPercentile(long count, float percentile)
    {
        long target = (long)Math.Ceiling((percentile / 100.0) * count);
        long sum    = 0;

        for (int i = 0; i < hdrHistogramBins; i++)
        {
            sum += hdrHistogram[i];

            if (sum >= target)
                return (i + 0.5f) / hdrHistogramBins;
        }

        return 1;
    }

    float FLHDRBlack(long count, float avgPQ)
    {
        float minPQ = FLHDRPercentile(count, hdrBlackPercentile);
        minPQ       = MathF.Min(minPQ, hdrBlackMaxAdvance);

        float bright = MathF.Min(avgPQ * avgPQ * 4.0f, 1.0f);
        return minPQ * (1.0f - bright);
    }

    void FLHDRUpdate(HDRLumiStats frame)
    {
        hdrFrameStats = frame;

        if (!hdrHasStats)
        {
            hdrStats    = frame;
            hdrHasStats = true;
        }
        else
        {
            float diff = MathF.Abs(frame.AvgPQ - hdrStats.AvgPQ);

            float scene = SmoothStep(
                hdrSceneThresholdLow,
                hdrSceneThresholdHigh,
                diff);

            float coeff = 1.0f - MathF.Exp(-1.0f / hdrSmoothingPeriod);
            coeff       = coeff + (1.0f - coeff) * scene;

            hdrStats = new(
                hdrStats.MinPQ  + coeff * (frame.MinPQ  - hdrStats.MinPQ),
                hdrStats.AvgPQ  + coeff * (frame.AvgPQ  - hdrStats.AvgPQ),
                hdrStats.PeakPQ + coeff * (frame.PeakPQ - hdrStats.PeakPQ));
        }

        FLHDRApply();

        //if (CanDebug)
        //    Log.Debug(
        //        $"HDR Detect | " +
        //        $"Frame Min {PQToNits(hdrFrameStats.MinPQ):F3} Avg {PQToNits(hdrFrameStats.AvgPQ):F2} Peak {PQToNits(hdrFrameStats.PeakPQ):F1} | " +
        //        $"Smooth Min {PQToNits(hdrStats.MinPQ):F3} Avg {PQToNits(hdrStats.AvgPQ):F2} Peak {PQToNits(hdrStats.PeakPQ):F1}");
    }

    void FLHDRApply()
    {
        float sourceMinNits     = (float)PQToNits(hdrStats.MinPQ);
        float sourcePeakNits    = (float)PQToNits(hdrStats.PeakPQ);
        float sourceAvgNits     = Math.Clamp((float)PQToNits(hdrStats.AvgPQ), sourceMinNits, MathF.Max(sourcePeakNits, sourceMinNits));

        hdrData.SourceMinNits   = sourceMinNits;
        hdrData.SourcePeakNits  = sourcePeakNits;
        float splinePeakNits    = MathF.Max(sourcePeakNits, hdrData.TargetPeakNits);

        hdrData.Spline = GetSplineParams(
            sourceMinNits:  sourceMinNits,
            sourceAvgNits:  Math.Clamp(sourceAvgNits, sourceMinNits, splinePeakNits),
            sourcePeakNits: splinePeakNits,
            targetMinNits:  hdrData.TargetMinNits,
            targetPeakNits: hdrData.TargetPeakNits);

        context.UpdateSubresource(hdrData, hdrBuffer);
    }

    void FLHDRDisposeResources()
    {
        rtvHdr?.Dispose();
        rtvHdr = null;

        txtHdr?.Dispose();
        txtHdr = null;

        for (int i = 0; i < hdrBuffers; i++)
        {
            txtStageHdr[i]?.Dispose();
            txtStageHdr[i] = null;

            queryHdr[i]?.Dispose();
            queryHdr[i] = null;

            hdrPending[i]           = false;
            hdrPendingGen[i] = 0;
        }

        hdrWidth        = 0;
        hdrHeight       = 0;
        hdrSourceWidth  = 0;
        hdrSourceHeight = 0;
        hdrWriteIndex   = 0;
        hdrReadIndex    = 0;
        hdrGeneration   = 0;
        hdrHasStats     = false;
        hdrSyncNext     = false;
        hdrFrameStats   = default;
        hdrStats        = default;
    }

    void FLHDRDispose()
    {
        FLHDRDisposeResources();
        psHdr = null;
    }

    // SMPTE ST 2084 (PQ)
    const double pqM1 = 0.1593017578125;
    const double pqM2 = 78.84375;
    const double pqC1 = 0.8359375;
    const double pqC2 = 18.8515625;
    const double pqC3 = 18.6875;

    const float pqM1f = (float)pqM1;
    const float pqM2f = (float)pqM2;
    const float pqC1f = (float)pqC1;
    const float pqC2f = (float)pqC2;
    const float pqC3f = (float)pqC3;

    const float pqMaxNits = 10_000f;

    // Normalized linear luminance [0,1] -> PQ [0,1]
    static float PQOetf(float x)
    {
        x = MathF.Pow(MathF.Max(x, 0f), pqM1f);
        x = (pqC1f + pqC2f * x) / (1f + pqC3f * x);

        return MathF.Pow(x, pqM2f);
    }

    static double PQOetf(double x)
    {
        x = Math.Pow(Math.Max(x, 0d), pqM1);
        x = (pqC1 + pqC2 * x) / (1d + pqC3 * x);

        return Math.Pow(x, pqM2);
    }

    // PQ [0,1] -> Normalized linear luminance [0,1]
    static float PQEotf(float x)
    {
        x = MathF.Pow(Math.Clamp(x, 0f, 1f), 1f / pqM2f);
        x = MathF.Max(x - pqC1f, 0f) / (pqC2f - pqC3f * x);

        return MathF.Pow(x, 1f / pqM1f);
    }

    static double PQEotf(double x)
    {
        x = Math.Pow(Math.Clamp(x, 0d, 1d), 1d / pqM2);
        x = Math.Max(x - pqC1, 0d) / (pqC2 - pqC3 * x);

        return Math.Pow(x, 1d / pqM1);
    }

    // Nits <-> PQ
    static float NitsToPQ(float nits)
        => PQOetf(Math.Clamp(nits / pqMaxNits, 0f, 1f));

    static double NitsToPQ(double nits)
        => PQOetf(Math.Clamp(nits / pqMaxNits, 0d, 1d));

    static float PQToNits(float pq)
        => PQEotf(pq) * pqMaxNits;

    static double PQToNits(double pq)
        => PQEotf(pq) * pqMaxNits;

    // Dolby Vision L1: 12-bit PQ code -> nits
    static float PQToNitsDovi(ushort pq)
        => PQToNits(pq / 4095f);

    static float L1L3ToNitsDovi(ushort l1, ushort l3)
        => (float) PQToNits(Math.Clamp(l1 / 4095.0 + l3 / 2048.0 - 1.0, 0.0, 1.0));
    
    static ToneSplineParams GetSplineParams(
        double sourceMinNits,
        double sourcePeakNits,
        double sourceAvgNits,
        double targetMinNits,
        double targetPeakNits,
        double contrast = 0.5)
    {
        const double kneeAdaptation = 0.4;
        const double kneeMinimum    = 0.1;
        const double kneeMaximum    = 0.8;
        const double kneeDefault    = 0.4;

        const double slopeTuning = 1.5;
        const double slopeOffset = 0.2;

        double srcMin = NitsToPQ(sourceMinNits);
        double srcMax = NitsToPQ(sourcePeakNits);

        double dstMin = NitsToPQ(targetMinNits);
        double dstMax = NitsToPQ(targetPeakNits);

        // ---- Choose source pivot ---------------------------------------

        double srcKneeMin = srcMin + (srcMax - srcMin) * kneeMinimum;
        double srcKneeMax = srcMin + (srcMax - srcMin) * kneeMaximum;

        double srcPivot;

        if (sourceAvgNits > 0)
            srcPivot = NitsToPQ(sourceAvgNits);
        else
            srcPivot = srcMin + (srcMax - srcMin) * kneeDefault;

        srcPivot = Math.Clamp(srcPivot, srcKneeMin, srcKneeMax);

        // ---- Choose destination pivot --------------------------------

        double target       = (srcPivot - srcMin) / (srcMax - srcMin);
        double adapted      = dstMin + (dstMax - dstMin) * target;
        double dstKneeMin   = dstMin + (dstMax - dstMin) * kneeMinimum;
        double dstKneeMax   = dstMin + (dstMax - dstMin) * kneeMaximum;

        double tuning =
            1.0 -
            SmoothStep(kneeMaximum, kneeDefault, target) *
            SmoothStep(kneeMinimum, kneeDefault, target);

        double adaptation = kneeAdaptation + (1.0 - kneeAdaptation) * tuning;

        double dstPivot = srcPivot + (adapted - srcPivot) * adaptation;

        dstPivot = Math.Clamp(dstPivot, dstKneeMin, dstKneeMax);

        // ---- Slope at pivot ------------------------------------------

        double slope = (dstPivot - dstMin) / (srcPivot - srcMin);

        double ratio = srcMax / dstMax - 1.0;

        ratio = Math.Clamp(slopeTuning * ratio, slopeOffset, 1.0 + slopeOffset);

        slope = Math.Pow(slope, (1.0 - contrast) * ratio);

        // ---- Polynomial coefficients --------------------------------

        double inMin  = srcMin - srcPivot;
        double inMax  = srcMax - srcPivot;

        double outMin = dstMin - dstPivot;
        double outMax = dstMax - dstPivot;

        // Lower side: quadratic
        double pa =
            (outMin - slope * inMin) /
            (inMin * inMin);

        // Upper side: cubic
        double t = 2.0 * inMax * inMax;

        double qa =
            (slope * inMax - outMax) /
            (inMax * t);

        double qb =
            -3.0 * (slope * inMax - outMax) / t;

        return new()
        {
            SrcPivot = (float)srcPivot,
            DstPivot = (float)dstPivot,
            Pa       = (float)pa,
            Slope    = (float)slope,
            Qa       = (float)qa,
            Qb       = (float)qb,
        };
    }
}

