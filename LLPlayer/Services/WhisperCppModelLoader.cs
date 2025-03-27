using System.IO;
using FlyleafLib;
using Whisper.net.Ggml;

namespace LLPlayer.Services;

public class WhisperCppModelLoader
{
    public static List<WhisperCppModel> LoadAllModels()
    {
        WhisperConfig.EnsureModelsDirectory();

        List<WhisperCppModel> models = Enum.GetValues<GgmlType>()
            .Select(t => new WhisperCppModel { Model = t, })
            .ToList();

        foreach (WhisperCppModel model in models)
        {
            // Update download status
            string path = model.ModelFilePath;
            if (File.Exists(path))
            {
                model.Size = new FileInfo(path).Length;
            }
        }

        return models;
    }

    public static List<WhisperCppModel> LoadDownloadedModels()
    {
        return LoadAllModels()
            .Where(m => m.Downloaded)
            .ToList();
    }
}
