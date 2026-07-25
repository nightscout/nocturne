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
    }

    public static class Headers
    {
        public const string ClientId = "mfp-client-id";
        public const string UserId = "mfp-user-id";
        public const string ClientMetadata = "client-metadata";
    }

    /// <summary>
    ///     The <c>SyncFoodDiaryEntries</c> operation, copied verbatim from the MyFitnessPal Android
    ///     client. Kept byte-identical to the shipped document so it stays valid against the schema
    ///     (introspection is disabled, so a hand-written selection set cannot be checked ahead of
    ///     time) and matches any server-side operation safelist.
    /// </summary>
    public const string SyncFoodDiaryEntriesDocument =
        "query SyncFoodDiaryEntries($input: BatchSyncInput!) { batchSync(input: $input) { foodDiaryEntryConnection: foodDiaryEntries { foodDiaryEntryEdges: edges { foodDiaryEntryNode: node { __typename ...FoodDiaryEntry } foodDiaryEntryEdgeSync: syncEdgeInfo { __typename ...SyncEdgeInfo } } foodDiaryEntryPaging: pageInfo { __typename ...PageInfo } foodDiaryEntrySyncInfo: syncConnectionInfo { __typename ...SyncConnectionInfo } } } }  fragment FoodDiaryEntry on FoodDiaryEntry { __typename id createdAt ... on ActiveFoodDiaryEntry { date consumedAt eatingOccasion eatingOccasionSlot loggedAt quantity servingSize { amount nutritionMultiplier isFraction unit } food { __typename ... on IndividualFood { id description brand isVerified grams note createdAt nutrientSet { calories protein carbs fat fiber sugar sugarAlcohols saturatedFat sodium } servingSizes { amount nutritionMultiplier isFraction unit } } ... on MealIngredient { id description brand isVerified grams mealFoodId mealIngredientId createdAt nutrientSet { calories protein carbs fat fiber sugar sugarAlcohols saturatedFat sodium } servingSizes { amount nutritionMultiplier isFraction unit } } } consumedNutrientSet { calories protein carbs fat fiber sugar sugarAlcohols saturatedFat sodium } } }  fragment SyncEdgeInfo on SyncEdgeInfo { operation lastModifiedAt }  fragment PageInfo on PageInfo { hasPreviousPage hasNextPage startCursor endCursor }  fragment SyncConnectionInfo on SyncConnectionInfo { startSyncCursor endSyncCursor totalEdges }";

    public const string SyncFoodDiaryEntriesOperationName = "SyncFoodDiaryEntries";

    /// <summary>
    ///     Edges requested per page. The Android client uses 10; a larger page keeps the initial
    ///     full-history walk to a reasonable number of round trips.
    /// </summary>
    public const int PageSize = 100;

    /// <summary>
    ///     Upper bound on pages fetched in a single sync, so a corrupt cursor cannot loop forever.
    /// </summary>
    public const int MaxPagesPerSync = 200;
}
