using System.Diagnostics;
using System.IO;
using System.Text;

namespace LLPlayer.Services;

public class PipeClient : IDisposable
{
    private Process _proc;

    public PipeClient(string pipePath)
    {
        _proc = new Process
        {
            StartInfo = new ProcessStartInfo()
            {
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                FileName = pipePath,
                CreateNoWindow = true,
            }
        };
        _proc.Start();
    }

    public async Task SendMessage(string message)
    {
        Debug.WriteLine(message);
        //byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);

        // Enclose double quotes before and after since it is sent as a JSON string
        byte[] bytes = Encoding.UTF8.GetBytes('"' + message + '"');

        var length = BitConverter.GetBytes(bytes.Length);
        await _proc.StandardInput.BaseStream.WriteAsync(length, 0, length.Length);
        await _proc.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length);
        await _proc.StandardInput.BaseStream.FlushAsync();
    }

    public void Dispose()
    {
        _proc.Kill();
        _proc.WaitForExit();
    }
}

public class PDICSender : IDisposable
{
    private readonly PipeClient _pipeClient;
    public FlyleafManager FL { get; }

    public PDICSender(FlyleafManager fl)
    {
        FL = fl;

        string? exePath = FL.Config.Subs.PDICPipeExecutablePath;

        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"PDIC executable is not set correctly: {exePath}");
        }

        _pipeClient = new PipeClient(exePath);
    }

    public async void Dispose()
    {
        await _pipeClient.SendMessage("p:Dictionary,Close,");
        _pipeClient.Dispose();
    }

    public async Task Connect()
    {
        await _pipeClient.SendMessage("p:Dictionary,Open,");
    }

    // Send the same way as Firepop
    // webextension native extensions
    // ref: https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/Native_messaging

    // See Firepop source
    public async Task<int> SendWithPipe(string sentence, int offset)
    {
        try
        {
            await _pipeClient.SendMessage($"p:Dictionary,SetUrl,{App.Name}");
            await _pipeClient.SendMessage($"p:Dictionary,PopupSearch3,{offset},{sentence}");

            // Incremental search
            //await _pipeClient.SendMessage($"p:Simulate,InputWord3,word");

            return 0;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.ToString());
            return -1;
        }
    }
}
