using System.Buffers.Text;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Mcp.Reports;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Reports;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.Reports;
using Xunit;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Nocturne.API.Tests.Mcp;

public class CustomReportTests
{
    private static readonly ReportSpecValidator Validator = new(ReportSpecValidatorTests.Registry, TimeProvider.System);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string TwoPanels = """
        {
          "version": 1,
          "title": "<script>alert(1)</script>",
          "panels": [
            { "dataset": { "name": "glucose_series" }, "primitive": "time_series", "bindings": { "y": ["glucose"] }, "range": { "hours": 24 } },
            { "id": "stats", "dataset": { "name": "event_response", "cohorts": [{ "anchor": "Meal", "dayOfWeek": ["Mon"] }] },
              "primitive": "range_bands", "range": { "from": "2026-10-01T00:00:00Z", "to": "2026-10-15T00:00:00Z" } }
          ]
        }
        """;

    private static ReportSpec Normalised(string json)
    {
        var validation = Validator.Validate(JsonDocument.Parse(json).RootElement);
        validation.Errors.Should().BeEmpty();
        return validation.Spec!;
    }

    private static string Deflate(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(bytes);
        return Base64Url.EncodeToString(output.ToArray());
    }

    [Fact]
    public void A_linked_spec_decodes_and_validates_to_the_same_normalised_spec()
    {
        var spec = Normalised(TwoPanels);

        var url = ReportSpecLink.NewReportUrl("https://dev.nocturne.localhost:1612/", spec, Web);

        url.Should().StartWith("https://dev.nocturne.localhost:1612/reports/custom/new?spec=");
        var encoded = QueryHelpers.ParseQuery(new Uri(url!).Query)["spec"].ToString();
        var read = ReportSpecLink.Decode(encoded);
        read.Error.Should().BeNull();
        var again = Validator.Validate(read.Spec!.Value);
        again.Errors.Should().BeEmpty();
        JsonSerializer.Serialize(again.Spec, Web).Should().Be(JsonSerializer.Serialize(spec, Web));
    }

    [Fact]
    public void A_link_that_inflates_past_the_limit_is_refused_without_reading_it_all()
    {
        // 1 MB of one byte deflates to about a kilobyte: a short link, a large document.
        var encoded = Deflate(Encoding.UTF8.GetBytes(new string(' ', 1024 * 1024)));
        encoded.Length.Should().BeLessThan(ReportSpecLink.MaxEncodedLength);

        ReportSpecLink.Decode(encoded).Should().Be(ReportSpecLinkRead.Fail(ReportSpecLink.TooLarge));
    }

    [Fact]
    public void A_spec_at_the_limit_decodes_and_one_byte_over_does_not()
    {
        static string Padded(int length)
        {
            const string head = "{\"version\":1,\"title\":\"";
            return head + new string('a', length - head.Length - 2) + "\"}";
        }

        Padded(ReportSpecLink.MaxSpecBytes).Length.Should().Be(ReportSpecLink.MaxSpecBytes);
        ReportSpecLink.Decode(Deflate(Encoding.UTF8.GetBytes(Padded(ReportSpecLink.MaxSpecBytes)))).Error.Should().BeNull();
        ReportSpecLink.Decode(Deflate(Encoding.UTF8.GetBytes(Padded(ReportSpecLink.MaxSpecBytes + 1)))).Error
            .Should().Be(ReportSpecLink.TooLarge);
    }

    [Theory]
    [InlineData("not base64 !")]
    [InlineData("AAAA")]
    [InlineData("____")]
    public void A_damaged_link_is_refused_with_a_message_not_an_exception(string encoded)
    {
        ReportSpecLink.Decode(encoded).Error.Should().Be(ReportSpecLink.Damaged);
    }

    [Fact]
    public void A_link_that_inflates_to_something_other_than_JSON_is_refused()
    {
        ReportSpecLink.Decode(Deflate(Encoding.UTF8.GetBytes("not json"))).Error.Should().Be(ReportSpecLink.Damaged);
    }

