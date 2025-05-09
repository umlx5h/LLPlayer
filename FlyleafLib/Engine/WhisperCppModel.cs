using System.IO;
using System.Text.Json.Serialization;
using Whisper.net.Ggml;

namespace FlyleafLib;

#nullable enable

public class WhisperCppModel : NotifyPropertyChanged, IEquatable<WhisperCppModel>
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

    public bool Equals(WhisperCppModel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Model == other.Model;
    }

    public override bool Equals(object? obj) => obj is WhisperCppModel o && Equals(o);

    public override int GetHashCode()
    {
        return (int)Model;
    }
}
