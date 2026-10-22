using Ganss.Xss;
using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Legacy;

/// <summary>
/// Generic service for processing documents with sanitization and timestamp conversion
/// </summary>
public interface IDocumentProcessingService
{
    /// <summary>
    /// Process a collection of documents by sanitizing HTML content and converting timestamps
    /// </summary>
    /// <typeparam name="T">Document type that implements IProcessableDocument</typeparam>
    /// <returns>Processed documents with sanitized content and converted timestamps</returns>
    IEnumerable<T> ProcessDocuments<T>(IEnumerable<T> documents)
        where T : IProcessableDocument;

    /// <returns>Sanitized HTML content safe for display</returns>
    string SanitizeHtml(string? htmlContent);

    /// <summary>
    /// Convert timezone-aware timestamps to UTC and set utcOffset
    /// </summary>
    void ProcessTimestamp(IProcessableDocument document);

    /// <returns>Processed entry</returns>
    Entry ProcessEntry(Entry entry);

    /// <returns>Processed treatment</returns>
    Treatment ProcessTreatment(Treatment treatment);
}
