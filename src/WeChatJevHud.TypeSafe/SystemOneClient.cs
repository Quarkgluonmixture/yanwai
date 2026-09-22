using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WeChatJevHud.TypeSafe;

public sealed class SystemOneOptions
{
    public const string ApiKeyEnvironmentVariable = "TYPESAFE_API_KEY";

    public Uri Endpoint { get; init; } = new("https://api.typesafe.ai/v1/systemone");

    public string Model { get; init; } = "jev-latest";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
}

public sealed class JevException : Exception
{
    public JevException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// HTTP client for the TypeSafe System One endpoint. Request and response shapes
/// follow https://docs.typesafe.ai/api and https://docs.typesafe.ai/primitives .
/// The API key is read from the environment and is never logged.
/// </summary>
public sealed class SystemOneClient : IJevClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly SystemOneOptions _options;
    private readonly bool _ownsHttpClient;

    public SystemOneClient(string apiKey, SystemOneOptions? options = null, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new JevException(
                $"No TypeSafe API key. Set the {SystemOneOptions.ApiKeyEnvironmentVariable} environment variable.");
        }

        _options = options ?? new SystemOneOptions();
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.Timeout = _options.Timeout;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>
    /// Reads the key from the environment. An empty string counts as missing: an
    /// unset variable and a variable set to "" are the same failure here.
    /// </summary>
    public static SystemOneClient FromEnvironment(SystemOneOptions? options = null)
    {
        var key = Environment.GetEnvironmentVariable(SystemOneOptions.ApiKeyEnvironmentVariable);
        return new SystemOneClient(key ?? string.Empty, options);
    }

