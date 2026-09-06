using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

/// <summary>
/// Pins <see cref="Scope.Satisfies(string, string)"/> and its set overload, the single predicate
/// both the OAuth scope gate and the tenant permission check now resolve through. Before the
/// vocabularies were merged there were two predicates — a table lookup on the OAuth side and a
/// <c>.read</c> to <c>.readwrite</c> string rewrite on the tenant side — and a caller could be
/// admitted by one and refused by the other. These cases fix the merged behaviour where the two
/// used to agree, and name the one place they deliberately no longer do.
/// </summary>
[Trait("Category", "Unit")]
public class ScopeSatisfactionTests
{
    /// <summary>Every atom either predicate has ever been asked about.</summary>
    private static readonly IReadOnlySet<string> KnownAtoms =
        new HashSet<string>(Scope.PermissionAtoms.Concat(Scope.ValidRequestScopes), StringComparer.Ordinal);

    // ---- the tiers, where both old predicates agreed ----

    [Theory]
    [InlineData(Scope.GlucoseReadWrite, Scope.GlucoseRead)]
    [InlineData(Scope.TreatmentsReadWrite, Scope.TreatmentsRead)]
    [InlineData(Scope.DevicesReadWrite, Scope.DevicesRead)]
    [InlineData(Scope.TherapyReadWrite, Scope.TherapyRead)]
    [InlineData(Scope.AlertsReadWrite, Scope.AlertsRead)]
    [InlineData(Scope.HeartRateReadWrite, Scope.HeartRateRead)]
    [InlineData(Scope.StepCountReadWrite, Scope.StepCountRead)]
    [InlineData(Scope.SleepReadWrite, Scope.SleepRead)]
    [InlineData(Scope.FoodReadWrite, Scope.FoodRead)]
    public void Satisfies_ReadWriteSatisfiesItsOwnReadTier(string readWrite, string read)
    {
        Scope.Satisfies(readWrite, read)
            .Should().BeTrue($"'{readWrite}' should satisfy '{read}' as both predecessors agreed it did");
        Scope.Satisfies([readWrite], read)
            .Should().BeTrue($"the set overload should agree with the single-atom overload for '{readWrite}'");
    }

    [Theory]
    [InlineData(Scope.GlucoseRead, Scope.GlucoseReadWrite)]
    [InlineData(Scope.TreatmentsRead, Scope.TreatmentsReadWrite)]
    [InlineData(Scope.DevicesRead, Scope.DevicesReadWrite)]
    [InlineData(Scope.TherapyRead, Scope.TherapyReadWrite)]
    [InlineData(Scope.AlertsRead, Scope.AlertsReadWrite)]
    [InlineData(Scope.HeartRateRead, Scope.HeartRateReadWrite)]
    [InlineData(Scope.StepCountRead, Scope.StepCountReadWrite)]
    [InlineData(Scope.SleepRead, Scope.SleepReadWrite)]
    [InlineData(Scope.FoodRead, Scope.FoodReadWrite)]
    public void Satisfies_ReadDoesNotSatisfyItsOwnReadWriteTier(string read, string readWrite)
    {
        Scope.Satisfies(read, readWrite)
            .Should().BeFalse($"'{read}' is the narrower tier and must never reach '{readWrite}'");
        Scope.Satisfies([read], readWrite)
            .Should().BeFalse($"the set overload should agree with the single-atom overload for '{read}'");
    }

    // ---- the one resolved divergence ----

    [Fact]
    public void Satisfies_AuditManageSatisfiesAuditRead_OnTheSingleAtomOverload()
    {
        // The intentional behavioural change of the merge. The tenant predicate already read
        // audit.manage as covering audit.read; the OAuth table did not, so the same holder was
        // admitted through a membership and refused through a credential. The merged predicate
        // takes the tenant answer for both.
        Scope.Satisfies(Scope.AuditManage, Scope.AuditRead)
            .Should().BeTrue("a member who may change what is audited may read the log");
    }

    [Fact]
    public void Satisfies_AuditManageSatisfiesAuditRead_OnTheSetOverload()
    {
        // Same intentional change, asserted on the overload the middleware actually calls: the two
        // overloads must not disagree, or the divergence simply moves rather than being resolved.
        Scope.Satisfies([Scope.AuditManage], Scope.AuditRead)
            .Should().BeTrue("the set overload should resolve audit.manage the same way as the single-atom overload");
    }

    // ---- the deliberate asymmetry ----

    [Fact]
    public void ImpliedReadScope_DoesNotNarrowAuditManage()
    {
        // Satisfaction widens a check; narrowing reduces a grant. audit.manage participates only in
        // the first. If it were narrowable, bounding a member's membership by a read-only OAuth
        // credential would rewrite their audit.manage to audit.read and silently strip the manage
        // rights the credential was never asked about.
        Scope.ImpliedReadScope(Scope.AuditManage)
            .Should().BeNull("audit.manage must not narrow to audit.read, only satisfy it");
    }

