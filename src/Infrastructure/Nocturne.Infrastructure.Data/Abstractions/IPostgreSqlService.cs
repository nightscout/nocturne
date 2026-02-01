using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Abstractions;

/// <summary>
/// Statistics for a data source's data in the database (entries + treatments)
/// </summary>
public record DataSourceStats(
    string DataSource,
    long TotalEntries,
    int EntriesLast24Hours,
    DateTime? LastEntryTime,
    DateTime? FirstEntryTime,
    long TotalTreatments,
    int TreatmentsLast24Hours,
    DateTime? LastTreatmentTime,
    DateTime? FirstTreatmentTime
)
{
    /// <summary>
    /// Total items (entries + treatments) from this data source
    /// </summary>
    public long TotalItems => TotalEntries + TotalTreatments;

    /// <summary>
    /// Total items in the last 24 hours
    /// </summary>
    public int ItemsLast24Hours => EntriesLast24Hours + TreatmentsLast24Hours;

    /// <summary>
    /// Most recent item time (entry or treatment)
    /// </summary>
    public DateTime? LastItemTime => LastEntryTime > LastTreatmentTime ? LastEntryTime : LastTreatmentTime ?? LastEntryTime;
};

/// <summary>
/// Interface for database operations - PostgreSQL implementation
/// Compatible with IDataService for drop-in replacement
/// </summary>
public interface IPostgreSqlService
{
    /// <summary>
    /// Test the database connection
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if connection is successful, false otherwise</returns>
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the most recent entry
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The current entry, or null if no entries exist</returns>
    Task<Entry?> GetCurrentEntryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the latest entry timestamp for a specific data source
    /// </summary>
    /// <param name="dataSource">The data source name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The latest timestamp, or null if no entries exist</returns>
    Task<DateTime?> GetLatestEntryTimestampBySourceAsync(string dataSource, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the oldest entry timestamp for a specific data source
    /// </summary>
    /// <param name="dataSource">The data source name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The oldest timestamp, or null if no entries exist</returns>
    Task<DateTime?> GetOldestEntryTimestampBySourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get statistics for entries from a specific data source
    /// </summary>
    /// <param name="dataSource">The data source name (e.g., "glooko-connector")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Statistics including total count, last 24h count, and timestamps</returns>
    Task<DataSourceStats> GetEntryStatsBySourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get an entry by its ID
    /// </summary>
    /// <param name="id">The entry ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The entry if found, null otherwise</returns>
    Task<Entry?> GetEntryByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get entries with optional filtering and pagination
    /// </summary>
    /// <param name="type">Optional entry type filter</param>
    /// <param name="count">Maximum number of entries to return</param>
    /// <param name="skip">Number of entries to skip</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of entries</returns>
    Task<IEnumerable<Entry>> GetEntriesAsync(
        string? type = null,
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get entries with advanced filtering options
    /// </summary>
    /// <param name="type">Optional entry type filter</param>
    /// <param name="count">Maximum number of entries to return</param>
    /// <param name="skip">Number of entries to skip</param>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="dateString">Optional date filter</param>
    /// <param name="reverseResults">Whether to reverse the order of results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of entries matching the filter</returns>
    Task<IEnumerable<Entry>> GetEntriesWithAdvancedFilterAsync(
        string? type = null,
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        string? dateString = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Check for duplicate entries in the database within a time window
    /// </summary>
    /// <param name="device">Device identifier</param>
    /// <param name="type">Entry type (e.g., "sgv", "mbg", "cal")</param>
    /// <param name="sgv">Sensor glucose value in mg/dL</param>
    /// <param name="mills">Timestamp in milliseconds since Unix epoch</param>
    /// <param name="windowMinutes">Time window in minutes to check for duplicates (default: 5)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Existing entry if duplicate found, null otherwise</returns>
    Task<Entry?> CheckForDuplicateEntryAsync(
        string? device,
        string type,
        double? sgv,
        long mills,
        int windowMinutes = 5,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create a single entry
    /// </summary>
    /// <param name="entry">The entry to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created entry, or null if creation failed</returns>
    Task<Entry?> CreateEntryAsync(Entry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create multiple entries
    /// </summary>
    /// <param name="entries">The entries to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of created entries</returns>
    Task<IEnumerable<Entry>> CreateEntriesAsync(
        IEnumerable<Entry> entries,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update an entry by ID
    /// </summary>
    /// <param name="id">The entry ID</param>
    /// <param name="entry">The updated entry data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated entry, or null if not found</returns>
    Task<Entry?> UpdateEntryAsync(
        string id,
        Entry entry,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete an entry by ID
    /// </summary>
    /// <param name="id">The entry ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteEntryAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete all entries with the specified data source
    /// </summary>
    /// <param name="dataSource">The data source to filter by (e.g., "demo-service", "dexcom-connector")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of entries deleted</returns>
    Task<long> DeleteEntriesByDataSourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Bulk delete entries using query filters
    /// </summary>
    /// <param name="findQuery">Query filter for entries to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of entries deleted</returns>
    Task<long> BulkDeleteEntriesAsync(
        string findQuery,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get a treatment by its ID
    /// </summary>
    /// <param name="id">The treatment ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The treatment if found, null otherwise</returns>
    Task<Treatment?> GetTreatmentByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get the latest treatment timestamp for a specific data source
    /// </summary>
    /// <param name="dataSource">The data source name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The latest timestamp, or null if no treatments exist</returns>
    Task<DateTime?> GetLatestTreatmentTimestampBySourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get the oldest treatment timestamp for a specific data source
    /// </summary>
    /// <param name="dataSource">The data source name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The oldest timestamp, or null if no treatments exist</returns>
    Task<DateTime?> GetOldestTreatmentTimestampBySourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Check for duplicate treatment in the database by ID or OriginalId
    /// </summary>
    /// <param name="id">Treatment ID (GUID)</param>
    /// <param name="originalId">Original treatment ID (MongoDB ObjectId)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Existing treatment if duplicate found, null otherwise</returns>
    Task<Treatment?> CheckForDuplicateTreatmentAsync(
        string? id,
        string? originalId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get treatments with pagination
    /// </summary>
    /// <param name="count">Maximum number of treatments to return</param>
    /// <param name="skip">Number of treatments to skip</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of treatments</returns>
    Task<IEnumerable<Treatment>> GetTreatmentsAsync(
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get treatments within a time range (by mills)
    /// </summary>
    /// <param name="startMills">Start time in milliseconds since Unix epoch</param>
    /// <param name="endMills">End time in milliseconds since Unix epoch</param>
    /// <param name="count">Maximum number of treatments to return</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of treatments in the time range</returns>
    Task<IEnumerable<Treatment>> GetTreatmentsByTimeRangeAsync(
        long startMills,
        long endMills,
        int count = 10000,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get treatments with advanced filtering options
    /// </summary>
    /// <param name="count">Maximum number of treatments to return</param>
    /// <param name="skip">Number of treatments to skip</param>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="reverseResults">Whether to reverse the order of results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of treatments matching the filter</returns>
    Task<IEnumerable<Treatment>> GetTreatmentsWithAdvancedFilterAsync(
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create a single treatment
    /// </summary>
    /// <param name="treatment">The treatment to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created treatment, or null if creation failed</returns>
    Task<Treatment?> CreateTreatmentAsync(
        Treatment treatment,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create multiple treatments
    /// </summary>
    /// <param name="treatments">The treatments to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of created treatments</returns>
    Task<IEnumerable<Treatment>> CreateTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update a treatment by ID
    /// </summary>
    /// <param name="id">The treatment ID</param>
    /// <param name="treatment">The updated treatment data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated treatment, or null if not found</returns>
    Task<Treatment?> UpdateTreatmentAsync(
        string id,
        Treatment treatment,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a treatment by ID
    /// </summary>
    /// <param name="id">The treatment ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteTreatmentAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk delete treatments using query filters
    /// </summary>
    /// <param name="findQuery">Query filter for treatments to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of treatments deleted</returns>
    Task<long> BulkDeleteTreatmentsAsync(
        string findQuery,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete all treatments with the specified data source
    /// </summary>
    /// <param name="dataSource">The data source to filter by (e.g., "demo-service", "dexcom-connector")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of treatments deleted</returns>
    Task<long> DeleteTreatmentsByDataSourceAsync(
        string dataSource,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get the current active profile
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The current profile, or null if no profiles exist</returns>
    Task<Profile?> GetCurrentProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the profile active at a specific timestamp
    /// </summary>
    /// <param name="timestamp">Unix timestamp in milliseconds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The profile active at the timestamp if found, null otherwise</returns>
    Task<Profile?> GetProfileAtTimestampAsync(
        long timestamp,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get a profile by its ID
    /// </summary>
    /// <param name="id">The profile ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The profile if found, null otherwise</returns>
    Task<Profile?> GetProfileByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get profiles with pagination
    /// </summary>
    /// <param name="count">Maximum number of profiles to return</param>
    /// <param name="skip">Number of profiles to skip</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of profiles</returns>
    Task<IEnumerable<Profile>> GetProfilesAsync(
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get profiles with advanced filtering options
    /// </summary>
    /// <param name="count">Maximum number of profiles to return</param>
    /// <param name="skip">Number of profiles to skip</param>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="reverseResults">Whether to reverse the order of results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of profiles matching the filter</returns>
    Task<IEnumerable<Profile>> GetProfilesWithAdvancedFilterAsync(
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create multiple profiles
    /// </summary>
    /// <param name="profiles">The profiles to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of created profiles</returns>
    Task<IEnumerable<Profile>> CreateProfilesAsync(
        IEnumerable<Profile> profiles,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update a profile by ID
    /// </summary>
    /// <param name="id">The profile ID</param>
    /// <param name="profile">The updated profile data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated profile, or null if not found</returns>
    Task<Profile?> UpdateProfileAsync(
        string id,
        Profile profile,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a profile by ID
    /// </summary>
    /// <param name="id">The profile ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteProfileAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all food entries
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of all food entries</returns>
    Task<IEnumerable<Food>> GetFoodAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a food entry by its ID
    /// </summary>
    /// <param name="id">The food ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The food entry if found, null otherwise</returns>
    Task<Food?> GetFoodByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get food entries by type
    /// </summary>
    /// <param name="type">The food type to filter by</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of food entries of the specified type</returns>
    Task<IEnumerable<Food>> GetFoodByTypeAsync(
        string type,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get food entries with advanced filtering options
    /// </summary>
    /// <param name="count">Maximum number of food entries to return</param>
    /// <param name="skip">Number of food entries to skip</param>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="reverseResults">Whether to reverse the order of results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of food entries matching the filter</returns>
    Task<IEnumerable<Food>> GetFoodWithAdvancedFilterAsync(
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get food entries with advanced filtering options including type filter
    /// </summary>
    /// <param name="count">Maximum number of food entries to return</param>
    /// <param name="skip">Number of food entries to skip</param>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="type">Optional food type filter</param>
    /// <param name="reverseResults">Whether to reverse the order of results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of food entries matching the filter</returns>
    Task<IEnumerable<Food>> GetFoodWithAdvancedFilterAsync(
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        string? type = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create multiple food entries
    /// </summary>
    /// <param name="foods">The food entries to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of created food entries</returns>
    Task<IEnumerable<Food>> CreateFoodAsync(
        IEnumerable<Food> foods,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update a food entry by ID
    /// </summary>
    /// <param name="id">The food ID</param>
    /// <param name="food">The updated food data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated food entry, or null if not found</returns>
    Task<Food?> UpdateFoodAsync(
        string id,
        Food food,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a food entry by ID
    /// </summary>
    /// <param name="id">The food ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteFoodAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk delete food entries using query filters
    /// </summary>
    /// <param name="findQuery">Query filter for food entries to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of food entries deleted</returns>
    Task<long> BulkDeleteFoodAsync(string findQuery, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count food entries with optional filtering
    /// </summary>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of food entries matching the filter</returns>
    Task<long> CountFoodAsync(
        string? findQuery = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Count food entries with optional filtering including type filter
    /// </summary>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="type">Optional food type filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of food entries matching the filter</returns>
    Task<long> CountFoodAsync(
        string? findQuery = null,
        string? type = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Count entries with optional filtering
    /// </summary>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="type">Optional entry type filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of entries matching the filter</returns>
    Task<long> CountEntriesAsync(
        string? findQuery = null,
        string? type = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Count treatments with optional filtering
    /// </summary>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of treatments matching the filter</returns>
    Task<long> CountTreatmentsAsync(
        string? findQuery = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Count profiles with optional filtering
    /// </summary>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of profiles matching the filter</returns>
    Task<long> CountProfilesAsync(
        string? findQuery = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get activities with pagination
    /// </summary>
    /// <param name="count">Maximum number of activities to return</param>
    /// <param name="skip">Number of activities to skip</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of activities</returns>
    Task<IEnumerable<Activity>> GetActivityAsync(
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get activities with pagination (alternative method)
    /// </summary>
    /// <param name="count">Maximum number of activities to return</param>
    /// <param name="skip">Number of activities to skip</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of activities</returns>
    Task<IEnumerable<Activity>> GetActivitiesAsync(
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get an activity by its ID
    /// </summary>
    /// <param name="id">The activity ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The activity if found, null otherwise</returns>
    Task<Activity?> GetActivityByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get activities with advanced filtering options
    /// </summary>
    /// <param name="count">Maximum number of activities to return</param>
    /// <param name="skip">Number of activities to skip</param>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="reverseResults">Whether to reverse the order of results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of activities matching the filter</returns>
    Task<IEnumerable<Activity>> GetActivityWithAdvancedFilterAsync(
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create multiple activities
    /// </summary>
    /// <param name="activities">The activities to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of created activities</returns>
    Task<IEnumerable<Activity>> CreateActivityAsync(
        IEnumerable<Activity> activities,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create multiple activities (alternative method)
    /// </summary>
    /// <param name="activities">The activities to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of created activities</returns>
    Task<IEnumerable<Activity>> CreateActivitiesAsync(
        IEnumerable<Activity> activities,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update an activity by ID
    /// </summary>
    /// <param name="id">The activity ID</param>
    /// <param name="activity">The updated activity data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated activity, or null if not found</returns>
    Task<Activity?> UpdateActivityAsync(
        string id,
        Activity activity,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete an activity by ID
    /// </summary>
    /// <param name="id">The activity ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteActivityAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count activities with optional filtering
    /// </summary>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of activities matching the filter</returns>
    Task<long> CountActivitiesAsync(
        string? findQuery = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get device status entries with pagination
    /// </summary>
    /// <param name="count">Maximum number of device status entries to return</param>
    /// <param name="skip">Number of device status entries to skip</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of device status entries</returns>
    Task<IEnumerable<DeviceStatus>> GetDeviceStatusAsync(
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get a device status entry by its ID
    /// </summary>
    /// <param name="id">The device status ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The device status entry if found, null otherwise</returns>
    Task<DeviceStatus?> GetDeviceStatusByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get device status entries with advanced filtering options
    /// </summary>
    /// <param name="count">Maximum number of device status entries to return</param>
    /// <param name="skip">Number of device status entries to skip</param>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="reverseResults">Whether to reverse the order of results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of device status entries matching the filter</returns>
    Task<IEnumerable<DeviceStatus>> GetDeviceStatusWithAdvancedFilterAsync(
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create multiple device status entries
    /// </summary>
    /// <param name="deviceStatuses">The device status entries to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of created device status entries</returns>
    Task<IEnumerable<DeviceStatus>> CreateDeviceStatusAsync(
        IEnumerable<DeviceStatus> deviceStatuses,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update a device status entry by ID
    /// </summary>
    /// <param name="id">The device status ID</param>
    /// <param name="deviceStatus">The updated device status data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated device status entry, or null if not found</returns>
    Task<DeviceStatus?> UpdateDeviceStatusAsync(
        string id,
        DeviceStatus deviceStatus,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a device status entry by ID
    /// </summary>
    /// <param name="id">The device status ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteDeviceStatusAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk delete device status entries using query filters
    /// </summary>
    /// <param name="findQuery">Query filter for device status entries to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of device status entries deleted</returns>
    Task<long> BulkDeleteDeviceStatusAsync(
        string findQuery,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Count device status entries with optional filtering
    /// </summary>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of device status entries matching the filter</returns>
    Task<long> CountDeviceStatusAsync(
        string? findQuery = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get all settings entries
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of all settings entries</returns>
    Task<IEnumerable<Settings>> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get settings entries with advanced filtering options
    /// </summary>
    /// <param name="count">Maximum number of settings entries to return</param>
    /// <param name="skip">Number of settings entries to skip</param>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="reverseResults">Whether to reverse the order of results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of settings entries matching the filter</returns>
    Task<IEnumerable<Settings>> GetSettingsWithAdvancedFilterAsync(
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get a settings entry by its ID
    /// </summary>
    /// <param name="id">The settings ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The settings entry if found, null otherwise</returns>
    Task<Settings?> GetSettingsByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a settings entry by its key
    /// </summary>
    /// <param name="key">The settings key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The settings entry if found, null otherwise</returns>
    Task<Settings?> GetSettingsByKeyAsync(
        string key,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create multiple settings entries
    /// </summary>
    /// <param name="settings">The settings entries to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of created settings entries</returns>
    Task<IEnumerable<Settings>> CreateSettingsAsync(
        IEnumerable<Settings> settings,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update a settings entry by ID
    /// </summary>
    /// <param name="id">The settings ID</param>
    /// <param name="settings">The updated settings data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated settings entry, or null if not found</returns>
    Task<Settings?> UpdateSettingsAsync(
        string id,
        Settings settings,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a settings entry by ID
    /// </summary>
    /// <param name="id">The settings ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteSettingsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk delete settings entries using query filters
    /// </summary>
    /// <param name="findQuery">Query filter for settings entries to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of settings entries deleted</returns>
    Task<long> BulkDeleteSettingsAsync(
        string findQuery,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Count settings entries with optional filtering
    /// </summary>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of settings entries matching the filter</returns>
    Task<long> CountSettingsAsync(
        string? findQuery = null,
        CancellationToken cancellationToken = default
    );


}
