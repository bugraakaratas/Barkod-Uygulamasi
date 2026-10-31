using KumasBarkod.Core.Scanning;

namespace KumasBarkod.Tests.Scanning;

public class ScanBurstDetectorTests
{
    private readonly ScanBurstDetector _detector = new(maxGapMs: 60, minLength: 8);

    [Fact]
    public void Default_threshold_tolerates_slow_scanners()
    {
        var detector = new ScanBurstDetector();
        Assert.Equal(100, detector.MaxGapMs);

        var t = 0;
        foreach (var ch in "K1000123") detector.OnText(ch.ToString(), t += 95);
        Assert.Equal("K1-000123", detector.OnEnter(t + 95));
    }

    /// <summary>Metni karakter karakter, aralarında <paramref name="gapMs"/> ms ile "yaz"; sonra Enter.</summary>
    private string? Type(string text, int gapMs, int start = 1000, int enterGap = -1)
    {
        var t = start;
        foreach (var ch in text)
        {
            _detector.OnText(ch.ToString(), t);
            t += gapMs;
        }
        return _detector.OnEnter(t - gapMs + (enterGap < 0 ? gapMs : enterGap));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(60)]
    public void Fast_valid_barcode_is_a_scan(int gap)
    {
        Assert.Equal("K1-000123", Type("K1000123", gap));
    }

    [Theory]
    [InlineData(61)]
    [InlineData(150)]
    public void Human_typing_speed_is_not_a_scan(int gap)
    {
        Assert.Null(Type("K1000123", gap));
    }

    [Fact]
    public void Fast_but_not_an_id_is_not_a_scan()
    {
        Assert.Null(Type("LOT-ABCDE", 5));
    }

    [Fact]
    public void Short_fast_input_is_not_a_scan()
    {
        Assert.Null(Type("K100012", 5));
    }

    [Fact]
    public void Late_enter_is_not_a_scan()
    {
        Assert.Null(Type("K1000123", 5, enterGap: 500));
    }

    [Fact]
    public void Slow_typing_before_scan_is_ignored()
    {
        // İnsan "LOT" yazıp durur, sonra okuyucu barkodu gönderir
        _detector.OnText("L", 0);
        _detector.OnText("O", 200);
        _detector.OnText("T", 400);

        Assert.True(_detector.StartsNewBurst(2000));
        Assert.Equal("K1-000123", Type("K1000123", 5, start: 2000));
    }

    [Fact]
    public void Buffer_is_cleared_after_enter()
    {
        Assert.Equal("K1-000001", Type("K1000001", 5, start: 0));
        Assert.Equal("K1-000002", Type("K1000002", 5, start: 30)); // hemen ardından ikinci okutma
    }

    [Fact]
    public void Starts_new_burst_reports_first_character()
    {
        Assert.True(_detector.StartsNewBurst(0));
        _detector.OnText("K", 0);
        Assert.False(_detector.StartsNewBurst(10));
        Assert.True(_detector.StartsNewBurst(100));
    }

    [Fact]
    public void Timestamp_wraparound_is_handled()
    {
        // Environment.TickCount taşması: int.MaxValue → int.MinValue
        Assert.Equal("K1-000123", Type("K1000123", 5, start: int.MaxValue - 20));
    }
}
