using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.Tests;

/// <summary>
/// The Lexicon sync flow end-to-end against a canned pack (no network):
/// fetch → cache → apply into the trigger/timer stores, sticky user
/// overrides in both directions, offline startup from cache, and the
/// version-skip that avoids re-churning every engine.
/// </summary>
public sealed class LexiconSyncServiceTests : IDisposable
{
    private readonly TestDataDir _dir = new();
    private readonly TriggerService _triggers;
    private readonly TimerService _timers;

    public LexiconSyncServiceTests()
    {
        // Audio is only touched when an ENGINE fires a trigger — these tests
        // never create engines, so the sync flow runs without the TTS stack.
        _triggers = new TriggerService(null!, new object());
        _timers = new TimerService(null!, new object());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dir.Dispose();
    }

    private sealed class CannedHandler(Func<string> body) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body(), Encoding.UTF8, "application/json"),
            });
        }
    }

    private LexiconSyncService Service(Func<string> body, out CannedHandler handler)
    {
        handler = new CannedHandler(body);
        return new LexiconSyncService(_triggers, _timers, "https://example.test", new HttpClient(handler));
    }

    private static string Pack(string version) => $$"""
    {
      "version": "{{version}}",
      "zones": [
        {
          "zone": "Deathtoll",
          "expansion": "KoS",
          "encounters": [
            {
              "mob": "Tarinax",
              "position": 1,
              "triggers": [
                {
                  "regex": "casts Death Sweep",
                  "sound_data": "sweep incoming",
                  "sound_type": 3,
                  "category_restrict": true,
                  "category": "Tarinax",
                  "timer": false,
                  "timer_name": "",
                  "active": true,
                  "cooldown_seconds": 2
                },
                {
                  "regex": "curator disabled this one",
                  "sound_data": "",
                  "sound_type": 0,
                  "category_restrict": false,
                  "category": "Tarinax",
                  "timer": false,
                  "timer_name": "",
                  "active": false,
                  "cooldown_seconds": 1
                }
              ],
              "spell_timers": [
                {
                  "name": "Death Sweep",
                  "checked": true,
                  "timer_duration_s": 45,
                  "warning_value": 10,
                  "remove_value": -15,
                  "only_master_ticks": false,
                  "restrict": false,
                  "absolute": false,
                  "start_wav": "",
                  "warning_wav": "tts",
                  "radial_display": false,
                  "modable": true,
                  "tooltip": "",
                  "fill_color": -16776961,
                  "panel1": true,
                  "panel2": false,
                  "category": "Tarinax",
                  "restrict_category": true,
                  "damage_type": "slashing",
                  "control_effect": ""
                }
              ]
            }
          ]
        }
      ]
    }
    """;

    [Fact]
    public async Task Sync_Applies_The_Pack_Into_Both_Stores_And_Caches_It()
    {
        var service = Service(() => Pack("v1"), out var handler);
        await service.SyncAsync();

        var trigger = Assert.Single(_triggers.Definitions, t => t.RegexText == "casts Death Sweep");
        Assert.Equal("lexicon", trigger.Source);
        Assert.Equal("Deathtoll", trigger.Zone);
        Assert.True(trigger.Enabled);

        var timer = Assert.Single(_timers.Definitions, d => d.Name == "Death Sweep");
        Assert.Equal("lexicon", timer.Source);
        Assert.Equal(45, timer.DurationSeconds);
        Assert.True(timer.Modable);

        Assert.Equal(1, handler.Requests);
        Assert.True(File.Exists(_dir.FileIn("lexicon_pack.json")), "pack must be cached for offline startup");
        // Lexicon rows are a synced mirror — they must never be persisted
        // into the user's own triggers.json.
        Assert.DoesNotContain("casts Death Sweep",
            File.Exists(_dir.FileIn("triggers.json")) ? File.ReadAllText(_dir.FileIn("triggers.json")) : "");
    }

    [Fact]
    public async Task Startup_Applies_The_Cached_Pack_Before_Fetching()
    {
        // Seed the cache, then start with a handler that FAILS — offline
        // startup must still apply the cached pack.
        File.WriteAllText(_dir.FileIn("lexicon_pack.json"), Pack("v1"));
        var handler = new ThrowingHandler();
        var service = new LexiconSyncService(_triggers, _timers, "https://example.test", new HttpClient(handler));

        await service.StartupAsync();

        Assert.Single(_triggers.Definitions, t => t.RegexText == "casts Death Sweep");
        Assert.Single(_timers.Definitions, d => d.Name == "Death Sweep");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("offline");
    }

    [Fact]
    public async Task User_Overrides_Reapply_In_Both_Directions()
    {
        // The user disabled the curator-enabled trigger and enabled the
        // curator-disabled one; both choices must stick across a sync.
        File.WriteAllText(_dir.FileIn("lexicon_overrides.json"), """
        {
          "DisabledTriggers": [ "Deathtoll|Tarinax|casts Death Sweep" ],
          "DisabledTimers": [],
          "EnabledTriggers": [ "Deathtoll|Tarinax|curator disabled this one" ],
          "EnabledTimers": []
        }
        """);
        var service = Service(() => Pack("v1"), out _);
        await service.SyncAsync();

        Assert.False(Assert.Single(_triggers.Definitions, t => t.RegexText == "casts Death Sweep").Enabled);
        Assert.True(Assert.Single(_triggers.Definitions, t => t.RegexText == "curator disabled this one").Enabled);
    }

    [Fact]
    public async Task Unchanged_Version_Skips_The_ReApply_Churn()
    {
        var applies = 0;
        _triggers.DefinitionsChanged += () => applies++;
        var service = Service(() => Pack("v1"), out var handler);

        await service.SyncAsync();
        await service.SyncAsync();

        Assert.Equal(2, handler.Requests);
        Assert.Equal(1, applies); // second sync: same version, no re-apply
    }
}
