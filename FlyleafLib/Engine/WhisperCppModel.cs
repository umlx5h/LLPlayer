using System.IO;
using System.Text.Json.Serialization;
using Whisper.net.Ggml;

namespace FlyleafLib;

public class WhisperCppModel : NotifyPropertyChanged
{
    public GgmlType Model { get; set; }

    [JsonIgnore]
    public long Size
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                Raise(nameof(Downloaded));
            }
        }
    }

    [JsonIgnore]
    public string ModelFileName
    {
        get
        {
            string modelName = Model.ToString().ToLower();
            return $"ggml-{modelName}.bin";
        }
    }

    [JsonIgnore]
    public string ModelFilePath => Path.Combine(WhisperConfig.ModelsDirectory, ModelFileName);

    [JsonIgnore]
    public bool Downloaded => Size > 0;

    public override string ToString() => Model.ToString();

    public override bool Equals(object? obj)
    {
        if (obj is not WhisperCppModel model)
            return false;

        return model.Model == Model;
    }

    public override int GetHashCode() => (int)Model;
}
