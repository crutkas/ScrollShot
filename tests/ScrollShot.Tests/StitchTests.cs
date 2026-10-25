using ScrollShot;
using Xunit;
using static ScrollShot.Program;

namespace ScrollShot.Tests;

// Synthetic-frame tests for the pure stitching logic in Stitch.cs.
// Approach inspired by TinyClips' PanoramaStitcherTests (MIT, https://github.com/jamesmontemagno/tiny-clips).
public class StitchTests
{
    const int White = unchecked((int)0xFFFFFFFF);
    static int G(int gray) => unchecked((int)0xFF000000) | (gray << 16) | (gray << 8) | gray;

    // A tall "document": rows of random gray pixels (every row distinct).
    static int[][] Noise(int rows, int w, int seed, double ink = 1.0)
    {
        var r = new Random(seed);
        var doc = new int[rows][];
        for (int y = 0; y < rows; y++)
        {
            doc[y] = new int[w];
            for (int x = 0; x < w; x++) doc[y][x] = r.NextDouble() < ink ? G(r.Next(256)) : White;
        }
        return doc;
    }

    // Perfectly periodic content (identical list rows / table stripes) with the given period.
    static int[][] Periodic(int rows, int w, int period, int seed)
    {
        var tile = Noise(period, w, seed);
        return Enumerable.Range(0, rows).Select(y => tile[y % period]).ToArray();
    }

    // The visible viewport: optional fixed header/footer around a pane showing doc[offset...].
    static Frame View(int[][] doc, int offset, int paneH, int[][]? header = null, int[][]? footer = null, int[][]? sidebar = null)
    {
        var rows = new List<int[]>();
        if (header != null) rows.AddRange(header);
        for (int y = 0; y < paneH; y++)
        {
            var row = (int[])doc[offset + y].Clone();
            if (sidebar != null) Array.Copy(sidebar[y], row, sidebar[y].Length);
            rows.Add(row);
        }
        if (footer != null) rows.AddRange(footer);
        int w = rows[0].Length;
        return new Frame { W = w, H = rows.Count, Px = rows.SelectMany(r => r).ToArray() };
    }

