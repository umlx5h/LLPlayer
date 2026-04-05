using System.Buffers;
using System.Diagnostics;

using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

using ID3D11Device = Vortice.Direct3D11.ID3D11Device;

namespace FlyleafLib.MediaFramework.MediaRenderer;

internal static partial class ShaderCompiler
{
    const int               MAX_CACHE_SIZE  = 64;
    const string            MAIN            = "main";
    const string            LOG_PREFIX      = "[Shader] ";
    static readonly string  SHADERVER       = Environment.OSVersion.Version.Major >= 10 ? "_5_0" : "_4_0_level_9_3";
    static readonly string  PSVER           = $"ps{SHADERVER}";
    static readonly string  VSVER           = $"vs{SHADERVER}";
    internal static Blob    VSBlob          = Compile(VS, false);
    internal static Blob    VSSimpleBlob    = Compile(VSSimple, false);

    class BlobWrapper { public Blob blob; } // For locking per Blob (before creation)
    static Dictionary<string, BlobWrapper> cache = [];

    internal static ID3D11PixelShader CompilePS(ID3D11Device device, string uniqueId, ReadOnlySpan<char> hlslSample, List<string> defines = null)
    {
        BlobWrapper bw;

        lock (cache)
        {
            if (cache.Count > MAX_CACHE_SIZE)
            {
                LogInfo("Clearing Cache");

                foreach (var bw1 in cache.Values)
                    bw1.blob.Dispose();

                cache.Clear();
            }
            else if (cache.TryGetValue(uniqueId, out var bw2))
            {
                if (CanDebug)
                    LogDebug($"Using from Cache '{uniqueId}'");

                lock (bw2)
                    return device.CreatePixelShader(bw2.blob);
            }

            bw = new();
            Monitor.Enter(bw);
            cache.Add(uniqueId, bw);
        }

        if (CanDebug)
            LogDebug($"Compiling '{uniqueId}'");

        // PS_HEADER + hlslSample + PS_FOOTER (Max 10KB)
        Debug.Assert(PS_HEADER.Length + PS_FOOTER.Length + Encoding.UTF8.GetMaxByteCount(hlslSample.Length) < 12_000);
        byte[] bufferPool   = ArrayPool<byte>.Shared.Rent(12 * 1024);
        Span<byte> buffer   = bufferPool;
        PS_HEADER.CopyTo(buffer);
        int offset          = PS_HEADER.Length;
        offset             += Encoding.UTF8.GetBytes(hlslSample, buffer[offset..]);
        PS_FOOTER.CopyTo(buffer[offset..]);
        offset             += PS_FOOTER.Length;
        bw.blob = Compile(buffer[..offset], true, defines);
        ArrayPool<byte>.Shared.Return(bufferPool);

        var ps = device.CreatePixelShader(bw.blob);
        Monitor.Exit(bw);

        return ps;
    }

    internal static unsafe Blob Compile(ReadOnlySpan<byte> bytes, bool isPS = true, List<string> defines = null)
    {
        ShaderMacro[] definesMacro = null;

        if (defines != null)
        {
            // NOTE: requires NULL termination (+1)
            definesMacro = new ShaderMacro[defines.Count + 1];

            for(int i = 0; i < defines.Count; i++)
                definesMacro[i].Name = defines[i];
        }

        // NOTE: Enable for Reviewing HLSL after defines
        //fixed (byte* hlslPtr = bytes)
        //{
        //    Compiler.Preprocess((nint)hlslPtr, new((uint)bytes.Length), null, definesMacro, null, out var debugBlob, out var debugError);
        //    Engine.Log.Error(debugBlob.AsString());
        //}

        // NOTE: Optimization could actually cause issues (mainly with literals) | Use SkipOptimization instead when debugging HLSL
        Compiler.Compile(bytes, definesMacro, null, MAIN, null, isPS ? PSVER : VSVER, ShaderFlags.OptimizationLevel3, out var shaderBlob, out var psError);

        if (psError != null && psError.BufferPointer != IntPtr.Zero)
        {
            string[] errors = BytePtrToStringUTF8((byte*)psError.BufferPointer).Split('\n');

            foreach (string line in errors)
                LogError($"{line}");
        }

        return shaderBlob;
    }

    static void LogError(string msg) => Engine.Log.Error($"{LOG_PREFIX}{msg}");
    static void LogInfo (string msg) => Engine.Log.Info ($"{LOG_PREFIX}{msg}");
    static void LogDebug(string msg) => Engine.Log.Debug($"{LOG_PREFIX}{msg}");
    static void LogTrace(string msg) => Engine.Log.Trace($"{LOG_PREFIX}{msg}");
}
