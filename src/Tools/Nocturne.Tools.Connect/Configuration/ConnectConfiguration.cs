using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Nocturne.Tools.Abstractions.Configuration;

namespace Nocturne.Tools.Connect.Configuration;

/// <summary>
/// Configuration for the Nocturne Connect tool.
/// </summary>
public class ConnectConfiguration : IToolConfiguration
{
    /// <inheritdoc/>
    public string ToolName => "Nocturne Connect";

    /// <inheritdoc/>
    public string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    // Core Configuration
    /// <summary>
    /// The base Nightscout URL used for API requests (e.g. https://example.herokuapp.com).
    /// </summary>
    [Required(ErrorMessage = "Nightscout URL is required")]
    [Url(ErrorMessage = "Nightscout URL must be a valid URL")]
    public string NightscoutUrl { get; set; } = string.Empty;

    /// <summary>
    /// The Nightscout API secret used to authenticate requests against the Nightscout API.
    /// This should be kept secure and at least 12 characters long.
    /// </summary>
    [Required(ErrorMessage = "Nightscout API Secret is required")]
    [MinLength(12, ErrorMessage = "API Secret should be at least 12 characters")]
    public string NightscoutApiSecret { get; set; } = string.Empty;

    /// <summary>
    /// The source connector to use when fetching data; valid values are:
    /// "glooko", "dexcomshare", "linkup", or "mylife".
    /// </summary>
    [Required(ErrorMessage = "Connect Source is required")]
    public string ConnectSource { get; set; } = string.Empty;

    // Optional Configuration
    /// <summary>
    /// The units used for displaying glucose values (e.g., "mg/dl" or "mmol/L").
    /// Defaults to "mg/dl".
    /// </summary>
    public string DisplayUnits { get; set; } = "mg/dl";
    public string Language { get; set; } = "en";
    public string NodeEnvironment { get; set; } = "production";
    public string Hostname { get; set; } = "localhost";
    public int Port { get; set; } = 1337;
    public string? DataDirectory { get; set; }

    // Glooko Configuration
    [RequiredIf(
        nameof(ConnectSource),
        "glooko",
        ErrorMessage = "Glooko email is required when using Glooko as source"
    )]
    [EmailAddress(ErrorMessage = "Glooko email must be a valid email address")]
    public string? GlookoEmail { get; set; }

    [RequiredIf(
        nameof(ConnectSource),
        "glooko",
        ErrorMessage = "Glooko password is required when using Glooko as source"
    )]
    public string? GlookoPassword { get; set; }

    public string GlookoServer { get; set; } = "eu.api.glooko.com";
    public int GlookoTimezoneOffset { get; set; } = 0;

    // Dexcom Share Configuration
    [RequiredIf(
        nameof(ConnectSource),
        "dexcomshare",
        ErrorMessage = "Dexcom username is required when using Dexcom Share as source"
    )]
    public string? DexcomUsername { get; set; }

    [RequiredIf(
        nameof(ConnectSource),
        "dexcomshare",
        ErrorMessage = "Dexcom password is required when using Dexcom Share as source"
    )]
    public string? DexcomPassword { get; set; }

    public string DexcomRegion { get; set; } = "us";

    // LibreLinkUp Configuration
    [RequiredIf(
        nameof(ConnectSource),
        "linkup",
        ErrorMessage = "LibreLinkUp username is required when using LibreLinkUp as source"
    )]
    public string? LibreUsername { get; set; }

    [RequiredIf(
        nameof(ConnectSource),
        "linkup",
        ErrorMessage = "LibreLinkUp password is required when using LibreLinkUp as source"
    )]
    public string? LibrePassword { get; set; }

    public string LibreRegion { get; set; } = "EU";

    [RequiredIf(
        nameof(ConnectSource),
        "mylife",
        ErrorMessage = "MyLife username is required when using MyLife as source"
    )]
    public string? MyLifeUsername { get; set; }

    [RequiredIf(
        nameof(ConnectSource),
        "mylife",
        ErrorMessage = "MyLife password is required when using MyLife as source"
    )]
    public string? MyLifePassword { get; set; }

    public string? MyLifePatientId { get; set; }
    public bool MyLifeEnableGlucoseSync { get; set; } = true;
    public bool MyLifeEnableManualBgSync { get; set; } = true;
    public bool MyLifeEnableMealCarbConsolidation { get; set; } = true;
    public bool MyLifeEnableTempBasalConsolidation { get; set; } = true;
    public int MyLifeTempBasalConsolidationWindowMinutes { get; set; } = 5;

    /// <inheritdoc/>
    public ValidationResult ValidateConfiguration()
    {
        var validSources = new[]
        {
            "glooko",
            "dexcomshare",
            "linkup",
            "mylife",
        };

        if (!validSources.Contains(ConnectSource?.ToLowerInvariant()))
        {
            return new ValidationResult(
                $"Invalid connect source '{ConnectSource}'. Valid sources are: {string.Join(", ", validSources)}"
            );
        }

        // Additional validation can be added here
        return ValidationResult.Success!;
    }
}

/// <summary>
/// Custom validation attribute for conditional required fields.
/// </summary>
public class RequiredIfAttribute : ValidationAttribute
{
    private readonly string _dependentProperty;
    private readonly object _targetValue;

    public RequiredIfAttribute(string dependentProperty, object targetValue)
    {
        _dependentProperty = dependentProperty;
        _targetValue = targetValue;
    }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        var dependentProperty = validationContext.ObjectType.GetProperty(_dependentProperty);
        if (dependentProperty == null)
        {
            return new ValidationResult($"Unknown property: {_dependentProperty}");
        }

        var dependentValue = dependentProperty.GetValue(validationContext.ObjectInstance);

        if (Equals(dependentValue, _targetValue))
        {
            if (value == null || (value is string str && string.IsNullOrWhiteSpace(str)))
            {
                return new ValidationResult(
                    ErrorMessage
                        ?? $"{validationContext.DisplayName} is required when {_dependentProperty} is {_targetValue}"
                );
            }
        }

        return ValidationResult.Success;
    }
}
