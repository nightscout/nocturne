namespace Nocturne.Connectors.MyFitnessPal.Configurations;

public static class MyFitnessPalConstants
{
    /// <summary>
    ///     Mobile OAuth client id. The web client id is rejected by the token endpoint.
    /// </summary>
    public const string ClientId = "mfp-mobile-android-google";

    /// <summary>
    ///     Sent as the <c>client-metadata</c> header, base64 encoded, alongside the bearer token.
    /// </summary>
    public const string AppVersion = "26.27.0";

    /// <summary>
    ///     Fallback when the token endpoint omits <c>expires_in</c>. It normally returns 30 days.
    /// </summary>
    public static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromDays(30);

    public static class Servers
    {
        /// <summary>OAuth token endpoint host. Reachable where <c>www</c> is Cloudflare-blocked.</summary>
        public const string Auth = "https://api.myfitnesspal.com";

        /// <summary>Apex host serving the Apollo "query-envoy" GraphQL API.</summary>
        public const string GraphQl = "https://myfitnesspal.com";
    }

    public static class Endpoints
    {
        public const string Token = "/v2/oauth2/token";
        public const string GraphQl = "/v2/query-envoy/graphql";

        /// <summary>
        ///     Legacy per-day diary. Returns meal-level totals only, which is the sole place
        ///     production exposes which meal an entry belongs to.
        /// </summary>
        public const string Diary = "/v2/diary";
    }

    public static class Headers
    {
        public const string ClientId = "mfp-client-id";
        public const string UserId = "mfp-user-id";
        public const string ClientMetadata = "client-metadata";
    }

    /// <summary>
    ///     The <c>SyncFoodDiaryEntries</c> operation, validated field by field against the live
    ///     production graph.
    /// </summary>
    /// <remarks>
    ///     The Android client's own document does not work here: it is generated against the
    ///     preprod schema and production rejects <c>eatingOccasion</c>, <c>eatingOccasionSlot</c>,
    ///     <c>grams</c> and <c>note</c>, and names the carbohydrate field
    ///     <c>totalCarbohydrates</c> rather than <c>carbs</c>. Introspection is disabled, so any
    ///     change here has to be checked against the live endpoint, which does report every
    ///     unknown field in one response.
    /// </remarks>
    public const string SyncFoodDiaryEntriesDocument =
        "query SyncFoodDiaryEntries($input: BatchSyncInput!) { batchSync(input: $input) { foodDiaryEntries { edges { node { __typename id createdAt ... on ActiveFoodDiaryEntry { date consumedAt loggedAt quantity servingSize { amount nutritionMultiplier isFraction unit } consumedNutrientSet { calories protein totalCarbohydrates fat fiber sugar sugarAlcohols saturatedFat sodium } food { __typename ... on IndividualFood { id description brand isVerified createdAt nutrientSet { calories protein totalCarbohydrates fat fiber sugar sugarAlcohols saturatedFat sodium } servingSizes { amount nutritionMultiplier isFraction unit } } ... on MealIngredient { id description brand isVerified createdAt nutrientSet { calories protein totalCarbohydrates fat fiber sugar sugarAlcohols saturatedFat sodium } servingSizes { amount nutritionMultiplier isFraction unit } mealFoodId mealIngredientId } } } } syncEdgeInfo { operation lastModifiedAt } } pageInfo { hasPreviousPage hasNextPage startCursor endCursor } syncConnectionInfo { startSyncCursor endSyncCursor totalEdges } } } }";

    public const string SyncFoodDiaryEntriesOperationName = "SyncFoodDiaryEntries";

    /// <summary>
    ///     Edges requested per page. The server caps this at 100; larger values are silently
    ///     clamped, so ask for the maximum to keep the initial history walk short.
    /// </summary>
    public const int PageSize = 100;

    /// <summary>
    ///     Upper bound on pages fetched in a single sync, so a corrupt cursor cannot loop forever.
    /// </summary>
    public const int MaxPagesPerSync = 200;

    /// <summary>
    ///     How often the lookahead is set aside and the diary is read all the way back to its first
    ///     entry. Only such a read establishes that an entry was deleted rather than merely not
    ///     seen; it costs one request per hundred entries of history.
    /// </summary>
    public static readonly TimeSpan FullWalkInterval = TimeSpan.FromDays(1);

    /// <summary>
    ///     How many consecutive pages lying entirely before the window are read before the walk
    ///     gives up. Pages are ordered by modification rather than diary date, so recently edited
    ///     old entries can sit ahead of the window; this reads past a block of them without
    ///     walking the whole diary when the user simply has not logged anything lately.
    /// </summary>
    public const int PreWindowPageLookahead = 3;

    /// <summary>
    ///     Meal names cost one legacy diary request per day, so a sync names at most this many days.
    ///     A window holding more — a long <c>LookbackDays</c>, or a first full-history sync — has its
    ///     most recent days named and the remainder imported unnamed, rather than issuing hundreds of
    ///     requests or giving up on the whole window.
    /// </summary>
    public const int MaxDiaryDaysPerSync = 60;
}
