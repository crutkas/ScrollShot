namespace ScrollShot;

// Pure stitching logic (no Win32): frame comparison, shift estimation and composition.
// Kept free of platform dependencies so tests/ScrollShot.Tests can compile it directly.
static partial class Program
{
    internal sealed class Frame
    {
        public int W, H, OX, OY;
        public int[] Px = [];
        public ReadOnlySpan<int> Row(int y) => Px.AsSpan(y * W, W);
    }

    internal sealed class Image { public int W, H; public int[] Px = []; }

    // Tolerates a few changed pixels (blinking caret, hover effects) and changes confined to a
    // narrow vertical strip (overlay scrollbars that fade in/out after each scroll).
    internal static bool Same(Frame a, Frame b)
    {
        if (a.W != b.W || a.H != b.H) return false;
        int diff = 0, minX = int.MaxValue, maxX = -1;
        for (int i = 0; i < a.Px.Length; i++)
        {
            if (a.Px[i] == b.Px[i]) continue;
            diff++;
            int x = i % a.W;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (diff > 150 && maxX - minX > 24) return false;
        }
        return true;
    }

    internal static bool DiffBounds(Frame a, Frame b, out int top, out int bottom, out int left, out int right)
    {
        top = int.MaxValue; bottom = -1; left = int.MaxValue; right = -1;
        for (int y = 0; y < a.H; y++)
        {
            var ra = a.Row(y); var rb = b.Row(y);
            if (ra.SequenceEqual(rb)) continue;
            top = Math.Min(top, y); bottom = y;
            for (int x = 0; x < a.W; x++)
                if (ra[x] != rb[x]) { left = Math.Min(left, x); right = Math.Max(right, x); }
        }
        return bottom >= 0;
    }

    // Per column: does it move with the content (scrolling pane) or stay put (sidebar, sticky panel)?
    internal static void VoteColumns(Frame prev, Frame cur, int top, int bottom, int dy, int[] scroll, int[] stat)
    {
        int W = cur.W;
        for (int y = top; y + dy <= bottom; y++)
        {
            int c = y * W, s = (y + dy) * W;
            for (int x = 0; x < W; x++)
            {
                int p = cur.Px[c + x];
                bool sh = Close(p, prev.Px[s + x]), st = Close(p, prev.Px[c + x]);
                if (sh && !st) scroll[x]++;
                else if (st && !sh) stat[x]++;
            }
        }
    }

    internal static int Gray(int p) => (((p >> 16) & 0xFF) * 77 + ((p >> 8) & 0xFF) * 150 + (p & 0xFF) * 29) >> 8;
    static bool Close(int a, int b) => a == b || Math.Abs(Gray(a) - Gray(b)) <= 24;

    // Per-row signature: grayscale averaged into bins. Tolerant of sub-pixel scroll offsets
    // (WinUI/XAML scroll by fractional pixels, so anti-aliasing never matches exactly).
    static float[] RowSig(ReadOnlySpan<int> row, int bins, out bool informative)
    {
        var s = new float[bins];
        int n = row.Length, lo = 255, hi = 0;
        for (int x = 0; x < n; x++)
        {
            int g = Gray(row[x]);
            s[x * bins / n] += g;
            if (g < lo) lo = g; if (g > hi) hi = g;
        }
        for (int k = 0; k < bins; k++) { int w = (k + 1) * n / bins - k * n / bins; if (w > 0) s[k] /= w; }
        informative = hi - lo > 24; // flat rows match anything, so they aren't evidence
        return s;
    }

