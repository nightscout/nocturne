using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Nocturne.Aspire.Host.Tests;

public class DevOptionsTests
{
    [Fact]
    public void With_no_Dev_section_every_shortcut_is_on()
    {
        var options = DevOptions.From(Configuration());

        options.AutoLogin.Should().BeTrue();
        options.AutoSeed.Enabled.Should().BeTrue();
        options.AutoSeed.SampleDataDays.Should().Be(30);
    }

    [Fact]
    public void The_auto_seed_turns_off_on_its_own()
    {
        var options = DevOptions.From(Configuration(("Dev:AutoSeed:Enabled", "false")));

        options.AutoSeed.Enabled.Should().BeFalse();
        options.AutoSeed.SampleDataDays.Should().Be(30);
        options.AutoLogin.Should().BeTrue();
    }

    [Fact]
    public void The_double_underscore_environment_form_binds()
    {
        const string prefix = "NOCTURNE_DEV_OPTIONS_TEST_";
        Environment.SetEnvironmentVariable($"{prefix}Dev__AutoSeed__Enabled", "false");
        Environment.SetEnvironmentVariable($"{prefix}Dev__AutoLogin", "false");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();

            var options = DevOptions.From(configuration);

            options.AutoSeed.Enabled.Should().BeFalse();
            options.AutoLogin.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable($"{prefix}Dev__AutoSeed__Enabled", null);
            Environment.SetEnvironmentVariable($"{prefix}Dev__AutoLogin", null);
        }
    }

    [Fact]
    public void The_sample_data_days_bind()
    {
        var options = DevOptions.From(Configuration(("Dev:AutoSeed:SampleDataDays", "7")));

        options.AutoSeed.SampleDataDays.Should().Be(7);
        options.AutoSeed.Enabled.Should().BeTrue();
    }

    [Fact]
    public void The_retired_top_level_key_is_not_read()
    {
        var options = DevOptions.From(Configuration(("NOCTURNE_DEV_AUTO_LOGIN", "false")));

        options.AutoLogin.Should().BeTrue();
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value)))
            .Build();
}
