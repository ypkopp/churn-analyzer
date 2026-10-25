using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

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
public sealed class LlmService(IConfiguration cfg, IMemoryCache cache)
{
    public record Result(bool Ok, string? Brief, string? Reason, string? Model, string? EvaluationId);

    // Host allowlist — a coarse gate, NOT a complete SSRF defence. DNS can
    // rebind between resolution and connect; redirects are forbidden below,
    // so a resolved allowlist host cannot silently hop elsewhere. Operator
    // still configures the endpoint deliberately.
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
    private int CacheMaxEntries => cfg.GetValue("Ai:CacheMaxEntries", 64);
    private int CacheTtlSeconds => cfg.GetValue("Ai:CacheTtlSeconds", 600);

    /// <summary>Validate that the configured endpoint is safe to call:
    /// HTTPS only, host in the allowlist, and no internal/metadata IP.</summary>
    private async Task<bool> EndpointAllowedAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        if (!AllowedHosts.Any(a => host.Equals(a, StringComparison.OrdinalIgnoreCase)
                                   || host.EndsWith("." + a, StringComparison.OrdinalIgnoreCase)))
            return false;
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
            else
            {
                if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal) return false;
                if (b[0] == 0xfc || b[0] == 0xfd) return false;
            }
        }
        return true;
    }

    // One in-flight upstream call per evaluationId — a second request for the
    // same batch shares the wait instead of starting a duplicate paid call.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>Ask the configured model for a brief over the de-identified
    /// aggregate. Caller passes only aggregate fields — never raw rows.</summary>
    public async Task<Result> BriefAsync(object aggregate, string evaluationId, CancellationToken ct)
    {
        if (!Enabled) return new(false, null, DisabledReason ?? "ai_not_configured", Model, evaluationId);
        if (!await EndpointAllowedAsync(Endpoint!))
            return new(false, null, "ai_endpoint_not_allowed", Model, evaluationId);

        var key = $"ai_brief:{evaluationId}";
        if (cache.TryGetValue(key, out string? hit) && hit is not null)
            return new(true, hit, null, Model, evaluationId);

        var gate = _locks.GetOrAdd(Guid.Parse(evaluationId), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out hit) && hit is not null)
                return new(true, hit, null, Model, evaluationId);
            var res = await CallAsync(aggregate, evaluationId, ct);
            if (res.Ok && res.Brief is not null)
            {
                var opts = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(TimeSpan.FromSeconds(CacheTtlSeconds))
                    .SetSize(1);
                if (cache is MemoryCache mc) mc.Compact(0); // bounded by size options below
                var entry = cache.CreateEntry(key);
                entry.SetOptions(opts).Value = res.Brief;
                entry.Dispose();
            }
            return res;
        }
        finally { gate.Release(); }
    }

    private async Task<Result> CallAsync(object aggregate, string evaluationId, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = Model,
            messages = new[]
            {
                new { role = "system", content =
                    "Ты кратко резюмируешь аналитику приоритетов оттока для маркетолога на русском языке. " +
                    "Вход — обезличенная сводка ОДНОГО батча оценки. " +
                    "Score — эвристический индекс приоритета, не вероятность; денежная сумма — " +
                    "сценарный индекс, не ожидаемая потеря и не экономия; no_assessment — недостаток " +
                    "данных, а не низкий риск. Пиши кратко, со ссылкой на числа из входа, осторожные " +
                    "рекомендации и ограничения данных. Не заявляй accuracy." },
                new { role = "user", content = JsonSerializer.Serialize(aggregate) }
            },
            max_tokens = 600, temperature = 0.2
        });

        // A redirect-following client could land on an arbitrary host — the
        // allowlist is checked on the configured endpoint, not on a Location
        // header we cannot validate. Auto-redirects are therefore disabled.
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        try
        {
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            // redirect to any other location is treated as controlled unavailability
            if ((int)res.StatusCode >= 300 && (int)res.StatusCode < 400)
                return new(false, null, "ai_redirect_not_allowed", Model, evaluationId);

            // read the body as a byte stream up to MaxResponseBytes+1 — no
            // whole-string allocation before the limit check.
            long total = 0;
            var sb = new StringBuilder();
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    total += read;
                    if (total > MaxResponseBytes)
                        return new(false, null, "ai_response_too_large", Model, evaluationId);
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }

            if (!res.IsSuccessStatusCode)
                return new(false, null, $"ai_http_{(int)res.StatusCode}", Model, evaluationId);
            string body = sb.ToString();
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch { return new(false, null, "ai_invalid_json", Model, evaluationId); }
            string? text = null;
            try { text = doc.RootElement.GetProperty("choices")[0]
                    .GetProperty("message").GetProperty("content").GetString(); }
            catch { return new(false, null, "ai_no_choices", Model, evaluationId); }
            return string.IsNullOrWhiteSpace(text)
                ? new(false, null, "ai_empty", Model, evaluationId)
                : new(true, text, null, Model, evaluationId);
        }
        catch (HttpRequestException) { return new(false, null, "ai_error", Model, evaluationId); }
        catch (OperationCanceledException) { return new(false, null, "ai_timeout", Model, evaluationId); }
        catch (Exception) { return new(false, null, "ai_error", Model, evaluationId); }
    }
}
