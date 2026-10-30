using System.Windows.Data;

using Vortice.DXGI;
using Vortice.MediaFoundation;

using FlyleafLib.MediaFramework.MediaDevice;

namespace FlyleafLib;

public class VideoEngine
{
    /// <summary>
    /// List of Video Capture Devices
    /// </summary>
    public ObservableCollection<VideoDevice>
                            CapDevices          { get; set; } = [];

    /// <summary>
    /// List of GPU Adpaters <see cref="Config.VideoConfig.GPUAdapter"/>
    /// </summary>
    public Dictionary<long, GPUAdapter>
                            GPUAdapters         { get; private set; }

    internal IDXGIFactory2  Factory;

    private readonly object lockCapDevices = new();

    internal VideoEngine() // We consider from UI here
    {
        if (DXGI.CreateDXGIFactory1(out Factory).Failure)
            throw new InvalidOperationException("Cannot create IDXGIFactory1");

        BindingOperations.EnableCollectionSynchronization(CapDevices, lockCapDevices);
        GPUAdapters = GetAdapters();
    }

    /// <summary>
    /// Enumerates Video Capture Devices which can be retrieved from <see cref="CapDevices"/>
    /// </summary>
    public void RefreshCapDevices()
    {
        lock (lockCapDevices)
        {
            Engine.Video.CapDevices.Clear();

            var devices = MediaFactory.MFEnumVideoDeviceSources();
            foreach (var device in devices)
            {
                Engine.Video.CapDevices.Add(new(device.FriendlyName, device.SymbolicLink));
                device.Dispose();
            }

            devices.Dispose();
        }
    }

    private Dictionary<long, GPUAdapter> GetAdapters()
    {
        Dictionary<long, GPUAdapter> adapters = [];

        string dump = "";

        for (uint i = 0; Factory.EnumAdapters(i, out var adapter).Success; i++)
        {
            var desc = adapter.Description;
            adapters[desc.Luid] = GetGPUAdapter(adapter, desc);
            dump += $"[#{i+1}] {adapters[desc.Luid]}\r\n";
        }

        Engine.Log.Info($"GPU Adapters\r\n{dump}");

        return adapters;
    }

    public GPUAdapter GetGPUAdapter(IDXGIAdapter adapter, AdapterDescription desc)
        => new()
        {
            SystemMemory    = desc.DedicatedSystemMemory.Value,
            VideoMemory     = desc.DedicatedVideoMemory.Value,
            SharedMemory    = desc.SharedSystemMemory.Value,
            Vendor          = (GPUVendor)desc.VendorId,
            Description     = desc.Description,
            Id              = desc.DeviceId,
            Luid            = desc.Luid,
            dxgiAdapter     = adapter
        };

    public List<GPUOutput> GetGPUOutputs(IDXGIAdapter adapter)
    {
        List<GPUOutput> outputs = [];
        if (adapter == null)
            return outputs;

        for (uint i = 0; adapter.EnumOutputs(i, out var output).Success; i++)
        {
            IDXGIOutput6 output6 = null;
            GPUOutput gpuOutput;

            if (Environment.OSVersion.Version.Major >= 10)
                output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();

            if (output6 != null)
            {
                var outdesc = output6.Description1;

                gpuOutput = new()
                {
                    Hwnd        = outdesc.Monitor,
                    DeviceName  = outdesc.DeviceName,
                    Left        = outdesc.DesktopCoordinates.Left,
                    Top         = outdesc.DesktopCoordinates.Top,
                    Right       = outdesc.DesktopCoordinates.Right,
                    Bottom      = outdesc.DesktopCoordinates.Bottom,
                    IsAttached  = outdesc.AttachedToDesktop,
                    Rotation    = outdesc.Rotation,
                    MaxLuminance= outdesc.MaxLuminance,
                    MinLuminance= outdesc.MinLuminance
                };

                output6.Dispose();
            }
            else
            {
                var outdesc = output.Description;

                gpuOutput = new()
                {
                    Hwnd        = outdesc.Monitor,
                    DeviceName  = outdesc.DeviceName,
                    Left        = outdesc.DesktopCoordinates.Left,
                    Top         = outdesc.DesktopCoordinates.Top,
                    Right       = outdesc.DesktopCoordinates.Right,
                    Bottom      = outdesc.DesktopCoordinates.Bottom,
                    IsAttached  = outdesc.AttachedToDesktop,
                    Rotation    = outdesc.Rotation,
                    MaxLuminance= 200
                };
            }

            // Currently not used
            //var devMode = DEVMODE.Get(gpuOutput.DeviceName);
            //gpuOutput.RefreshRate = devMode.dmDisplayFrequency;

            outputs.Add(gpuOutput);
            output.Dispose();
        }

        return outputs;
    }
}
