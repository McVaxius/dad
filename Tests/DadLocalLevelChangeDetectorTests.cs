using dad.Services;
using Xunit;

namespace dad.Tests;

// B5: the level-up trigger must never fire on the first capture (login) and must fire exactly once per
// distinct (active job, level) transition, coalescing repeated identical captures.
public sealed class DadLocalLevelChangeDetectorTests
{
    [Fact]
    public void FirstCaptureNeverFires()
    {
        var detector = new DadLocalLevelChangeDetector();
        Assert.False(detector.Register(21, 80));
    }

    [Fact]
    public void SameJobAndLevelDoesNotFire()
    {
        var detector = new DadLocalLevelChangeDetector();
        detector.Register(21, 80);
        Assert.False(detector.Register(21, 80));
        Assert.False(detector.Register(21, 80));
    }

    [Fact]
    public void LevelChangeFiresOnce()
    {
        var detector = new DadLocalLevelChangeDetector();
        detector.Register(21, 80);
        Assert.True(detector.Register(21, 81));
        Assert.False(detector.Register(21, 81));
    }

    [Fact]
    public void JobChangeFires()
    {
        var detector = new DadLocalLevelChangeDetector();
        detector.Register(21, 80);
        Assert.True(detector.Register(19, 73));
    }

    [Fact]
    public void ResetRequiresAFreshFirstCapture()
    {
        var detector = new DadLocalLevelChangeDetector();
        detector.Register(21, 80);
        detector.Reset();
        Assert.False(detector.Register(19, 73));
        Assert.True(detector.Register(19, 74));
    }
}
