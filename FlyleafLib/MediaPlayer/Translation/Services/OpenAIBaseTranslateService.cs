using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FlyleafLib.MediaPlayer.Translation.Services;

#nullable enable

// All LLM translation use this class
// Currently only supports OpenAI compatible API
public class OpenAIBaseTranslateService : ITranslateService
{
    private readonly HttpClient _httpClient;
    private readonly OpenAIBaseTranslateSettings _settings;
    private readonly TranslateChatConfig _chatConfig;
    private readonly bool _wordMode;

    private ChatTranslateMethod TranslateMethod => _chatConfig.TranslateMethod;

    public OpenAIBaseTranslateService(OpenAIBaseTranslateSettings settings, TranslateChatConfig chatConfig, bool wordMode)
    {
        _httpClient = settings.GetHttpClient();
        _settings = settings;
        _chatConfig = chatConfig;
        _wordMode = wordMode;
    }

    private string? _basePrompt;
    private readonly ConcurrentQueue<OpenAIMessage> _messageQueue = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public TranslateServiceType ServiceType => _settings.ServiceType;

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public void Initialize(Language src, TargetLanguage target)
    {
        (TranslateLanguage srcLang, TranslateLanguage targetLang) = this.TryGetLanguage(src, target);

        // setup prompt
        string prompt = !_wordMode && TranslateMethod == ChatTranslateMethod.KeepContext
            ? _chatConfig.PromptKeepContext
            : _chatConfig.PromptOneByOne;

        string targetLangName = _chatConfig.IncludeTargetLangRegion
            ? target.DisplayName() : targetLang.Name;

        _basePrompt = prompt
            .Replace("{source_lang}", srcLang.Name)
            .Replace("{target_lang}", targetLangName);
    }

    public async Task<string> TranslateAsync(string text, CancellationToken token)
    {
        if (!_wordMode && TranslateMethod == ChatTranslateMethod.KeepContext)
        {
            return await DoKeepContext(text, token);
        }

        return await DoOneByOne(text, token);
    }

    private async Task<string> DoKeepContext(string text, CancellationToken token)
    {
        if (_basePrompt == null)
            throw new InvalidOperationException("must be initialized");

        // Trim message history if required
        while (_messageQueue.Count / 2 > _chatConfig.SubtitleContextCount)
        {
            if (_chatConfig.ContextRetainPolicy == ChatContextRetainPolicy.KeepSize)
            {
                Debug.Assert(_messageQueue.Count >= 2);

                // user
                _messageQueue.TryDequeue(out _);
                // assistant
                _messageQueue.TryDequeue(out _);
            }
            else if (_chatConfig.ContextRetainPolicy == ChatContextRetainPolicy.Reset)
            {
                // clear
                _messageQueue.Clear();
            }
        }

        List<OpenAIMessage> messages = new(_messageQueue.Count + 2)
        {
            new OpenAIMessage { role = "system", content = _basePrompt },
        };

        // add history
        messages.AddRange(_messageQueue);

        // add new message
        OpenAIMessage newMessage = new() { role = "user", content = text };
        messages.Add(newMessage);

        string reply = await SendChatRequest(
            _httpClient, _settings, messages.ToArray(), token);

        // add to message history if success
        _messageQueue.Enqueue(newMessage);
        _messageQueue.Enqueue(new OpenAIMessage { role = "assistant", content = reply });

        return reply;
    }

    private async Task<string> DoOneByOne(string text, CancellationToken token)
    {
        if (_basePrompt == null)
            throw new InvalidOperationException("must be initialized");

        string prompt = _basePrompt.Replace("{source_text}", text);

        OpenAIMessage[] messages =
        [
            new() { role = "user", content = prompt }
        ];

        return await SendChatRequest(_httpClient, _settings, messages, token);
    }

    public static async Task<string> Hello(OpenAIBaseTranslateSettings settings)
    {
        using HttpClient client = settings.GetHttpClient();

        OpenAIMessage[] messages =
        [
            new() { role = "user", content = "Hello" }
        ];

        return await SendChatRequest(client, settings, messages, CancellationToken.None);
    }

