using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
///     Token acquisition runs on the same retry loop as the connector services, so a configured
///     <see cref="Nocturne.Connectors.Core.Models.BaseConnectorConfiguration.MaxRetryAttempts"/>
///     counts total attempts here too, and 0 has to buy one attempt instead of skipping acquisition.
/// </summary>
public class AuthTokenProviderBaseRetryTests
{
    [Fact]
    public async Task ExecuteWithRetryAsync_ZeroMaxRetries_AttemptsExactlyOnce()
    {
        using var provider = BuildProvider();
        var delays = new RecordingRetryDelayStrategy();
        var attempts = 0;

        var token = await provider.InvokeExecuteWithRetryAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult<(string? Result, bool ShouldRetry)>((null, true));
            },
            delays,
            maxRetries: 0);

        token.Should().BeNull();
        attempts.Should().Be(1, "0 is clamped to a single attempt");
        delays.DelayedAttempts.Should().BeEmpty("a single attempt has nothing to wait between");
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_RetryableFailure_AttemptsUpToMaxRetries()
    {
        using var provider = BuildProvider();
        var delays = new RecordingRetryDelayStrategy();
        var attempts = 0;

        var token = await provider.InvokeExecuteWithRetryAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult<(string? Result, bool ShouldRetry)>((null, true));
            },
            delays,
            maxRetries: 3);

        token.Should().BeNull();
        attempts.Should().Be(3, "maxRetries counts attempts, not retries on top of a first try");
        delays.DelayedAttempts.Should().Equal([0, 1], "three attempts leave two gaps to delay in");
    }

    /// <summary>
    ///     A status on the exception is the source's verdict. Sending a rejected credential again
    ///     cannot change the answer and risks vendor-side lockout.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task ExecuteWithRetryAsync_HttpFailureCarryingARejection_AttemptsExactlyOnce(
        HttpStatusCode status)
    {
        using var provider = BuildProvider();
        var delays = new RecordingRetryDelayStrategy();
        var attempts = 0;

        var token = await provider.InvokeExecuteWithRetryAsync(
            _ =>
            {
                attempts++;
                throw new HttpRequestException("rejected", null, status);
            },
            delays,
            maxRetries: 3);

        token.Should().BeNull();
        attempts.Should().Be(1);
        delays.DelayedAttempts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ExecuteWithRetryAsync_HttpFailureCarryingARetryableStatus_AttemptsUpToMaxRetries(
        HttpStatusCode status)
    {
        using var provider = BuildProvider();
        var attempts = 0;

        var token = await provider.InvokeExecuteWithRetryAsync(
            _ =>
            {
                attempts++;
                throw new HttpRequestException("busy", null, status);
            },
            new RecordingRetryDelayStrategy(),
            maxRetries: 3);

        token.Should().BeNull();
        attempts.Should().Be(3);
    }

    /// <summary>
    ///     A transport failure carries no status because no answer arrived, and that is exactly the
    ///     failure another attempt can change.
    /// </summary>
    [Fact]
    public async Task ExecuteWithRetryAsync_HttpFailureCarryingNoStatus_AttemptsUpToMaxRetries()
    {
        using var provider = BuildProvider();
        var attempts = 0;

        var token = await provider.InvokeExecuteWithRetryAsync(
            _ =>
            {
                attempts++;
                throw new HttpRequestException("connection reset");
            },
            new RecordingRetryDelayStrategy(),
            maxRetries: 3);

        token.Should().BeNull();
        attempts.Should().Be(3);
    }

    /// <summary>
    ///     The configured value is what reaches the login loop, so a tenant raising or lowering
    ///     it changes how many times the connector authenticates.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(10, 10)]
    public async Task AcquireToken_AttemptsLoginAsManyTimesAsMaxRetryAttempts(
        int maxRetryAttempts, int expectedAttempts)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(Guid.NewGuid());

        using var httpClient = new HttpClient();
        using var provider = new CountingTokenProvider(
            httpClient,
            new ConnectorTokenCache(),
            NoOpResolver,
            tenantAccessor.Object,
            NullLogger<CountingTokenProvider>.Instance,
            Mock.Of<IRetryDelayStrategy>());

        var token = await provider.GetValidTokenAsync(
            new TestConnectorConfig { MaxRetryAttempts = maxRetryAttempts },
            CancellationToken.None);

        token.Should().BeNull("every login attempt was made to fail");
        provider.LoginCalls.Should().Be(expectedAttempts);
    }

    /// <summary>
    ///     A run that never got a token fetches nothing, which several connectors report as a
    ///     successful sync that found no data. The refusal recorded here is the only thing that tells
    ///     the tenant their credentials were the reason.
    /// </summary>
    [Fact]
    public async Task AcquireToken_RefusedByTheSource_RecordsASignInRefusalForTheTenant()
    {
        var cache = new ConnectorTokenCache();
        var tenantId = Guid.NewGuid();
        using var provider = BuildSignInProvider(cache, tenantId,
            _ => throw new HttpRequestException("rejected", null, HttpStatusCode.Unauthorized));

        var token = await provider.GetValidTokenAsync(new TestConnectorConfig(), CancellationToken.None);

        token.Should().BeNull();
        cache.GetSignInRefusal(SignInProvider.Name, tenantId)
            .Should().Contain("username and password", "the tenant can only act on what they entered");
    }

    /// <summary>
    ///     A transient failure must not tell someone their password is wrong, however many attempts
    ///     it consumes.
    /// </summary>
    [Fact]
    public async Task AcquireToken_ExhaustedByTransportFailures_RecordsNoSignInRefusal()
    {
        var cache = new ConnectorTokenCache();
        var tenantId = Guid.NewGuid();
        using var provider = BuildSignInProvider(cache, tenantId,
            _ => throw new HttpRequestException("connection reset"));

        var token = await provider.GetValidTokenAsync(new TestConnectorConfig(), CancellationToken.None);

        token.Should().BeNull();
        cache.GetSignInRefusal(SignInProvider.Name, tenantId).Should().BeNull();
    }

    [Fact]
    public async Task AcquireToken_AcceptedAfterARefusal_ClearsTheSignInRefusal()
    {
        var cache = new ConnectorTokenCache();
        var tenantId = Guid.NewGuid();
        cache.SetSignInRefusal(SignInProvider.Name, tenantId, "recorded by an earlier run");

        using var provider = BuildSignInProvider(cache, tenantId,
            _ => Task.FromResult<(string? Result, bool ShouldRetry)>(("token-1", false)));

        var token = await provider.GetValidTokenAsync(new TestConnectorConfig(), CancellationToken.None);

        token.Should().Be("token-1");
        cache.GetSignInRefusal(SignInProvider.Name, tenantId).Should().BeNull();
    }

    private static SignInProvider BuildSignInProvider(
        IConnectorTokenCache cache,
        Guid tenantId,
        Func<int, Task<(string? Result, bool ShouldRetry)>> login)
    {
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(tenantId);

        return new SignInProvider(
            new HttpClient(),
            cache,
            NoOpResolver,
            tenantAccessor.Object,
            NullLogger<SignInProvider>.Instance,
            login);
    }

    /// <summary>Runs one caller-supplied login attempt per try through the shared retry loop.</summary>
    private sealed class SignInProvider(
        HttpClient httpClient,
        IConnectorTokenCache tokenCache,
        IConnectorServerResolver<TestConnectorConfig> serverResolver,
        ITenantAccessor tenantAccessor,
        ILogger logger,
        Func<int, Task<(string? Result, bool ShouldRetry)>> login)
        : AuthTokenProviderBase<TestConnectorConfig>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
    {
        internal const string Name = "SignIn";

        protected override string ConnectorName => Name;

        protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            TestConnectorConfig config, CancellationToken cancellationToken)
        {
            var token = await ExecuteWithRetryAsync(
                login,
                new RecordingRetryDelayStrategy(),
                LoginAttempts(config),
                "sign-in",
                cancellationToken);

            return (token, DateTime.UtcNow.AddHours(1), null);
        }
    }

    private static readonly ConnectorServerResolver<TestConnectorConfig> NoOpResolver = new(null, null, null);

    private static RetryTokenProvider BuildProvider() => new(
        new HttpClient(),
        new ConnectorTokenCache(),
        NoOpResolver,
        Mock.Of<ITenantAccessor>(),
        NullLogger<RetryTokenProvider>.Instance);

    private sealed class RetryTokenProvider(
        HttpClient httpClient,
        IConnectorTokenCache tokenCache,
        IConnectorServerResolver<TestConnectorConfig> serverResolver,
        ITenantAccessor tenantAccessor,
        ILogger logger)
        : AuthTokenProviderBase<TestConnectorConfig>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
    {
        protected override string ConnectorName => "Test";

        // Exposes the protected retry helper so its attempt-count behaviour can be tested directly.
        public Task<string?> InvokeExecuteWithRetryAsync(
            Func<int, Task<(string? Result, bool ShouldRetry)>> operation,
            IRetryDelayStrategy retryDelayStrategy,
            int maxRetries)
            => ExecuteWithRetryAsync(
                operation,
                retryDelayStrategy,
                maxRetries,
                "test token acquisition",
                CancellationToken.None);

        protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            TestConnectorConfig config, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>Fails every login, recording how many times it was asked to try.</summary>
    private sealed class CountingTokenProvider(
        HttpClient httpClient,
        IConnectorTokenCache tokenCache,
        IConnectorServerResolver<TestConnectorConfig> serverResolver,
        ITenantAccessor tenantAccessor,
        ILogger logger,
        IRetryDelayStrategy retryDelayStrategy)
        : AuthTokenProviderBase<TestConnectorConfig>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
    {
        internal int LoginCalls { get; private set; }

        protected override string ConnectorName => "Counting";

        protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            TestConnectorConfig config, CancellationToken cancellationToken)
        {
            var token = await ExecuteWithRetryAsync<string>(
                _ =>
                {
                    LoginCalls++;
                    return Task.FromResult<(string? Result, bool ShouldRetry)>((null, true));
                },
                retryDelayStrategy,
                LoginAttempts(config),
                "counting login",
                cancellationToken);

            return (token, DateTime.UtcNow.AddHours(1), null);
        }
    }
}
