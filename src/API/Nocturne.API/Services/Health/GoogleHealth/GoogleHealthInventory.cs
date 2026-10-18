using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Models.Health;

namespace Nocturne.API.Services.Health.GoogleHealth;

/// <summary>
/// Turns Google's catalog, the tenant's grant and selection, and the last inventory scan into the
/// per-category view the settings page renders.
/// </summary>
public static class GoogleHealthInventory
{
    public sealed record Context(
        IReadOnlyCollection<string> GrantedScopes,
        IReadOnlyCollection<string> Selected,
        bool PreviewRequired,
        string? ErrorCode,
        IReadOnlyCollection<string> ErrorDataTypes,
        bool IncludeCycleTracking);

    public static GoogleHealthCategory[] Categories(
        IReadOnlyCollection<GoogleHealthInventoryItem>? inventory, Context context) =>
        Enum.GetValues<GoogleHealthCategoryKind>()
            .Where(kind => kind != GoogleHealthCategoryKind.CycleTracking || context.IncludeCycleTracking)
            .Select(kind =>
            {
                var types = GoogleHealthClient.Capabilities
                    .Where(capability => capability.Category == kind)
                    .Select(capability => Describe(
                        capability, inventory?.FirstOrDefault(item => item.DataType == capability.DataType), context))
                    .ToArray();
                return new GoogleHealthCategory
                {
                    Kind = kind,
                    HasSelectable = types.Any(type => type.Selectable),
                    Types = types
                };
            })
            .Where(category => category.Types.Length > 0)
            .ToArray();

    private static GoogleHealthDataTypeStatus Describe(
        GoogleHealthCapability capability, GoogleHealthInventoryItem? scanned, Context context)
    {
        var status = new GoogleHealthDataTypeStatus
        {
            DataType = capability.DataType,
            Destination = capability.Destination,
            Count = scanned?.Count ?? 0,
            ErrorCode = scanned?.ErrorCode
        };
        if (!capability.Supported)
        {
            status.State = capability.UnavailableReason ?? GoogleHealthTypeState.NotYetSupported;
            status.Found = GoogleHealthFound.NotScanned;
            return status;
        }

        // The grant can change after a scan (a token refresh returning a narrower grant), so it
        // comes from the live session; the scan contributes only what it counted.
        var granted = context.GrantedScopes.Contains(
            GoogleHealthClient.ScopeFor(capability.DataType), StringComparer.Ordinal);
        status.Selectable = granted && scanned?.ErrorCode is null;
        var found = status.Count > 0;
        (status.State, status.Found) = scanned switch
        {
            { ErrorCode: not null } => (GoogleHealthTypeState.ReadFailed, GoogleHealthFound.ScanFailed),
            null => (GoogleHealthTypeState.NotScanned, GoogleHealthFound.NotScanned),
            _ when !granted => (GoogleHealthTypeState.PermissionNotGranted, GoogleHealthFound.NoPermission),
            _ => (Imported(capability.DataType, found, context), found ? GoogleHealthFound.Found : GoogleHealthFound.None)
        };
        return status;
    }

    private static GoogleHealthTypeState Imported(string dataType, bool found, Context context)
    {
        if (context.PreviewRequired || !context.Selected.Contains(dataType, StringComparer.Ordinal))
            return found ? GoogleHealthTypeState.Available : GoogleHealthTypeState.AvailableNoData;
        if (context.ErrorCode is not null &&
            (context.ErrorDataTypes.Count == 0 || context.ErrorDataTypes.Contains(dataType, StringComparer.Ordinal)))
            return GoogleHealthTypeState.NeedsAttention;
        return found ? GoogleHealthTypeState.Importing : GoogleHealthTypeState.ImportingNoData;
    }
}
