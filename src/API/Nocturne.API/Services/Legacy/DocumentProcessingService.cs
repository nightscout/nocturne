using Ganss.Xss;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Legacy;

/// <summary>
/// Processes incoming Nightscout documents before they are persisted. Responsibilities include
/// HTML sanitization of free-text fields (using a Ganss.Xss allowlist that mirrors the legacy
/// Nightscout purifier), mills/timestamp normalization, and identifier generation.
/// </summary>
/// <seealso cref="IDocumentProcessingService"/>
public class DocumentProcessingService : IDocumentProcessingService
{
    private readonly HtmlSanitizer _htmlSanitizer;
    private readonly ILogger<DocumentProcessingService> _logger;

    public DocumentProcessingService(ILogger<DocumentProcessingService> logger)
    {
        _logger = logger;
        _htmlSanitizer = new HtmlSanitizer();

        // Configure allowed tags and attributes - similar to legacy Nightscout purifier
        _htmlSanitizer.AllowedTags.Clear();
        _htmlSanitizer.AllowedTags.Add("img");
        _htmlSanitizer.AllowedTags.Add("b");
        _htmlSanitizer.AllowedTags.Add("i");
        _htmlSanitizer.AllowedTags.Add("u");
        _htmlSanitizer.AllowedTags.Add("strong");
        _htmlSanitizer.AllowedTags.Add("em");
        _htmlSanitizer.AllowedTags.Add("a");
        _htmlSanitizer.AllowedTags.Add("br");
        _htmlSanitizer.AllowedTags.Add("p");
        _htmlSanitizer.AllowedTags.Add("span");
        _htmlSanitizer.AllowedTags.Add("div");

        // Remove dangerous attributes
        _htmlSanitizer.AllowedAttributes.Clear();
        _htmlSanitizer.AllowedAttributes.Add("class");
        _htmlSanitizer.AllowedAttributes.Add("style");
        _htmlSanitizer.AllowedAttributes.Add("title");
        _htmlSanitizer.AllowedAttributes.Add("alt");

        // Remove javascript: and other dangerous schemes
        _htmlSanitizer.AllowedSchemes.Clear();
        _htmlSanitizer.AllowedSchemes.Add("http");
        _htmlSanitizer.AllowedSchemes.Add("https");
        _htmlSanitizer.AllowedSchemes.Add("mailto");

        _ = _htmlSanitizer.Sanitize("<b>warmup</b>");
    }

    /// <inheritdoc />
    public IEnumerable<T> ProcessDocuments<T>(
        IEnumerable<T> documents
    )
        where T : IProcessableDocument
    {
        _logger.LogDebug(
            "Processing {Count} documents of type {Type}",
            documents.Count(),
            typeof(T).Name
        );

        var processedDocuments = new List<T>();

        foreach (var document in documents)
        {
            // Process timestamp and timezone
            ProcessTimestamp(document);

            // Sanitize HTML content in all sanitizable fields
            var sanitizableFields = document.GetSanitizableFields();
            foreach (var field in sanitizableFields)
            {
                if (!string.IsNullOrWhiteSpace(field.Value))
                {
                    var sanitized = SanitizeHtml(field.Value);
                    document.SetSanitizedField(field.Key, sanitized);
                }
            }

            processedDocuments.Add(document);
        }

        _logger.LogDebug(
            "Processed {Count} documents of type {Type}",
            processedDocuments.Count,
            typeof(T).Name
        );

        return processedDocuments;
    }

