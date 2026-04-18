using System.Net.Http;

namespace FlyleafLib.MediaPlayer.Translation.Services;

#nullable enable

public class BingTranslateService : MicrosoftTranslateServiceBase
{
    private readonly BingTranslateSettings _settings;

    public BingTranslateService(BingTranslateSettings settings) : base(settings)
    {
        _settings = settings;
    }

    // curl --location 'https://edge.microsoft.com/translate/auth'
    //_httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
    protected override async Task<string> GetAccessTokenAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.GetAsync("https://edge.microsoft.com/translate/auth", token).ConfigureAwait(false);
        return await ReadTokenResponseAsync(response, token).ConfigureAwait(false);
    }
}