    [Fact]
    public void TryGetImpliedReadScope_DoesNotNarrowAuditManage()
    {
        // The Try- form is the one MemberScopeResolver calls; see ImpliedReadScope_DoesNotNarrowAuditManage
        // for why a hit here would strip manage rights.
        Scope.TryGetImpliedReadScope(Scope.AuditManage, out _)
            .Should().BeFalse("audit.manage has no read counterpart to narrow to");
    }

    // ---- full access ----

    [Fact]
    public void Satisfies_FullAccessSatisfiesEveryPermissionAtom()
    {
        Scope.PermissionAtoms.Should().NotBeEmpty("an empty vocabulary would make this check vacuous");

        foreach (var atom in Scope.PermissionAtoms)
        {
            Scope.Satisfies(Scope.FullAccess, atom)
                .Should().BeTrue($"'*' should satisfy the permission atom '{atom}'");
        }
    }

    [Fact]
    public void Satisfies_FullAccessSatisfiesEveryRequestableScope()
    {
        Scope.ValidRequestScopes.Should().NotBeEmpty("an empty vocabulary would make this check vacuous");

        foreach (var scope in Scope.ValidRequestScopes)
        {
            Scope.Satisfies(Scope.FullAccess, scope)
                .Should().BeTrue($"'*' should satisfy the requestable scope '{scope}'");
        }
    }

    // ---- non-implications ----

    [Fact]
    public void Satisfies_DeviceCapabilitiesDoNotImplyOneAnother()
    {
        Scope.Satisfies(Scope.DeviceNotify, Scope.DeviceActuate)
            .Should().BeFalse("pushing a notification is not permission to actuate hardware");
        Scope.Satisfies(Scope.DeviceActuate, Scope.DeviceNotify)
            .Should().BeFalse("actuating hardware is a separate capability, not a superset of notify");
    }

    [Fact]
    public void Satisfies_DeviceCapabilitiesHaveNoReadWriteTier()
    {
        // The capability grants are not a data category, so the old tenant predicate's
        // ".read" -> ".readwrite" rewrite had nothing to construct here and the merged predicate
        // must not invent a tier either.
        KnownAtoms.Should().NotContain("device.readwrite",
            "the device capabilities are tierless, so no device.readwrite atom exists");

        Scope.Satisfies("device.readwrite", Scope.DeviceNotify)
            .Should().BeFalse("a scope that is not in the vocabulary satisfies nothing");
        Scope.Satisfies("device.readwrite", Scope.DeviceActuate)
            .Should().BeFalse("a scope that is not in the vocabulary satisfies nothing");
    }

    [Fact]
    public void Satisfies_SharingReadWriteDoesNotReachTheTenantSharingAtoms()
    {
        // A documented trap: the three sharing atoms share a prefix but are not a tier.
        // sharing.readwrite is the OAuth-facing scope over a subject's own sharing configuration;
        // sharing.manage mints public links to the tenant and sharing.guest mints guest links.
        Scope.Satisfies(Scope.SharingReadWrite, Scope.SharingManage)
            .Should().BeFalse("sharing.readwrite governs the subject's own configuration, not tenant sharing");
        Scope.Satisfies(Scope.SharingReadWrite, Scope.SharingGuest)
            .Should().BeFalse("minting guest links is a separate tenant-administration atom");
        Scope.Satisfies([Scope.SharingReadWrite], Scope.SharingManage)
            .Should().BeFalse("the set overload should agree with the single-atom overload");
        Scope.Satisfies([Scope.SharingReadWrite], Scope.SharingGuest)
            .Should().BeFalse("the set overload should agree with the single-atom overload");
    }

    [Fact]
    public void Satisfies_AnUnknownAtomSatisfiesNothing()
    {
        KnownAtoms.Should().NotBeEmpty("an empty vocabulary would make this check vacuous");

        foreach (var atom in KnownAtoms)
        {
            Scope.Satisfies("glucose.destroy", atom)
                .Should().BeFalse($"an atom outside the vocabulary should not satisfy '{atom}'");
            Scope.Satisfies(["glucose.destroy"], atom)
                .Should().BeFalse($"the set overload should also refuse an unknown atom against '{atom}'");
        }
    }

    // ---- the set overload's short circuits ----

    [Fact]
    public void Satisfies_AnEmptyGrantedSetSatisfiesNothing()
    {
        KnownAtoms.Should().NotBeEmpty("an empty vocabulary would make this check vacuous");

        var granted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var atom in KnownAtoms)
        {
            Scope.Satisfies(granted, atom)
                .Should().BeFalse($"a credential holding no scopes should not satisfy '{atom}'");
        }
    }

    [Fact]
    public void Satisfies_ASetHoldingOnlyFullAccessSatisfiesEverything()
    {
        KnownAtoms.Should().NotBeEmpty("an empty vocabulary would make this check vacuous");

        var granted = new HashSet<string>(StringComparer.Ordinal) { Scope.FullAccess };

        foreach (var atom in KnownAtoms)
        {
            Scope.Satisfies(granted, atom)
                .Should().BeTrue($"a set holding '*' should satisfy '{atom}' without enumerating it");
        }
    }
}
