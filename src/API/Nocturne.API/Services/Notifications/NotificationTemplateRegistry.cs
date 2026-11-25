using Nocturne.Core.Models;

namespace Nocturne.API.Services.Notifications;

/// <summary>
/// Registry that maps notification type strings to their default <see cref="NotificationTemplate"/>
/// (title, message, action labels, icons, etc.). Templates are registered at startup by
/// <see cref="NotificationTemplates.BuiltInNotificationTemplates"/>.
/// </summary>
public interface INotificationTemplateRegistry
{
    /// <summary>Returns the template for the specified notification type, or <see langword="null"/> if not registered.</summary>
    /// <param name="type">The notification type string (e.g. <c>"meal_matching.suggested_match"</c>).</param>
    NotificationTemplate? GetTemplate(string type);
}

/// <summary>
/// Default in-memory implementation of <see cref="INotificationTemplateRegistry"/>.
/// Templates are keyed by <see cref="NotificationTemplate.Type"/> and registered via <see cref="Register"/>.
/// </summary>
/// <seealso cref="INotificationTemplateRegistry"/>
public class NotificationTemplateRegistry : INotificationTemplateRegistry
{
    private readonly Dictionary<string, NotificationTemplate> _templates = new();

    public IReadOnlyCollection<NotificationTemplate> Templates => _templates.Values;

    public void Register(NotificationTemplate template)
    {
        if (!_templates.TryAdd(template.Type, template))
            throw new InvalidOperationException($"A template for '{template.Type}' is already registered.");
    }

    public NotificationTemplate? GetTemplate(string type)
    {
        return _templates.GetValueOrDefault(type);
    }
}
