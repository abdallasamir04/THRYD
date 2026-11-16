using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SchoolManagement.Web.Entities;

namespace SchoolManagement.Web.Services;

/// <summary>An attachment as shown on a task (form, card, details page).</summary>
public sealed record TaskAttachmentView(int Id, int? TaskId, Guid Key, string FileName, string Extension, long SizeBytes, DateTime UploadedAt, string? UploadedBy)
{
    public string Kind => TaskAttachmentRules.KindLabel(Extension);
    public string Size => TaskAttachmentRules.SizeLabel(SizeBytes);
    public bool OpensInBrowser => TaskAttachmentRules.OpensInBrowser(Extension);
}

/// <summary>An attachment file opened for download.</summary>
public sealed record TaskAttachmentFile(string FileName, string ContentType, bool Inline, Stream Content);

/// <summary>
/// What may be attached to a task, and the content check behind it. The extension decides the type, and the file's
/// own bytes must match it (signature, and for Office documents the package layout), so a renamed file is refused.
/// </summary>
public static class TaskAttachmentRules
{
    public const long MaxBytes = 15 * 1024 * 1024;
    public const int MaxPerTask = 10;

    /// <summary>The value of a file input's accept attribute.</summary>
    public const string Accept = ".pdf,.xlsx,.xls,.docx,.doc,.pptx,.ppt,.png,.jpg,.jpeg";

    private static readonly Dictionary<string, (string ContentType, string Kind)> Types = new(StringComparer.Ordinal)
    {
        ["pdf"] = ("application/pdf", "PDF"),
        ["xlsx"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Excel"),
        ["xls"] = ("application/vnd.ms-excel", "Excel"),
        ["docx"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "Word"),
        ["doc"] = ("application/msword", "Word"),
        ["pptx"] = ("application/vnd.openxmlformats-officedocument.presentationml.presentation", "PowerPoint"),
        ["ppt"] = ("application/vnd.ms-powerpoint", "PowerPoint"),
        ["png"] = ("image/png", "Image"),
        ["jpg"] = ("image/jpeg", "Image"),
        ["jpeg"] = ("image/jpeg", "Image"),
    };

    private static readonly byte[] Pdf = "%PDF-"u8.ToArray();
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public static bool IsAllowed(string extension) => Types.ContainsKey(extension);
    public static string ContentType(string extension) => Types.TryGetValue(extension, out var t) ? t.ContentType : "application/octet-stream";
    public static string KindLabel(string extension) => Types.TryGetValue(extension, out var t) ? t.Kind : "File";

    /// <summary>Icon of the file kind (sprite name).</summary>
    public static string Icon(string extension) => KindLabel(extension) switch
    {
        "PDF" => "file-pdf",
        "Excel" => "sheet",
        "Word" => "file-text",
        "PowerPoint" => "presentation",
        "Image" => "image",
        _ => "file",
    };

    /// <summary>PDFs and images open in a browser tab; Office files always download.</summary>
    public static bool OpensInBrowser(string extension) => extension is "pdf" or "png" or "jpg" or "jpeg";

    public static string SizeLabel(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{Math.Max(1, (int)Math.Round(bytes / 1024.0))} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };

    /// <summary>The lower-case extension (no dot) of an uploaded name.</summary>
    public static string ExtensionOf(string? fileName) => Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();

    /// <summary>
    /// A display / download name: the last path segment only, without control or reserved characters, at most
    /// <see cref="TaskAttachment.FileNameMaxLength"/> characters with the extension kept.
    /// </summary>
    public static string CleanName(string? fileName, string extension)
    {
        var name = (fileName ?? "").Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(char.IsControl(ch) || invalid.Contains(ch) || ch is '"' or '<' or '>' or '|' or ':' or '*' or '?' ? '_' : ch);
        var stem = Path.GetFileNameWithoutExtension(sb.ToString().Trim()).Trim().Trim('.').Trim();
        if (stem.Length == 0) stem = "attachment";
        var max = TaskAttachment.FileNameMaxLength - extension.Length - 1;
        if (stem.Length > max) stem = stem[..max].Trim();
        return $"{stem}.{extension}";
    }

    /// <summary>Null when the bytes are a genuine file of the type <paramref name="extension"/> claims; otherwise the reason.</summary>
    public static string? CheckContent(string extension, ReadOnlySpan<byte> head, Stream whole)
    {
        static bool Starts(ReadOnlySpan<byte> data, byte[] sig) => data.Length >= sig.Length && data[..sig.Length].SequenceEqual(sig);
        var ok = extension switch
        {
            // A PDF header may follow a few bytes of junk; readers look in the first 1 KB.
            "pdf" => head[..Math.Min(head.Length, 1024)].IndexOf(Pdf) >= 0,
            "png" => Starts(head, Png),
            "jpg" or "jpeg" => Starts(head, Jpeg),
            "doc" or "xls" or "ppt" => Starts(head, Ole),
            "docx" => Starts(head, Zip) && OfficePackage(whole, "word/document.xml"),
            "xlsx" => Starts(head, Zip) && OfficePackage(whole, "xl/workbook.xml"),
            "pptx" => Starts(head, Zip) && OfficePackage(whole, "ppt/presentation.xml"),
            _ => false,
        };
        return ok ? null : $"This file is not a valid {KindLabel(extension)} file (its content does not match .{extension}).";
    }

    /// <summary>An Office Open XML package of the expected kind, without macros (only the entry list is read).</summary>
    private static bool OfficePackage(Stream stream, string mainPart)
    {
        try
        {
            stream.Position = 0;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return names.Contains("[Content_Types].xml") && names.Contains(mainPart)
                   && !names.Any(n => n.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException) { return false; }
        finally { stream.Position = 0; }
    }

    public static string Sha256Hex(Stream stream)
    {
        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        return hash;
    }
}

/// <summary>
/// Where task attachment bytes are kept: a folder outside wwwroot (never served as static files), configured by
/// <c>Storage:TaskAttachments</c> (default <c>App_Data/task-attachments</c> under the content root). Files are named by
/// a GUID, so an uploaded name can never choose a path.
/// </summary>
public interface ITaskFileStore
{
    Task SaveAsync(string storedName, Stream content, CancellationToken ct = default);
    Stream? Open(string storedName);
    void Delete(string storedName);
}

public sealed class DiskTaskFileStore : ITaskFileStore
{
    private readonly string _root;

    public DiskTaskFileStore(IConfiguration config, IWebHostEnvironment env)
    {
        var configured = config["Storage:TaskAttachments"];
        _root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "App_Data", "task-attachments")
            : Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured));
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(string storedName, Stream content, CancellationToken ct = default)
    {
        var path = PathOf(storedName);
        content.Position = 0;
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, ct);
    }

    public Stream? Open(string storedName)
    {
        var path = PathOf(storedName);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true) : null;
    }

    public void Delete(string storedName)
    {
        try { File.Delete(PathOf(storedName)); }
        catch (IOException) { /* in use or already gone: the orphan sweep is not needed for correctness */ }
    }

    /// <summary>Only names the application generated (32 hex characters) map to a path inside the root.</summary>
    private string PathOf(string storedName)
    {
        if (storedName.Length != 32 || !storedName.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid stored file name.", nameof(storedName));
        return Path.Combine(_root, storedName);
    }
}
