using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

/// <summary>
/// A TTL is a deadline, and the panel is the thing watching it: the figure has
/// to read as a clock and has to move on its own, and a write that changes it
/// has to reach the header describing the key.
/// </summary>
public sealed class RedisKeyExpiryTests
{
    [Fact]
    public void ARemainingLifeReadsAsAClock()
    {
        var clock = new RedisPanelFixtures.StoppedClock(new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero));
        var key = new RedisKeyItemViewModel(
            Summary("session:9f3c1a", TimeSpan.FromSeconds((56 * 60) + 6)),
            clock);

        Assert.True(key.IsExpiring);
        Assert.Equal("00:56:06", key.Ttl);
    }

    [Fact]
    public void ARemainingLifeCountsDownWithoutReReadingTheServer()
    {
        var clock = new RedisPanelFixtures.StoppedClock(new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero));
        var key = new RedisKeyItemViewModel(Summary("cart", TimeSpan.FromHours(2)), clock);
        Assert.Equal("02:00:00", key.Ttl);

        clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal("01:58:30", key.Ttl);

        clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal("expired", key.Ttl);
    }

    [Fact]
    public void AKeyWithNoDeadlineIsNotOnAClock()
    {
        var key = new RedisKeyItemViewModel(Summary("feature:checkout-v2", null));

        Assert.False(key.IsExpiring);
        Assert.Equal("persistent", key.Ttl);
    }

    [Fact]
    public async Task WritingAValueWithATtlRefreshesTheKeysOwnHeader()
    {
        var clock = new RedisPanelFixtures.StoppedClock(new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero));
        var session = new RedisPanelFixtures.StubSession(TimeSpan.FromMinutes(56));
        using var panel = NewPanel(session, clock);
        await panel.Initialization;
        // The stub answers from memory, so the selection's read completes
        // inside the setter.
        panel.SelectedKey = panel.Keys[0];

        Assert.Contains("TTL 00:56:00", panel.SelectedKeyMetadata);

        // The write sets a new deadline, and the read that follows it is what
        // the header has to be describing.
        session.TimeToLive = TimeSpan.FromMinutes(10);
        panel.ExpirySeconds = "600";
        panel.SetExpiryCommand.Execute(null);

        Assert.Contains("TTL 00:10:00", panel.SelectedKeyMetadata);
        Assert.Equal("00:10:00", panel.Keys[0].Ttl);
    }

    [Fact]
    public async Task TickingMovesEveryExpiringRowOn()
    {
        var clock = new RedisPanelFixtures.StoppedClock(new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero));
        using var panel = NewPanel(new RedisPanelFixtures.StubSession(TimeSpan.FromMinutes(5)), clock);
        await panel.Initialization;

        var changed = 0;
        panel.Keys[0].PropertyChanged += (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(RedisKeyItemViewModel.Ttl), StringComparison.Ordinal))
            {
                changed++;
            }
        };

        clock.Advance(TimeSpan.FromSeconds(1));
        panel.TickExpiries();

        Assert.Equal(1, changed);
        Assert.Equal("00:04:59", panel.Keys[0].Ttl);
    }


    [Theory]
    [InlineData("string", "Value", null, null, "Set value")]
    [InlineData("hash", "Value", "Field", null, "Add fields")]
    [InlineData("list", "Value", null, null, "Append")]
    [InlineData("set", "Member", null, null, "Add members")]
    [InlineData("zset", "Member", null, "Score", "Add members")]
    [InlineData("stream", "Value", "Field", null, "Append entry")]
    [InlineData("json", "JSON", null, null, "Replace document")]
    [InlineData("timeseries", "Sample", null, null, "Add sample")]
    public void EachTypeAsksForWhatItActuallyNeeds(
        string type,
        string valueLabel,
        string? fieldLabel,
        string? scoreLabel,
        string actionLabel)
    {
        var form = RedisKeyForm.For(type);

        Assert.Equal(valueLabel, form.ValueLabel);
        Assert.Equal(fieldLabel, form.FieldLabel);
        Assert.Equal(scoreLabel, form.ScoreLabel);
        Assert.Equal(actionLabel, form.ActionLabel);
        Assert.Equal(fieldLabel is not null, form.HasField);
        Assert.Equal(scoreLabel is not null, form.HasScore);
    }

    [Fact]
    public async Task SelectingAKeyFillsTheTtlBoxWithWhatItHas()
    {
        using var panel = NewPanel(
            new RedisPanelFixtures.StubSession(TimeSpan.FromSeconds(90)),
            new RedisPanelFixtures.StoppedClock(DateTimeOffset.UnixEpoch));
        await panel.Initialization;

        panel.SelectedKey = panel.Keys[0];

        Assert.Equal("90", panel.ExpirySeconds);
    }

    /// <summary>
    /// A deadline is a property of the key, not of anything written into it —
    /// so it is applied on its own, for every type. Before this, only a string
    /// could be given one, and only by writing a value at the same time.
    /// </summary>
    [Theory]
    [InlineData("hash")]
    [InlineData("list")]
    [InlineData("zset")]
    public async Task WritingAnyTypeAppliesTheTtlTheBoxStates(string type)
    {
        var session = new RedisPanelFixtures.StubSession(null, type: type);
        using var panel = NewPanel(session, new RedisPanelFixtures.StoppedClock(DateTimeOffset.UnixEpoch));
        await panel.Initialization;
        panel.SelectedKey = panel.Keys[0];

        panel.ExpirySeconds = "45";
        panel.SetExpiryCommand.Execute(null);

        Assert.Equal(TimeSpan.FromSeconds(45), Assert.Single(session.ExpiryWrites));
    }

    [Fact]
    public async Task ClearingTheTtlBoxTakesTheDeadlineAway()
    {
        var session = new RedisPanelFixtures.StubSession(TimeSpan.FromSeconds(60));
        using var panel = NewPanel(session, new RedisPanelFixtures.StoppedClock(DateTimeOffset.UnixEpoch));
        await panel.Initialization;
        panel.SelectedKey = panel.Keys[0];
        Assert.Equal("60", panel.ExpirySeconds);

        panel.ExpirySeconds = string.Empty;
        panel.SetExpiryCommand.Execute(null);

        Assert.Null(Assert.Single(session.ExpiryWrites));
        Assert.Equal("persistent", panel.Keys[0].Ttl);
    }

    [Fact]
    public async Task ANewKeyCanBeBornWithADeadline()
    {
        var session = new RedisPanelFixtures.StubSession(null);
        using var panel = NewPanel(session, new RedisPanelFixtures.StoppedClock(DateTimeOffset.UnixEpoch));
        await panel.Initialization;

        panel.NewKeyName = "session:9f3c1a";
        panel.NewKeyValue = "v";
        panel.NewKeyExpirySeconds = "120";
        panel.CreateKeyCommand.Execute(null);

        Assert.Equal(TimeSpan.FromSeconds(120), Assert.Single(session.ExpiryWrites));
        Assert.Equal(string.Empty, panel.NewKeyExpirySeconds);
    }


    /// <summary>
    /// A collection is filled a handful of entries at a time. Writing one per
    /// round trip is the form making the user do the repeating.
    /// </summary>
    [Fact]
    public async Task ACollectionIsWrittenARowAtATime()
    {
        var session = new RedisPanelFixtures.StubSession(null, type: "hash");
        using var panel = NewPanel(session, new RedisPanelFixtures.StoppedClock(DateTimeOffset.UnixEpoch));
        await panel.Initialization;
        panel.SelectedKey = panel.Keys[0];

        panel.MutationEntries[0].Field = "email";
        panel.MutationEntries[0].Value = "ops@asura.dev";
        panel.AddMutationEntry();
        panel.MutationEntries[1].Field = "plan";
        panel.MutationEntries[1].Value = "team";
        // A row nobody filled in is not an empty entry to write.
        panel.AddMutationEntry();
        panel.SaveValueCommand.Execute(null);

        Assert.Equal(
            ["hash email=ops@asura.dev", "hash plan=team"],
            session.Writes);
    }

    [Fact]
    public async Task ASortedSetCarriesAScorePerRow()
    {
        var session = new RedisPanelFixtures.StubSession(null, type: "zset");
        using var panel = NewPanel(session, new RedisPanelFixtures.StoppedClock(DateTimeOffset.UnixEpoch));
        await panel.Initialization;
        panel.SelectedKey = panel.Keys[0];

        panel.MutationEntries[0].Value = "ada";
        panel.MutationEntries[0].Score = "12";
        panel.AddMutationEntry();
        panel.MutationEntries[1].Value = "grace";
        panel.MutationEntries[1].Score = "30.5";
        panel.SaveValueCommand.Execute(null);

        Assert.Equal(["zset ada@12", "zset grace@30.5"], session.Writes);
    }

    [Fact]
    public async Task ANewCollectionIsCreatedFromItsRows()
    {
        var session = new RedisPanelFixtures.StubSession(null);
        using var panel = NewPanel(session, new RedisPanelFixtures.StoppedClock(DateTimeOffset.UnixEpoch));
        await panel.Initialization;

        panel.NewKeyName = "presence:online";
        panel.NewKeyType = "set";
        panel.NewKeyEntries[0].Value = "ada";
        panel.AddNewKeyEntry();
        panel.NewKeyEntries[1].Value = "grace";
        panel.CreateKeyCommand.Execute(null);

        Assert.Equal(["set ada", "set grace"], session.Writes);
        // The next key starts from a clean row.
        Assert.Equal(string.Empty, Assert.Single(panel.NewKeyEntries).Value);
    }

    [Fact]
    public async Task AFormWithNothingInItSaysSoRatherThanWritingNothing()
    {
        var session = new RedisPanelFixtures.StubSession(null, type: "list");
        using var panel = NewPanel(session, new RedisPanelFixtures.StoppedClock(DateTimeOffset.UnixEpoch));
        await panel.Initialization;
        panel.SelectedKey = panel.Keys[0];

        panel.SaveValueCommand.Execute(null);

        Assert.Empty(session.Writes);
        Assert.Equal("Enter at least one entry.", panel.ErrorMessage);
    }

    [Fact]
    public void RemovingTheLastRowLeavesTheFormItsRow()
    {
        var panel = NewPanel(
            new RedisPanelFixtures.StubSession(null),
            new RedisPanelFixtures.StoppedClock(DateTimeOffset.UnixEpoch));
        using (panel)
        {
            panel.MutationEntries[0].Value = "typed";
            panel.RemoveMutationEntry(panel.MutationEntries[0]);

            Assert.Equal(string.Empty, Assert.Single(panel.MutationEntries).Value);
        }
    }

    [Fact]
    public void ADocumentTypeIsEditedAsADocument()
    {
        Assert.True(RedisKeyForm.For("json").IsJson);
        Assert.False(RedisKeyForm.For("json").HasEntries);
        Assert.False(RedisKeyForm.For("json").HasSingleValue);

        foreach (var collection in new[] { "hash", "list", "set", "zset" })
        {
            Assert.True(RedisKeyForm.For(collection).HasEntries, collection);
            Assert.False(RedisKeyForm.For(collection).HasSingleValue, collection);
        }

        foreach (var single in new[] { "stream", "timeseries" })
        {
            Assert.True(RedisKeyForm.For(single).HasSingleValue, single);
        }

        // A string and a document are one value: writing it is editing it, so
        // they have no add form at all.
        foreach (var whole in new[] { "string", "json" })
        {
            Assert.False(RedisKeyForm.For(whole).HasAddForm, whole);
            Assert.True(RedisKeyForm.For(whole).CanEditEntry, whole);
        }

        // A stream entry cannot be rewritten; Redis has no command for it.
        Assert.False(RedisKeyForm.For("stream").CanEditEntry);
    }


    /// <summary>
    /// A collection you can only add to is a collection you cannot correct.
    /// Each type addresses its entries differently, so the panel says which
    /// type the removal is for and names the action in that type's words.
    /// </summary>
    [Theory]
    [InlineData("hash", "Remove field", "hash email")]
    [InlineData("list", "Remove value", "list 0")]
    [InlineData("set", "Remove member", "set value")]
    [InlineData("zset", "Remove member", "zset value")]
    [InlineData("stream", "Remove entry", "stream event")]
    public async Task AnEntryCanBeTakenOutOfACollection(string type, string label, string removal)
    {
        var session = new RedisPanelFixtures.StubSession(null, type: type);
        using var panel = NewPanel(session, new RedisPanelFixtures.StoppedClock(DateTimeOffset.UnixEpoch));
        await panel.Initialization;
        panel.SelectedKey = panel.Keys[0];

        Assert.True(panel.MutationForm.CanRemoveEntries);
        Assert.Equal(label, panel.RemoveEntryLabel);

        // Nothing is pointed at, so there is nothing to remove.
        Assert.False(panel.RemoveEntryCommand.CanExecute(null));

        panel.SelectedValueEntry = panel.ValueEntries[0];
        Assert.True(panel.RemoveEntryCommand.CanExecute(null));
        panel.RemoveEntryCommand.Execute(null);

        Assert.Equal(removal, Assert.Single(session.Removals));
        // The read that follows leaves nothing pointed at.
        Assert.Null(panel.SelectedValueEntry);
    }

    [Fact]
    public async Task AValueWithNoEntriesOffersNoEntryRemoval()
    {
        var session = new RedisPanelFixtures.StubSession(null, type: "string");
        using var panel = NewPanel(session, new RedisPanelFixtures.StoppedClock(DateTimeOffset.UnixEpoch));
        await panel.Initialization;
        panel.SelectedKey = panel.Keys[0];
        panel.SelectedValueEntry = panel.ValueEntries[0];

        Assert.False(panel.MutationForm.CanRemoveEntries);
        Assert.False(panel.RemoveEntryCommand.CanExecute(null));
        Assert.Empty(session.Removals);
    }

    [Fact]
    public async Task AStaleListRemovalRefreshesWithoutRetryingTheMutation()
    {
        var session = new RedisPanelFixtures.StubSession(null, type: "list")
        {
            RemovalOutcome = RedisEntryRemovalOutcome.Stale,
        };
        using var panel = NewPanel(session, new RedisPanelFixtures.StoppedClock(DateTimeOffset.UnixEpoch));
        await panel.Initialization;
        panel.SelectedKey = panel.Keys[0];
        panel.SelectedValueEntry = panel.ValueEntries[0];
        var readsBeforeRemoval = session.ReadCount;

        panel.RemoveEntryCommand.Execute(null);

        Assert.Single(session.Removals);
        Assert.Equal(readsBeforeRemoval + 1, session.ReadCount);
        Assert.Null(panel.SelectedValueEntry);
        Assert.Contains("changed after it was displayed", panel.ErrorMessage, StringComparison.Ordinal);
    }


    /// <summary>
    /// Two forms, because they are two acts: one rewrites what is already in
    /// the key, the other puts something new in it. Before this there was one
    /// form that could only add, so an entry could never be corrected.
    /// </summary>
    [Fact]
    public async Task EditingAnEntryRewritesItRatherThanAddingAnother()
    {
        var session = new RedisPanelFixtures.StubSession(null, type: "hash");
        using var panel = NewPanel(session, new RedisPanelFixtures.StoppedClock(DateTimeOffset.UnixEpoch));
        await panel.Initialization;
        panel.SelectedKey = panel.Keys[0];
        panel.SelectedValueEntry = panel.ValueEntries[0];

        // The form opens on what the entry holds.
        Assert.Equal("value", panel.EditValue);

        panel.EditValue = "corrected";
        panel.SaveEntryCommand.Execute(null);

        Assert.Equal("hash email=corrected", Assert.Single(session.Writes));
    }

    [Fact]
    public async Task AListElementIsRewrittenWhereItStands()
    {
        var session = new RedisPanelFixtures.StubSession(null, type: "list");
        using var panel = NewPanel(session, new RedisPanelFixtures.StoppedClock(DateTimeOffset.UnixEpoch));
        await panel.Initialization;
        panel.SelectedKey = panel.Keys[0];
        panel.SelectedValueEntry = panel.ValueEntries[0];
        panel.EditValue = "corrected";
        panel.SaveEntryCommand.Execute(null);

        // Not an append: the element at that position is the one written.
        Assert.Equal("list[0]=corrected", Assert.Single(session.Writes));
    }

    [Fact]
    public async Task AStringIsEditedRatherThanAddedTo()
    {
        var session = new RedisPanelFixtures.StubSession(null);
        using var panel = NewPanel(session, new RedisPanelFixtures.StoppedClock(DateTimeOffset.UnixEpoch));
        await panel.Initialization;
        panel.SelectedKey = panel.Keys[0];

        Assert.False(panel.MutationForm.HasAddForm);
        Assert.Equal("Value", panel.MutationForm.EntryFormHeading);
        // A whole value needs nothing selected to be editable.
        Assert.True(panel.SaveEntryCommand.CanExecute(null));
    }

    [Fact]
    public async Task AStreamEntryCanBeRemovedButNotRewritten()
    {
        var session = new RedisPanelFixtures.StubSession(null, type: "stream");
        using var panel = NewPanel(session, new RedisPanelFixtures.StoppedClock(DateTimeOffset.UnixEpoch));
        await panel.Initialization;
        panel.SelectedKey = panel.Keys[0];
        panel.SelectedValueEntry = panel.ValueEntries[0];

        Assert.False(panel.MutationForm.CanEditEntry);
        Assert.True(panel.MutationForm.HasEntryForm);
        Assert.False(panel.SaveEntryCommand.CanExecute(null));
        Assert.True(panel.RemoveEntryCommand.CanExecute(null));
    }

    private static RedisRuntimePanelViewModel NewPanel(RedisPanelFixtures.StubSession session, TimeProvider clock) =>
        RedisPanelFixtures.Panel(session, clock);

    private static RedisKeySummary Summary(string name, TimeSpan? ttl) =>
        new(new RedisKeyReference(name, System.Text.Encoding.UTF8.GetBytes(name)), "string", ttl, 40);

}