    [Fact]
    public void A_link_longer_than_the_limit_is_refused_before_decoding()
    {
        ReportSpecLink.Decode(new string('A', ReportSpecLink.MaxEncodedLength + 1)).Error.Should().Be(ReportSpecLink.TooLarge);
        ReportSpecLink.Decode("").Error.Should().NotBeNull();
    }

    [Fact]
    public void A_spec_over_the_limit_gets_no_link()
    {
        ReportSpecLink.Encode(new byte[ReportSpecLink.MaxSpecBytes + 1]).Should().BeNull();
    }

    [Fact]
    public void An_older_version_is_rewritten_one_step_at_a_time_then_validated()
    {
        var rewriter = new ReportSpecRewriter(
        [
            new ReportSpecRewrite(0, spec =>
            {
                foreach (var panel in spec["panels"]!.AsArray())
                    if (panel!["dataset"]!["name"]!.GetValue<string>() == "glucose")
                        panel["dataset"]!["name"] = "glucose_series";
            }),
        ]);
        var old = JsonDocument.Parse("""
            { "version": 0, "panels": [{ "dataset": { "name": "glucose" }, "primitive": "time_series", "bindings": { "y": ["glucose"] } }] }
            """).RootElement;

        var rewritten = rewriter.Rewrite(old);

        rewritten.GetProperty("version").GetInt32().Should().Be(ReportSpec.CurrentVersion);
        Validator.Validate(rewritten).Errors.Should().BeEmpty();
    }

    [Fact]
    public void A_version_with_no_rewrite_is_left_for_the_validator_to_refuse()
    {
        var old = JsonDocument.Parse("""
            { "version": 0, "panels": [{ "dataset": { "name": "glucose_series" }, "primitive": "time_series", "bindings": { "y": ["glucose"] } }] }
            """).RootElement;

        var rewritten = ReportSpecRewriter.Current.Rewrite(old);

        Validator.Validate(rewritten).Errors.Should().ContainSingle()
            .Which.ToString().Should().Be("version: 0 is not a supported version. Send the number 1.");
    }

    [Theory]
    [InlineData("", "Enter a name.")]
    [InlineData("   ", "Enter a name.")]
    [InlineData("a\nb", "A name cannot have line breaks, control characters or hidden format characters.")]
    [InlineData("Lunch\u202Eevil", "A name cannot have line breaks, control characters or hidden format characters.")]
    [InlineData("Lunch\u200B", "A name cannot have line breaks, control characters or hidden format characters.")]
    public void A_name_is_refused_with_a_reason(string name, string message)
    {
        var errors = new List<CustomReportSpecError>();
        ReportSpecStore.ReadName(name, errors).Should().BeNull();
        errors.Should().ContainSingle().Which.Should().Be(new CustomReportSpecError("name", message));
    }

    [Fact]
    public void A_name_is_trimmed_and_capped_at_120_characters()
    {
        var errors = new List<CustomReportSpecError>();
        ReportSpecStore.ReadName("  Lunch  ", errors).Should().Be("Lunch");
        ReportSpecStore.ReadName(new string('x', 120), errors).Should().HaveLength(120);
        errors.Should().BeEmpty();
        ReportSpecStore.ReadName(new string('x', 121), errors).Should().BeNull();
        errors.Should().ContainSingle().Which.Message.Should().Be("A name can have at most 120 characters.");
    }

    [Theory]
    [InlineData("""{"panels":[{},{}]}""", 2)]
    [InlineData("""{"panels":{}}""", 0)]
    [InlineData("""[]""", 0)]
    public void The_list_counts_panels_without_validating(string json, int count) =>
        ReportSpecStore.PanelCount(json).Should().Be(count);

