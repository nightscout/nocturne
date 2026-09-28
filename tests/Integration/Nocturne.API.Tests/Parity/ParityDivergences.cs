using System.Text.Json;

namespace Nocturne.API.Tests.Integration.Parity;

/// <summary>
/// The requests on which Nocturne deliberately answers differently from Nightscout 15.0.3, each
/// with its reason. <see cref="ParityTestBase"/> does not compare these with Nightscout; it asserts
/// the status and body kind Nocturne answers with instead, so they stay tested.
/// </summary>
/// <remarks>
/// Three kinds: Nightscout has no such route (A), Nightscout fails with a 500 (B), and Nocturne
/// accepts or validates input Nightscout treats otherwise, which is out of scope for #1746 (C).
/// A difference that is a Nocturne bug belongs in <c>tests/quarantine.txt</c>, not here.
/// </remarks>
public static class ParityDivergences
{
    private const string NoNightscoutRoute =
        "(A) Nightscout 15.0.3 has no such route (404); Nocturne serves it.";

    private const string NoV3Bulk =
        "(A) Nightscout 15.0.3's v3 has no bulk create (404).";

    private const string ReadableByWorld =
        "(A) Nightscout's AUTH_DEFAULT_ROLES=readable raises a 'readable by world' admin notice; a Nocturne tenant host serves no anonymous reads, so it has none.";

    private const string CountCrashes =
        "(B) Nightscout 15.0.3 answers 500 (Converting circular structure to JSON) for every count query.";

    private const string DateStringCrash =
        "(B) Nightscout 15.0.3 answers 500 (dateString.replace is not a function) here.";

    private const string SliceMongoError =
        "(B) Nightscout 15.0.3 answers 500 (Mongo BadValue) for this slice.";

    private const string UndefinedPropertyCrash =
        "(B) Nightscout 15.0.3 answers 500 (Cannot read properties of undefined) here.";

    private const string V3Tolerant =
        "(C) Nightscout rejects the payload (missing app/date/body); Nocturne accepts it. Out of scope for #1746, behaviour unchanged; strict v3 validation to be decided separately.";

    private const string V3TolerantSkip =
        "(C) Nightscout rejects an out-of-tolerance skip (400); Nocturne tolerates it. Out of scope for #1746, behaviour unchanged.";

    private const string V3RejectedSeed =
        "(C) the v3 profiles seeded here are ones Nightscout rejects (no date field), so Nightscout has none to find. Out of scope for #1746, behaviour unchanged.";

    private const string SummaryHoursValidated =
        "(C) Nocturne rejects an invalid hours value (400) where Nightscout answers an empty summary. Out of scope for #1746, behaviour unchanged.";

    /// <summary>A divergence: the test and request it applies to, and what Nocturne answers.</summary>
    /// <param name="Test">The test, as <c>{version}.{class}.{method}</c>.</param>
    /// <param name="Request">
    /// The method and path, without the query. A trailing <c>*</c> matches any rest of the path, for
    /// a path carrying today's date or a timestamp.
    /// </param>
    /// <param name="Status">The status Nocturne answers with.</param>
    /// <param name="Body">The JSON kind of Nocturne's body.</param>
    /// <param name="Reason">Why Nocturne does not match Nightscout here.</param>
    public sealed record Divergence(string Test, string Request, int Status, JsonValueKind Body, string Reason);

