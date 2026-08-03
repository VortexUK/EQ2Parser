using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EQ2Parser.Core.Upload;

public sealed record UploadResult(bool Success, int StatusCode, string Message, string Body = "");

/// <summary>
/// HTTP client for the EQ2Lexicon site — the exact transport contract the
/// ACT plugin v0.1.17 ships (and the server's gzip middleware expects):
///   * Bearer token auth,
///   * HMAC-SHA256 over the UNCOMPRESSED JSON in X-Lexicon-Signature,
///   * body gzip-compressed with Content-Encoding: gzip,
///   * 60 s timeout (uploads are background work; patience is free).
/// HttpMessageHandler is injectable for tests.
/// </summary>
public sealed class LexiconUploadClient : IDisposable
{
    public const string SignatureHeader = "X-Lexicon-Signature";

    private readonly HttpClient _http;

    public LexiconUploadClient(string serverUrl, string apiToken, HttpMessageHandler? handler = null)
    {
        ServerUrl = serverUrl.TrimEnd('/');
        ApiToken = apiToken;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"EQ2Parser/{typeof(LexiconUploadClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}");
    }

    public string ServerUrl { get; }
    private string ApiToken { get; }

    public void Dispose() => _http.Dispose();

    /// <summary>Refuses to put the bearer token on the wire in cleartext:
    /// https only, with a loopback exception for local dev servers. Returns
    /// the problem, or null when the URL is acceptable.</summary>
    public static string? UrlProblem(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "Server URL is not a valid http(s) address.";
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            return "Uploads need an https:// server URL — your API token would otherwise travel unencrypted.";
        return null;
    }

    /// <summary>HMAC-SHA256 lowercase hex, keyed by the API token — must match
    /// the server's recomputation over the decompressed body.</summary>
    public static string Sign(string payloadJson, string apiToken) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(apiToken), Encoding.UTF8.GetBytes(payloadJson)));

    public static byte[] CompressUtf8Gzip(string json)
    {
        var raw = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw);
        return ms.ToArray();
    }

    public async Task<UploadResult> UploadAsync(LexiconPayload payload, CancellationToken ct = default)
    {
        var json = payload.ToJson();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ServerUrl}/api/parses/ingest");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        request.Headers.Add(SignatureHeader, Sign(json, ApiToken));

        var content = new ByteArrayContent(CompressUtf8Gzip(json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        content.Headers.ContentEncoding.Add("gzip");
        request.Content = content;

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new UploadResult(false, (int)response.StatusCode, ExtractDetail(body) ?? $"Server responded {(int)response.StatusCode}", body);
            var status = ExtractField(body, "status") ?? "ok";
            return new UploadResult(true, (int)response.StatusCode, $"Uploaded ({status}).", body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new UploadResult(false, 0, "Upload timed out.");
        }
        catch (HttpRequestException ex)
        {
            return new UploadResult(false, 0, $"Network error: {ex.Message}");
        }
    }

    public async Task<UploadResult> WhoAmIAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ServerUrl}/api/auth/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new UploadResult(response.IsSuccessStatusCode, (int)response.StatusCode,
                response.IsSuccessStatusCode ? "OK" : ExtractDetail(body) ?? "Auth failed", body);
        }
        catch (HttpRequestException ex)
        {
            return new UploadResult(false, 0, $"Network error: {ex.Message}");
        }
    }

    private static string? ExtractDetail(string body) => ExtractField(body, "detail");

    private static string? ExtractField(string body, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
