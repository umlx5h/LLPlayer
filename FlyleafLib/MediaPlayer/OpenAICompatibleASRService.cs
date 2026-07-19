using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FlyleafLib.MediaPlayer;

#nullable enable

public sealed class OpenAICompatibleASRService : IASRService
{
    private const int MaxErrorBodyLength = 2048;

    private readonly Config _config;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;

    public OpenAICompatibleASRService(Config config)
        : this(config, CreateHttpClient(), true)
    {
    }

    public OpenAICompatibleASRService(Config config, HttpClient httpClient)
        : this(config, httpClient, false)
    {
    }

    private OpenAICompatibleASRService(Config config, HttpClient httpClient, bool disposeHttpClient)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _disposeHttpClient = disposeHttpClient;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<(string text, TimeSpan start, TimeSpan end, string language)> Do(
        MemoryStream waveStream,
        [EnumeratorCancellation] CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(waveStream);

        OpenAICompatibleASRConfig apiConfig = _config.Subtitles.OpenAICompatibleASRConfig;
        Uri endpoint = BuildTranscriptionsUri(apiConfig.BaseUrl);
        byte[] waveBytes = waveStream.ToArray();
        TimeSpan waveDuration = ReadWaveDuration(waveBytes);

        using MultipartFormDataContent form = new();
        ByteArrayContent audio = new(waveBytes);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "audio.wav");
        form.Add(new StringContent(apiConfig.Model.Trim(), Encoding.UTF8), "model");
        form.Add(new StringContent("verbose_json", Encoding.UTF8), "response_format");

        WhisperConfig commonConfig = _config.Subtitles.WhisperConfig;
        if (!commonConfig.LanguageDetection && !string.IsNullOrWhiteSpace(commonConfig.Language))
        {
            form.Add(new StringContent(commonConfig.Language.Trim(), Encoding.UTF8), "language");
        }

        using HttpRequestMessage request = new(HttpMethod.Post, endpoint) { Content = form };
        if (!string.IsNullOrWhiteSpace(apiConfig.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiConfig.ApiKey.Trim());
        }

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(apiConfig.TimeoutSeconds));

        string body;
        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"ASR endpoint returned {(int)response.StatusCode} ({response.ReasonPhrase}): {FormatErrorBody(body)}",
                    null,
                    response.StatusCode);
            }
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The ASR endpoint did not respond within {apiConfig.TimeoutSeconds} seconds.", ex);
        }

        using JsonDocument document = ParseResponse(body);
        JsonElement root = document.RootElement;
        _ = ReadRequiredText(root);
        string language = ResolveLanguage(root, commonConfig);
        List<(string text, TimeSpan start, TimeSpan end)> parsedSegments = [];

        if (root.TryGetProperty("segments", out JsonElement segments) &&
            segments.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement segment in segments.EnumerateArray())
            {
                if (!TryReadSegment(segment, waveDuration, out string text, out TimeSpan start, out TimeSpan end))
                {
                    continue;
                }

                parsedSegments.Add((text, start, end));
            }
        }

        parsedSegments.Sort(static (left, right) =>
        {
            int startComparison = left.start.CompareTo(right.start);
            return startComparison != 0 ? startComparison : right.end.CompareTo(left.end);
        });

        TimeSpan previousEnd = TimeSpan.Zero;
        foreach ((string text, TimeSpan start, TimeSpan end) in parsedSegments)
        {
            TimeSpan normalizedStart = start < previousEnd ? previousEnd : start;
            if (end <= normalizedStart)
            {
                continue;
            }

            previousEnd = end;
            yield return (text, normalizedStart, end, language);
        }
    }

    private static HttpClient CreateHttpClient() => new() { Timeout = Timeout.InfiniteTimeSpan };

    public static Uri BuildTranscriptionsUri(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "Base URL must be an absolute HTTP(S) URL without a query or fragment.",
                nameof(baseUrl));
        }

        UriBuilder builder = new(uri);
        string path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase))
        {
            path = path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? $"{path}/audio/transcriptions"
                : $"{path}/v1/audio/transcriptions";
        }

        builder.Path = path;
        return builder.Uri;
    }

    private static JsonDocument ParseResponse(string body)
    {
        try
        {
            JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new InvalidDataException("ASR endpoint returned JSON that is not an object.");
            }

            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("ASR endpoint returned invalid JSON.", ex);
        }
    }

    private static string ReadRequiredText(JsonElement root)
    {
        if (!root.TryGetProperty("text", out JsonElement text) || text.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("ASR endpoint response is missing the required string field 'text'.");
        }

        return text.GetString() ?? string.Empty;
    }

    private static string ResolveLanguage(JsonElement root, WhisperConfig commonConfig)
    {
        if (root.TryGetProperty("language", out JsonElement languageElement) &&
            languageElement.ValueKind == JsonValueKind.String)
        {
            string? language = languageElement.GetString();
            if (!string.IsNullOrWhiteSpace(language) &&
                !language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return language;
            }
        }

        if (!commonConfig.LanguageDetection && !string.IsNullOrWhiteSpace(commonConfig.Language))
        {
            return commonConfig.Language;
        }

        return "und";
    }

    private static bool TryReadSegment(
        JsonElement segment,
        TimeSpan waveDuration,
        out string text,
        out TimeSpan start,
        out TimeSpan end)
    {
        text = string.Empty;
        start = TimeSpan.Zero;
        end = TimeSpan.Zero;

        if (segment.ValueKind != JsonValueKind.Object ||
            !segment.TryGetProperty("text", out JsonElement textElement) ||
            textElement.ValueKind != JsonValueKind.String ||
            !segment.TryGetProperty("start", out JsonElement startElement) ||
            !segment.TryGetProperty("end", out JsonElement endElement) ||
            !startElement.TryGetDouble(out double startSeconds) ||
            !endElement.TryGetDouble(out double endSeconds) ||
            !double.IsFinite(startSeconds) ||
            !double.IsFinite(endSeconds))
        {
            return false;
        }

        text = (textElement.GetString() ?? string.Empty).Trim();
        double durationSeconds = waveDuration.TotalSeconds;
        startSeconds = Math.Clamp(startSeconds, 0, durationSeconds);
        endSeconds = Math.Clamp(endSeconds, 0, durationSeconds);
        if (string.IsNullOrWhiteSpace(text) || endSeconds <= startSeconds)
        {
            return false;
        }

        start = TimeSpan.FromSeconds(startSeconds);
        end = TimeSpan.FromSeconds(endSeconds);
        return true;
    }

    private static TimeSpan ReadWaveDuration(byte[] bytes)
    {
        // AudioReader passes IASRService a fixed 44-byte PCM WAV header.
        ReadOnlySpan<byte> data = bytes;
        if (data.Length < 44 ||
            !data[..4].SequenceEqual("RIFF"u8) ||
            !data.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("ASR input is not a valid PCM WAV stream.");
        }

        int byteRate = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(28, 4));
        int dataSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(40, 4));
        if (byteRate <= 0 || dataSize <= 0 || dataSize > data.Length - 44)
        {
            throw new InvalidDataException("ASR WAV stream has an invalid byte rate or data size.");
        }

        return TimeSpan.FromSeconds(dataSize / (double)byteRate);
    }

    private static string FormatErrorBody(string body)
    {
        string compact = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (compact.Length > MaxErrorBodyLength)
        {
            compact = compact[..MaxErrorBodyLength] + "...";
        }

        return string.IsNullOrEmpty(compact) ? "No response body." : compact;
    }
}
