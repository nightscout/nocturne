using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Nocturne.Infrastructure.Data.Security;

/// <summary>
/// The Data Protection key ring, stored in <see cref="NocturneDbContext.DataProtectionKeys"/>. It opens
/// a context per call from the raw factory rather than resolving one from a DI scope, as
/// <c>PersistKeysToDbContext</c> would; the keys belong to the instance, not to any tenant.
/// </summary>
internal sealed class NocturneDbXmlRepository(
    IDbContextFactory<NocturneDbContext> contexts,
    ILogger<NocturneDbXmlRepository> logger) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var db = contexts.CreateDbContext();
        return db.DataProtectionKeys.AsNoTracking()
            .Select(key => key.Xml)
            .ToList()
            .Select(TryParse)
            .OfType<XElement>()
            .ToList()
            .AsReadOnly();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var db = contexts.CreateDbContext();
        db.DataProtectionKeys.Add(new DataProtectionKey
        {
            FriendlyName = friendlyName,
            Xml = element.ToString(SaveOptions.DisableFormatting),
        });
        db.SaveChanges();
    }

    private XElement? TryParse(string? xml)
    {
        if (string.IsNullOrEmpty(xml))
        {
            logger.LogWarning("Skipping a Data Protection key with no XML");
            return null;
        }

        return XElement.Parse(xml);
    }
}
