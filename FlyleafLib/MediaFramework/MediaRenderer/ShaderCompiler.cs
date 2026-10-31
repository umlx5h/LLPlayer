using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

using ID3D11Device = Vortice.Direct3D11.ID3D11Device;

namespace FlyleafLib.MediaFramework.MediaRenderer;

internal static partial class ShaderCompiler
{
    const int               BUFFER_SIZE     = 32 * 1024;
    const int               MAX_CACHE_SIZE  = 64;
    const string            MAIN            = "main";
    const string            LOG_PREFIX      = "[Shader] ";
    static readonly string  SHADERVER       = Environment.OSVersion.Version.Major >= 10 ? "_5_0" : "_4_0_level_9_3";
    static readonly string  PSVER           = $"ps{SHADERVER}";
    static readonly string  VSVER           = $"vs{SHADERVER}";
    internal static Blob    VSBlob          = Compile(VS, false);
    internal static Blob    VSSimpleBlob    = Compile(VSSimple, false);

    class BlobWrapper { public Blob blob; }
    static Dictionary<string, BlobWrapper> cache = [];

    internal static ID3D11PixelShader CompilePS(ID3D11Device device, string uniqueId, ReadOnlySpan<char> hlslSample, List<string> defines = null)
    {
        BlobWrapper bw;

        lock (cache)
        {
            if (!cache.TryGetValue(uniqueId, out bw))
            {
                if (cache.Count >= MAX_CACHE_SIZE)
                {
                    LogInfo("Clearing Cache");

                    foreach (var cached in cache.Values)
                    {
                        lock (cached)
                        {
                            cached.blob?.Dispose();
                            cached.blob = null;
                        }
                    }

                    cache.Clear();
                }

                bw = new();
                cache.Add(uniqueId, bw);
            }
        }

        lock (bw)
        {
            if (bw.blob == null)
            {
                if (CanDebug)
                    LogDebug($"Compiling '{uniqueId}'");

                Debug.Assert(PS_HEADER.Length + PS_BT2020.Length + PS_DOVI.Length + PS_MAIN.Length + PS_FOOTER.Length + Encoding.UTF8.GetMaxByteCount(hlslSample.Length) < BUFFER_SIZE);

                byte[] bufferPool = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);

                try
                {
                    Span<byte> buffer = bufferPool;
                    int offset = 0;

                    PS_HEADER.CopyTo(buffer[offset..]);
                    offset += PS_HEADER.Length;
                    
                    if (defines != null)
                        if (defines.Contains(Renderer.dBT2020))
                        {
                            PS_BT2020.CopyTo(buffer[offset..]);
                            offset += PS_BT2020.Length;

                            if (defines.Contains(Renderer.dPQSpline))
                            {
                                PS_PQSpline.CopyTo(buffer[offset..]);
                                offset += PS_PQSpline.Length;

                                if (defines.Contains(Renderer.dDovi))
                                {
                                    PS_DOVI.CopyTo(buffer[offset..]);
                                    offset += PS_DOVI.Length;
                                }
                            }
                        }

                    PS_MAIN.CopyTo(buffer[offset..]);
                    offset += PS_MAIN.Length;
                    offset += Encoding.UTF8.GetBytes(hlslSample, buffer[offset..]);
                    PS_FOOTER.CopyTo(buffer[offset..]);
                    offset += PS_FOOTER.Length;

                    bw.blob = Compile(buffer[..offset], true, defines);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(bufferPool);
                }
            }
            else if (CanDebug)
                LogDebug($"Using from Cache '{uniqueId}'");

            return device.CreatePixelShader(bw.blob);
        }
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
        //    Engine.Log.Error(CleanHLSL(debugBlob.AsString()));
        //}

        // NOTE: Optimization could actually cause issues (mainly with literals) | Use SkipOptimization instead when debugging HLSL
        Compiler.Compile(bytes, definesMacro, null, MAIN, null, isPS ? PSVER : VSVER, ShaderFlags.OptimizationLevel3, out var shaderBlob, out var psError);

        if (psError != null)
        {
            #if DEBUG
            if (psError.BufferPointer != IntPtr.Zero)
            {
                string[] errors = BytePtrToStringUTF8((byte*)psError.BufferPointer).Split('\n');

                foreach (string line in errors)
                    LogError($"{line}");
            }
            #else
                LogError("Pixel shader compilation failed");
            #endif

            psError.Dispose();
        }

        return shaderBlob;
    }

    [SuppressMessage("Performance", "SYSLIB1045:Convert to 'GeneratedRegexAttribute'.", Justification = "DebugOnly")]
    static string CleanHLSL(string text)
    {
        text = Regex.Replace(text, @"\s*\[\s*", "[");
        text = Regex.Replace(text, @"\s*\]\s*", "]");
        text = Regex.Replace(text, @"(?m)^\s*#line.*(?:\r?\n)?", "");
        text = Regex.Replace(text, @"\s*\.\s*", ".");
        text = Regex.Replace(text, @"\s+([,;)])", "$1");
        text = Regex.Replace(text, @"\(\s+", "(");
        text = Regex.Replace(text, @"(\w)\s+\(", "$1(");
        text = Regex.Replace(text, @"(?m)^[ \t]+$", "");
        text = Regex.Replace(text, @"(\r?\n){3,}", "$1$1");

        return text.Trim();
    }

    static void LogError(string msg) => Engine.Log.Error($"{LOG_PREFIX}{msg}");
    static void LogInfo (string msg) => Engine.Log.Info ($"{LOG_PREFIX}{msg}");
    static void LogDebug(string msg) => Engine.Log.Debug($"{LOG_PREFIX}{msg}");
    static void LogTrace(string msg) => Engine.Log.Trace($"{LOG_PREFIX}{msg}");
}
