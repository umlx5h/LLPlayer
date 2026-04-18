using System.Net.Http;

namespace FlyleafLib.MediaPlayer.Translation.Services;

#nullable enable

public class AzureTranslateService : MicrosoftTranslateServiceBase
{
    private readonly AzureTranslateSettings _settings;

    public AzureTranslateService(AzureTranslateSettings settings) : base(settings)
    {
        _settings = settings;
    }

    // ref: https://learn.microsoft.com/en-us/azure/ai-services/translator/text-translation/reference/authentication#authenticating-with-an-access-token
    protected override async Task<string> GetAccessTokenAsync(HttpClient client, CancellationToken token)
    {
        string url = "https://api.cognitive.microsoft.com/sts/v1.0/issueToken";
        if (!string.IsNullOrEmpty(_settings.Region) && _settings.Region != "global")
        {
            url = $"https://{_settings.Region}.api.cognitive.microsoft.com/sts/v1.0/issueToken";
        }

        using HttpRequestMessage req = new(HttpMethod.Post, url);
        req.Headers.Add("Ocp-Apim-Subscription-Key", _settings.ApiKey);

        using var response = await client.SendAsync(req, token).ConfigureAwait(false);
        return await ReadTokenResponseAsync(response, token).ConfigureAwait(false);
    }
}