    // Finds dy such that cur row (top+i) ~= prev row (top+i+dy) within the scrolling region.
    // `expected` (from the scroll position) breaks ties between credible shifts; without it the
    // smallest credible shift wins.
    //
    // Periodic content (list rows, table stripes) has several near-equal minima, and the global one
    // is often an alias that would duplicate rows. Following the approach of TinyClips by James
    // Montemagno (MIT, https://github.com/jamesmontemagno/tiny-clips, PanoramaAccumulator.EstimateVerticalShift),
    // we take local minima within a loose band of the best signature error, confirm each against
    // real pixels, and prefer the smallest shift that the pixels support.
    internal static int FindShift(Frame prev, Frame cur, int top, int bottom, int left, int right, int? expected, out double score, int minDy = 1, int maxDy = int.MaxValue)
    {
        int h = bottom - top + 1, bins = Math.Clamp((right - left + 1) / 4, 8, 64);
        int edge = h / 20; // ignore thin bands at the region edges (sticky headers/footers inside the pane)
        var a = new float[h][]; var b = new float[h][]; var info = new bool[h];
        for (int i = 0; i < h; i++)
        {
            a[i] = RowSig(prev.Row(top + i)[left..(right + 1)], bins, out _);
            b[i] = RowSig(cur.Row(top + i)[left..(right + 1)], bins, out info[i]);
        }

        var err = new double[h + 1];
        Array.Fill(err, double.MaxValue);
        double best = double.MaxValue;
        var valid = new List<double>();
        for (int dy = Math.Max(1, minDy); dy < h; dy++)
        {
            if (dy > maxDy || h - dy < h / 5) break; // need real overlap; tiny overlaps match by accident
            double sum = 0; int cnt = 0;
            for (int i = edge; i + dy < h - edge; i++)
            {
                if (!info[i]) continue;
                var x = b[i]; var y = a[i + dy];
                double d = 0;
                for (int k = 0; k < bins; k++) d += Math.Abs(x[k] - y[k]);
                sum += d; cnt++;
            }
            if (cnt < 8) continue;
            err[dy] = sum / (cnt * bins);
            valid.Add(err[dy]);
            best = Math.Min(best, err[dy]);
        }
        score = 0;
        if (valid.Count == 0) return -1;
        valid.Sort();
        double median = valid[valid.Count / 2];
        score = median > 0 ? 1 - best / median : (best == 0 ? 1 : 0);
        if (best > 6 || score < 0.6) return -1;

        // Candidates: local minima inside the acceptance band. Check the smallest shifts (where the
        // true one sits on periodic content) and the lowest-error ones (the true one elsewhere).
        double band = Math.Max(best * 1.6, best + 0.5);
        var minima = new List<int>();
        for (int dy = 1; dy < h; dy++)
            if (err[dy] <= band && err[dy] <= err[dy - 1] && err[dy] <= err[dy + 1]) minima.Add(dy);
        const int Attempts = 6;
        var cands = minima.Take(Attempts).Union(minima.OrderBy(d => err[d]).Take(Attempts)).ToList();

        // Credible = real pixels agree about as well as the best candidate's do. A relative tolerance
        // keeps sub-pixel (never exact) scrolling working while rejecting near-miss aliases.
        var pix = cands.Select(d => PixelError(prev, cur, top, bottom, left, right, d, edge, info)).ToList();
        double tol = pix.Min() * 1.2 + 0.5;
        int pick = -1;
        for (int k = 0; k < cands.Count; k++)
        {
            if (pix[k] > tol) continue;
            int d = cands[k];
            if (pick < 0) { pick = d; continue; }
            bool better = expected is int e
                ? Math.Abs(d - e) < Math.Abs(pick - e) || (Math.Abs(d - e) == Math.Abs(pick - e) && d < pick)
                : d < pick;
            if (better) pick = d;
        }
        return pick;
    }

    // Mean |Δluma| between cur row i and prev row i+dy, sampled over informative rows.
    static double PixelError(Frame prev, Frame cur, int top, int bottom, int left, int right, int dy, int edge, bool[] info)
    {
        int h = bottom - top + 1, w = right - left + 1;
        int rowStep = Math.Max(1, (h - dy) / 200), colStep = Math.Max(1, w / 256);
        long sum = 0, n = 0;
        for (int i = edge; i + dy < h - edge; i += rowStep)
        {
            if (!info[i]) continue;
            var rc = cur.Row(top + i); var rp = prev.Row(top + i + dy);
            for (int x = left; x <= right; x += colStep) { sum += Math.Abs(Gray(rc[x]) - Gray(rp[x])); n++; }
        }
        return n > 0 ? (double)sum / n : double.MaxValue;
    }

    internal static Image Compose(Frame first, Frame last, List<int[]> strips, int top, int bottom, bool[] scrollCols, bool scrolled)
    {
        int W = first.W;
        if (!scrolled) { top = 0; bottom = first.H - 1; }
        int height = (bottom + 1) + strips.Count + (last.H - 1 - bottom);
        var img = new Image { W = W, H = height, Px = new int[W * height] };
        int y = 0;
        void Put(ReadOnlySpan<int> row) { row.CopyTo(img.Px.AsSpan(y * W, W)); y++; }

        for (int r = 0; r <= bottom; r++) Put(first.Row(r));

        // Columns that don't scroll (e.g. a fixed sidebar) are extended with the
        // background color instead of repeating slices of the static content.
        var counts = new Dictionary<int, int>();
        for (int r = top; r <= bottom; r++)
            foreach (int p in first.Row(r)) counts[p] = counts.GetValueOrDefault(p) + 1;
        int bg = counts.Count > 0 ? counts.MaxBy(kv => kv.Value).Key : unchecked((int)0xFFFFFFFF);
        var tmp = new int[W];
        foreach (var s in strips)
        {
            for (int x = 0; x < W; x++) tmp[x] = scrollCols[x] ? s[x] : bg;
            Put(tmp);
        }
        for (int r = bottom + 1; r < last.H; r++) Put(last.Row(r));
        return img;
    }
}