    [Theory]
    [InlineData(37)]
    [InlineData(80)]
    [InlineData(150)]
    public void FindShift_KnownShift(int dy)
    {
        var doc = Noise(1000, 120, 1);
        var prev = View(doc, 100, 200);
        var cur = View(doc, 100 + dy, 200);
        Assert.Equal(dy, FindShift(prev, cur, 0, 199, 0, 119, null, out double score));
        Assert.True(score >= 0.9, $"score {score}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FindShift_SmallSteps(int dy)
    {
        var doc = Noise(600, 120, 2);
        Assert.Equal(dy, FindShift(View(doc, 50, 200), View(doc, 50 + dy, 200), 0, 199, 0, 119, null, out _));
    }

    // On repeating content every shift that is a multiple of the period away also matches;
    // the true (smallest) one must win so rows aren't duplicated.
    [Theory]
    [InlineData(48, 5)]
    [InlineData(48, 17)]
    [InlineData(48, 30)]
    [InlineData(48, 47)]
    [InlineData(32, 20)]
    [InlineData(64, 40)]
    public void FindShift_PeriodicContent_PicksSmallestShift(int period, int dy)
    {
        var doc = Periodic(800, 128, period, 3);
        var prev = View(doc, 0, 240);
        var cur = View(doc, dy, 240);
        Assert.Equal(dy, FindShift(prev, cur, 0, 239, 0, 127, null, out _));
    }

    // List rows that share structure (separators, stripes) but have distinct text: the smaller
    // aliases don't hold up against real pixels, so the true shift wins.
    [Fact]
    public void FindShift_ListRows_RejectsSmallerAliases()
    {
        const int w = 128, item = 40, dy = 90;
        var text = Noise(1000, w, 4, ink: 0.3);
        var doc = new int[1000][];
        for (int y = 0; y < doc.Length; y++)
        {
            int k = y % item;
            doc[y] = k == 0 ? Enumerable.Repeat(G(60), w).ToArray()                    // separator
                : k is >= 10 and < 22 ? text[y]                                          // per-item text
                : Enumerable.Repeat((y / item) % 2 == 0 ? White : G(235), w).ToArray(); // stripes
        }
        Assert.Equal(dy, FindShift(View(doc, 0, 240), View(doc, dy, 240), 0, 239, 0, w - 1, null, out _));
    }

    // With a scroll-position prediction, the credible shift closest to it wins.
    [Fact]
    public void FindShift_PredictedWindow_PrefersExpected()
    {
        var doc = Periodic(800, 128, 10, 5);
        int dy = 37;
        Assert.Equal(dy, FindShift(View(doc, 0, 240), View(doc, dy, 240), 0, 239, 0, 127, dy, out _, dy - 12, dy + 12));
    }

    [Fact]
    public void FindShift_NoCredibleAlignment()
    {
        var prev = View(Noise(200, 120, 6), 0, 200);
        var cur = View(Noise(200, 120, 7), 0, 200);
        Assert.Equal(-1, FindShift(prev, cur, 0, 199, 0, 119, null, out double score));
        Assert.True(score < 0.6, $"score {score}");
    }

    [Fact]
    public void Same_IgnoresOverlayScrollbarStrip()
    {
        var a = View(Noise(200, 200, 8), 0, 200);
        var b = new Frame { W = a.W, H = a.H, Px = (int[])a.Px.Clone() };
        for (int y = 0; y < 200; y++)
            for (int x = 190; x < 200; x++) b.Px[y * b.W + x] = G(10); // 2000 changed pixels, 10 px wide
        Assert.True(Same(a, b));
    }

    [Fact]
    public void Same_ToleratesCaretBlink()
    {
        var a = View(Noise(200, 200, 9), 0, 200);
        var b = new Frame { W = a.W, H = a.H, Px = (int[])a.Px.Clone() };
        for (int y = 50; y < 70; y++) b.Px[y * b.W + 20] ^= 0x00FFFFFF;
        Assert.True(Same(a, b));
    }

    [Fact]
    public void Same_DetectsWideChanges()
    {
        var a = View(Noise(200, 200, 10), 0, 200);
        var b = new Frame { W = a.W, H = a.H, Px = (int[])a.Px.Clone() };
        for (int y = 0; y < 200; y += 2)
            for (int x = 0; x < 200; x += 10) b.Px[y * b.W + x] ^= 0x00FFFFFF;
        Assert.False(Same(a, b));
    }

    // Simulates a capture loop the way Program.CaptureToFile drives it.
    static (Image img, bool[] scrollCols) Stitch(Frame[] frames)
    {
        Assert.True(DiffBounds(frames[0], frames[1], out int top, out int bottom, out int left, out int right));
        int W = frames[0].W;
        var scroll = new int[W]; var stat = new int[W];
        var strips = new List<int[]>();
        for (int n = 1; n < frames.Length; n++)
        {
            var (prev, cur) = (frames[n - 1], frames[n]);
            int dy = FindShift(prev, cur, top, bottom, left, right, null, out _);
            Assert.True(dy > 0, $"frame {n}: no shift");
            VoteColumns(prev, cur, top, bottom, dy, scroll, stat);
            for (int y = bottom - dy + 1; y <= bottom; y++) strips.Add(cur.Row(y).ToArray());
        }
        var cols = Enumerable.Range(0, W).Select(x => scroll[x] >= stat[x]).ToArray();
        return (Compose(frames[0], frames[^1], strips, top, bottom, cols, true), cols);
    }

    [Fact]
    public void VoteColumnsAndCompose_FillStaticSidebarWithBackground()
    {
        const int w = 160, pane = 200, side = 40, step = 50;
        var doc = Noise(1000, w, 11, ink: 0.3);
        var sidebar = Noise(pane, side, 12);
        var frames = new[] { 0, step, 2 * step }.Select(o => View(doc, o, pane, sidebar: sidebar)).ToArray();

        var (img, cols) = Stitch(frames);

        Assert.All(cols.Take(side), c => Assert.False(c));
        Assert.All(cols.Skip(side), c => Assert.True(c));
        Assert.Equal(pane + 2 * step, img.H);
        for (int y = 0; y < img.H; y++)
            for (int x = 0; x < w; x++)
            {
                int expected = y < pane ? frames[0].Px[y * w + x] : x < side ? White : doc[y][x];
                Assert.True(expected == img.Px[y * w + x], $"pixel ({x},{y})");
            }
    }

    [Fact]
    public void Compose_KeepsFixedHeaderAndFooterOnce()
    {
        const int w = 120, pane = 180, step = 60;
        var doc = Noise(1000, w, 13);
        var header = Noise(30, w, 14);
        var footer = Noise(30, w, 15);
        var frames = new[] { 0, step, 2 * step }.Select(o => View(doc, o, pane, header, footer)).ToArray();

        var (img, _) = Stitch(frames);

        int body = pane + 2 * step;
        Assert.Equal(header.Length + body + footer.Length, img.H);
        var rows = Enumerable.Range(0, img.H).Select(y => img.Px.AsSpan(y * w, w).ToArray()).ToArray();
        for (int y = 0; y < header.Length; y++) Assert.Equal(header[y], rows[y]);
        for (int y = 0; y < body; y++) Assert.Equal(doc[y], rows[header.Length + y]);
        for (int y = 0; y < footer.Length; y++) Assert.Equal(footer[y], rows[header.Length + body + y]);
    }
}
