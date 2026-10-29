namespace WinfoMain.Desktop.SceneEngine;

using System.Drawing;
using WinfoMain.Common;
using WinfoMain.Desktop.UIA;

/// <summary>
/// Matches UIA TextPattern probe words with OCR words to get pixel-accurate positions.
/// Handles three cases:
///   1. Perfect match — identical text, OCR bounds
///   2. Corrected — Probe text (correct), OCR bounds (accurate position)
///   3. ProbeOnly — OCR missed the word entirely, bounds interpolated
/// </summary>
public static class ProbeOcrMatcher
{
    public enum MatchKind { Perfect, Corrected, ProbeOnly }

    public sealed class MatchedWord
    {
        /// <summary>Authoritative text (always from Probe).</summary>
        public string Text { get; init; } = "";

        /// <summary>Window-local pixel bounds.</summary>
        public Rectangle Bounds { get; init; }

        /// <summary>How this word was matched.</summary>
        public MatchKind Kind { get; init; }

        /// <summary>OCR text if different from Probe (diagnostic, Case 2 only).</summary>
        public string? OcrText { get; init; }

        /// <summary>Index of the ProbeLine this word came from.</summary>
        public int ProbeLineIndex { get; init; }
    }

    public sealed class MatchResult
    {
        public List<MatchedWord> Words { get; init; } = [];
        public int ProbeWordCount { get; init; }
        public int OcrWordCount { get; init; }
        public int PerfectMatches { get; init; }
        public int CorrectedMatches { get; init; }
        public int ProbeOnlyWords { get; init; }
    }

    // Internal: a probe word with its source line index
    private readonly record struct ProbeWord(string Text, int LineIndex);

    /// <summary>
    /// Match probe words against OCR words.
    /// </summary>
    /// <param name="probeResults">From UiaProbe.ProbeAsync().</param>
    /// <param name="ocrContent">From DesktopWindow.ContentOcr (window-local bounds).</param>
    /// <param name="windowBounds">Window bounds for screen→local conversion of probe coords.</param>
    public static MatchResult Match(
        List<UiaProbe.ProbeResult> probeResults,
        WinContent ocrContent,
        Rectangle windowBounds)
    {
        // Step 1: Flatten both sources
        var probeWords = FlattenProbe(probeResults);

        // Collect the screen-absolute bounds of all multi-line probe elements,
        // convert to window-local, and filter OCR words to that region.
        // This prevents matching document text against title bar / status bar OCR words.
        var probeRegion = ComputeProbeRegion(probeResults, windowBounds);
        var ocrWords = FilterOcrWords(ocrContent.Words, probeRegion);

        // Step 2: Two-pointer alignment
        var matched = new List<MatchedWord>();
        int pi = 0; // probe index
        int oi = 0; // ocr index
        int perfect = 0, corrected = 0, probeOnly = 0;

        while (pi < probeWords.Count)
        {
            if (oi >= ocrWords.Count)
            {
                // Remaining probe words have no OCR partner
                for (int i = pi; i < probeWords.Count; i++)
                {
                    matched.Add(new MatchedWord
                    {
                        Text = probeWords[i].Text,
                        Bounds = Rectangle.Empty,
                        Kind = MatchKind.ProbeOnly,
                        ProbeLineIndex = probeWords[i].LineIndex,
                    });
                    probeOnly++;
                }
                pi = probeWords.Count;
                break;
            }

            var pw = probeWords[pi];
            var ow = ocrWords[oi];


            // Case 1: Exact match
            if (string.Equals(pw.Text, ow.Text, StringComparison.Ordinal))
            {
                matched.Add(new MatchedWord
                {
                    Text = pw.Text,
                    Bounds = ow.Bounds,
                    Kind = MatchKind.Perfect,
                    ProbeLineIndex = pw.LineIndex,
                });
                perfect++;
                pi++;
                oi++;
                continue;
            }

            // Case 2: Fuzzy match (OCR misread)
            if (IsFuzzyMatch(pw.Text, ow.Text))
            {
                matched.Add(new MatchedWord
                {
                    Text = pw.Text,
                    Bounds = ow.Bounds,
                    Kind = MatchKind.Corrected,
                    OcrText = ow.Text,
                    ProbeLineIndex = pw.LineIndex,
                });
                corrected++;
                pi++;
                oi++;
                continue;
            }

            // Mismatch — look ahead to resynchronize

            // Fast path: if the probe word is an OCR-invisible punctuation token
            // (standalone dash, comma, dot, semicolon etc.), OCR simply never emits it.
            // Mark as ProbeOnly and advance probe only — oi stays put so the next
            // probe word gets a fair chance to match the current OCR word.
            if (IsOcrInvisibleToken(pw.Text))
            {
                matched.Add(new MatchedWord
                {
                    Text = pw.Text,
                    Bounds = Rectangle.Empty, // interpolated later
                    Kind = MatchKind.ProbeOnly,
                    ProbeLineIndex = pw.LineIndex,
                });
                probeOnly++;
                pi++;
                continue;
            }

            // Look ahead in OCR: maybe OCR has extra words (rare)
            int ocrAhead = FindAhead(pw.Text, ocrWords, oi + 1, maxLookahead: 5);
            if (ocrAhead >= 0)
            {
                // Skip OCR words that have no probe partner
                oi = ocrAhead;
                continue; // re-evaluate at new oi
            }

            // Look ahead in Probe: maybe OCR missed words (Case 3)
            int probeAhead = FindAheadProbe(ow.Text, probeWords, pi + 1, maxLookahead: 20);
            if (probeAhead >= 0)
            {
                // Probe words pi..probeAhead-1 are missing from OCR
                for (int i = pi; i < probeAhead; i++)
                {
                    matched.Add(new MatchedWord
                    {
                        Text = probeWords[i].Text,
                        Bounds = Rectangle.Empty, // interpolated later
                        Kind = MatchKind.ProbeOnly,
                        ProbeLineIndex = probeWords[i].LineIndex,
                    });
                    probeOnly++;
                }
                pi = probeAhead;
                continue; // re-evaluate — oi unchanged, should now match
            }

            // No resync found — emit as ProbeOnly, advance probe
            matched.Add(new MatchedWord
            {
                Text = pw.Text,
                Bounds = Rectangle.Empty,
                Kind = MatchKind.ProbeOnly,
                ProbeLineIndex = pw.LineIndex,
            });
            probeOnly++;
            pi++;
        }

        // Step 3: Interpolate bounds for ProbeOnly words
        InterpolateBounds(matched);

        return new MatchResult
        {
            Words = matched,
            ProbeWordCount = probeWords.Count,
            OcrWordCount = ocrWords.Count,
            PerfectMatches = perfect,
            CorrectedMatches = corrected,
            ProbeOnlyWords = probeOnly,
        };
    }

