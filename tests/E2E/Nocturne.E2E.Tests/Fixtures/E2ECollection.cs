using Xunit;

namespace Nocturne.E2E.Tests.Fixtures;

[CollectionDefinition("e2e")]
public sealed class E2ECollection : ICollectionFixture<AppHostFixture> { }
