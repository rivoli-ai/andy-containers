using Andy.Containers.Api.Services;
using FluentAssertions;
using Xunit;

namespace Andy.Containers.Api.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ResizeDebouncer"/> (conductor #863).
/// Cover the contract that animation-frame storms produce one
/// forward, that no-change inputs are skipped, and that the timer
/// resets correctly when new observations arrive within the quiet
/// period.
/// </summary>
public class ResizeDebouncerTests
{
    private record Forwarded(int Cols, int Rows);

    private (ResizeDebouncer debouncer, List<Forwarded> log) CreateDebouncer(
        int initialCols = 80,
        int initialRows = 24,
        TimeSpan? quietPeriod = null)
    {
        var log = new List<Forwarded>();
        var debouncer = new ResizeDebouncer(
            providerCommand: "docker",
            externalId: "ext-test",
            containerUser: "test-user",
            tmuxSession: "web",
            initialCols: initialCols,
            initialRows: initialRows,
            quietPeriod: quietPeriod ?? TimeSpan.FromMilliseconds(50),
            forward: (c, r) =>
            {
                lock (log) { log.Add(new Forwarded(c, r)); }
            },
            ct: CancellationToken.None);
        return (debouncer, log);
    }

    // MARK: - Single observation

    [Fact]
    public async Task SingleObservation_ForwardsAfterQuietPeriod()
    {
        var (d, log) = CreateDebouncer(initialCols: 80, initialRows: 24);
        d.Observe(120, 40);

        await Task.Delay(150);

        log.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new Forwarded(120, 40));
        d.ForwardCount.Should().Be(1);
    }

    [Fact]
    public async Task SingleObservation_MatchingInitial_DoesNotForward()
    {
        // The initial size is the value that was used to create
        // the tmux session. An incoming resize at the same size is
        // a no-op — no need to spawn a tmux exec.
        var (d, log) = CreateDebouncer(initialCols: 120, initialRows: 40);
        d.Observe(120, 40);

        await Task.Delay(150);

        log.Should().BeEmpty(
            "the size matches the initial value the session was created with");
        d.ForwardCount.Should().Be(0);
    }

    // MARK: - Animation storm

    [Fact]
    public async Task AnimationStorm_ProducesSingleForwardAtEnd()
    {
        // Simulates an inspector-collapse animation: 12 events at
        // intermediate widths, the final value is 200x40.
        var (d, log) = CreateDebouncer(initialCols: 100, initialRows: 30);

        for (int width = 100; width <= 200; width += 10)
        {
            d.Observe(width, 30);
            await Task.Delay(20); // intra-animation interval
        }

        // Settle past the quiet period.
        await Task.Delay(150);

        log.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new Forwarded(200, 30),
                "only the final stable value reaches tmux");
        d.ForwardCount.Should().Be(1);
    }

    [Fact]
    public async Task AnimationStorm_QuietPeriodResetsOnEachObservation()
    {
        // Rapid-fire observations within the quiet period should
        // postpone the forward. If we only waited for the FIRST
        // observation's quiet period the forward would fire mid-storm.
        var quietPeriod = TimeSpan.FromMilliseconds(100);
        var (d, log) = CreateDebouncer(quietPeriod: quietPeriod);

        // Six observations 50 ms apart — each one should reset the
        // timer, so total elapsed at the last one is 300 ms but
        // the forward should not have fired yet because the quiet
        // period (100 ms) hasn't elapsed since the last observation.
        for (int i = 0; i < 6; i++)
        {
            d.Observe(100 + i, 30);
            await Task.Delay(50);
        }

        log.Should().BeEmpty(
            "the timer should still be pending — last observation was 50 ms ago, quiet period is 100 ms");

        await Task.Delay(150);

        log.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new Forwarded(105, 30));
    }

    // MARK: - Distinct settle points

    [Fact]
    public async Task TwoSettlePoints_EachProduceAForward()
    {
        var (d, log) = CreateDebouncer();

        d.Observe(120, 40);
        await Task.Delay(150);
        d.Observe(160, 40);
        await Task.Delay(150);

        log.Should().HaveCount(2);
        log[0].Should().BeEquivalentTo(new Forwarded(120, 40));
        log[1].Should().BeEquivalentTo(new Forwarded(160, 40));
        d.ForwardCount.Should().Be(2);
    }

    [Fact]
    public async Task SecondSettleAtSameSize_DoesNotForwardAgain()
    {
        var (d, log) = CreateDebouncer();

        d.Observe(120, 40);
        await Task.Delay(150);
        // Same value again — should be skipped.
        d.Observe(120, 40);
        await Task.Delay(150);

        log.Should().ContainSingle();
        d.ForwardCount.Should().Be(1);
    }

    // MARK: - Validation

    [Theory]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(-1, 24)]
    [InlineData(80, -1)]
    public async Task NonPositiveValues_AreIgnored(int cols, int rows)
    {
        var (d, log) = CreateDebouncer(initialCols: 80, initialRows: 24);
        d.Observe(cols, rows);

        await Task.Delay(150);

        log.Should().BeEmpty();
        d.ForwardCount.Should().Be(0);
    }

    // MARK: - Cancellation

    [Fact]
    public async Task ObserveAfterTokenCancellation_DoesNotFire()
    {
        var cts = new CancellationTokenSource();
        var log = new List<Forwarded>();
        var d = new ResizeDebouncer(
            providerCommand: "docker",
            externalId: "ext-test",
            containerUser: "test-user",
            tmuxSession: "web",
            initialCols: 80,
            initialRows: 24,
            quietPeriod: TimeSpan.FromMilliseconds(50),
            forward: (c, r) =>
            {
                lock (log) { log.Add(new Forwarded(c, r)); }
            },
            ct: cts.Token);

        d.Observe(120, 40);
        cts.Cancel();
        await Task.Delay(150);

        log.Should().BeEmpty(
            "cancelling the token should suppress any pending forward");
    }

    // MARK: - FlushForTesting

    [Fact]
    public void FlushForTesting_ForwardsCurrentPending()
    {
        var (d, log) = CreateDebouncer();
        d.Observe(180, 50);
        d.FlushForTesting();

        log.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new Forwarded(180, 50));
    }

    [Fact]
    public void FlushForTesting_SkipsWhenAlreadyAtThatSize()
    {
        var (d, log) = CreateDebouncer(initialCols: 100, initialRows: 30);
        d.Observe(100, 30); // matches initial — already-at-size
        d.FlushForTesting();

        log.Should().BeEmpty();
    }
}
