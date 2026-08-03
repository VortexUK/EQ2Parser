using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Upload;

namespace EQ2Parser.Core.Tests;

public class LogPathsTests
{
    [Theory]
    [InlineData(@"C:\EQ2\logs\Varsoon\eq2log_Kayleigh.txt", "Varsoon")]
    [InlineData(@"C:\EQ2\logs\Antonia Bayle\eq2log_Bob.txt", "Antonia Bayle")]
    [InlineData("/home/u/eq2/logs/Varsoon/eq2log_X.txt", "Varsoon")]
    [InlineData(@"C:\EQ2\logs\eq2log_Kayleigh.txt", "")] // legacy layout — parent is "logs"
    [InlineData(@"C:\EQ2\LOGS\eq2log_K.txt", "")] //         …case-insensitively
    [InlineData("eq2log_K.txt", "")] //                      no directory at all
    [InlineData(@"C:\", "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Server_Is_The_Parent_Directory_Unless_Its_The_Logs_Root(string? path, string expected) =>
        Assert.Equal(expected, LogPaths.ParseServerName(path));
}

public class UploadUrlGuardTests
{
    [Theory]
    [InlineData("https://varsoon.eq2lexicon.com")]
    [InlineData("https://varsoon.eq2lexicon.com/")]
    [InlineData("http://localhost:8000")] // loopback dev exception
    [InlineData("http://127.0.0.1:8000")]
    public void Accepts_Https_And_Loopback_Http(string url) =>
        Assert.Null(LexiconUploadClient.UrlProblem(url));

    [Theory]
    [InlineData("http://varsoon.eq2lexicon.com")] // cleartext token — refused
    [InlineData("ftp://example.com")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Rejects_Cleartext_And_Garbage(string url) =>
        Assert.NotNull(LexiconUploadClient.UrlProblem(url));
}

public class UploadQueueTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    private static Encounter BuildEncounter()
    {
        var engine = new ParserEngine(@"C:\EQ2\logs\Varsoon\eq2log_Menludiir.txt", "Menludiir");
        engine.ChangeZone("Deathtoll");
        Assert.True(engine.SetEncounter(T0, "Menludiir", "Lord Bob"));
        engine.AddSwing(SwingCategory.Melee, true, "None", "Menludiir", "Strike", 1000, T0, "Lord Bob", "crushing");
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menludiir", Combatant.KillingAbility, DamageValue.Death, T0.AddSeconds(10), "Lord Bob", "death");
        engine.EndCombat();
        return engine.History[^1];
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private static UploadResult Ok() => new(true, 200, "Uploaded (inserted).");

    [Fact]
    public async Task Uploads_A_Finished_Encounter_With_Its_Server()
    {
        LexiconPayload? sent = null;
        using var queue = new UploadQueue((p, _) =>
        {
            sent = p;
            return Task.FromResult(Ok());
        });

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Uploaded == 1, "upload");

        Assert.Equal("Varsoon", sent!.LoggerServer);
        Assert.Equal("Menludiir", sent.LoggerName);
        Assert.Equal("Lord Bob", sent.Encounter.Title);
        Assert.Contains("1 uploaded this session", queue.Status);
    }

    [Fact]
    public async Task Transient_Failures_Retry_Then_Succeed()
    {
        var attempts = 0;
        using var queue = new UploadQueue((_, _) =>
        {
            var n = Interlocked.Increment(ref attempts);
            return Task.FromResult(n < 3 ? new UploadResult(false, 503, "unavailable") : Ok());
        })
        { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero] };

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Uploaded == 1, "retried upload");
        Assert.Equal(3, attempts);
        Assert.Equal(0, queue.Failed);
    }

    [Fact]
    public async Task Permanent_Rejection_Fails_Without_Retry()
    {
        var attempts = 0;
        using var queue = new UploadQueue((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new UploadResult(false, 422, "Implausible parse."));
        })
        { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero] };

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Failed == 1, "permanent failure");
        Assert.Equal(1, attempts);
        Assert.Equal(0, queue.Uploaded);
    }

    [Fact]
    public async Task Rejected_Token_Pauses_The_Queue_Until_Reset()
    {
        var attempts = 0;
        using var queue = new UploadQueue((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new UploadResult(false, 401, "Invalid or expired token."));
        });

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.AuthPaused, "auth pause");
        Assert.Equal(1, attempts);

        // While paused, new work is dropped — the bad credential is never
        // re-sent for every trash fight of a play session.
        queue.Enqueue(BuildEncounter(), "Varsoon");
        await Task.Delay(100);
        Assert.Equal(1, attempts);

        queue.ResetAuthPause();
        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => attempts == 2, "resume after reset");
    }

    [Fact]
    public async Task Provenance_Warnings_Ride_Along_On_Auto_Uploads_Only()
    {
        var payloads = new List<LexiconPayload>();
        using var queue = new UploadQueue(
            (p, _) =>
            {
                lock (payloads)
                {
                    payloads.Add(p);
                }
                return Task.FromResult(Ok());
            },
            provenance: _ => [LogProvenance.WriterVerified]);

        queue.Enqueue(BuildEncounter(), "Varsoon"); //                     auto: probes
        queue.Enqueue(BuildEncounter(), "Varsoon", withProvenance: false); // manual: claims nothing
        await WaitFor(() => queue.Uploaded == 2, "both uploads");

        Assert.Equal([LogProvenance.WriterVerified], payloads[0].ClientWarnings);
        Assert.Null(payloads[1].ClientWarnings);
        // And the wire shape: omitted entirely when null, present when set.
        Assert.DoesNotContain("client_warnings", payloads[1].ToJson());
        Assert.Contains($"\"client_warnings\":[\"{LogProvenance.WriterVerified}\"]", payloads[0].ToJson());
    }

    [Fact]
    public async Task Probe_Failure_Never_Blocks_The_Upload()
    {
        using var queue = new UploadQueue(
            (_, _) => Task.FromResult(Ok()),
            provenance: _ => throw new InvalidOperationException("probe exploded"));

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Uploaded == 1, "upload despite probe failure");
    }

    [Fact]
    public async Task Sender_Exception_Is_A_Failure_Not_A_Crash()
    {
        // The reconfigure race: the client can be disposed mid-send. The
        // drain must survive and count it, not die silently.
        using var queue = new UploadQueue((_, _) =>
            throw new ObjectDisposedException(nameof(LexiconUploadClient)))
        { RetryDelays = [] };

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Failed == 1, "surfaced failure");
    }
}
