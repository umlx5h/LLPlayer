namespace FlyleafLib;

#nullable enable

public class OpenAICompatibleASRConfig : NotifyPropertyChanged
{
    public string BaseUrl { get; set => Set(ref field, value); } = "http://localhost:8000/v1";
    public string Model { get; set => Set(ref field, value); } = "sensevoice";
    public string ApiKey { get; set => Set(ref field, value); } = string.Empty;
    public int TimeoutSeconds { get; set => Set(ref field, value); } = 120;
}
