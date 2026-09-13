using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Services;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// <see cref="TranslationsController.Validate"/> is the only gate in front of
/// the anonymous relay ingress: everything it lets through is written into a
/// committed catalog file and a pull request on the upstream repository.
/// </summary>
public class TranslationsControllerValidationTests
{
    private static TranslationsController CreateController()
    {
        var service = new GitHubTranslationService(
            Mock.Of<IHttpClientFactory>(),
            Options.Create(new GitHubTranslationOptions()),
            NullLogger<GitHubTranslationService>.Instance);

        return new TranslationsController(
            service,
            NullLogger<TranslationsController>.Instance)
        {
            ProblemDetailsFactory = new TestProblemDetailsFactory(),
        };
    }

    private static TranslationContributionRequest Request(
        string locale = "fr",
        List<TranslationEntryDto>? entries = null,
        string name = "Jane Doe",
        string? gitHubUsername = null,
        string? email = null,
        string? note = null) => new()
        {
            Locale = locale,
            Entries = entries ?? [new TranslationEntryDto { MsgId = "Hello", Translations = ["Bonjour"] }],
            Contributor = new TranslationContributorDto
            {
                Name = name,
                GitHubUsername = gitHubUsername,
                Email = email,
            },
            Note = note,
        };

    private static string? Reject(TranslationContributionRequest request)
    {
        var result = CreateController().Validate(request);
        if (result is null) return null;
        result.StatusCode.Should().Be(400);
        return ((ProblemDetails)result.Value!).Detail;
    }

    // --- locale ---------------------------------------------------------
    // The locale regex is also the path guard: the value is interpolated
    // straight into "{CatalogDir}/{locale}.po", so anything that admits a
    // separator or a dot segment becomes a repo-path traversal.

    [Theory]
    [InlineData("fr")]
    [InlineData("fil")]
    [InlineData("pt-BR")]
    [InlineData("zh-Hans")]
    [InlineData("es-419")]
    public void Validate_Accepts_Well_Formed_Locales(string locale)
    {
        Reject(Request(locale: locale)).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("f")]
    [InlineData("FR")]
    [InlineData("fren")]
    [InlineData("fr_BR")]
    [InlineData("fr-")]
    [InlineData("pt-BRAZILIAN")]
    [InlineData("../../secrets")]
    [InlineData("fr/../../.github/workflows/ci")]
    [InlineData("..")]
    [InlineData("fr.po")]
    [InlineData("fr\n")]
    [InlineData("fr\nen")]
    public void Validate_Rejects_Malformed_Locales(string locale)
    {
        Reject(Request(locale: locale)).Should().StartWith("Invalid locale");
    }

    // --- entry caps -----------------------------------------------------

    [Fact]
    public void Validate_Rejects_Empty_Entry_List()
    {
        Reject(Request(entries: [])).Should().Be("Between 1 and 500 entries required");
    }

    [Fact]
    public void Validate_Accepts_Exactly_The_Entry_Cap()
    {
        var entries = Enumerable.Range(0, 500)
            .Select(i => new TranslationEntryDto { MsgId = $"m{i}", Translations = ["t"] })
            .ToList();

        Reject(Request(entries: entries)).Should().BeNull();
    }

    [Fact]
    public void Validate_Rejects_One_Entry_Over_The_Cap()
    {
        var entries = Enumerable.Range(0, 501)
            .Select(i => new TranslationEntryDto { MsgId = $"m{i}", Translations = ["t"] })
            .ToList();

        Reject(Request(entries: entries)).Should().Be("Between 1 and 500 entries required");
    }

    [Fact]
    public void Validate_Accepts_MsgId_At_The_Length_Cap()
    {
        var entries = new List<TranslationEntryDto>
        {
            new() { MsgId = new string('m', 4096), Translations = ["t"] },
        };

        Reject(Request(entries: entries)).Should().BeNull();
    }

    [Fact]
    public void Validate_Rejects_MsgId_Over_The_Length_Cap()
    {
        var entries = new List<TranslationEntryDto>
        {
            new() { MsgId = new string('m', 4097), Translations = ["t"] },
        };

        Reject(Request(entries: entries)).Should().StartWith("Each entry needs a msgid");
    }

