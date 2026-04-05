/* Child Renderers TODO (shared RGBA intermediate texture -single heavy process- & Deferred Context for performance):
 * - Review Control requirements (SwapChain) & VPConfig attached to Control?*
 * - Deferred Context and FinishCommandList
 * - Separate VPConfig
 * - Requires render to RGBA intermediate texture (probably during FillPlanes)
 * - Render will only Scale (might separate Filters too)
 * - Shared RenderIdle during ShowFrame X and RenderPlay
 * - Separate RenderIdle
 * - Separate ProcessRequests
 * - Separate RefreshPlay
 */

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

using ID3D11DeviceContext   = Vortice.Direct3D11.ID3D11DeviceContext;

using static FlyleafLib.MediaFramework.MediaRenderer.Renderer;

namespace FlyleafLib.MediaFramework.MediaRenderer;

class ChildRenderer : IVP
{
    public VPConfig             Config          => ucfg;
    VPConfig ucfg;
    public Renderer             Renderer        { get; private set; }
    public SwapChain            SwapChain       { get; private set; }
    public Viewport             Viewport        { get; private set; }
    public int                  ControlWidth    { get; private set; }
    public int                  ControlHeight   { get; private set; }
    public int                  SideXPixels     => sideXPixels;
    public int                  SideYPixels     => sideYPixels;
    int                         sideXPixels, sideYPixels;

    ID3D11DeviceContext     context;
    ID3D11Buffer            vsBuffer;
    VSBufferType            vsData    = new();

    VPRequestType   vpRequestsIn, vpRequests; // In: From User | ProcessRequests Copy

    public ChildRenderer(VPConfig config)
    {
        ucfg        = config;
        context     = Renderer.Device.CreateDeferredContext();
        vsBuffer    = Renderer.Device.CreateBuffer(vsDesc);
        
        context.IASetVertexBuffer       (0, Renderer.vertexBuffer, sizeof(float) * 5);
        context.IASetInputLayout        (Renderer.inputLayout);
        context.IASetPrimitiveTopology  (PrimitiveTopology.TriangleList);
        context.VSSetConstantBuffer     (0, vsBuffer);
        context.VSSetShader             (Renderer.vsMain);
        context.PSSetSampler            (0, Renderer.samplerLinear);
        context.PSSetShader             (Renderer.psShader["rgba"]);
    }

    void IVP.VPRequest(VPRequestType request)
        => VPRequest(request);

    internal void VPRequest(VPRequestType request)
    {
        vpRequestsIn |= request;
        //RenderRequest();
    }

    void IVP.MonitorChanged(GPUOutput monitor) { }
    void IVP.UpdateSize(int width, int height)
    {   // TBR
        ControlWidth    = width;
        ControlHeight   = height;
    }

    void Dispose()
    {
        vsBuffer.Dispose();
        context.Dispose();
    }
}


