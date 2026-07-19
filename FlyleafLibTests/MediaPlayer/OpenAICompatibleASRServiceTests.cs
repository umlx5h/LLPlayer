using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AwesomeAssertions;

namespace FlyleafLib.MediaPlayer;

public class OpenAICompatibleASRServiceTests
{
    [Theory]
    [InlineData("http://localhost:8000", "http://localhost:8000/v1/audio/transcriptions")]
    [InlineData("http://localhost:8000/", "http://localhost:8000/v1/audio/transcriptions")]
    [InlineData("http://localhost:8000/v1", "http://localhost:8000/v1/audio/transcriptions")]
    [InlineData("https://asr.example/api/v1/", "https://asr.example/api/v1/audio/transcriptions")]
    [InlineData("https://asr.example/v1/audio/transcriptions", "https://asr.example/v1/audio/transcriptions")]
    public void BuildTranscriptionsUri_NormalizesSupportedBaseUrls(string baseUrl, string expected)
    {
        OpenAICompatibleASRService.BuildTranscriptionsUri(baseUrl).Should().Be(new Uri(expected));
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost:8000")]
    [InlineData("ftp://localhost:8000")]
    [InlineData("https://asr.example/v1?tenant=one")]
    public void BuildTranscriptionsUri_RejectsInvalidBaseUrls(string baseUrl)
    {
        Action act = () => OpenAICompatibleASRService.BuildTranscriptionsUri(baseUrl);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Do_SendsScopedCredentialsAndMapsTimestampedSegments()
    {
        CapturedRequest captured = new();
        using HttpClient client = new(new DelegateHandler(async (request, token) =>
        {
            captured.Method = request.Method;
            captured.Uri = request.RequestUri;
            captured.Authorization = request.Headers.Authorization;
            captured.Parts = await ReadParts(request, token);

            return JsonResponse("""
                {
                  "text": "first second",
                  "language": "zh",
                  "segments": [
                    {"start": 0.25, "end": 0.75, "text": " first "},
                    {"start": 1.5, "end": 4.0, "text": "second"}
                  ]
                }
                """);
        }));
        Config config = CreateConfig();
        config.Subtitles.OpenAICompatibleASRConfig.ApiKey = "provider-secret";
        config.Subtitles.WhisperConfig.LanguageDetection = false;
        config.Subtitles.WhisperConfig.Language = "zh";

        await using OpenAICompatibleASRService service = new(config, client);
        List<(string text, TimeSpan start, TimeSpan end, string language)> results =
            await CollectAsync(service.Do(CreateWave(TimeSpan.FromSeconds(2)), CancellationToken.None));

        captured.Method.Should().Be(HttpMethod.Post);
        captured.Uri.Should().Be(new Uri("http://localhost:8000/v1/audio/transcriptions"));
        captured.Authorization.Should().Be(new AuthenticationHeaderValue("Bearer", "provider-secret"));
        captured.Parts["model"].Text.Should().Be("sensevoice");
        captured.Parts["response_format"].Text.Should().Be("verbose_json");
        captured.Parts["language"].Text.Should().Be("zh");
        captured.Parts["file"].Bytes.Should().StartWith(Encoding.ASCII.GetBytes("RIFF"));

        results.Should().HaveCount(2);
        results[0].Should().Be(("first", TimeSpan.FromSeconds(0.25), TimeSpan.FromSeconds(0.75), "zh"));
        results[1].Should().Be(("second", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2), "zh"));
    }

    [Fact]
    public async Task Do_ReturnsNoSubtitleWhenSegmentsAreMissing()
    {
        CapturedRequest captured = new();
        using HttpClient client = new(new DelegateHandler(async (request, token) =>
        {
            captured.Authorization = request.Headers.Authorization;
            captured.Parts = await ReadParts(request, token);
            return JsonResponse("""{"text":" hello ","language":"auto","segments":[]}""");
        }));
        Config config = CreateConfig();
        config.Subtitles.WhisperConfig.LanguageDetection = true;

        await using OpenAICompatibleASRService service = new(config, client);
        List<(string text, TimeSpan start, TimeSpan end, string language)> results =
            await CollectAsync(service.Do(CreateWave(TimeSpan.FromSeconds(1.25)), CancellationToken.None));

        captured.Authorization.Should().BeNull();
        captured.Parts.Should().NotContainKey("language");
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Do_ReturnsNoSubtitleForLongTextWhenProviderReturnsNoSegments()
    {
        const string kennedyText =
            "i believe that this nation should commit itself to achieving the goal before this decade is out " +
            "of landing a man on the moon and returning him safely to the earth " +
            "no single space project in this period will be more impressive to mankind " +
            "or more important for the long range exploration of space";
        using HttpClient client = new(new DelegateHandler((_, _) => Task.FromResult(JsonResponse($$"""
            {
              "task": "transcribe",
              "language": "en",
              "duration": 0,
              "text": "{{kennedyText}}",
              "segments": []
            }
            """))));
        Config config = CreateConfig();
        await using OpenAICompatibleASRService service = new(config, client);

        List<(string text, TimeSpan start, TimeSpan end, string language)> results =
            await CollectAsync(service.Do(CreateWave(TimeSpan.FromSeconds(21)), CancellationToken.None));

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Do_ReportsEndpointErrorDetails()
    {
        using HttpClient client = new(new DelegateHandler((_, _) => Task.FromResult(
            JsonResponse("""{"detail":"unknown model"}""", HttpStatusCode.BadRequest))));
        Config config = CreateConfig();
        await using OpenAICompatibleASRService service = new(config, client);

        Func<Task> act = async () =>
            await CollectAsync(service.Do(CreateWave(TimeSpan.FromSeconds(1)), CancellationToken.None));

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*400*unknown model*");
    }

    [Fact]
    public async Task Do_ReportsConfiguredTimeout()
    {
        using HttpClient client = new(new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse("""{"text":"too late"}""");
        }));
        Config config = CreateConfig();
        config.Subtitles.OpenAICompatibleASRConfig.TimeoutSeconds = 1;
        await using OpenAICompatibleASRService service = new(config, client);

        Func<Task> act = async () =>
            await CollectAsync(service.Do(CreateWave(TimeSpan.FromSeconds(1)), CancellationToken.None));

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*1 seconds*");
    }

    [Fact]
    public async Task Constructor_DisablesOwnedHttpClientTimeout()
    {
        Config config = CreateConfig();
        config.Subtitles.OpenAICompatibleASRConfig.TimeoutSeconds = 120;
        await using OpenAICompatibleASRService service = new(config);
        HttpClient client = (HttpClient)typeof(OpenAICompatibleASRService)
            .GetField("_httpClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;

        client.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task Do_ReportsTimeoutWhileReadingResponseBody()
    {
        using HttpClient client = new(new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new BlockingContent() })));
        Config config = CreateConfig();
        config.Subtitles.OpenAICompatibleASRConfig.TimeoutSeconds = 1;
        await using OpenAICompatibleASRService service = new(config, client);

        Func<Task> act = async () =>
            await CollectAsync(service.Do(CreateWave(TimeSpan.FromSeconds(1)), CancellationToken.None));

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*1 seconds*");
    }

