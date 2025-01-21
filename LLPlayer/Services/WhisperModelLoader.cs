using System.IO;
using FlyleafLib;
using Whisper.net.Ggml;

namespace LLPlayer.Services;

public class WhisperModelLoader
{
    public static List<WhisperModel> LoadAllModels()
    {
        EnsureModelsDirectory();

        List<WhisperModel> models = Enum.GetValues<GgmlType>()
            .Select(t => new WhisperModel { Model = t, })
            .ToList();

        foreach (WhisperModel model in models)
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

    public static List<WhisperModel> LoadDownloadedModels()
    {
        return LoadAllModels()
            .Where(m => m.Downloaded)
            .ToList();
    }

    public static void EnsureModelsDirectory()
    {
        if (!Directory.Exists(WhisperModel.ModelsDirectory))
        {
            Directory.CreateDirectory(WhisperModel.ModelsDirectory);
        }
    }
}