    private sealed class FakeRepository : IReportSpecRepository
    {
        public List<SavedReportSpec> Rows { get; } = [];

        public Task<IReadOnlyList<SavedReportSpec>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SavedReportSpec>>(Rows);

        public Task<SavedReportSpec?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Rows.FirstOrDefault(r => r.Id == id));

        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(Rows.Count);

        public Task<SavedReportSpecWrite> CreateAsync(string name, string specJson, int version, Guid? createdBySubjectId, CancellationToken ct = default)
        {
            if (Rows.Any(r => r.Name == name))
                return Task.FromResult(new SavedReportSpecWrite(SavedReportSpecWriteStatus.NameTaken));
            var row = new SavedReportSpec(Guid.CreateVersion7(), name, specJson, version, createdBySubjectId, DateTime.UtcNow, DateTime.UtcNow);
            Rows.Add(row);
            return Task.FromResult(new SavedReportSpecWrite(SavedReportSpecWriteStatus.Saved, row));
        }

        public Task<SavedReportSpecWrite> UpdateAsync(Guid id, string? name, string? specJson, int? version, CancellationToken ct = default)
        {
            var index = Rows.FindIndex(r => r.Id == id);
            if (index < 0)
                return Task.FromResult(new SavedReportSpecWrite(SavedReportSpecWriteStatus.NotFound));
            Rows[index] = Rows[index] with { Name = name ?? Rows[index].Name };
            return Task.FromResult(new SavedReportSpecWrite(SavedReportSpecWriteStatus.Saved, Rows[index]));
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Rows.RemoveAll(r => r.Id == id) > 0);
    }

    private static readonly Guid Creator = Guid.CreateVersion7();

    private static readonly CustomReportEditor Member = new(Creator, EditsAll: false);

    private static ReportSpecStore Store(FakeRepository repository)
    {
        var appearance = new Mock<ITenantAppearanceReader>();
        appearance.Setup(a => a.GetTenantAppearanceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDisplayPreferences());
        return new ReportSpecStore(repository, Validator, appearance.Object, Mock.Of<ITenantAccessor>(),
            Options.Create(new MvcJsonOptions()));
    }

    [Fact]
    public async Task Create_stores_the_normalised_spec_and_refuses_an_invalid_one_naming_the_panel()
    {
        var repository = new FakeRepository();
        var store = Store(repository);

        var saved = await store.CreateAsync(" Lunch ", JsonDocument.Parse(TwoPanels).RootElement, Member, default);

        saved.Status.Should().Be(CustomReportWriteStatus.Saved);
        saved.Report!.Name.Should().Be("Lunch");
        var stored = JsonNode.Parse(repository.Rows.Single().SpecJson)!;
        stored["panels"]![0]!["id"]!.GetValue<string>().Should().Be("p1");
        stored["panels"]![0]!["bindings"]!["x"]!.GetValue<string>().Should().Be("time");

        var refused = await store.CreateAsync("Other", JsonDocument.Parse("""
            { "version": 1, "panels": [{ "dataset": { "name": "insulin" }, "primitive": "time_series", "bindings": { "y": ["glucose"] } }] }
            """).RootElement, Member, default);
        refused.Status.Should().Be(CustomReportWriteStatus.Invalid);
        refused.Errors.Should().ContainSingle().Which.Path.Should().Be("panels[0].dataset.name");

        var taken = await store.CreateAsync("Lunch", JsonDocument.Parse(TwoPanels).RootElement, Member, default);
        taken.Status.Should().Be(CustomReportWriteStatus.NameTaken);
        repository.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task A_stored_spec_that_no_longer_validates_opens_as_stored_with_the_errors()
    {
        var repository = new FakeRepository();
        const string old = """{"version":0,"panels":[{"dataset":{"name":"glucose_series"},"primitive":"time_series","bindings":{"y":["glucose"]}}]}""";
        repository.Rows.Add(new SavedReportSpec(Guid.CreateVersion7(), "Old", old, 0, null, DateTime.UtcNow, DateTime.UtcNow));

        var report = await Store(repository).GetAsync(repository.Rows[0].Id, Member, default);

        report!.Errors.Should().ContainSingle().Which.Path.Should().Be("version");
        report.Spec.GetProperty("version").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task A_spec_over_16_kB_is_refused_before_it_is_validated()
    {
        var huge = JsonDocument.Parse(
            "{\"version\":1,\"title\":\"" + new string('a', ReportSpecLink.MaxSpecBytes) + "\",\"panels\":[]}").RootElement;

        var write = await Store(new FakeRepository()).CreateAsync("Big", huge, Member, default);

        write.Errors.Should().ContainSingle().Which.Should().Be(new CustomReportSpecError("spec", ReportSpecLink.TooLarge));
    }

    [Fact]
    public async Task A_missing_spec_is_an_error_naming_the_spec_not_an_exception()
    {
        var store = Store(new FakeRepository());

        (await store.CreateAsync("No spec", null, Member, default)).Errors
            .Should().ContainSingle().Which.Should().Be(new CustomReportSpecError("spec", "Send a spec."));
        (await store.CreateAsync("Undefined", default(JsonElement), Member, default)).Errors
            .Should().ContainSingle().Which.Path.Should().Be("spec");
    }

    [Fact]
    public async Task The_tenant_keeps_at_most_100_reports()
    {
        var repository = new FakeRepository();
        for (var i = 0; i < ReportSpecStore.MaxReports; i++)
            repository.Rows.Add(new SavedReportSpec(Guid.CreateVersion7(), $"r{i}", "{}", 1, Creator, DateTime.UtcNow, DateTime.UtcNow));

        var write = await Store(repository).CreateAsync("One more", JsonDocument.Parse(TwoPanels).RootElement, Member, default);

        write.Status.Should().Be(CustomReportWriteStatus.Invalid);
        write.Errors.Should().ContainSingle().Which.Should().Be(new CustomReportSpecError("name", ReportSpecStore.TooMany));
        repository.Rows.Should().HaveCount(ReportSpecStore.MaxReports);
    }

    [Fact]
    public async Task Only_the_creator_or_a_member_who_edits_every_report_renames_or_deletes()
    {
        var repository = new FakeRepository();
        var store = Store(repository);
        var saved = (await store.CreateAsync("Mine", JsonDocument.Parse(TwoPanels).RootElement, Member, default)).Report!;
        saved.CanEdit.Should().BeTrue();

        var other = new CustomReportEditor(Guid.CreateVersion7(), EditsAll: false);
        (await store.ListAsync(other, default)).Single().CanEdit.Should().BeFalse();
        (await store.GetAsync(saved.Id, other, default))!.CanEdit.Should().BeFalse();
        (await store.UpdateAsync(saved.Id, "Taken", null, other, default)).Status.Should().Be(CustomReportWriteStatus.Forbidden);
        (await store.DeleteAsync(saved.Id, other, default)).Should().Be(CustomReportWriteStatus.Forbidden);
        (await store.DeleteAsync(saved.Id, new CustomReportEditor(null, EditsAll: false), default))
            .Should().Be(CustomReportWriteStatus.Forbidden, "a caller with no subject owns nothing");

        (await store.UpdateAsync(saved.Id, "Renamed", null, Member, default)).Status.Should().Be(CustomReportWriteStatus.Saved);
        var owner = new CustomReportEditor(Guid.CreateVersion7(), EditsAll: true);
        (await store.ListAsync(owner, default)).Single().CanEdit.Should().BeTrue();
        (await store.DeleteAsync(saved.Id, owner, default)).Should().Be(CustomReportWriteStatus.Saved);
        (await store.DeleteAsync(saved.Id, owner, default)).Should().Be(CustomReportWriteStatus.NotFound);
    }
}