    [Fact]
    public async Task Do_PreservesCallerCancellation()
    {
        using HttpClient client = new(new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse("""{"text":"too late"}""");
        }));
        Config config = CreateConfig();
        await using OpenAICompatibleASRService service = new(config, client);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Func<Task> act = async () =>
            await CollectAsync(service.Do(CreateWave(TimeSpan.FromSeconds(1)), cancellation.Token));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Do_SortsAndNormalizesNonMonotonicSegments()
    {
        using HttpClient client = new(new DelegateHandler((_, _) => Task.FromResult(JsonResponse("""
            {
              "text": "early overlap contained late",
              "segments": [
                {"start": 1.5, "end": 2.0, "text": "late"},
                {"start": 0.0, "end": 0.6, "text": "early"},
                {"start": 0.5, "end": 1.2, "text": "overlap"},
                {"start": 0.7, "end": 0.8, "text": "contained"},
                {"start": 1.3, "end": 1.3, "text": "degenerate"}
              ]
            }
            """))));
        Config config = CreateConfig();
        await using OpenAICompatibleASRService service = new(config, client);

        List<(string text, TimeSpan start, TimeSpan end, string language)> results =
            await CollectAsync(service.Do(CreateWave(TimeSpan.FromSeconds(2)), CancellationToken.None));

        results.Should().Equal(
            ("early", TimeSpan.Zero, TimeSpan.FromSeconds(0.6), "und"),
            ("overlap", TimeSpan.FromSeconds(0.6), TimeSpan.FromSeconds(1.2), "und"),
            ("late", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2), "und"));
    }

    private static Config CreateConfig()
    {
        Config config = new(true);
        config.Subtitles.OpenAICompatibleASRConfig.BaseUrl = "http://localhost:8000/v1";
        config.Subtitles.OpenAICompatibleASRConfig.Model = "sensevoice";
        config.Subtitles.OpenAICompatibleASRConfig.TimeoutSeconds = 30;
        return config;
    }

    private static async Task<Dictionary<string, CapturedPart>> ReadParts(
        HttpRequestMessage request,
        CancellationToken token)
    {
        MultipartFormDataContent multipart = request.Content.Should()
            .BeOfType<MultipartFormDataContent>().Which;
        Dictionary<string, CapturedPart> parts = [];

        foreach (HttpContent part in multipart)
        {
            string name = part.Headers.ContentDisposition!.Name!.Trim('"');
            byte[] bytes = await part.ReadAsByteArrayAsync(token);
            parts[name] = new CapturedPart(Encoding.UTF8.GetString(bytes), bytes);
        }

        return parts;
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static MemoryStream CreateWave(TimeSpan duration)
    {
        const int sampleRate = 16000;
        const short channels = 1;
        const short bitsPerSample = 16;
        int dataSize = (int)(sampleRate * duration.TotalSeconds) * channels * bitsPerSample / 8;
        MemoryStream stream = new(44 + dataSize);
        using (BinaryWriter writer = new(stream, Encoding.UTF8, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bitsPerSample / 8);
            writer.Write((short)(channels * bitsPerSample / 8));
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            writer.Write(new byte[dataSize]);
        }
        stream.Position = 0;
        return stream;
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        List<T> results = [];
        await foreach (T item in source)
        {
            results.Add(item);
        }
        return results;
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    private sealed class BlockingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.Delay(Timeout.InfiniteTimeSpan);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class CapturedRequest
    {
        public HttpMethod? Method { get; set; }
        public Uri? Uri { get; set; }
        public AuthenticationHeaderValue? Authorization { get; set; }
        public Dictionary<string, CapturedPart> Parts { get; set; } = [];
    }

    private sealed record CapturedPart(string Text, byte[] Bytes);
}