    public static readonly IReadOnlyList<Divergence> All =
    [
        new("V1.CountParityTests.CountActivity_Empty_ReturnsSameShape", "GET /api/v1/count/activity/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountDeviceStatus_Empty_ReturnsSameShape", "GET /api/v1/count/devicestatus/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountDeviceStatus_WithData_ReturnsSameShape", "GET /api/v1/count/devicestatus/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountDeviceStatus_WithDeviceFilter_ReturnsSameShape", "GET /api/v1/count/devicestatus/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountEntries_Empty_ReturnsSameShape", "GET /api/v1/count/entries/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountEntries_InvalidFilter_ReturnsSameShape", "GET /api/v1/count/entries/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountEntries_WithData_ReturnsSameShape", "GET /api/v1/count/entries/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountEntries_WithDateFilter_ReturnsSameShape", "GET /api/v1/count/entries/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountEntries_WithSgvFilter_ReturnsSameShape", "GET /api/v1/count/entries/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountEntries_WithTypeFilter_ReturnsSameShape", "GET /api/v1/count/entries/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountGeneric_Empty_ReturnsSameShape", "GET /api/v1/count/devicestatus/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountGeneric_Empty_ReturnsSameShape", "GET /api/v1/count/entries/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountGeneric_Empty_ReturnsSameShape", "GET /api/v1/count/food/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountGeneric_Empty_ReturnsSameShape", "GET /api/v1/count/profile/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountGeneric_Empty_ReturnsSameShape", "GET /api/v1/count/treatments/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountGeneric_InvalidStorage_ReturnsSameShape", "GET /api/v1/count/nonexistent/where", 400, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountTreatments_Empty_ReturnsSameShape", "GET /api/v1/count/treatments/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountTreatments_WithData_ReturnsSameShape", "GET /api/v1/count/treatments/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.CountParityTests.CountTreatments_WithEventTypeFilter_ReturnsSameShape", "GET /api/v1/count/treatments/where", 200, JsonValueKind.Object, CountCrashes),
        new("V1.FoodParityTests.DeleteFood_ByCategory_ReturnsSameShape", "DELETE /api/v1/food", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.IobParityTests.GetIobHourly_Empty_ReturnsSameShape", "GET /api/v1/iob/hourly", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.IobParityTests.GetIobHourly_With24Hours_ReturnsSameShape", "GET /api/v1/iob/hourly", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.IobParityTests.GetIobHourly_WithData_ReturnsSameShape", "GET /api/v1/iob/hourly", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.IobParityTests.GetIobHourly_WithHours_ReturnsSameShape", "GET /api/v1/iob/hourly", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.IobParityTests.GetIobTreatments_Empty_ReturnsSameShape", "GET /api/v1/iob/treatments", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.IobParityTests.GetIobTreatments_WithCount_ReturnsSameShape", "GET /api/v1/iob/treatments", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.IobParityTests.GetIobTreatments_WithData_ReturnsSameShape", "GET /api/v1/iob/treatments", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.IobParityTests.GetIob_Empty_ReturnsSameShape", "GET /api/v1/iob", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.IobParityTests.GetIob_NoProfile_ReturnsSameShape", "GET /api/v1/iob", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.IobParityTests.GetIob_WithCount_ReturnsSameShape", "GET /api/v1/iob", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.IobParityTests.GetIob_WithData_ReturnsSameShape", "GET /api/v1/iob", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.IobParityTests.GetIob_ZeroInsulin_ReturnsSameShape", "GET /api/v1/iob", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.NotificationsParityTests.DeleteAdminNotifies_ByGroup_ReturnsSameShape", "DELETE /api/v1/adminnotifies", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.NotificationsParityTests.GetAdminNotifies_Empty_ReturnsSameShape", "GET /api/v1/adminnotifies", 200, JsonValueKind.Object, ReadableByWorld),
        new("V1.NotificationsParityTests.GetAdminNotifies_WithData_ReturnsSameShape", "GET /api/v1/adminnotifies", 200, JsonValueKind.Object, ReadableByWorld),
        new("V1.NotificationsParityTests.PostAdminNotify_Empty_ReturnsSameShape", "POST /api/v1/adminnotifies", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.NotificationsParityTests.PostAdminNotify_MissingTitle_ReturnsSameShape", "POST /api/v1/adminnotifies", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.NotificationsParityTests.PostAdminNotify_Persistent_ReturnsSameShape", "POST /api/v1/adminnotifies", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.NotificationsParityTests.PostAdminNotify_Simple_ReturnsSameShape", "POST /api/v1/adminnotifies", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.NotificationsParityTests.PostAdminNotify_WithGroup_ReturnsSameShape", "POST /api/v1/adminnotifies", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.NotificationsParityTests.PostNotificationAck_LevelZero_ReturnsSameShape", "POST /api/v1/notifications/ack", 400, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.NotificationsParityTests.PostNotificationAck_MissingLevel_ReturnsSameShape", "POST /api/v1/notifications/ack", 400, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.NotificationsParityTests.PostNotificationAck_Simple_ReturnsSameShape", "POST /api/v1/notifications/ack", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.NotificationsParityTests.PostNotificationAck_WithAllFields_ReturnsSameShape", "POST /api/v1/notifications/ack", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V1.NotificationsParityTests.PostPushoverCallback_Valid_ReturnsSameShape", "POST /api/v1/notifications/pushovercallback", 200, JsonValueKind.Object, UndefinedPropertyCrash),
        new("V1.ProfileParityTests.GetProfile_NonExistentId_ReturnsSameShape", "GET /api/v1/profile/nonexistent123", 200, JsonValueKind.Array, NoNightscoutRoute),
        new("V1.TimeQueryParityTests.GetSliceWithPrefix_ReturnsSameShape", "GET /api/v1/slice/entries/date/sgv/*", 200, JsonValueKind.Array, DateStringCrash),
        new("V1.TimeQueryParityTests.GetSliceWithRegex_ReturnsSameShape", "GET /api/v1/slice/entries/date/sgv/*", 200, JsonValueKind.Array, DateStringCrash),
        new("V1.TimeQueryParityTests.GetSliceWithType_EntriesDateMbg_ReturnsSameShape", "GET /api/v1/slice/entries/date/mbg", 200, JsonValueKind.Array, DateStringCrash),
        new("V1.TimeQueryParityTests.GetSliceWithType_EntriesDateSgv_ReturnsSameShape", "GET /api/v1/slice/entries/date/sgv", 200, JsonValueKind.Array, DateStringCrash),
        new("V1.TimeQueryParityTests.GetSlice_EntriesDate_ReturnsSameShape", "GET /api/v1/slice/entries/date", 200, JsonValueKind.Array, DateStringCrash),
        new("V1.TimeQueryParityTests.GetSlice_EntriesSgv_ReturnsSameShape", "GET /api/v1/slice/entries/sgv", 200, JsonValueKind.Array, SliceMongoError),
        new("V1.TimeQueryParityTests.GetSlice_InvalidStorage_ReturnsSameShape", "GET /api/v1/slice/nonexistent/date", 400, JsonValueKind.String, DateStringCrash),
        new("V1.TimeQueryParityTests.GetSlice_TreatmentsCreatedAt_ReturnsSameShape", "GET /api/v1/slice/treatments/created_at", 200, JsonValueKind.Array, DateStringCrash),
        new("V1.TimeQueryParityTests.GetSlice_WithCount_ReturnsSameShape", "GET /api/v1/slice/entries/date", 200, JsonValueKind.Array, DateStringCrash),
        new("V2.DDataParityTests.GetDDataAt_FutureTimestamp_ReturnsSameShape", "GET /api/v2/ddata/at/*", 400, JsonValueKind.Object, DateStringCrash),
        new("V2.DDataParityTests.GetDDataAt_InvalidTimestamp_ReturnsSameShape", "GET /api/v2/ddata/at/invalid", 400, JsonValueKind.Object, DateStringCrash),
        new("V2.DDataParityTests.GetDDataAt_UnixTimestamp_ReturnsSameShape", "GET /api/v2/ddata/at/*", 200, JsonValueKind.Object, DateStringCrash),
        new("V2.DDataParityTests.GetDData_Empty_ReturnsSameShape", "GET /api/v2/ddata", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.DDataParityTests.GetDData_WithAllDataTypes_ReturnsSameShape", "GET /api/v2/ddata", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.DDataParityTests.GetDData_WithDeviceStatus_ReturnsSameShape", "GET /api/v2/ddata", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.DDataParityTests.GetDData_WithEntries_ReturnsSameShape", "GET /api/v2/ddata", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.DDataParityTests.GetDData_WithProfile_ReturnsSameShape", "GET /api/v2/ddata", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.DDataParityTests.GetDData_WithTreatments_ReturnsSameShape", "GET /api/v2/ddata", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.DDataParityTests.GetRawDData_Empty_ReturnsSameShape", "GET /api/v2/ddata/raw", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.DDataParityTests.GetRawDData_WithData_ReturnsSameShape", "GET /api/v2/ddata/raw", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.DDataParityTests.GetRawDData_WithTimestamp_ReturnsSameShape", "GET /api/v2/ddata/raw", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.LoopParityTests.GetLoopStatus_ReturnsSameShape", "GET /api/v2/loop/status", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.LoopParityTests.PostLoopSend_Complete_ReturnsSameShape", "POST /api/v2/loop/send", 400, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.LoopParityTests.PostLoopSend_Empty_ReturnsSameShape", "POST /api/v2/loop/send", 400, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.LoopParityTests.PostLoopSend_InvalidDeviceToken_ReturnsSameShape", "POST /api/v2/loop/send", 400, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.LoopParityTests.PostLoopSend_MissingData_ReturnsSameShape", "POST /api/v2/loop/send", 400, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.LoopParityTests.PostLoopSend_MissingLoopSettings_ReturnsSameShape", "POST /api/v2/loop/send", 400, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.LoopParityTests.PostLoopSend_WithSoundOptions_ReturnsSameShape", "POST /api/v2/loop/send", 400, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.NotificationsParityTests.GetNotificationStatus_ReturnsSameShape", "GET /api/v2/notifications/status", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.NotificationsParityTests.PostLoopNotification_Empty_ReturnsSameShape", "POST /api/v2/notifications/loop", 400, JsonValueKind.Object, UndefinedPropertyCrash),
        new("V2.NotificationsParityTests.PostLoopNotification_Simple_ReturnsSameShape", "POST /api/v2/notifications/loop", 200, JsonValueKind.Object, UndefinedPropertyCrash),
        new("V2.NotificationsParityTests.PostLoopNotification_Urgent_ReturnsSameShape", "POST /api/v2/notifications/loop", 200, JsonValueKind.Object, UndefinedPropertyCrash),
        new("V2.NotificationsParityTests.PostLoopNotification_WithDeviceToken_ReturnsSameShape", "POST /api/v2/notifications/loop", 200, JsonValueKind.Object, UndefinedPropertyCrash),
        new("V2.NotificationsParityTests.PostLoopNotification_WithPushoverFields_ReturnsSameShape", "POST /api/v2/notifications/loop", 200, JsonValueKind.Object, UndefinedPropertyCrash),
        new("V2.NotificationsParityTests.PostNotification_Empty_ReturnsSameShape", "POST /api/v2/notifications", 400, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.NotificationsParityTests.PostNotification_HighLevel_ReturnsSameShape", "POST /api/v2/notifications", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.NotificationsParityTests.PostNotification_InvalidLevel_ReturnsSameShape", "POST /api/v2/notifications", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.NotificationsParityTests.PostNotification_MissingMessage_ReturnsSameShape", "POST /api/v2/notifications", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.NotificationsParityTests.PostNotification_MissingTitle_ReturnsSameShape", "POST /api/v2/notifications", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.NotificationsParityTests.PostNotification_Simple_ReturnsSameShape", "POST /api/v2/notifications", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.NotificationsParityTests.PostNotification_WithGroup_ReturnsSameShape", "POST /api/v2/notifications", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.NotificationsParityTests.PostNotification_WithPlugin_ReturnsSameShape", "POST /api/v2/notifications", 200, JsonValueKind.Object, NoNightscoutRoute),
        new("V2.SummaryParityTests.GetSummary_InvalidHours_Negative_ReturnsSameShape", "GET /api/v2/summary", 400, JsonValueKind.Object, SummaryHoursValidated),
        new("V2.SummaryParityTests.GetSummary_InvalidHours_String_ReturnsSameShape", "GET /api/v2/summary", 400, JsonValueKind.Object, SummaryHoursValidated),
        new("V2.SummaryParityTests.GetSummary_InvalidHours_Zero_ReturnsSameShape", "GET /api/v2/summary", 400, JsonValueKind.Object, SummaryHoursValidated),
        new("V3.EntriesParityTests.BulkCreate_Empty_ReturnsSameShape", "POST /api/v3/entries/bulk", 400, JsonValueKind.Object, NoV3Bulk),
        new("V3.EntriesParityTests.BulkCreate_MixedTypes_ReturnsSameShape", "POST /api/v3/entries/bulk", 201, JsonValueKind.Array, NoV3Bulk),
        new("V3.EntriesParityTests.BulkCreate_ReturnsSameShape", "POST /api/v3/entries/bulk", 201, JsonValueKind.Array, NoV3Bulk),
        new("V3.EntriesParityTests.Create_Empty_ReturnsSameShape", "POST /api/v3/entries", 201, JsonValueKind.Object, V3Tolerant),
        new("V3.EntriesParityTests.Create_Invalid_ReturnsSameShape", "POST /api/v3/entries", 201, JsonValueKind.Object, V3Tolerant),
        new("V3.EntriesParityTests.Create_Mbg_ReturnsSameShape", "POST /api/v3/entries", 201, JsonValueKind.Object, V3Tolerant),
        new("V3.EntriesParityTests.Create_Single_ReturnsSameShape", "POST /api/v3/entries", 201, JsonValueKind.Object, V3Tolerant),
        new("V3.EntriesParityTests.Create_WithAllFields_ReturnsSameShape", "POST /api/v3/entries", 201, JsonValueKind.Object, V3Tolerant),
        new("V3.EntriesParityTests.Search_InvalidSkip_ReturnsSameShape", "GET /api/v3/entries", 200, JsonValueKind.Object, V3TolerantSkip),
        new("V3.EntriesParityTests.Update_NotFound_ReturnsSameShape", "PUT /api/v3/entries/nonexistent123456789012", 404, JsonValueKind.Object, V3Tolerant),
        new("V3.ProfileParityTests.Create_Empty_ReturnsSameShape", "POST /api/v3/profile", 201, JsonValueKind.Array, V3Tolerant),
        new("V3.ProfileParityTests.Create_MissingStore_ReturnsSameShape", "POST /api/v3/profile", 201, JsonValueKind.Array, V3Tolerant),
        new("V3.ProfileParityTests.Create_Simple_ReturnsSameShape", "POST /api/v3/profile", 201, JsonValueKind.Array, V3Tolerant),
        new("V3.ProfileParityTests.Create_WithMmol_ReturnsSameShape", "POST /api/v3/profile", 201, JsonValueKind.Array, V3Tolerant),
        new("V3.ProfileParityTests.Create_WithMultipleTimePeriods_ReturnsSameShape", "POST /api/v3/profile", 201, JsonValueKind.Array, V3Tolerant),
        new("V3.ProfileParityTests.Search_MultipleProfiles_ReturnsSameShape", "GET /api/v3/profile", 200, JsonValueKind.Object, V3RejectedSeed),
        new("V3.ProfileParityTests.Search_WithData_ReturnsSameShape", "GET /api/v3/profile", 200, JsonValueKind.Object, V3RejectedSeed),
        new("V3.ProfileParityTests.Search_WithLimit_ReturnsSameShape", "GET /api/v3/profile", 200, JsonValueKind.Object, V3RejectedSeed),
        new("V3.ProfileParityTests.Search_WithSort_ReturnsSameShape", "GET /api/v3/profile", 200, JsonValueKind.Object, V3RejectedSeed),
        new("V3.ProfileParityTests.Update_NotFound_ReturnsSameShape", "PUT /api/v3/profile/nonexistent123456789012", 200, JsonValueKind.Object, V3Tolerant),
        new("V3.TreatmentsParityTests.BulkCreate_Empty_ReturnsSameShape", "POST /api/v3/treatments/bulk", 400, JsonValueKind.Object, NoV3Bulk),
        new("V3.TreatmentsParityTests.BulkCreate_ReturnsSameShape", "POST /api/v3/treatments/bulk", 201, JsonValueKind.Array, NoV3Bulk),
        new("V3.TreatmentsParityTests.Create_Bolus_ReturnsSameShape", "POST /api/v3/treatments", 201, JsonValueKind.Object, V3Tolerant),
        new("V3.TreatmentsParityTests.Create_Empty_ReturnsSameShape", "POST /api/v3/treatments", 201, JsonValueKind.Object, V3Tolerant),
        new("V3.TreatmentsParityTests.Create_InvalidEventType_ReturnsSameShape", "POST /api/v3/treatments", 201, JsonValueKind.Object, V3Tolerant),
        new("V3.TreatmentsParityTests.Create_MealBolus_ReturnsSameShape", "POST /api/v3/treatments", 201, JsonValueKind.Object, V3Tolerant),
        new("V3.TreatmentsParityTests.Create_Note_ReturnsSameShape", "POST /api/v3/treatments", 201, JsonValueKind.Object, V3Tolerant),
        new("V3.TreatmentsParityTests.Create_ProfileSwitch_ReturnsSameShape", "POST /api/v3/treatments", 201, JsonValueKind.Object, V3Tolerant),
        new("V3.TreatmentsParityTests.Create_SensorStart_ReturnsSameShape", "POST /api/v3/treatments", 201, JsonValueKind.Object, V3Tolerant),
        new("V3.TreatmentsParityTests.Create_SiteChange_ReturnsSameShape", "POST /api/v3/treatments", 201, JsonValueKind.Object, V3Tolerant),
        new("V3.TreatmentsParityTests.Create_TempBasal_ReturnsSameShape", "POST /api/v3/treatments", 201, JsonValueKind.Object, V3Tolerant),
        new("V3.TreatmentsParityTests.Update_NotFound_ReturnsSameShape", "PUT /api/v3/treatments/nonexistent123456789012", 404, JsonValueKind.Object, V3Tolerant),
    ];

    /// <summary>The divergence covering <paramref name="request"/> in <paramref name="test"/>, if any.</summary>
    public static Divergence? Find(string test, string request) =>
        All.FirstOrDefault(d => d.Test == test && (d.Request.EndsWith('*')
            ? request.StartsWith(d.Request[..^1], StringComparison.Ordinal)
            : d.Request == request));
}
