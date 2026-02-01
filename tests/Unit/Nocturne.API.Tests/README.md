# Nocturne API Tests

This directory contains unit tests and integration tests for the Nocturne API.

## Test Categories

### Unit Tests

Unit tests run without external dependencies and test individual components in isolation. These tests are fast and can
be run without any setup.

### Integration Tests

Integration tests use real MongoDB containers via Testcontainers to test end-to-end functionality. These tests require
Docker to be installed and running.

## Running Tests

### Running All Tests

```bash
dotnet test
```

### Running Only Unit Tests

```bash
dotnet test --filter "FullyQualifiedName!~Integration"
```

### Running Only Integration Tests

```bash
dotnet test --filter "FullyQualifiedName~Integration"
```

## Docker Setup for Integration Tests

Integration tests require Docker to be installed and running to create MongoDB test containers.

### Installing Docker

#### Windows

1. Download and install [Docker Desktop for Windows](https://docs.docker.com/desktop/windows/install/)
2. Start Docker Desktop
3. Ensure WSL 2 backend is enabled (recommended)

#### macOS

1. Download and install [Docker Desktop for Mac](https://docs.docker.com/desktop/mac/install/)
2. Start Docker Desktop

#### Linux

1. Install Docker Engine using your distribution's package manager
2. Start the Docker service
3. Add your user to the docker group (optional, to run without sudo)

### Verifying Docker Installation

```bash
docker --version
docker run hello-world
```

### Troubleshooting Docker Issues

If you encounter Docker-related errors when running integration tests:

1. **Docker not running**: Ensure Docker Desktop is started (Windows/macOS) or the Docker service is running (Linux)

2. **Permission issues (Linux)**: Add your user to the docker group:

   ```bash
   sudo usermod -aG docker $USER
   newgrp docker
   ```

3. **WSL 2 issues (Windows)**: Ensure WSL 2 backend is enabled in Docker Desktop settings

4. **Port conflicts**: If you have MongoDB running locally on port 27017, the tests will automatically use a different
   port

## Test Structure

```
Nocturne.API.Tests/
├── Controllers/           # Controller unit tests
├── Services/             # Service unit tests
├── Models/               # Model unit tests
├── Integration/          # Integration tests
│   ├── EntriesIntegrationTests.cs      # End-to-end API tests
│   └── MongoDbServiceIntegrationTests.cs # Database integration tests
└── GlobalUsings.cs       # Global using statements
```

## Test Data

Integration tests automatically seed test data into temporary MongoDB containers. Each test run uses a fresh database
instance, ensuring test isolation.

## Best Practices

1. **Arrange-Act-Assert**: All tests follow the AAA pattern with clear separation between sections
2. **Test Isolation**: Each test is independent and doesn't rely on other tests
3. **Descriptive Names**: Test method names clearly describe what is being tested
4. **Fast Unit Tests**: Unit tests run quickly without external dependencies
5. **Comprehensive Integration**: Integration tests cover real-world scenarios

## Continuous Integration

When running in CI/CD environments:

1. Ensure Docker is available in the CI environment
2. Consider using pre-built MongoDB images to speed up test execution
3. Use test parallelization carefully with integration tests to avoid port conflicts

## Adding New Tests

### Unit Tests

1. Create tests in the appropriate folder (Controllers, Services, Models)
2. Use mocking for external dependencies
3. Focus on testing a single component in isolation

### Integration Tests

1. Add tests to the Integration folder
2. Use the existing base classes for database setup
3. Test real interactions between components
4. Verify end-to-end scenarios

## Performance Considerations

- Unit tests should complete in milliseconds
- Integration tests may take several seconds due to container startup
- Consider using `[Collection]` attributes to control test parallelization
- Use `Skip` attribute to temporarily disable slow or flaky tests

## Legacy Tests

Legacy tests from the original Nocturne application are marked with the `[Parity]` attribute. These tests ensure that
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
