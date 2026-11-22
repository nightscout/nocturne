using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Auth;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

[Trait("Category", "Unit")]
public class StepUpCodeAttemptsTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly Guid _subject = Guid.CreateVersion7();

    [Fact]
    public void A_token_takes_its_allowance_then_is_spent()
    {
        var attempts = new StepUpCodeAttempts(_time);
        var token = Guid.CreateVersion7();

        var verdicts = Enumerable.Range(0, StepUpCodeAttempts.WrongCodesPerToken + 1)
            .Select(_ => attempts.Begin(token, _subject).Verdict)
            .ToList();

        verdicts.Take(StepUpCodeAttempts.WrongCodesPerToken - 1).Should().AllBeEquivalentTo(StepUpAttemptVerdict.Allowed);
        verdicts[StepUpCodeAttempts.WrongCodesPerToken - 1].Should().Be(StepUpAttemptVerdict.LastForToken);
        verdicts[^1].Should().Be(StepUpAttemptVerdict.TokenSpent);
    }

    [Fact]
    public void A_member_is_refused_across_tokens_until_the_window_from_the_first_wrong_code_ends()
    {
        var attempts = new StepUpCodeAttempts(_time);
        for (var i = 0; i < StepUpCodeAttempts.WrongCodesPerMember; i++)
        {
            attempts.Begin(Guid.CreateVersion7(), _subject).Verdict.Should().Be(StepUpAttemptVerdict.Allowed);
            _time.Advance(TimeSpan.FromSeconds(30));
        }

        var (verdict, retryAfter) = attempts.Begin(Guid.CreateVersion7(), _subject);
        verdict.Should().Be(StepUpAttemptVerdict.MemberLocked);
        retryAfter.Should().Be(StepUpCodeAttempts.MemberWindow - TimeSpan.FromSeconds(30 * StepUpCodeAttempts.WrongCodesPerMember));

        _time.Advance(retryAfter);
        attempts.Begin(Guid.CreateVersion7(), _subject).Verdict.Should().Be(StepUpAttemptVerdict.Allowed);
    }

    [Fact]
    public void A_correct_code_clears_the_members_count()
    {
        var attempts = new StepUpCodeAttempts(_time);
        for (var i = 0; i < StepUpCodeAttempts.WrongCodesPerMember - 1; i++)
            attempts.Begin(Guid.CreateVersion7(), _subject);

        var redeemed = Guid.CreateVersion7();
        attempts.Begin(redeemed, _subject);
        attempts.Succeeded(redeemed, _subject);

        attempts.Begin(Guid.CreateVersion7(), _subject).Verdict.Should().Be(StepUpAttemptVerdict.Allowed);
    }

    [Fact]
    public void One_members_wrong_codes_do_not_refuse_another_member()
    {
        var attempts = new StepUpCodeAttempts(_time);
        for (var i = 0; i < StepUpCodeAttempts.WrongCodesPerMember; i++)
            attempts.Begin(Guid.CreateVersion7(), _subject);

        attempts.Begin(Guid.CreateVersion7(), Guid.CreateVersion7()).Verdict.Should().Be(StepUpAttemptVerdict.Allowed);
    }
}
