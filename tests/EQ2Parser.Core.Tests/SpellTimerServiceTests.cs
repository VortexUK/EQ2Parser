using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Core.Tests;

public class SpellTimerServiceTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    private static SpellTimerService Service(TimerDefinition def, TimerOptions? options = null)
    {
        var service = new SpellTimerService(options);
        service.AddOrUpdateDefinition(def);
        return service;
    }

    private static readonly TimerDefinition Doom = new()
    {
        Name = "Doom",
        DurationSeconds = 30,
        WarningSeconds = 10,
        RemoveSeconds = -5,
    };

    [Fact]
    public void Notify_Starts_A_Master_Timer()
    {
        var service = Service(Doom);
        var started = new List<(TimerFrame, ActiveTimer)>();
        service.TimerStarted += (f, t) => started.Add((f, t));

        Assert.True(service.Notify("bossmob", "Doom", self: true, "sofja", T0));
        var (frame, timer) = Assert.Single(started);
        Assert.Equal("Doom - sofja", frame.Key);
        Assert.True(timer.IsMaster);
        Assert.Equal(30, timer.SecondsLeft(T0), precision: 3);
    }

    [Fact]
    public void Unknown_Spell_Does_Nothing()
    {
        var service = Service(Doom);
        Assert.False(service.Notify("a", "Unheard Of", true, "b", T0));
    }

    [Fact]
    public void Retrigger_Windows_Dedupe_Then_SubTimer_Then_Master()
    {
        var service = Service(Doom);
        var timers = new List<ActiveTimer>();
        service.TimerStarted += (_, t) => timers.Add(t);

        Assert.True(service.Notify("x", "Doom", true, "sofja", T0));
        // <2s: ignored outright.
        Assert.False(service.Notify("x", "Doom", true, "sofja", T0.AddSeconds(1)));
        // 2-12s: sub-timer.
        Assert.True(service.Notify("x", "Doom", true, "sofja", T0.AddSeconds(5)));
        Assert.False(timers[^1].IsMaster);
        // >12s since newest: fresh master.
        Assert.True(service.Notify("x", "Doom", true, "sofja", T0.AddSeconds(20)));
        Assert.True(timers[^1].IsMaster);
        Assert.Equal(3, timers.Count);
    }

    [Fact]
    public void The_Windows_Are_Configurable()
    {
        var service = Service(Doom, new TimerOptions
        {
            RetriggerIgnore = TimeSpan.FromMilliseconds(100),
            SubTimerWindow = TimeSpan.FromSeconds(1),
        });
        Assert.True(service.Notify("x", "Doom", true, "v", T0));
        Assert.True(service.Notify("x", "Doom", true, "v", T0.AddSeconds(0.5))); // sub under custom window
        Assert.True(service.Notify("x", "Doom", true, "v", T0.AddSeconds(2)));   // master past it
    }

    [Fact]
    public void OneOnly_Refuses_While_A_Master_Runs()
    {
        var service = Service(Doom with { AbsoluteTiming = true });
        Assert.True(service.Notify("x", "Doom", true, "v", T0));
        Assert.False(service.Notify("x", "Doom", true, "v", T0.AddSeconds(15)));
        // After expiry it can start again.
        Assert.True(service.Notify("x", "Doom", true, "v", T0.AddSeconds(31)));
    }

    [Fact]
    public void RestrictToMe_Gates_On_Self()
    {
        var service = Service(Doom with { RestrictToMe = true });
        Assert.False(service.Notify("someone", "Doom", self: false, "else", T0));
        Assert.True(service.Notify("me", "Doom", self: true, "else", T0));
    }

    [Fact]
    public void Category_Restricted_Definition_Wins_When_It_Matches()
    {
        var service = new SpellTimerService();
        service.AddOrUpdateDefinition(Doom with { DurationSeconds = 30 });
        service.AddOrUpdateDefinition(Doom with { Category = "bossmob", RestrictToCategory = true, DurationSeconds = 60 });

        var timers = new List<ActiveTimer>();
        service.TimerStarted += (_, t) => timers.Add(t);

        // Attacker matches the restricted category → 60s definition wins.
        service.Notify("Bossmob", "Doom", true, "sofja", T0);
        Assert.Equal(60, timers[^1].DurationSeconds);

        // No category match → unrestricted 30s definition.
        service.Notify("someone", "Doom", true, "other", T0);
        Assert.Equal(30, timers[^1].DurationSeconds);
    }

    [Fact]
    public void Tick_Raises_Warning_Expiry_And_Removes_Past_The_Linger()
    {
        var service = Service(Doom);
        var events = new List<string>();
        service.WarningReached += (_, _) => events.Add("warning");
        service.TimerExpired += (_, _) => events.Add("expired");
        service.FrameRemoved += _ => events.Add("removed");

        service.Notify("x", "Doom", true, "v", T0);
        service.Tick(T0.AddSeconds(15));
        Assert.Equal([], events); // 15 left > 10 warning threshold

        service.Tick(T0.AddSeconds(21));
        Assert.Equal(["warning"], events);

        service.Tick(T0.AddSeconds(30.5));
        Assert.Equal(["warning", "expired"], events);

        // RemoveSeconds -5 → the bar lingers until 35s.
        service.Tick(T0.AddSeconds(34));
        Assert.Equal(["warning", "expired"], events);
        Assert.Single(service.Frames);

        service.Tick(T0.AddSeconds(36));
        Assert.Equal(["warning", "expired", "removed"], events);
        Assert.Empty(service.Frames);
    }

    // ---- timer mods (ACT ApplyTimerMod: final = base × (1 + Σ mods),
    //      same-name mods replace, Modable=false ignores them) ----

    [Fact]
    public void Timer_Mods_Scale_Modable_Timers_At_Start()
    {
        var service = Service(new TimerDefinition { Name = "Doom", DurationSeconds = 60, Modable = true });
        service.AddTimerMod("Bossmob", "Sluggish Recast", 0.5);
        // Same name never stacks — re-adding replaces.
        service.AddTimerMod("Bossmob", "Sluggish Recast", 0.5);

        Assert.True(service.Notify("Bossmob", "Doom", self: false, "sofja", T0));
        var timer = Assert.Single(Assert.Single(service.Frames).Timers);
        Assert.Equal(90, timer.DurationSeconds);
        Assert.Equal(60, timer.BaseDurationSeconds);

        // Different names sum additively: 60 × (1 + 0.5 + 0.25) = 105.
        service.AddTimerMod("Bossmob", "Temporal Drag", 0.25);
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "menludiir", T0));
        var second = Assert.Single(service.Frames.First(f => f.Combatant == "menludiir").Timers);
        Assert.Equal(105, second.DurationSeconds);
    }

    [Fact]
    public void NonModable_Timers_Ignore_Mods_And_Other_Owners_Are_Unaffected()
    {
        var service = Service(new TimerDefinition { Name = "Doom", DurationSeconds = 60, Modable = false });
        service.AddTimerMod("Bossmob", "Sluggish Recast", 0.5);
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "sofja", T0));
        Assert.Equal(60, Assert.Single(Assert.Single(service.Frames).Timers).DurationSeconds);

        var modable = Service(new TimerDefinition { Name = "Doom", DurationSeconds = 60, Modable = true });
        modable.AddTimerMod("Bossmob", "Sluggish Recast", 0.5);
        // A different caster has no mods — unmodified.
        Assert.True(modable.Notify("Othermob", "Doom", self: false, "sofja", T0));
        Assert.Equal(60, Assert.Single(Assert.Single(modable.Frames).Timers).DurationSeconds);
    }

    [Fact]
    public void Owner_Death_Drops_Mods_And_Reverts_Timers_Started_Within_Two_Seconds()
    {
        var service = Service(new TimerDefinition { Name = "Doom", DurationSeconds = 60, Modable = true });
        service.AddTimerMod("Bossmob", "Sluggish Recast", 0.5);
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "sofja", T0));

        // Death 1 s later: inside the grace window — the recast never
        // committed, the bar reverts to base.
        service.ClearTimerMods("Bossmob", T0.AddSeconds(1));
        Assert.Equal(60, Assert.Single(Assert.Single(service.Frames).Timers).DurationSeconds);

        // Mods are gone: the next timer is unmodified.
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "menludiir", T0.AddSeconds(5)));
        Assert.Equal(60, Assert.Single(service.Frames.First(f => f.Combatant == "menludiir").Timers).DurationSeconds);
    }

    [Fact]
    public void Old_Modified_Timers_Survive_Owner_Death()
    {
        var service = Service(new TimerDefinition { Name = "Doom", DurationSeconds = 60, Modable = true });
        service.AddTimerMod("Bossmob", "Sluggish Recast", 0.5);
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "sofja", T0));

        // Death 3 s later: past the 2 s window — the committed recast keeps
        // its modified duration.
        service.ClearTimerMods("Bossmob", T0.AddSeconds(3));
        Assert.Equal(90, Assert.Single(Assert.Single(service.Frames).Timers).DurationSeconds);
    }

    [Fact]
    public void Dispel_Reverts_Only_Within_One_Second()
    {
        var service = Service(new TimerDefinition { Name = "Doom", DurationSeconds = 60, Modable = true });
        service.AddTimerMod("Bossmob", "Sluggish Recast", 0.5);
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "sofja", T0));

        service.RemoveTimerMod("Bossmob", "Sluggish Recast", T0.AddSeconds(1.5));
        // 1.5 s > the 1 s dispel window — stays modified…
        Assert.Equal(90, Assert.Single(Assert.Single(service.Frames).Timers).DurationSeconds);
        // …but the mod itself is gone for future timers.
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "menludiir", T0.AddSeconds(5)));
        Assert.Equal(60, Assert.Single(service.Frames.First(f => f.Combatant == "menludiir").Timers).DurationSeconds);
    }
}
