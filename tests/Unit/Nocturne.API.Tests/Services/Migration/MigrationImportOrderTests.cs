using FluentAssertions;
using Nocturne.API.Services.Migration;
using Xunit;

namespace Nocturne.API.Tests.Services.Migration;

public class MigrationImportOrderTests
{
    /// <summary>
    /// A percent temp basal resolves against the schedule in force as it is written, so profiles
    /// import ahead of the treatments, in the order Mongo lists collections or the API lists them.
    /// </summary>
    [Theory]
    [InlineData("treatments", "entries", "devicestatus", "profile", "food", "activity")]
    [InlineData("subjects", "entries", "treatments", "devicestatus", "profile", "food", "activity")]
    [InlineData("activity", "food", "treatments", "profile")]
    public void Profiles_ImportBeforeTreatments(params string[] listed)
    {
        var order = listed.OrderBy(MigrationJob.ImportRank).ToList();

        order.IndexOf("profile").Should().BeLessThan(order.IndexOf("treatments"));
        if (order.Contains("subjects"))
            order[0].Should().Be("subjects");
        order.Where(c => c is not ("subjects" or "profile"))
            .Should().Equal(listed.Where(c => c is not ("subjects" or "profile")));
    }
}
