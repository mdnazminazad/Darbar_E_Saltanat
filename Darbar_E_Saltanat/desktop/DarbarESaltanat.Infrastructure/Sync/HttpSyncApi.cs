using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DarbarESaltanat.Application.Sync;

namespace DarbarESaltanat.Infrastructure.Sync;

/// <summary>REST client for /api/v1/sync/* and /devices/*. Uses only HTTPS (plain HTTP is allowed for localhost testing).</summary>
public sealed class HttpSyncApi(HttpClient http) : ISyncApi
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public Task<PushResponse> PushAsync(SyncEndpoint ep, PushRequest request, CancellationToken ct = default) =>
        SendAsync<PushResponse>(ep, HttpMethod.Post, "sync/push", request, ct);

    public Task<PullResponse> PullAsync(SyncEndpoint ep, string? since, string? cursor, int limit, CancellationToken ct = default)
    {
        var q = new List<string> { $"limit={limit}", $"device_id={Uri.EscapeDataString(ep.DeviceId)}" };
        if (!string.IsNullOrEmpty(since)) q.Add($"since={Uri.EscapeDataString(since)}");
        if (!string.IsNullOrEmpty(cursor)) q.Add($"cursor={Uri.EscapeDataString(cursor)}");
        return SendAsync<PullResponse>(ep, HttpMethod.Get, "sync/pull?" + string.Join("&", q), null, ct);
    }

    public Task AcknowledgeAsync(SyncEndpoint ep, string pulledUntil, int pendingCount, CancellationToken ct = default) =>
        SendAsync<JsonElement>(ep, HttpMethod.Post, "sync/acknowledge", new { pulled_until = pulledUntil, pending_count = pendingCount }, ct);

    public Task HeartbeatAsync(SyncEndpoint ep, int pendingCount, string appVersion, CancellationToken ct = default) =>
        SendAsync<JsonElement>(ep, HttpMethod.Post, "devices/heartbeat", new { pending_count = pendingCount, app_version = appVersion }, ct);

    public Task<DeviceInfo> GetDeviceAsync(SyncEndpoint ep, CancellationToken ct = default) =>
        SendAsync<DeviceInfo>(ep, HttpMethod.Get, "devices/me", null, ct);

    private async Task<T> SendAsync<T>(SyncEndpoint ep, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var baseUri = new Uri(ep.BaseUrl.TrimEnd('/') + "/");
        if (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback)
            throw new SyncRejectedException("The API address must use https://");
        using var req = new HttpRequestMessage(method, new Uri(baseUri, path));
        req.Headers.Add("X-Device-Id", ep.DeviceId);
        req.Headers.Add("X-Device-Token", ep.DeviceToken);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);

        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new SyncOfflineException($"Cannot reach the server: {ex.Message}", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new SyncOfflineException("The server did not respond in time", ex); }

        using (res)
        {
            JsonElement envelope = default;
            try { envelope = await res.Content.ReadFromJsonAsync<JsonElement>(Json, ct); }
            catch (JsonException) when (!res.IsSuccessStatusCode) { /* non-JSON error page from a proxy */ }
            catch (JsonException ex) { throw new SyncOfflineException("The server returned an unreadable response", ex); }

            if (res.IsSuccessStatusCode)
            {
                if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("data", out var data))
                    throw new SyncOfflineException("Unexpected response from the server");
                return data.Deserialize<T>(Json) ?? throw new SyncOfflineException("Empty response from the server");
            }
            var message = envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m)
                ? m.GetString() ?? res.ReasonPhrase : res.ReasonPhrase;
            var text = $"{(int)res.StatusCode} {message}";
            throw res.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new SyncAuthException(text!),
                HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout => new SyncOfflineException(text!),
                >= HttpStatusCode.InternalServerError => new SyncOfflineException(text!),
                _ => new SyncRejectedException(text!),
            };
        }
    }
}