    public static bool HasApiKey =>
        !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(SystemOneOptions.ApiKeyEnvironmentVariable));

    public async Task<JevResult> AskAsync(
        string state,
        IReadOnlyList<JevQuestion> questions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(questions);
        if (questions.Count == 0)
        {
            throw new ArgumentException("At least one question is required.", nameof(questions));
        }

        var payload = BuildRequest(state, questions, _options.Model);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(_options.Endpoint, content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new JevException($"Jev request failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new JevException($"Jev request timed out after {_options.Timeout.TotalSeconds:N0}s.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                throw new JevException($"Jev returned {(int)response.StatusCode}: {Truncate(body, 400)}");
            }

            return ParseResponse(body, questions, stopwatch.Elapsed);
        }
    }

    internal static string BuildRequest(string state, IReadOnlyList<JevQuestion> questions, string model)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("state", state);
            writer.WriteString("model", model);
            writer.WriteStartObject("questions");

            foreach (var question in questions)
            {
                writer.WriteStartObject(question.Key);
                switch (question)
                {
                    case NoulQuestion noul:
                        writer.WriteString("type", "noul");
                        writer.WriteString("instructions", noul.Instructions);
                        break;

                    case ChoiceQuestion choice:
                        writer.WriteString("type", "choice");
                        writer.WriteString("instructions", choice.Instructions);
                        writer.WriteStartObject("criteria");
                        foreach (var option in choice.Options)
                        {
                            writer.WriteString(option.Key, option.Description);
                        }

                        writer.WriteEndObject();
                        break;

                    case ScoreQuestion score:
                        writer.WriteString("type", "score");
                        writer.WriteString("instructions", score.Instructions);
                        writer.WriteStartArray("criteria");
                        foreach (var level in score.Levels)
                        {
                            writer.WriteStringValue(level);
                        }

                        writer.WriteEndArray();
                        break;

                    default:
                        throw new JevException($"Unsupported question type: {question.GetType().Name}");
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static JevResult ParseResponse(
        string body,
        IReadOnlyList<JevQuestion> questions,
        TimeSpan latency)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new JevException($"Jev returned unparseable JSON: {Truncate(body, 200)}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            var model = root.TryGetProperty("model", out var modelElement)
                ? modelElement.GetString() ?? "unknown"
                : "unknown";

            var answers = new Dictionary<string, JevAnswer>(StringComparer.Ordinal);
            if (root.TryGetProperty("answers", out var answersElement)
                && answersElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var question in questions)
                {
                    if (answersElement.TryGetProperty(question.Key, out var answerElement))
                    {
                        answers[question.Key] = ParseAnswer(question, answerElement);
                    }
                }
            }

            var usage = new JevUsage(0, 0);
            if (root.TryGetProperty("usage", out var usageElement))
            {
                usage = new JevUsage(
                    ReadInt(usageElement, "input_tokens"),
                    ReadInt(usageElement, "output_tokens"));
            }

            return new JevResult(model, answers, usage, latency);
        }
    }

    private static JevAnswer ParseAnswer(JevQuestion question, JsonElement element)
    {
        switch (question)
        {
            case NoulQuestion:
                return new NoulAnswer(question.Key, ReadDouble(element, "noul"));

            case ChoiceQuestion:
            {
                var choice = element.TryGetProperty("choice", out var choiceElement)
                    ? choiceElement.GetString() ?? string.Empty
                    : string.Empty;

                var probabilities = new List<ChoiceProbability>();
                if (element.TryGetProperty("probabilities", out var probabilityElement)
                    && probabilityElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in probabilityElement.EnumerateObject())
                    {
                        probabilities.Add(new ChoiceProbability(property.Name, property.Value.GetDouble()));
                    }
                }

                probabilities.Sort((left, right) => right.Probability.CompareTo(left.Probability));
                return new ChoiceAnswer(
                    question.Key,
                    choice,
                    probabilities,
                    ReadOptionalDouble(element, "confidence"));
            }

            case ScoreQuestion score:
            {
                var legend = ReadLegend(element, score);
                var probabilities = ReadScoreProbabilities(element, legend.Count);

                return new ScoreAnswer(
                    question.Key,
                    ReadDouble(element, "score"),
                    legend,
                    probabilities,
                    ReadOptionalDouble(element, "confidence"));
            }

            default:
                throw new JevException($"Unsupported question type: {question.GetType().Name}");
        }
    }

    /// <summary>
    /// The legend comes back keyed by stringified level index, so it is ordered by
    /// that index rather than by property order. Falls back to the levels we sent.
    /// </summary>
    private static List<string> ReadLegend(JsonElement element, ScoreQuestion question)
    {
        var pairs = new List<(int Index, string Text)>();
        if (element.TryGetProperty("legend", out var legendElement)
            && legendElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in legendElement.EnumerateObject())
            {
                if (int.TryParse(property.Name, out var index))
                {
                    pairs.Add((index, property.Value.GetString() ?? string.Empty));
                }
            }
        }

        if (pairs.Count == 0)
        {
            return new List<string>(question.Levels);
        }

        pairs.Sort((left, right) => left.Index.CompareTo(right.Index));
        var legend = new List<string>(pairs.Count);
        foreach (var pair in pairs)
        {
            legend.Add(pair.Text);
        }

        return legend;
    }

    /// <summary>
    /// A score answer's probabilities come back as an object keyed by the stringified
    /// level index ({"0":0.0,"1":0.02,...}), not as the array the prose docs show.
    /// Both shapes are accepted; an unrecognised shape returns empty so the caller can
    /// tell "no distribution" apart from "all zero".
    /// </summary>
    private static List<double> ReadScoreProbabilities(JsonElement element, int levelCount)
    {
        var probabilities = new List<double>();
        if (!element.TryGetProperty("probabilities", out var probabilityElement))
        {
            return probabilities;
        }

        if (probabilityElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in probabilityElement.EnumerateArray())
            {
                probabilities.Add(value.GetDouble());
            }

            return probabilities;
        }

        if (probabilityElement.ValueKind != JsonValueKind.Object)
        {
            return probabilities;
        }

        var byIndex = new SortedDictionary<int, double>();
        foreach (var property in probabilityElement.EnumerateObject())
        {
            if (int.TryParse(property.Name, out var index) && property.Value.ValueKind == JsonValueKind.Number)
            {
                byIndex[index] = property.Value.GetDouble();
            }
        }

        if (byIndex.Count == 0)
        {
            return probabilities;
        }

        var size = Math.Max(levelCount, byIndex.Keys.Max() + 1);
        for (var i = 0; i < size; i++)
        {
            probabilities.Add(byIndex.TryGetValue(i, out var value) ? value : 0);
        }

        return probabilities;
    }

    private static double ReadDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : throw new JevException($"Jev answer is missing the numeric field '{name}'.");

    private static double? ReadOptionalDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
