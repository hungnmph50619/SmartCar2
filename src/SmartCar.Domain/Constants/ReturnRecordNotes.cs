namespace SmartCar.Domain.Constants;

public static class ReturnRecordNotes
{
    private const string AccessoriesMarker = "[RETURN_ACCESSORIES]";
    private const string NoteMarker = "[RETURN_NOTE]";

    public static string? Build(string? accessories, string? note)
    {
        var normalizedAccessories = Normalize(accessories);
        var normalizedNote = Normalize(note);

        if (normalizedAccessories is null)
        {
            return normalizedNote;
        }

        return $"{AccessoriesMarker}{normalizedAccessories}\n{NoteMarker}{normalizedNote ?? string.Empty}";
    }

    public static string? ExtractAccessories(string? storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue) ||
            !storedValue.Contains(AccessoriesMarker, StringComparison.Ordinal))
        {
            return null;
        }

        var start = storedValue.IndexOf(AccessoriesMarker, StringComparison.Ordinal) + AccessoriesMarker.Length;
        var end = storedValue.IndexOf(NoteMarker, start, StringComparison.Ordinal);
        var value = end < 0 ? storedValue[start..] : storedValue[start..end];
        return Normalize(value);
    }

    public static string? ExtractNote(string? storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return null;
        }

        var markerIndex = storedValue.IndexOf(NoteMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return Normalize(storedValue);
        }

        return Normalize(storedValue[(markerIndex + NoteMarker.Length)..]);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
