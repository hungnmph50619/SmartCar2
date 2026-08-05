using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;

namespace SmartCar.Web.Services;

public interface ISecureDocumentStorage
{
    Task<string> SaveAsync(
        IFormFile file,
        string ownerId,
        CancellationToken cancellationToken = default);

    bool TryResolve(string storedPath, out string fullPath, out string contentType);

    void Delete(string? storedPath);
}

public sealed class SecureDocumentStorage : ISecureDocumentStorage
{
    private readonly IWebHostEnvironment _environment;
    private readonly string _secureRoot;

    public SecureDocumentStorage(IWebHostEnvironment environment)
    {
        _environment = environment;
        _secureRoot = Path.Combine(
            environment.ContentRootPath,
            "App_Data",
            "SecureDocuments");
    }

    public async Task<string> SaveAsync(
        IFormFile file,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        var safeOwnerId = SanitizeSegment(ownerId);
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var folder = Path.Combine(_secureRoot, safeOwnerId);
        Directory.CreateDirectory(folder);

        var fileName = $"{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(folder, fileName);

        await using var stream = File.Create(fullPath);
        await file.CopyToAsync(stream, cancellationToken);

        return $"secure-documents/{safeOwnerId}/{fileName}";
    }

    public bool TryResolve(string storedPath, out string fullPath, out string contentType)
    {
        fullPath = string.Empty;
        contentType = "application/octet-stream";

        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return false;
        }

        if (storedPath.StartsWith("secure-documents/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = storedPath["secure-documents/".Length..]
                .Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(_secureRoot, relative));
            var root = Path.GetFullPath(_secureRoot) + Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            fullPath = candidate;
        }
        else if (storedPath.StartsWith("/uploads/documents/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = storedPath.TrimStart('/')
                .Replace('/', Path.DirectorySeparatorChar);
            var webRoot = Path.GetFullPath(_environment.WebRootPath) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(_environment.WebRootPath, relative));

            if (!candidate.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            fullPath = candidate;
        }
        else
        {
            return false;
        }

        if (!File.Exists(fullPath))
        {
            return false;
        }

        contentType = ImageFileValidator.GetContentType(fullPath);
        return contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }

    public void Delete(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return;
        }

        if (TryResolve(storedPath, out var fullPath, out _) && File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    private static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Where(character => !invalid.Contains(character) && character is not '/' and not '\\')
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }
}
