using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.Tests;

/// <summary>The raid-boss list sync against a canned server (no network):
/// fetch → cache → apply, offline startup from the cache, the version skip,
/// the refresh interval, and that a failed fetch keeps the last good list.</summary>
public sealed class RaidBossSyncServiceTests : IDisposable
{
    private readonly TestDataDir _dir = new();
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dir.Dispose();
    }

    private sealed class CannedHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests;
        public string? LastUrl;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            LastUrl = request.RequestUri?.ToString();
            return Task.FromResult(respond());
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string Pack(string version, params string[] bosses) =>
        $$"""{"version":"{{version}}","bosses":[{{string.Join(",", bosses.Select(b => $"\"{b}\""))}}]}""";

    private static RaidBossSyncService Service(Func<HttpResponseMessage> respond, out CannedHandler handler)
    {
        handler = new CannedHandler(respond);
        return new RaidBossSyncService("https://example.test/", new HttpClient(handler));
    }

    [Fact]
    public async Task First_Fetch_Applies_And_Caches_The_List()
    {
        var svc = Service(() => Json(Pack("v1", "Trakanon", "Hoshkar")), out var handler);
        await svc.EnsureFreshAsync(T0);

        Assert.Equal("https://example.test/api/zones/raid-bosses", handler.LastUrl);
        Assert.Equal("v1", svc.Version);
        Assert.NotNull(svc.Bosses);
        Assert.Contains("trakanon", svc.Bosses!);
        Assert.Contains("hoshkar", svc.Bosses!);
        Assert.True(File.Exists(_dir.FileIn("raid_bosses.json")));
    }

    [Fact]
    public async Task Cached_List_Applies_Offline_Before_Any_Fetch()
    {
        File.WriteAllText(_dir.FileIn("raid_bosses.json"), Pack("cached", "Trakanon"));
        var svc = Service(() => throw new HttpRequestException("offline"), out var handler);

        await svc.EnsureFreshAsync(T0);

        Assert.Equal(1, handler.Requests);  // tried, failed quietly
        Assert.Equal("cached", svc.Version);
        Assert.Contains("trakanon", svc.Bosses!);
    }

    [Fact]
    public async Task Same_Version_Is_Not_Reapplied_And_Refresh_Waits_For_The_Interval()
    {
        var version = "v1";
        var svc = Service(() => Json(Pack(version, "Trakanon")), out var handler);
        await svc.EnsureFreshAsync(T0);
        var first = svc.Bosses;

        await svc.EnsureFreshAsync(T0.AddMinutes(5));            // inside the interval — no request
        Assert.Equal(1, handler.Requests);

        await svc.EnsureFreshAsync(T0 + RaidBossSyncService.RefreshInterval);  // due — re-fetch, same version
        Assert.Equal(2, handler.Requests);
        Assert.Same(first, svc.Bosses);                          // not re-applied

        version = "v2";
        await svc.EnsureFreshAsync(T0 + RaidBossSyncService.RefreshInterval * 2);
        Assert.Equal("v2", svc.Version);
        Assert.NotSame(first, svc.Bosses);
    }

    [Fact]
    public async Task A_Bad_Body_Keeps_The_Last_Good_List()
    {
        var body = Pack("v1", "Trakanon");
        var svc = Service(() => Json(body), out _);
        await svc.EnsureFreshAsync(T0);

        body = "<html>maintenance</html>";
        await svc.SyncAsync();

        Assert.Equal("v1", svc.Version);
        Assert.Contains("trakanon", svc.Bosses!);
        Assert.Contains("Trakanon", File.ReadAllText(_dir.FileIn("raid_bosses.json")));  // cache untouched
    }

    [Fact]
    public async Task Corrupt_Cache_Is_Ignored_Not_Fatal()
    {
        File.WriteAllText(_dir.FileIn("raid_bosses.json"), "{ not json");
        var svc = Service(() => Json(Pack("v1", "Trakanon")), out _);

        await svc.EnsureFreshAsync(T0);

        Assert.Equal("v1", svc.Version);
    }
}
