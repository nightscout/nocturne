# Nocturne API Unit Tests

Unit tests for the Nocturne API: no database, no Docker, no containers. The API's integration tests
live in `tests/Integration/Nocturne.API.Tests`.

## Running Tests

```bash
# The whole project. Five tests here are Category=Performance — memory and throughput thresholds
# that depend on the machine — and the standard filter is what keeps them out
dotnet test tests/Unit/Nocturne.API.Tests --filter "Category!=Integration&Category!=Performance&Category!=E2E"

# One class
dotnet test tests/Unit/Nocturne.API.Tests --filter "FullyQualifiedName~EntryServiceTests"
```

## Test Structure

Tests mirror the API's own layout — `Controllers/`, `Services/`, `Middleware/`, `Validators/` and so
on — with `TestDoubles/` for shared fakes, `GoldenFiles/` for the recorded v1/v2/v3 responses, and
`TestSuite/` for the tests that police the suite itself.

## Best Practices

1. **Arrange-Act-Assert**: All tests follow the AAA pattern with clear separation between sections
2. **Test Isolation**: Each test is independent and doesn't rely on other tests
3. **Descriptive Names**: Test method names clearly describe what is being tested
4. **Use mocking**: For external dependencies, so these tests stay in milliseconds

## Adding New Tests

1. Create tests in the folder matching the code under test
2. Mock external dependencies and test a single component in isolation
3. Use `[Collection]` to control parallelisation, and `Skip` to park a flaky test

## Legacy Tests

Legacy tests from the original Nightscout application are marked with the `[Parity]` attribute. These tests ensure that
the new C# codebase maintains 1:1 functionality with the original implementation.

### List of Legacy Tests still to implement:

[x] - adminnotifies.test.js (NotificationV1ServiceTests)
[ ] - admintools.test.js
[x] - api.alexa.test.js (AlexaServiceTests)
[x] - api.devicestatus.test.js (DeviceStatusServiceTests)
[x] - api.entries.test.js (EntryServiceTests)
[ ] - api.root.test.js
[ ] - security.test.js
[x] - api.status.test.js (StatusServiceTests)
[x] - api.treatments.test.js (TreatmentServiceTests)
[ ] - unauthorized.test.js
[ ] - verifyauth.test.js
[ ] - basic.test.js
[ ] - create.test.js
[ ] - delete.test.js
[ ] - workflow.test.js
[ ] - patch.test.js
[ ] - read.test.js
[ ] - renderer.test.js
[ ] - search.test.js
[ ] - security.test.js
[ ] - socket.test.js
[ ] - update.test.js
[x] - ar2.test.js (? COVERED - AR2 forecasting algorithm with cone generation, notifications, and virtual assistant
support tested in Ar2Tests and Ar2Service)
[x] - basalprofileplugin.test.js (ProfileServiceTests)
[x] - bgnow.test.js (BgNowTests)
[x] - boluswizardpreview.test.js (BolusWizardServiceTests)
[ ] - bridge.test.js
[x] - cannulaage.test.js (CannulaAgeServiceTests, LegacyDeviceAgeServiceThresholdTests)
[ ] - careportal.test.js
[ ] - ci.test.env
[ ] - renderer.test.js
[ ] - test.js.temporary_removed
[x] - cob.test.js (CobServiceTests, CobTests)
[ ] - calcdelta.test.js
[ ] - treatmenttocurve.test.js
[ ] - dateTools.test.js
[ ] - dbsize.test.js
[x] - ddata.test.js (DDataServiceTests)
[x] - direction.test.js (DirectionServiceTests)
[ ] - env.test.js
[ ] - errorcodes.test.js
[ ] - expressextensions.test.js
[ ] - fail.test.js
[ ] - hashauth.test.js
[x] - insulinage.test.js (InsulinAgeServiceTests)
[x] - iob.test.js (OrefIobParityTests)
[ ] - language.test.js
[x] - levels.test.js (LevelsTests)
[x] - loop.test.js (LoopServiceTests)
[ ] - maker.test.js
[ ] - mmconnect.test.js
[ ] - storage.test.js
[ ] - api.test.js
[x] - notifications.test.js (NotificationV2ServiceTests)
[x] - notifications-api.test.js (NotificationV1ServiceTests)
[ ] - storage.test.js
[x] - openaps.test.js (OpenApsServiceTests)
[ ] - pebble.test.js
[ ] - pluginbase.test.js
[ ] - plugins.test.js
[x] - profile.test.js (ProfileServiceTests)
[ ] - profileeditor.test.js
[x] - pump.test.js (PumpServiceTests)
[ ] - pushnotify.test.js
[x] - pushover.test.js (PushoverServiceTests)
[ ] - query.test.js
[ ] - rawbg.test.js
[ ] - reports.test.js
[ ] - reportstorage.test.js
[ ] - sandbox.test.js
[ ] - security.test.js
[x] - sensorage.test.js (SensorAgeServiceTests)
[ ] - settings.test.js
[x] - simplealarms.test.js (SimpleAlarmsTests)
[x] - timeago.test.js (TimeAgoTests)
[x] - times.test.js (TimesTests)
[ ] - treatmentnotify.test.js
[x] - units.test.js (UnitsTests)
[ ] - upbat.test.js
[x] - utils.test.js (UtilsTests)
[ ] - verifyauth.test.js
[ ] - XX_clean.test.js
