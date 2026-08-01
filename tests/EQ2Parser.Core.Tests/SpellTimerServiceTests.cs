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

    [Fact]
    public void Same_Mob_And_Ability_In_Two_Zones_Are_Distinct_And_Zone_Picks_The_Twin()
    {
        // Mayong recurs across zones with retuned abilities: both versions
        // coexist (zone-qualified identity), and the current zone's version
        // starts — the other stays a fallback.
        var service = new SpellTimerService();
        service.AddOrUpdateDefinition(new TimerDefinition
        {
            Name = "Blanket of Eternal Night", Category = "Mayong Mistmoore",
            Zone = "Mistmoore's Inner Sanctum", RestrictToCategory = true, DurationSeconds = 53,
        });
        service.AddOrUpdateDefinition(new TimerDefinition
        {
            Name = "Blanket of Eternal Night", Category = "Mayong Mistmoore",
            Zone = "Throne of New Tunaria", RestrictToCategory = true, DurationSeconds = 61,
        });
        Assert.Equal(2, service.Definitions.Count);

        var timers = new List<ActiveTimer>();
        service.TimerStarted += (_, t) => timers.Add(t);

        service.Notify("Mayong Mistmoore", "Blanket of Eternal Night", false, "sofja", T0,
            currentZone: "Throne of New Tunaria");
        Assert.Equal(61, timers[^1].DurationSeconds);

        service.Notify("Mayong Mistmoore", "Blanket of Eternal Night", false, "sofja", T0.AddSeconds(120),
            currentZone: "Mistmoore's Inner Sanctum");
        Assert.Equal(53, timers[^1].DurationSeconds);

        // Unknown zone: falls back to a version rather than starting nothing.
        service.Notify("Mayong Mistmoore", "Blanket of Eternal Night", false, "menludiir", T0.AddSeconds(240));
        Assert.Equal(3, timers.Count);
    }

    // ---- timer mods (ACT ApplyTimerMod: final = base × (1 + Σ mods),
    //      same-name mods replace, Modable=false ignores them) ----

    [Fact]
    public void Timer_Mods_Scale_Modable_Timers_At_Start()
    {
        var service = Service(new TimerDefinition { Name = "Doom", DurationSeconds = 60, Modable = true });
        service.AddTimerMod("Bossmob", "Sluggish Recast", 0.5, T0, TimeSpan.FromSeconds(30));
        // Same name never stacks — re-adding replaces.
        service.AddTimerMod("Bossmob", "Sluggish Recast", 0.5, T0, TimeSpan.FromSeconds(30));

        Assert.True(service.Notify("Bossmob", "Doom", self: false, "sofja", T0));
        var timer = Assert.Single(Assert.Single(service.Frames).Timers);
        Assert.Equal(90, timer.DurationSeconds);
        Assert.Equal(60, timer.BaseDurationSeconds);

        // Different names sum additively: 60 × (1 + 0.5 + 0.25) = 105.
        service.AddTimerMod("Bossmob", "Temporal Drag", 0.25, T0, TimeSpan.FromSeconds(30));
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "menludiir", T0));
        var second = Assert.Single(service.Frames.First(f => f.Combatant == "menludiir").Timers);
        Assert.Equal(105, second.DurationSeconds);
    }

    [Fact]
    public void NonModable_Timers_Ignore_Mods_And_Other_Owners_Are_Unaffected()
    {
        var service = Service(new TimerDefinition { Name = "Doom", DurationSeconds = 60, Modable = false });
        service.AddTimerMod("Bossmob", "Sluggish Recast", 0.5, T0, TimeSpan.FromSeconds(30));
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "sofja", T0));
        Assert.Equal(60, Assert.Single(Assert.Single(service.Frames).Timers).DurationSeconds);

        var modable = Service(new TimerDefinition { Name = "Doom", DurationSeconds = 60, Modable = true });
        modable.AddTimerMod("Bossmob", "Sluggish Recast", 0.5, T0, TimeSpan.FromSeconds(30));
        // A different caster has no mods — unmodified.
        Assert.True(modable.Notify("Othermob", "Doom", self: false, "sofja", T0));
        Assert.Equal(60, Assert.Single(Assert.Single(modable.Frames).Timers).DurationSeconds);
    }

    [Fact]
    public void Owner_Death_Drops_Mods_And_Reverts_Timers_Started_Within_Two_Seconds()
    {
        var service = Service(new TimerDefinition { Name = "Doom", DurationSeconds = 60, Modable = true });
        service.AddTimerMod("Bossmob", "Sluggish Recast", 0.5, T0, TimeSpan.FromSeconds(30));
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
        service.AddTimerMod("Bossmob", "Sluggish Recast", 0.5, T0, TimeSpan.FromSeconds(30));
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
        service.AddTimerMod("Bossmob", "Sluggish Recast", 0.5, T0, TimeSpan.FromSeconds(30));
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "sofja", T0));

        service.RemoveTimerMod("Bossmob", "Sluggish Recast", T0.AddSeconds(1.5));
        // 1.5 s > the 1 s dispel window — stays modified…
        Assert.Equal(90, Assert.Single(Assert.Single(service.Frames).Timers).DurationSeconds);
        // …but the mod itself is gone for future timers.
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "menludiir", T0.AddSeconds(5)));
        Assert.Equal(60, Assert.Single(service.Frames.First(f => f.Combatant == "menludiir").Timers).DurationSeconds);
    }

    [Fact]
    public void Mods_Expire_After_Their_Debuff_Duration()
    {
        var service = Service(new TimerDefinition { Name = "Doom", DurationSeconds = 60, Modable = true });
        service.AddTimerMod("Bossmob", "Sluggish Recast", 0.5, T0, TimeSpan.FromSeconds(30));

        // 31 s later the debuff has worn off — unmodified.
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "sofja", T0.AddSeconds(31)));
        Assert.Equal(60, Assert.Single(Assert.Single(service.Frames).Timers).DurationSeconds);

        // Re-applying refreshes the window.
        service.AddTimerMod("Bossmob", "Sluggish Recast", 0.5, T0.AddSeconds(40), TimeSpan.FromSeconds(30));
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "menludiir", T0.AddSeconds(60)));
        Assert.Equal(90, Assert.Single(service.Frames.First(f => f.Combatant == "menludiir").Timers).DurationSeconds);
    }

    [Fact]
    public void Traumatic_Swipe_Hits_Produce_And_Cures_Remove_The_Mod()
    {
        // ACT_English_Parser hardcodes exactly one recast debuff: every
        // Traumatic Swipe hit → ApplyTimerMod(victim, 50%, 30 s).
        var service = Service(new TimerDefinition { Name = "Doom", DurationSeconds = 60, Modable = true });
        Assert.False(service.NotifyRecastDebuff("Teramo", "Bossmob", "Kidney Stab", T0));
        Assert.True(service.NotifyRecastDebuff("Teramo", "Bossmob", "Traumatic Swipe", T0));

        Assert.True(service.Notify("Bossmob", "Doom", self: false, "sofja", T0.AddSeconds(2)));
        Assert.Equal(90, Assert.Single(Assert.Single(service.Frames).Timers).DurationSeconds);

        // A cure stripping Traumatic Swipe drops the mod (unrelated effects don't).
        service.NotifyDispel("Bossmob", "Some Other Effect", T0.AddSeconds(3));
        service.NotifyDispel("Bossmob", "Traumatic Swipe", T0.AddSeconds(3));
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "menludiir", T0.AddSeconds(10)));
        Assert.Equal(60, Assert.Single(service.Frames.First(f => f.Combatant == "menludiir").Timers).DurationSeconds);
    }

    [Fact]
    public void Swiper_Death_Rescales_Running_Timers_ProRata()
    {
        // The debuff dies with its applier: elapsed time stays spent, the
        // remaining portion shrinks back to the unmodified rate.
        var service = Service(new TimerDefinition { Name = "Doom", DurationSeconds = 60, Modable = true });
        service.NotifyRecastDebuff("Teramo", "Bossmob", "Traumatic Swipe", T0);
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "sofja", T0));
        var timer = Assert.Single(Assert.Single(service.Frames).Timers);
        Assert.Equal(90, timer.DurationSeconds);

        // Teramo dies 30 s in: 30 elapsed + 60 remaining × (1/1.5) = 70.
        service.NotifyDeath("Teramo", T0.AddSeconds(30));
        Assert.Equal(70, timer.DurationSeconds);

        // The pending mod is gone too — the next timer is unmodified.
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "menludiir", T0.AddSeconds(35)));
        Assert.Equal(60, Assert.Single(service.Frames.First(f => f.Combatant == "menludiir").Timers).DurationSeconds);

        // A second death is a no-op — the contribution was already removed.
        service.NotifyDeath("Teramo", T0.AddSeconds(40));
        Assert.Equal(70, timer.DurationSeconds);
    }

    [Fact]
    public void Reapplied_Swipe_Transfers_Ownership_To_The_Newest_Swiper()
    {
        // Same-name mods replace (ACT semantics) — the newest applier owns
        // the debuff, so the FIRST swiper's death changes nothing.
        var service = Service(new TimerDefinition { Name = "Doom", DurationSeconds = 60, Modable = true });
        service.NotifyRecastDebuff("Teramo", "Bossmob", "Traumatic Swipe", T0);
        service.NotifyRecastDebuff("Sennhe", "Bossmob", "Traumatic Swipe", T0.AddSeconds(5));
        Assert.True(service.Notify("Bossmob", "Doom", self: false, "sofja", T0.AddSeconds(6)));
        var timer = Assert.Single(Assert.Single(service.Frames).Timers);
        Assert.Equal(90, timer.DurationSeconds);

        service.NotifyDeath("Teramo", T0.AddSeconds(10));
        Assert.Equal(90, timer.DurationSeconds);

        // The owner's death does rescale: 4 elapsed + 86 × (1/1.5) ≈ 61.
        service.NotifyDeath("Sennhe", T0.AddSeconds(10));
        Assert.Equal(61, timer.DurationSeconds);
    }

    [Fact]
    public void Linked_Notify_Resolves_Within_The_Triggers_Zone_And_Mob()
    {
        // Two same-named timers; the trigger's own filing decides which
        // starts — not the name-based last-wins the fallback would pick.
        var service = new SpellTimerService();
        service.AddOrUpdateDefinition(new TimerDefinition
        {
            Name = "Treyloth Reflect", Category = "Treyloth D'Kulvith",
            Zone = "Freethinker Hideout", DurationSeconds = 30,
        });
        service.AddOrUpdateDefinition(new TimerDefinition
        {
            Name = "Treyloth Reflect", Category = "Someone Else",
            Zone = "Another Zone", DurationSeconds = 99,
        });

        Assert.True(service.NotifyLinked("Treyloth Reflect", "Freethinker Hideout", "Treyloth D'Kulvith",
            "Treyloth D'Kulvith", "sofja", T0));
        var scoped = Assert.Single(service.Frames.SelectMany(f => f.Timers));
        Assert.Equal(30, scoped.DurationSeconds);

        // No filing on the trigger (plain ACT import) → name-based
        // fallback, where the last-added definition wins.
        Assert.True(service.NotifyLinked("Treyloth Reflect", "", "", "x", "othervictim", T0.AddSeconds(60)));
        var fallback = service.Frames.SelectMany(f => f.Timers).Single(t => t.DurationSeconds != 30);
        Assert.Equal(99, fallback.DurationSeconds);
    }

    [Fact]
    public void Multi_Name_Category_Matches_Any_Alternative()
    {
        // Split mobs: one restricted timer whose Category lists every name
        // the mob takes ("the earth rumbler" splits into bisected copies).
        var service = Service(new TimerDefinition
        {
            Name = "Rumbling of Earth",
            Category = "the earth rumbler|bisected rumbler|trisected rumbler",
            RestrictToCategory = true,
            DurationSeconds = 30,
        });
        Assert.True(service.Notify("Bisected Rumbler", "Rumbling of Earth", self: false, "sofja", T0));
        Assert.True(service.Notify("the earth rumbler", "Rumbling of Earth", self: false, "sofja", T0.AddSeconds(40)));
        Assert.False(service.Notify("a lava rumbler", "Rumbling of Earth", self: false, "sofja", T0.AddSeconds(80)));
    }
}
