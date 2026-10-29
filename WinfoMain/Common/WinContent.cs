namespace WinfoMain.Common;

using System.Drawing;

/// <summary>
/// Base class for a word with text and screen position.
/// Any code that only needs Text + Bounds works with this type.
/// </summary>
public class WinWordBase
{
    public string Text { get; set; } = "";
    public Rectangle Bounds { get; set; }
}

/// <summary>
/// OCR-specific word with additional properties from pixel-based recognition.
/// </summary>
public class WinWordOcr : WinWordBase
{
    /// <summary>Original OCR height before descender correction (for font size estimation).</summary>
    public int OcrHeight { get; set; }

    /// <summary>Estimated average character width in pixels (Bounds.Width / Text.Length).</summary>
    public float CharWidth => Text.Length > 0 ? (float)Bounds.Width / Text.Length : 0;

    /// <summary>Character height in pixels (from Bounds).</summary>
    public int CharHeight => Bounds.Height;
}

/// <summary>
/// Typed list of words. Notifies its owner line when the collection changes
/// so the line can invalidate its cached Bounds.
/// </summary>
public class WinWordList : List<WinWordBase>
{
    internal Action? OnChanged;

    public new void Add(WinWordBase item) { base.Add(item); OnChanged?.Invoke(); }
    public new void Clear() { base.Clear(); OnChanged?.Invoke(); }
    public new bool Remove(WinWordBase item) { var r = base.Remove(item); OnChanged?.Invoke(); return r; }
    public new void RemoveAt(int index) { base.RemoveAt(index); OnChanged?.Invoke(); }
}

/// <summary>Typed list of lines.</summary>
public class WinLineList : List<WinLineBase> { }

/// <summary>
/// Base class for a line of text.
/// Bounds are lazily computed from the contained words and cached until the word list changes.
/// </summary>
public class WinLineBase
{
    private Rectangle? _boundsCache;

    public WinLineBase()
    {
        Words = new WinWordList();
        Words.OnChanged = () => _boundsCache = null;
    }

    public WinWordList Words { get; }

    /// <summary>Concatenated text of all words in this line.</summary>
    public string Text => string.Join(" ", Words.Select(w => w.Text));

    /// <summary>
    /// Bounding rectangle enclosing all words in this line.
    /// Cached — recomputed only when the word list changes.
    /// </summary>
    public Rectangle Bounds
    {
        get
        {
            if (_boundsCache.HasValue) return _boundsCache.Value;
            if (Words.Count == 0) return Rectangle.Empty;
            int x = Words.Min(w => w.Bounds.X);
            int y = Words.Min(w => w.Bounds.Y);
            int right = Words.Max(w => w.Bounds.Right);
            int bottom = Words.Max(w => w.Bounds.Bottom);
            _boundsCache = new Rectangle(x, y, right - x, bottom - y);
            return _boundsCache.Value;
        }
    }

    /// <summary>Manually invalidate the cached bounds (e.g. after modifying word bounds in-place).</summary>
    public void InvalidateBounds() => _boundsCache = null;
}

/// <summary>
/// OCR-specific line with metrics from pixel-based recognition.
/// </summary>
public class WinLineOcr : WinLineBase
{
    /// <summary>
    /// Median height of the OCR word bounds in this line.
    /// </summary>
    public int MedianWordHeight { get; set; }
}

/// <summary>
/// Content of a window — words and lines recognized by OCR.
/// </summary>
public class WinContent
{
    public WinWordList Words { get; } = new();
    public WinLineList Lines { get; } = new();

    /// <summary>Full text of all lines, separated by newlines.</summary>
    public string Text => string.Join("\n", Lines.Select(l => l.Text));

    /// <summary>Clear all words and lines.</summary>
    public void Clear()
    {
        Words.Clear();
        Lines.Clear();
    }
}
