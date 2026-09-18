namespace SmartCar.Web.Services;

public static class ExtensionEvidencePathParser
{
    private const string EvidenceMarker = "[EVIDENCE]";
    private const string NoteMarker = "[NOTE]";
    private const string ImageMarker = "Ảnh minh chứng:";

    public static IReadOnlyList<string> ExtractImagePaths(string? storedCustomerNote)
    {
        if (string.IsNullOrWhiteSpace(storedCustomerNote))
        {
            return Array.Empty<string>();
        }

        var evidenceStart = storedCustomerNote.IndexOf(
            EvidenceMarker,
            StringComparison.Ordinal);
        if (evidenceStart < 0)
        {
            return Array.Empty<string>();
        }

        evidenceStart += EvidenceMarker.Length;
        var noteStart = storedCustomerNote.IndexOf(
            NoteMarker,
            evidenceStart,
            StringComparison.Ordinal);

        var evidence = noteStart >= 0
            ? storedCustomerNote[evidenceStart..noteStart]
            : storedCustomerNote[evidenceStart..];

        var imageStart = evidence.IndexOf(
            ImageMarker,
            StringComparison.OrdinalIgnoreCase);
        if (imageStart < 0)
        {
            return Array.Empty<string>();
        }

        var paths = evidence[(imageStart + ImageMarker.Length)..]
            .Trim()
            .Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Where(path =>
                path.StartsWith("secure-documents/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/uploads/extensions/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return paths;
    }
}