    private static async Task<string> SendChatRequest(
        HttpClient client,
        OpenAIBaseTranslateSettings settings,
        OpenAIMessage[] messages,
        CancellationToken token)
    {
        string jsonResultString = string.Empty;
        int statusCode = -1;

        // Create the request payload
        OpenAIRequest request = new()
        {
            model = settings.Model,
            stream = false,
            messages = messages,

            temperature = settings.TemperatureManual ? settings.Temperature : null,
            top_p = settings.TopPManual ? settings.TopP : null
        };

        if (!settings.ModelRequired && string.IsNullOrWhiteSpace(settings.Model))
        {
            request.model = null;
        }

        try
        {
            // Convert to JSON
            string jsonContent = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            using var result = await client.PostAsync(settings.ChatPath, content, token);

            jsonResultString = await result.Content.ReadAsStringAsync(token);

            statusCode = (int)result.StatusCode;
            result.EnsureSuccessStatusCode();

            OpenAIResponse? chatResponse = JsonSerializer.Deserialize<OpenAIResponse>(jsonResultString);
            string reply = chatResponse!.choices[0].message.content;
            if (settings.ReasonStripRequired)
            {
                reply = ChatReplyParser.StripReasoning(reply);
            }

            return reply.Trim();
        }
        // Distinguish between timeout and cancel errors
        catch (OperationCanceledException ex)
            when (!ex.Message.StartsWith("The request was canceled due to the configured HttpClient.Timeout"))
        {
            // cancel
            throw;
        }
        catch (Exception ex)
        {
            // timeout and other error
            throw new TranslationException($"Cannot request to {settings.ServiceType}: {ex.Message}", ex)
            {
                Data =
                {
                    ["status_code"] = statusCode.ToString(),
                    ["response"] = jsonResultString
                }
            };
        }
    }

    public static async Task<List<string>> GetLoadedModels(OpenAIBaseTranslateSettings settings)
    {
        using HttpClient client = settings.GetHttpClient(true);

        string jsonResultString = string.Empty;
        int statusCode = -1;

        // getting models
        try
        {
            using var result = await client.GetAsync("/v1/models");

            jsonResultString = await result.Content.ReadAsStringAsync();

            statusCode = (int)result.StatusCode;
            result.EnsureSuccessStatusCode();

            JsonNode? node = JsonNode.Parse(jsonResultString);
            List<string> models = node!["data"]!.AsArray()
                .Select(model => model!["id"]!.GetValue<string>())
                .Order()
                .ToList();

            return models;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"get models error: {ex.Message}", ex)
            {
                Data =
                {
                    ["status_code"] = statusCode.ToString(),
                    ["response"] = jsonResultString
                }
            };
        }
    }
}

public class OpenAIMessage
{
    public string role { get; set; }
    public string content { get; set; }
}

public class OpenAIRequest
{
    public string? model { get; set; }
    public OpenAIMessage[] messages { get; set; }
    public bool stream { get; set; }
    public double? temperature { get; set; }
    public double? top_p { get; set; }
}

public class OpenAIResponse
{
    public OpenAIChoice[] choices { get; set; }
}

public class OpenAIChoice
{
    public OpenAIMessage message { get; set; }
}

public static class ChatReplyParser
{
    // Target tag names to remove (lowercase)
    private static readonly string[] Tags = ["think", "reason", "reasoning", "thought"];

    // open/close tag strings from tag names
    private static readonly string[] OpenTags;
    private static readonly string[] CloseTags;

    static ChatReplyParser()
    {
        OpenTags = new string[Tags.Length];
        CloseTags = new string[Tags.Length];
        for (int i = 0; i < Tags.Length; i++)
        {
            OpenTags[i] = $"<{Tags[i]}>";       // e.g. "<think>"
            CloseTags[i] = $"</{Tags[i]}>";    // e.g. "</think>"
        }
    }

    /// <summary>
    /// Removes a leading reasoning tag if present and returns only the generated message portion.
    /// </summary>
    public static string StripReasoning(string input)
    {
        // Return immediately if it doesn't start with a tag
        if (string.IsNullOrEmpty(input) || input[0] != '<')
            return input;

        var span = input.AsSpan();

        for (int i = 0; i < OpenTags.Length; i++)
        {
            if (span.StartsWith(OpenTags[i], StringComparison.OrdinalIgnoreCase))
            {
                int endIdx = span.IndexOf(CloseTags[i], StringComparison.OrdinalIgnoreCase);
                if (endIdx >= 0)
                {
                    int next = endIdx + CloseTags[i].Length;
                    // Skip over any consecutive line breaks and whitespace
                    while (next < span.Length && char.IsWhiteSpace(span[next]))
                    {
                        next++;
                    }
                    return span.Slice(next).ToString();
                }
            }
        }

        // Return original string if no tag matched
        return input;
    }
}