    // ── Flatten ──────────────────────────────────────────────────

    private static List<ProbeWord> FlattenProbe(List<UiaProbe.ProbeResult> probeResults)
    {
        var words = new List<ProbeWord>();
        int lineIndex = 0;

        foreach (var result in probeResults)
        {
            // Only match multi-line text blocks (Document, TermControl etc.)
            // Single-line TextBlocks (title bar, status bar) are already in the UIA tree
            // with correct bounds — no matching needed.
            if (result.Lines.Count <= 1) continue;

            foreach (var line in result.Lines)
            {
                if (string.IsNullOrEmpty(line.Text))
                {
                    lineIndex++;
                    continue;
                }

                var parts = line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                    words.Add(new ProbeWord(part, lineIndex));

                lineIndex++;
            }
        }

        return words;
    }

    /// <summary>
    /// Compute the window-local bounding region of all multi-line probe elements.
    /// Only OCR words inside this region will be used for matching.
    /// </summary>
    private static Rectangle ComputeProbeRegion(List<UiaProbe.ProbeResult> probeResults, Rectangle windowBounds)
    {
        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;
        bool any = false;

        foreach (var result in probeResults)
        {
            if (result.Lines.Count <= 1) continue;
            if (result.Bounds.IsEmpty) continue;

            // Convert screen-absolute to window-local
            int lx = result.Bounds.X - windowBounds.X;
            int ly = result.Bounds.Y - windowBounds.Y;
            int rx = lx + result.Bounds.Width;
            int ry = ly + result.Bounds.Height;

            if (lx < minX) minX = lx;
            if (ly < minY) minY = ly;
            if (rx > maxX) maxX = rx;
            if (ry > maxY) maxY = ry;
            any = true;
        }

        if (!any) return Rectangle.Empty;
        return new Rectangle(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Filter OCR words to only those inside (or overlapping with) the probe region,
    /// then sort strictly by Y (row) then X (column) for correct two-pointer alignment.
    /// </summary>
    private static WinWordList FilterOcrWords(WinWordList allWords, Rectangle region)
    {
        if (region.IsEmpty) return allWords;

        // Expand region slightly to catch edge words
        var expanded = Rectangle.Inflate(region, 10, 5);

        var filtered = new List<WinWordBase>();
        foreach (var w in allWords)
        {
            if (w.Bounds.IntersectsWith(expanded))
                filtered.Add(w);
        }

        // Sort by Y (with tolerance for same-line grouping) then X
        filtered.Sort((a, b) =>
        {
            int lineHeight = Math.Max(a.Bounds.Height, b.Bounds.Height);
            int yTolerance = Math.Max(lineHeight / 2, 5);
            int dy = a.Bounds.Y - b.Bounds.Y;
            if (Math.Abs(dy) <= yTolerance)
                return a.Bounds.X.CompareTo(b.Bounds.X);
            return dy;
        });

        var result = new WinWordList();
        result.AddRange(filtered);
        return result;
    }

    // ── Matching helpers ─────────────────────────────────────────

    /// <summary>
    /// Fuzzy match: two words are "the same" if they share ≥60% character similarity.
    /// Uses simple character-level comparison (Levenshtein distance).
    /// </summary>
    private static bool IsFuzzyMatch(string probe, string ocr)
    {
        if (probe.Length == 0 || ocr.Length == 0) return false;

        int maxLen = Math.Max(probe.Length, ocr.Length);
        int dist = LevenshteinDistance(probe, ocr);
        double similarity = 1.0 - (double)dist / maxLen;

        return similarity >= 0.6;
    }

    /// <summary>
    /// Returns true if the token is a standalone punctuation character that OCR
    /// typically does not emit as a separate word (e.g. "-", ",", ".", ";", ":").
    /// These can be safely skipped without triggering a lookahead resync.
    /// </summary>
    private static bool IsOcrInvisibleToken(string text)
    {
        if (text.Length == 0) return false;
        // All characters must be punctuation / symbol, and the token must be short
        if (text.Length > 3) return false;
        foreach (char c in text)
        {
            if (!char.IsPunctuation(c) && !char.IsSymbol(c))
                return false;
        }
        return true;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        int m = a.Length, n = b.Length;
        // Use single-row optimization
        var prev = new int[n + 1];
        var curr = new int[n + 1];

        for (int j = 0; j <= n; j++) prev[j] = j;

        for (int i = 1; i <= m; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= n; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[n];
    }

    /// <summary>Look ahead in OCR words for an exact or fuzzy match.</summary>
    private static int FindAhead(string probeText, WinWordList ocrWords, int startIdx, int maxLookahead)
    {
        int end = Math.Min(startIdx + maxLookahead, ocrWords.Count);
        for (int i = startIdx; i < end; i++)
        {
            if (string.Equals(probeText, ocrWords[i].Text, StringComparison.Ordinal))
                return i;
            if (IsFuzzyMatch(probeText, ocrWords[i].Text))
                return i;
        }
        return -1;
    }

    /// <summary>Look ahead in Probe words for an exact or fuzzy match.</summary>
    private static int FindAheadProbe(string ocrText, List<ProbeWord> probeWords, int startIdx, int maxLookahead)
    {
        int end = Math.Min(startIdx + maxLookahead, probeWords.Count);
        for (int i = startIdx; i < end; i++)
        {
            if (string.Equals(ocrText, probeWords[i].Text, StringComparison.Ordinal))
                return i;
            if (IsFuzzyMatch(ocrText, probeWords[i].Text))
                return i;
        }
        return -1;
    }

    // ── Bounds Interpolation ─────────────────────────────────────

    /// <summary>
    /// Fill in bounds for ProbeOnly words by interpolating from neighboring matched words.
    /// </summary>
    private static void InterpolateBounds(List<MatchedWord> words)
    {
        for (int i = 0; i < words.Count; i++)
        {
            if (words[i].Kind != MatchKind.ProbeOnly || !words[i].Bounds.IsEmpty)
                continue;

            // Find nearest matched word before and after
            var (leftIdx, rightIdx) = FindNeighbors(words, i);

            Rectangle bounds;
            if (leftIdx >= 0 && rightIdx >= 0)
            {
                bounds = InterpolateFromBoth(words, i, leftIdx, rightIdx);
            }
            else if (leftIdx >= 0)
            {
                bounds = InterpolateFromLeft(words, i, leftIdx);
            }
            else if (rightIdx >= 0)
            {
                bounds = InterpolateFromRight(words, i, rightIdx);
            }
            else
            {
                continue; // no neighbors at all
            }

            // Replace with interpolated bounds (MatchedWord is init-only, create new)
            words[i] = new MatchedWord
            {
                Text = words[i].Text,
                Bounds = bounds,
                Kind = words[i].Kind,
                OcrText = words[i].OcrText,
                ProbeLineIndex = words[i].ProbeLineIndex,
            };
        }
    }

    private static (int leftIdx, int rightIdx) FindNeighbors(List<MatchedWord> words, int idx)
    {
        int left = -1, right = -1;
        // Left neighbor: any word with valid bounds (including already-interpolated ProbeOnly)
        for (int i = idx - 1; i >= 0; i--)
        {
            if (!words[i].Bounds.IsEmpty)
            { left = i; break; }
        }
        // Right neighbor: only matched words (not yet interpolated ProbeOnly)
        for (int i = idx + 1; i < words.Count; i++)
        {
            if (words[i].Kind != MatchKind.ProbeOnly && !words[i].Bounds.IsEmpty)
            { right = i; break; }
        }
        return (left, right);
    }

    private static float EstimateCharWidth(MatchedWord w)
        => w.Text.Length > 0 ? (float)w.Bounds.Width / w.Text.Length : 8f;

    private static Rectangle InterpolateFromBoth(List<MatchedWord> words, int idx, int leftIdx, int rightIdx)
    {
        var left = words[leftIdx];
        var right = words[rightIdx];

        // Use ProbeLineIndex as primary signal, fall back to Y-distance
        bool sameLineAsLeft = words[idx].ProbeLineIndex == left.ProbeLineIndex
            || (Math.Abs(left.Bounds.Y - right.Bounds.Y) < left.Bounds.Height * 2
                && words[idx].ProbeLineIndex == right.ProbeLineIndex);

        if (sameLineAsLeft)
        {
            // Interpolate X between left.Right and right.X
            float charWidth = EstimateCharWidth(left);
            int x = left.Bounds.Right + (int)(charWidth * 0.5f);
            int w = (int)(words[idx].Text.Length * charWidth);
            return new Rectangle(x, left.Bounds.Y, Math.Max(w, 4), left.Bounds.Height);
        }
        else
        {
            // Different visual lines — place on a new line between left and right
            float charWidth = EstimateCharWidth(left);
            int w = (int)(words[idx].Text.Length * charWidth);
            // Use right neighbor's X as line start reference (it's likely a line beginning)
            int x = right.Bounds.X;
            int y = left.Bounds.Bottom + 2; // small gap below left neighbor
            return new Rectangle(x, y, Math.Max(w, 4), left.Bounds.Height);
        }
    }

    private static Rectangle InterpolateFromLeft(List<MatchedWord> words, int idx, int leftIdx)
    {
        var left = words[leftIdx];
        float charWidth = EstimateCharWidth(left);
        int w = (int)(words[idx].Text.Length * charWidth);

        // Different probe line → new line below at left margin
        if (words[idx].ProbeLineIndex != left.ProbeLineIndex)
        {
            int lineGap = words[idx].ProbeLineIndex - left.ProbeLineIndex;
            int y = left.Bounds.Bottom + left.Bounds.Height * Math.Max(lineGap - 1, 0) + 2;
            int leftMargin = FindLeftMargin(words);
            return new Rectangle(leftMargin, y, Math.Max(w, 4), left.Bounds.Height);
        }

        int x = left.Bounds.Right + (int)(charWidth * 0.5f);
        return new Rectangle(x, left.Bounds.Y, Math.Max(w, 4), left.Bounds.Height);
    }

    /// <summary>Find the leftmost X of any word with valid bounds (= left margin).</summary>
    private static int FindLeftMargin(List<MatchedWord> words)
    {
        int min = int.MaxValue;
        foreach (var w in words)
        {
            if (!w.Bounds.IsEmpty && w.Bounds.X < min)
                min = w.Bounds.X;
        }
        return min == int.MaxValue ? 0 : min;
    }

    private static Rectangle InterpolateFromRight(List<MatchedWord> words, int idx, int rightIdx)
    {
        var right = words[rightIdx];
        float charWidth = EstimateCharWidth(right);
        int w = (int)(words[idx].Text.Length * charWidth);
        int x = right.Bounds.X - w - (int)(charWidth * 0.5f);
        return new Rectangle(Math.Max(x, 0), right.Bounds.Y, Math.Max(w, 4), right.Bounds.Height);
    }
}
