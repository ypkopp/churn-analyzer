using System.Net;
using System.Text;
using System.Text.Json;

namespace Churn.Web.Services;

/// <summary>
/// Optional AI brief. Calls an operator-configured HTTPS endpoint with a
/// de-identified aggregate of the current evaluation batch — never emails,
/// raw snapshot rows, secrets, or service names. Fails closed when the key
/// or endpoint is absent, non-HTTPS, or resolves to an internal/metadata
/// address, so the feature is simply unavailable rather than proxied to an
/// arbitrary host. One explicit call per request; results are cached per
/// evaluationId so polling never triggers the model.
/// </summary>
public sealed class LlmService(IHttpClientFactory httpFactory, IConfiguration cfg)
{
    public record Result(bool Ok, string? Brief, string? Reason, string? Model, string? EvaluationId);

    private static readonly string[] AllowedHosts =
        (Environment.GetEnvironmentVariable("Ai__AllowedHosts")
            ?? "api.openai.com,api.anthropic.com,generativelanguage.googleapis.com")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Endpoint);
    public string? DisabledReason => !Enabled ? "ai_not_configured" : null;

    private string? ApiKey => cfg["Ai:ApiKey"] ?? Environment.GetEnvironmentVariable("Ai__ApiKey");
    private string? Endpoint => cfg["Ai:Endpoint"] ?? Environment.GetEnvironmentVariable("Ai__Endpoint");
    private string? Model => cfg["Ai:Model"] ?? Environment.GetEnvironmentVariable("Ai__Model") ?? "gpt-4o-mini";
    private int TimeoutSeconds => cfg.GetValue("Ai:TimeoutSeconds", 30);
    private int MaxResponseBytes => cfg.GetValue("Ai:MaxResponseBytes", 1 << 20);

    /// <summary>Validate that the configured endpoint is safe to call:
    /// HTTPS only, host in the allowlist, and no internal/metadata IP.
    /// Re-checks before every call (endpoint may be re-configured).</summary>
    private async Task<bool> EndpointAllowedAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        if (!AllowedHosts.Any(a => host.Equals(a, StringComparison.OrdinalIgnoreCase)
                                   || host.EndsWith("." + a, StringComparison.OrdinalIgnoreCase)))
            return false;
        // block internal/link-local/metadata targets — no SSRF proxying
        IPAddress[] addrs;
        try { addrs = await Dns.GetHostAddressesAsync(host); }
        catch { return false; }
        foreach (var a in addrs)
        {
            if (IPAddress.IsLoopback(a)) return false;
            var b = a.GetAddressBytes();
            if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                if (b[0] == 10 || b[0] == 127 || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)) return false;
            }
            else // IPv6: block link-local, unique-local, and the metadata-ish ::1 handled above
            {
                if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal) return false;
                if (b[0] == 0xfc || b[0] == 0xfd) return false;
            }
        }
        return true;
    }

    /// <summary>Ask the configured model for a brief over the de-identified
    /// aggregate. Caller passes only aggregate fields — never raw rows.</summary>
    public async Task<Result> BriefAsync(object aggregate, string evaluationId, CancellationToken ct)
    {
        if (!Enabled) return new(false, null, DisabledReason ?? "ai_not_configured", Model, evaluationId);
        if (!await EndpointAllowedAsync(Endpoint!))
            return new(false, null, "ai_endpoint_not_allowed", Model, evaluationId);

        var payload = JsonSerializer.Serialize(new
        {
            model = Model,
            messages = new[]
            {
                new { role = "system", content =
                    "You summarize churn-priority analytics for a marketer. " +
                    "Input is a de-identified aggregate of ONE evaluation batch. " +
                    "Score is a heuristic priority index, not a probability; the money figure " +
                    "is a scenario index, not expected loss or savings; no_assessment means " +
                    "insufficient data, not low risk. Be brief, cite input numbers, list " +
                    "cautious recommendations and data limitations. Do not claim accuracy." },
                new { role = "user", content = JsonSerializer.Serialize(aggregate) }
            },
            max_tokens = 600, temperature = 0.2
        });

        using var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        try
        {
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (body.Length > MaxResponseBytes) return new(false, null, "ai_response_too_large", Model, evaluationId);
            if (!res.IsSuccessStatusCode)
                return new(false, null, $"ai_http_{(int)res.StatusCode}", Model, evaluationId);
            var doc = JsonDocument.Parse(body);
            var text = doc.RootElement
                .GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return string.IsNullOrWhiteSpace(text)
                ? new(false, null, "ai_empty", Model, evaluationId)
                : new(true, text, null, Model, evaluationId);
        }
        catch (OperationCanceledException) { return new(false, null, "ai_timeout", Model, evaluationId); }
        catch (Exception) { return new(false, null, "ai_error", Model, evaluationId); }
    }
}