    /// <inheritdoc />
    public string SanitizeHtml(string? htmlContent)
    {
        if (string.IsNullOrWhiteSpace(htmlContent))
        {
            return string.Empty;
        }

        try
        {
            var sanitized = _htmlSanitizer.Sanitize(htmlContent);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Sanitized HTML content: {Original} -> {Sanitized}",
                    htmlContent.Substring(0, Math.Min(htmlContent.Length, 50)),
                    sanitized.Substring(0, Math.Min(sanitized.Length, 50))
                );
            }
            return sanitized;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sanitize HTML content: {Content}", htmlContent);
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public void ProcessTimestamp(IProcessableDocument document)
    {
        try
        {
            var hasExplicitCreatedAt = !string.IsNullOrWhiteSpace(document.CreatedAt);
            var hasExplicitMills = document.Mills > 0;

            // Determine if CreatedAt looks like a default constructor value vs explicit input
            var createdAtLooksLikeDefault =
                hasExplicitCreatedAt
                && (
                    document.CreatedAt?.EndsWith("Z") == true
                        && document.CreatedAt?.Contains('+') != true
                        && document.CreatedAt?.Contains('-') != true
                    || (
                        document.CreatedAt?.Contains('-') == true
                        && document.CreatedAt.LastIndexOf('-')
                            <= document.CreatedAt.LastIndexOf('T')
                    )
                );

            // Priority 1: If CreatedAt has timezone info (explicit offset), always process it to preserve timezone
            if (
                hasExplicitCreatedAt
                && (
                    document.CreatedAt?.Contains('+') == true
                    || (
                        document.CreatedAt?.Contains('-') == true
                        && document.CreatedAt.LastIndexOf('-') > document.CreatedAt.LastIndexOf('T')
                    )
                )
            )
            {
                // CreatedAt has timezone information - process it to preserve timezone offset
                if (DateTimeOffset.TryParse(document.CreatedAt, out var parsedDate))
                {
                    // Convert to UTC and set the ISO string
                    var utcDate = parsedDate.ToUniversalTime();
                    document.CreatedAt = utcDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

                    // Set UTC offset in minutes (same as timezone offset)
                    document.UtcOffset = (int)parsedDate.Offset.TotalMinutes;

                    // Set mills timestamp
                    document.Mills = utcDate.ToUnixTimeMilliseconds();

                    _logger.LogDebug(
                        "Processed timezone-aware timestamp: {Original} -> {UTC} (offset: {Offset})",
                        parsedDate.ToString(),
                        document.CreatedAt,
                        document.UtcOffset
                    );
                }
            }
            // Priority 2: If Mills is explicitly set and CreatedAt looks like default constructor value, use Mills
            else if (hasExplicitMills && createdAtLooksLikeDefault)
            {
                // Use existing Mills value to set CreatedAt
                var dateFromMills = DateTimeOffset.FromUnixTimeMilliseconds(document.Mills);
                document.CreatedAt = dateFromMills.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                // The timestamp carried no zone; honor an offset the client sent directly
                // (Nightscout entries may set utcOffset independently of date), else assume UTC.
                document.UtcOffset ??= 0;

                _logger.LogDebug(
                    "Used explicit Mills timestamp: {Mills} -> {CreatedAt}",
                    document.Mills,
                    document.CreatedAt
                );
            }
            // Priority 3: Process any other CreatedAt values (including UTC timestamps)
            else if (hasExplicitCreatedAt)
            {
                // Try to parse the timestamp as DateTimeOffset to handle timezone info
                if (DateTimeOffset.TryParse(document.CreatedAt, out var parsedDate))
                {
                    // Convert to UTC and set the ISO string
                    var utcDate = parsedDate.ToUniversalTime();
                    document.CreatedAt = utcDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

                    // Set UTC offset in minutes (same as timezone offset)
                    document.UtcOffset = (int)parsedDate.Offset.TotalMinutes;

                    // Set mills timestamp
                    document.Mills = utcDate.ToUnixTimeMilliseconds();

                    _logger.LogDebug(
                        "Processed CreatedAt timestamp: {Original} -> {UTC} (offset: {Offset})",
                        parsedDate.ToString(),
                        document.CreatedAt,
                        document.UtcOffset
                    );
                }
                else if (DateTime.TryParse(document.CreatedAt, out var utcDateTime))
                {
                    // If it's already UTC or no timezone info, ensure it's properly formatted
                    var dateTimeOffset = new DateTimeOffset(utcDateTime, TimeSpan.Zero);
                    document.CreatedAt = dateTimeOffset.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    document.Mills = dateTimeOffset.ToUnixTimeMilliseconds();
                    // The timestamp string carried no zone; honor a client-supplied offset, else assume UTC.
                    document.UtcOffset ??= 0;
                }
                else
                {
                    _logger.LogWarning("Invalid timestamp format: {Timestamp}", document.CreatedAt);
                    // Set to current UTC time as fallback
                    var now = DateTimeOffset.UtcNow;
                    document.CreatedAt = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    document.Mills = now.ToUnixTimeMilliseconds();
                    document.UtcOffset = 0;
                }
            }
            // Priority 4: Use Mills if available
            else if (hasExplicitMills)
            {
                // Use existing Mills value to set CreatedAt
                var dateFromMills = DateTimeOffset.FromUnixTimeMilliseconds(document.Mills);
                document.CreatedAt = dateFromMills.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                // mills fixes the instant (UTC); honor an independently-supplied utcOffset
                // (e.g. AAPS/Loop send date + utcOffset with no created_at), else assume UTC.
                document.UtcOffset ??= 0;
            }
            else
            {
                // No timestamp provided at all, set to current UTC time
                var now = DateTimeOffset.UtcNow;
                document.CreatedAt = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                document.Mills = now.ToUnixTimeMilliseconds();
                document.UtcOffset = 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Error processing timestamp for document: {Timestamp}",
                document.CreatedAt
            );
            // Fallback to current UTC time
            var now = DateTimeOffset.UtcNow;
            document.CreatedAt = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            document.Mills = now.ToUnixTimeMilliseconds();
            document.UtcOffset = 0;
        }
    }

    /// <inheritdoc />
    public Entry ProcessEntry(Entry entry)
    {
        ProcessTimestamp(entry);

        var sanitizableFields = entry.GetSanitizableFields();
        foreach (var field in sanitizableFields)
        {
            if (!string.IsNullOrEmpty(field.Value))
            {
                entry.SetSanitizedField(field.Key, SanitizeHtml(field.Value));
            }
        }

        return entry;
    }

    /// <inheritdoc />
    public Treatment ProcessTreatment(Treatment treatment)
    {
        ProcessTimestamp(treatment);

        var sanitizableFields = treatment.GetSanitizableFields();
        foreach (var field in sanitizableFields)
        {
            if (!string.IsNullOrEmpty(field.Value))
            {
                treatment.SetSanitizedField(field.Key, SanitizeHtml(field.Value));
            }
        }

        return treatment;
    }
}