    [Fact]
    public void Validate_Rejects_Empty_MsgId()
    {
        var entries = new List<TranslationEntryDto> { new() { MsgId = "", Translations = ["t"] } };

        Reject(Request(entries: entries)).Should().StartWith("Each entry needs a msgid");
    }

    [Fact]
    public void Validate_Rejects_Overlong_Context()
    {
        var entries = new List<TranslationEntryDto>
        {
            new() { MsgId = "Hello", Context = new string('c', 257), Translations = ["t"] },
        };

        Reject(Request(entries: entries)).Should().StartWith("Entry context must be");
    }

    [Fact]
    public void Validate_Accepts_Context_At_The_Length_Cap()
    {
        var entries = new List<TranslationEntryDto>
        {
            new() { MsgId = "Hello", Context = new string('c', 256), Translations = ["t"] },
        };

        Reject(Request(entries: entries)).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void Validate_Rejects_Out_Of_Range_Plural_Counts(int count)
    {
        var entries = new List<TranslationEntryDto>
        {
            new() { MsgId = "Hello", Translations = [.. Enumerable.Repeat("t", count)] },
        };

        Reject(Request(entries: entries)).Should().StartWith("Each entry needs 1-8");
    }

    [Fact]
    public void Validate_Accepts_Eight_Plural_Forms()
    {
        var entries = new List<TranslationEntryDto>
        {
            new() { MsgId = "Hello", Translations = [.. Enumerable.Repeat("t", 8)] },
        };

        Reject(Request(entries: entries)).Should().BeNull();
    }

    [Fact]
    public void Validate_Rejects_Empty_Translation_Value()
    {
        var entries = new List<TranslationEntryDto>
        {
            new() { MsgId = "Hello", Translations = ["Bonjour", ""] },
        };

        Reject(Request(entries: entries)).Should().StartWith("Each entry needs 1-8");
    }

    [Fact]
    public void Validate_Rejects_Overlong_Translation_Value()
    {
        var entries = new List<TranslationEntryDto>
        {
            new() { MsgId = "Hello", Translations = [new string('t', 8193)] },
        };

        Reject(Request(entries: entries)).Should().StartWith("Each entry needs 1-8");
    }

    // --- duplicate keys -------------------------------------------------

    [Fact]
    public void Validate_Rejects_Duplicate_MsgId_And_Context()
    {
        var entries = new List<TranslationEntryDto>
        {
            new() { MsgId = "Hello", Context = "greeting", Translations = ["Bonjour"] },
            new() { MsgId = "Hello", Context = "greeting", Translations = ["Salut"] },
        };

        Reject(Request(entries: entries)).Should().Be("Duplicate entry for the same msgid and context");
    }

    [Fact]
    public void Validate_Allows_Same_MsgId_In_Different_Contexts()
    {
        var entries = new List<TranslationEntryDto>
        {
            new() { MsgId = "Hello", Context = "greeting", Translations = ["Bonjour"] },
            new() { MsgId = "Hello", Context = "farewell", Translations = ["Salut"] },
        };

        Reject(Request(entries: entries)).Should().BeNull();
    }

    [Fact]
    public void Validate_Treats_Null_And_Empty_Context_As_The_Same_Key()
    {
        var entries = new List<TranslationEntryDto>
        {
            new() { MsgId = "Hello", Context = null, Translations = ["Bonjour"] },
            new() { MsgId = "Hello", Context = "", Translations = ["Salut"] },
        };

        Reject(Request(entries: entries)).Should().Be("Duplicate entry for the same msgid and context");
    }

    // --- control characters in translation values -----------------------
    // PoCatalogEditor.Escape only handles \\ \" \n \t \r, so any other
    // control character would be written raw into the committed catalog.

    [Theory]
    [InlineData("\u0000")]
    [InlineData("\u001b[31m")]
    [InlineData("\u000c")]
    [InlineData("\u0085")]
    [InlineData("\u009b")]
    public void Validate_Rejects_Control_Chars_In_Translations(string suffix)
    {
        var entries = new List<TranslationEntryDto>
        {
            new() { MsgId = "Hello", Translations = ["Bonjour" + suffix] },
        };

        Reject(Request(entries: entries)).Should().Be("Translations cannot contain control characters");
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\t")]
    public void Validate_Allows_The_Control_Chars_The_Catalog_Escaper_Handles(string suffix)
    {
        var entries = new List<TranslationEntryDto>
        {
            new() { MsgId = "Hello", Translations = ["Bonjour" + suffix] },
        };

        Reject(Request(entries: entries)).Should().BeNull();
    }

    // --- contributor identity -------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_Rejects_Blank_Contributor_Name(string name)
    {
        Reject(Request(name: name)).Should().StartWith("Contributor name is required");
    }

    [Fact]
    public void Validate_Rejects_Overlong_Contributor_Name()
    {
        Reject(Request(name: new string('n', 129))).Should().StartWith("Contributor name is required");
    }

    [Fact]
    public void Validate_Rejects_Control_Chars_In_Contributor_Name()
    {
        Reject(Request(name: "Jane\nCo-authored-by: victim <v@example.com>"))
            .Should().StartWith("Contributor name is required");
    }

    [Theory]
    [InlineData("janedoe")]
    [InlineData("Jane-Doe")]
    public void Validate_Accepts_Well_Formed_GitHub_Usernames(string username)
    {
        Reject(Request(gitHubUsername: username)).Should().BeNull();
    }

    [Theory]
    [InlineData("-janedoe")]
    [InlineData("janedoe-")]
    [InlineData("jane--doe")]
    [InlineData("jane doe")]
    [InlineData("jane\ndoe")]
    public void Validate_Rejects_Malformed_GitHub_Usernames(string username)
    {
        Reject(Request(gitHubUsername: username)).Should().Be("Invalid GitHub username");
    }

    [Fact]
    public void Validate_Accepts_GitHub_Username_At_39_Chars()
    {
        Reject(Request(gitHubUsername: new string('a', 39))).Should().BeNull();
    }

    [Fact]
    public void Validate_Rejects_GitHub_Username_At_40_Chars()
    {
        Reject(Request(gitHubUsername: new string('a', 40))).Should().Be("Invalid GitHub username");
    }

    [Theory]
    [InlineData("jane@example.com")]
    [InlineData("jane.doe+i18n@sub.example.co.uk")]
    public void Validate_Accepts_Well_Formed_Emails(string email)
    {
        Reject(Request(email: email)).Should().BeNull();
    }

    [Theory]
    [InlineData("jane")]
    [InlineData("jane@example")]
    [InlineData("jane@@example.com")]
    [InlineData("jane doe@example.com")]
    [InlineData("jane@example.com>")]
    [InlineData("<jane@example.com")]
    [InlineData("jane@example.com\nSigned-off-by: x <x@y.z>")]
    public void Validate_Rejects_Malformed_Emails(string email)
    {
        Reject(Request(email: email)).Should().Be("Invalid contributor email");
    }

    [Fact]
    public void Validate_Rejects_Overlong_Email()
    {
        var email = new string('a', 250) + "@e.co";
        Reject(Request(email: email)).Should().Be("Invalid contributor email");
    }

    // --- note ------------------------------------------------------------

    [Fact]
    public void Validate_Accepts_Note_At_The_Length_Cap()
    {
        Reject(Request(note: new string('n', 2000))).Should().BeNull();
    }

    [Fact]
    public void Validate_Rejects_Note_Over_The_Length_Cap()
    {
        Reject(Request(note: new string('n', 2001))).Should().Be("Note must be under 2000 characters");
    }

    [Fact]
    public void Validate_Accepts_A_Fully_Populated_Request()
    {
        Reject(Request(
            gitHubUsername: "janedoe",
            email: "jane@example.com",
            note: "Reviewed against the app UI")).Should().BeNull();
    }

    private sealed class TestProblemDetailsFactory : ProblemDetailsFactory
    {
        public override ProblemDetails CreateProblemDetails(
            HttpContext httpContext, int? statusCode = null, string? title = null,
            string? type = null, string? detail = null, string? instance = null) =>
            new() { Status = statusCode, Title = title, Type = type, Detail = detail, Instance = instance };

        public override ValidationProblemDetails CreateValidationProblemDetails(
            HttpContext httpContext, ModelStateDictionary modelStateDictionary, int? statusCode = null,
            string? title = null, string? type = null, string? detail = null, string? instance = null) =>
            new(modelStateDictionary) { Status = statusCode, Title = title, Type = type, Detail = detail, Instance = instance };
    }
}
