using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FlyleafLib.MediaPlayer.Translation.Services;

#nullable enable

// OpenAI, LMStudio, Claude
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

        string jsonResultString = "";
        int statusCode = -1;

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
            new OpenAIMessage
            {
                role = "system",
                content = _basePrompt
            }
        };

        // add history
        messages!.AddRange(_messageQueue);

        // add new message
        OpenAIMessage newMessage = new() { role = "user", content = text };
        messages.Add(newMessage);

        try
        {
            OpenAIRequest requestBody = new()
            {
                model = _settings.Model,
                messages = messages.ToArray(),
                stream = false
            };

            // Convert to JSON
            string jsonContent = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var result = await _httpClient.PostAsync("/v1/chat/completions", content, token);

            jsonResultString = await result.Content.ReadAsStringAsync(token);

            statusCode = (int)result.StatusCode;
            result.EnsureSuccessStatusCode();

            OpenAIResponse? chatResponse = JsonSerializer.Deserialize<OpenAIResponse>(jsonResultString);
            string reply = chatResponse!.choices[0].message.content;

            // add to message history if success
            _messageQueue.Enqueue(newMessage);
            _messageQueue.Enqueue(new OpenAIMessage { role = "assistant", content = reply });

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
            throw new TranslationException($"Cannot request to {ServiceType}: {ex.Message}", ex)
            {
                Data =
                {
                    ["status_code"] = statusCode.ToString(),
                    ["response"] = jsonResultString
                }
            };
        }
    }

    private async Task<string> DoOneByOne(string text, CancellationToken token)
    {
        if (_basePrompt == null)
            throw new InvalidOperationException("must be initialized");

        string jsonResultString = "";
        int statusCode = -1;

        try
        {
            string prompt = _basePrompt.Replace("{source_text}", text);

            // Create the request payload
            OpenAIRequest requestBody = new()
            {
                model = _settings.Model,
                messages =
                [
                    new OpenAIMessage
                    {
                        role = "user",
                        content = prompt
                    }
                ],
                stream = false
            };

            // Convert to JSON
            string jsonContent = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var result = await _httpClient.PostAsync("/v1/chat/completions", content, token);

            jsonResultString = await result.Content.ReadAsStringAsync(token);

            statusCode = (int)result.StatusCode;
            result.EnsureSuccessStatusCode();

            OpenAIResponse? chatResponse = JsonSerializer.Deserialize<OpenAIResponse>(jsonResultString);
            string reply = chatResponse!.choices[0].message.content;

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
            throw new TranslationException($"Cannot request to {ServiceType}: {ex.Message}", ex)
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

        string jsonResultString = "";
        int statusCode = -1;

        // getting models
        try
        {
            var result = await client.GetAsync("/v1/models");

            jsonResultString = await result.Content.ReadAsStringAsync();

            statusCode = (int)result.StatusCode;
            result.EnsureSuccessStatusCode();

            JsonNode? node = JsonNode.Parse(jsonResultString);
            List<string> models = node!["data"]!.AsArray().Select(model => model!["id"]!.GetValue<string>()).Order().ToList();

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

    public static async Task<string> Hello(OpenAIBaseTranslateSettings settings)
    {
        using HttpClient client = settings.GetHttpClient();

        string jsonResultString = "";
        int statusCode = -1;

        try
        {
            // Create the request payload
            OpenAIRequest requestBody = new()
            {
                model = settings.Model,
                messages =
                [
                    new OpenAIMessage
                    {
                        role = "user",
                        content = "Hello"
                    }
                ],
                stream = false
            };

            // Convert to JSON
            string jsonContent = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var result = await client.PostAsync("/v1/chat/completions", content);

            jsonResultString = await result.Content.ReadAsStringAsync();

            statusCode = (int)result.StatusCode;
            result.EnsureSuccessStatusCode();

            OpenAIResponse? chatResponse = JsonSerializer.Deserialize<OpenAIResponse>(jsonResultString);
            string reply = chatResponse!.choices[0].message.content;

            return reply.Trim();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"hello error: {ex.Message}", ex)
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
    public string model { get; set; }
    public OpenAIMessage[] messages { get; set; }
    public bool stream { get; set; }
}

public class OpenAIResponse
{
    public OpenAIChoice[] choices { get; set; }
}

public class OpenAIChoice
{
    public OpenAIMessage message { get; set; }
}
