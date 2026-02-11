using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Nocturne.Connectors.MyFitnessPal.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyFitnessPal.Services;

/// <summary>
/// Connector service for MyFitnessPal food diary data.
/// Uses the public diary sharing API to fetch food entries by username.
/// </summary>
public class MyFitnessPalConnectorService : BaseConnectorService<MyFitnessPalConnectorConfiguration>
{
    private readonly MyFitnessPalConnectorConfiguration _config;
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly IConnectorPublisher? _connectorPublisher;

    public MyFitnessPalConnectorService(
        HttpClient httpClient,
        ILogger<MyFitnessPalConnectorService> logger,
        MyFitnessPalConnectorConfiguration config,
        IRetryDelayStrategy retryDelayStrategy,
        IConnectorPublisher? publisher = null
    )
        : base(httpClient, logger, publisher)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _retryDelayStrategy =
            retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _connectorPublisher = publisher;
    }

    protected override string ConnectorSource => DataSources.MyFitnessPalConnector;
    public override string ServiceName => "MyFitnessPal";
    public override List<SyncDataType> SupportedDataTypes => [SyncDataType.Food];

    public override Task<bool> AuthenticateAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.Username))
        {
            TrackFailedRequest("Username is required");
            return Task.FromResult(false);
        }

        TrackSuccessfulRequest();
        return Task.FromResult(true);
    }

    public override Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
    {
        // MFP doesn't provide glucose data
        return Task.FromResult(Enumerable.Empty<Entry>());
    }

    public override async Task<SyncResult> SyncDataAsync(
        SyncRequest request,
        MyFitnessPalConnectorConfiguration config,
        CancellationToken cancellationToken
    )
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        try
        {
            if (!await AuthenticateAsync())
            {
                result.Success = false;
                result.Errors.Add("Authentication failed: missing username");
                result.EndTime = DateTimeOffset.UtcNow;
                return result;
            }

            var from = request.From ?? DateTime.UtcNow.AddDays(-config.LookbackDays);
            var to = request.To ?? DateTime.UtcNow;

            var diaryDays = await FetchDiaryAsync(from, to, cancellationToken);
            if (diaryDays == null)
            {
                result.Success = false;
                result.Errors.Add("Failed to fetch diary data from MyFitnessPal");
                result.EndTime = DateTimeOffset.UtcNow;
                return result;
            }

            var foodEntryImports = MapToConnectorFoodEntries(diaryDays, config);
            var count = foodEntryImports.Count;

            if (count > 0)
            {
                if (_connectorPublisher is not { IsAvailable: true })
                {
                    _logger.LogWarning(
                        "Publisher not available for connector food entry submission"
                    );
                    result.Success = false;
                    result.Errors.Add("Publisher not available");
                }
                else
                {
                    var published = await _connectorPublisher.PublishConnectorFoodEntriesAsync(
                        foodEntryImports,
                        ConnectorSource,
                        cancellationToken
                    );
                    if (!published)
                    {
                        result.Success = false;
                        result.Errors.Add("Failed to publish food entries");
                    }
                }
            }

            result.ItemsSynced[SyncDataType.Food] = count;
            _logger.LogInformation(
                "[{ConnectorSource}] Synced {Count} food entries from MyFitnessPal ({From:yyyy-MM-dd} to {To:yyyy-MM-dd})",
                ConnectorSource,
                count,
                from,
                to
            );
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add($"Failed to sync food data: {ex.Message}");
            _logger.LogError(ex, "Failed to sync food data from MyFitnessPal");
        }

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    /// <summary>
    /// Main sync method for background synchronization.
    /// </summary>
    public override async Task<bool> SyncDataAsync(
        MyFitnessPalConnectorConfiguration config,
        CancellationToken cancellationToken = default,
        DateTime? since = null
    )
    {
        _logger.LogInformation(
            "Starting background data sync for {ConnectorSource}",
            ConnectorSource
        );
        try
        {
            if (!await AuthenticateAsync())
            {
                _logger.LogError("Authentication failed for {ConnectorSource}", ConnectorSource);
                return false;
            }

            var from = since ?? DateTime.UtcNow.AddDays(-config.LookbackDays);
            var to = DateTime.UtcNow;

            var request = new SyncRequest
            {
                From = from,
                To = to,
                DataTypes = SupportedDataTypes,
            };

            var result = await SyncDataAsync(request, config, cancellationToken);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Background sync completed successfully for {ConnectorSource}",
                    ConnectorSource
                );
                foreach (var type in result.ItemsSynced.Keys)
                    if (result.ItemsSynced[type] > 0)
                        _logger.LogInformation(
                            "Synced {Count} {Type} items",
                            result.ItemsSynced[type],
                            type
                        );
                return true;
            }

            _logger.LogError(
                "Background sync for {ConnectorSource} failed: {Errors}",
                ConnectorSource,
                string.Join("; ", result.Errors)
            );
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error in background SyncDataAsync for {ConnectorSource}",
                ConnectorSource
            );
            return false;
        }
    }

    /// <summary>
    /// Fetches the diary data from MyFitnessPal using the public diary sharing API.
    /// </summary>
    private async Task<List<MfpDiaryDay>?> FetchDiaryAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken
    )
    {
        return await ExecuteWithRetryAsync(
            async () => await FetchDiaryCoreAsync(from, to, cancellationToken),
            _retryDelayStrategy,
            operationName: "FetchMyFitnessPalDiary",
            cancellationToken: cancellationToken
        );
    }

    private async Task<List<MfpDiaryDay>?> FetchDiaryCoreAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken
    )
    {
        var payload = new
        {
            key = "",
            username = _config.Username,
            from = from.ToString("yyyy-MM-dd"),
            to = to.ToString("yyyy-MM-dd"),
            show_food_diary = 1,
            show_food_notes = 0,
            show_exercise_diary = 0,
            show_exercise_notes = 0,
        };

        var json = JsonSerializer.Serialize(payload);
        var url =
            $"https://www.myfitnesspal.com/api/services/authenticate_diary_key?username={Uri.EscapeDataString(_config.Username)}";

        // Use curl to bypass Cloudflare's TLS fingerprinting which blocks .NET's HttpClient
        var responseBody = await ExecuteCurlPostAsync(url, json, cancellationToken);

        return JsonSerializer.Deserialize<List<MfpDiaryDay>>(responseBody, JsonDefaults.CaseInsensitive);
    }

    /// <summary>
    /// Executes an HTTP POST via curl to avoid Cloudflare TLS fingerprint detection.
    /// .NET's HttpClient uses a TLS stack that Cloudflare identifies as non-browser traffic.
    /// </summary>
    private async Task<string> ExecuteCurlPostAsync(
        string url,
        string jsonPayload,
        CancellationToken cancellationToken
    )
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "curl",
            ArgumentList =
            {
                "-s",
                "-w", "\n%{http_code}",
                "--max-time", "30",
                "-X", "POST",
                "-H", "Content-Type: application/json",
                "-H", "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                "-d", jsonPayload,
                url,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = await outputTask;
        var stderr = await errorTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError(
                "curl request to MyFitnessPal failed with exit code {ExitCode}: {Error}",
                process.ExitCode,
                stderr
            );
            throw new HttpRequestException(
                $"curl request failed (exit code {process.ExitCode}): {stderr}"
            );
        }

        // Parse the appended HTTP status code from -w "\n%{http_code}"
        var lastNewline = stdout.LastIndexOf('\n');
        if (lastNewline < 0)
            throw new HttpRequestException("Unexpected curl response format");

        var statusStr = stdout[(lastNewline + 1)..].Trim();
        var body = stdout[..lastNewline];

        if (int.TryParse(statusStr, out var statusCode) && statusCode is >= 200 and < 300)
            return body;

        _logger.LogError(
            "MyFitnessPal API returned HTTP {StatusCode}: {ResponseBody}",
            statusStr,
            body.Length > 500 ? body[..500] : body
        );
        throw new HttpRequestException(
            $"MyFitnessPal API returned HTTP {statusStr}",
            null,
            (HttpStatusCode)statusCode
        );
    }

    /// <summary>
    /// Maps MFP diary days to connector food entry imports for the meal matching pipeline.
    /// </summary>
    private List<ConnectorFoodEntryImport> MapToConnectorFoodEntries(
        List<MfpDiaryDay> diaryDays,
        MyFitnessPalConnectorConfiguration config
    )
    {
        var imports = new List<ConnectorFoodEntryImport>();

        foreach (var day in diaryDays)
        {
            if (!DateOnly.TryParse(day.Date, out var date))
            {
                _logger.LogWarning("Could not parse date {Date} from MFP diary", day.Date);
                continue;
            }

            foreach (var entry in day.FoodEntries)
            {
                var consumedAt = ResolveConsumedAt(entry, date, config);
                var nutrition = entry.NutritionalContents;
                var food = entry.Food;

                var import = new ConnectorFoodEntryImport
                {
                    ConnectorSource = ConnectorSource,
                    ExternalEntryId = entry.Id,
                    ExternalFoodId = food?.Id ?? string.Empty,
                    ConsumedAt = consumedAt,
                    LoggedAt = TryParseTimestamp(entry.LoggedAt),
                    MealName = entry.MealName,
                    Carbs = nutrition?.Carbohydrates ?? 0,
                    Protein = nutrition?.Protein ?? 0,
                    Fat = nutrition?.Fat ?? 0,
                    Energy = nutrition?.Energy?.Value ?? 0,
                    Servings = entry.Servings,
                    ServingDescription = FormatServingDescription(
                        entry.ServingSize,
                        entry.Servings
                    ),
                    Food =
                        food != null
                            ? new ConnectorFoodImport
                            {
                                ExternalId = food.Id,
                                Name = food.Description,
                                BrandName = food.BrandName,
                                Carbs = food.NutritionalContents?.Carbohydrates ?? 0,
                                Protein = food.NutritionalContents?.Protein ?? 0,
                                Fat = food.NutritionalContents?.Fat ?? 0,
                                Energy = food.NutritionalContents?.Energy?.Value ?? 0,
                                Portion = entry.ServingSize?.Value ?? 1,
                                Unit = entry.ServingSize?.Unit,
                            }
                            : null,
                };

                imports.Add(import);
            }
        }

        return imports;
    }

    /// <summary>
    /// Resolves the consumed-at time for an entry.
    /// MFP entries have a date and meal name but may not have an exact time.
    /// We assign approximate times based on meal position.
    /// </summary>
    private static DateTimeOffset ResolveConsumedAt(
        MfpFoodEntry entry,
        DateOnly date,
        MyFitnessPalConnectorConfiguration config
    )
    {
        if (entry.ConsumedAt != null && DateTimeOffset.TryParse(entry.ConsumedAt, out var parsed))
            return parsed.ToUniversalTime();

        var mealHour = entry.MealName switch
        {
            "Breakfast" => 8,
            "Lunch" => 12,
            "Dinner" => 18,
            "Snacks" => 15,
            _ => 12,
        };

        var dateTime = date.ToDateTime(new TimeOnly(mealHour, 0));
        return new DateTimeOffset(dateTime, TimeSpan.FromHours(config.TimezoneOffset)).ToUniversalTime();
    }

    private static DateTimeOffset? TryParseTimestamp(string? timestamp)
    {
        if (string.IsNullOrEmpty(timestamp))
            return null;

        return DateTimeOffset.TryParse(timestamp, out var result) ? result.ToUniversalTime() : null;
    }

    private static string? FormatServingDescription(MfpServingSize? servingSize, decimal servings)
    {
        if (servingSize == null)
            return null;

        return servings == 1
            ? $"{servingSize.Value} {servingSize.Unit}"
            : $"{servings} x {servingSize.Value} {servingSize.Unit}";
    }
}
