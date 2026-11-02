using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace tryAGI.Codex;

/// <summary>Bounded, single-attempt public Live feature requests. OAuth and API-key routes are chosen explicitly by the host.</summary>
public sealed class CodexLiveFeatureClient : IDisposable
{
    private readonly Func<CancellationToken, ValueTask<CodexCredentials>> credentials;
    private readonly bool sendAccount;
    private readonly HttpClient http;
    private readonly ICodexUsageObserver? usage;
    /// <summary>Creates a client that signs requests with ChatGPT OAuth credentials.</summary>
    /// <param name="authorization">Supplies credentials.</param>
    /// <param name="usage">Optional admission and accounting hooks.</param>
    /// <param name="handler">Optional HTTP handler.</param>
    public CodexLiveFeatureClient(ICodexCredentialProvider authorization, ICodexUsageObserver? usage = null, HttpMessageHandler? handler = null)
        : this(authorization.GetCredentialsAsync, true, usage, handler) { }
    private CodexLiveFeatureClient(Func<CancellationToken, ValueTask<CodexCredentials>> credentials, bool sendAccount,
        ICodexUsageObserver? usage, HttpMessageHandler? handler)
    {
        this.credentials = credentials; this.sendAccount = sendAccount; this.usage = usage;
        http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    /// <summary>Creates a client that signs requests with an OpenAI API key.</summary>
    /// <param name="key">Supplies the API key.</param>
    /// <param name="handler">Optional HTTP handler.</param>
    public static CodexLiveFeatureClient ForApiKey(Func<CancellationToken, ValueTask<string>> key, HttpMessageHandler? handler = null) =>
        new(async ct => new CodexCredentials(await key(ct).ConfigureAwait(false), "api-key"), false, null, handler);

    /// <summary>Creates a custom voice from a description. Single attempt: an uncertain creation is never retried.</summary>
    /// <param name="name">Voice name.</param>
    /// <param name="prompt">Description of the voice.</param>
    /// <param name="scriptHint">Optional representative script.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<CodexPromptVoice> CreateVoiceAsync(string name, string prompt, string? scriptHint = null, CancellationToken cancellationToken = default)
    {
        var body = CodexLiveFeatures.PromptVoice(name, prompt, scriptHint);
        var result = await PostAsync("audio/voices", body, null, cancellationToken).ConfigureAwait(false);
        var id = CodexRealtimeProtocol.Text(result, "id");
        if (id is null || !id.StartsWith("voice_", StringComparison.Ordinal) || !CodexLiveFeatures.Identifier(id))
            throw new CodexProtocolException("Voice creation returned no valid voice ID; do not retry an uncertain creation.");
        return new(id, name, prompt);
    }

    /// <summary>Asks the provider to answer multiple-choice questions about a text.</summary>
    /// <param name="input">Text to decide about, 1–16000 characters.</param>
    /// <param name="questions">Questions to answer.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The raw decision response; every question has a valid answer.</returns>
    public async Task<JsonElement> DecideAsync(string input, IReadOnlyList<CodexLiveChoiceQuestion> questions, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 16000) throw new ArgumentException("Decision input must contain 1..16000 characters.");
        var body = new JsonObject { ["model"] = "gpt-6-luna", ["input"] = input, ["questions"] = CodexLiveFeatures.Questions(questions) };
        var result = await PostAsync("decisions", body, "gpt-6-luna", cancellationToken).ConfigureAwait(false);
        var answers = CodexLiveFeatures.ReadAnswers(result) ?? throw new CodexProtocolException("No complete supported choice answers returned.");
        if (answers.Count != questions.Count || answers.Any(a => !questions.Any(q => q.Name == a.Name && q.Choices.Contains(a.Choice, StringComparer.Ordinal))))
            throw new CodexProtocolException("Decision answers did not match the requested choices.");
        return result;
    }

    private async Task<JsonElement> PostAsync(string path, JsonObject body, string? model, CancellationToken ct)
    {
        var id = "live-feature-" + Guid.NewGuid().ToString("N");
        var reserved = false; var dispatched = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            if (model is not null && usage is not null) { await usage.ReserveAsync(id, model, deadline.Token).ConfigureAwait(false); reserved = true; }
            var auth = await credentials(deadline.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/" + path)
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
            request.Headers.Add("originator", CodexEndpointOptions.DefaultOriginator);
            if (sendAccount) request.Headers.Add("ChatGPT-Account-Id", auth.AccountId);
            dispatched = true;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var code = new CodexRealtimeError(null, null, null, null);
                try
                {
                    using var error = JsonDocument.Parse(await CodexClient.ReadBoundedAsync(response.Content, 16384, deadline.Token).ConfigureAwait(false));
                    if (error.RootElement.ValueKind == JsonValueKind.Object && error.RootElement.TryGetProperty("error", out var fields))
                    {
                        static string? Safe(JsonElement e, string key) => CodexRealtimeProtocol.Text(e, key) is { } s && CodexLiveFeatures.Identifier(s) ? s : null;
                        code = new(null, Safe(fields, "code"), Safe(fields, "type"), Safe(fields, "param"));
                    }
                }
                catch (JsonException) { }
                throw new CodexProtocolException($"Live feature request rejected (HTTP {(int)response.StatusCode}); provider text omitted.", (int)response.StatusCode) { RealtimeError = code };
            }
            JsonDocument parsed;
            try { parsed = JsonDocument.Parse(await CodexClient.ReadBoundedAsync(response.Content, 65536, deadline.Token).ConfigureAwait(false)); }
            catch (JsonException) { throw new CodexProtocolException("Live feature response is not valid JSON; provider text omitted."); }
            using var document = parsed;
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new CodexProtocolException("Live feature response must be an object.");
            var result = document.RootElement.Clone();
            if (model is not null && usage is not null) await usage.CompleteAsync(id, model, result, deadline.Token).ConfigureAwait(false);
            reserved = false;
            return result;
        }
        finally
        {
            if (reserved && model is not null && usage is not null)
            {
                using var save = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (dispatched) await usage.IncompleteAsync(id, model, save.Token).ConfigureAwait(false);
                else await usage.NotDispatchedAsync(id, model, save.Token).ConfigureAwait(false);
            }
        }
    }
    /// <inheritdoc />
    public void Dispose() => http.Dispose();
}
