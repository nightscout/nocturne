using Nocturne.API.Services;

namespace Nocturne.API.Tests.Services;

public class PoCatalogEditorTests
{
    private const string Catalog = """
        msgid ""
        msgstr ""
        "Content-Type: text/plain; charset=UTF-8\n"
        "Plural-Forms: nplurals=2; plural=(n != 1);\n"

        #: src/routes/+page.svelte
        msgid "Hello"
        msgstr ""

        #: src/routes/+page.svelte
        msgctxt "greeting"
        msgid "Welcome"
        msgstr "old translation"

        #, fuzzy
        #: src/routes/settings/+page.svelte
        msgid "Save changes"
        msgstr "stale fuzzy value"

        #: src/lib/components/Count.svelte
        msgid "One item"
        msgid_plural "{0} items"
        msgstr[0] ""
        msgstr[1] ""

        #: src/routes/+layout.svelte
        msgid ""
        "A long message that "
        "spans multiple lines"
        msgstr ""
        "an old translation "
        "that also spans lines"

        #~ msgid "Obsolete message"
        #~ msgstr "obsolete translation"
        """;

    private static PoEditResult Apply(params (string Ctx, string MsgId, string[] Values)[] entries) =>
        PoCatalogEditor.ApplyTranslations(
            Catalog,
            entries.ToDictionary(
                e => (e.Ctx, e.MsgId),
                e => (IReadOnlyList<string>)e.Values));

    [Fact]
    public void Applies_Simple_Translation()
    {
        var result = Apply(("", "Hello", ["Bonjour"]));

        result.Applied.Should().Be(1);
        result.Unmatched.Should().BeEmpty();
        result.Text.Replace("\r\n", "\n").Should().Contain("msgid \"Hello\"\nmsgstr \"Bonjour\"");
    }

    [Fact]
    public void Matches_On_Context()
    {
        var result = Apply(("greeting", "Welcome", ["Bienvenue"]));

        result.Applied.Should().Be(1);
        result.Text.Should().Contain("msgstr \"Bienvenue\"");
        result.Text.Should().NotContain("msgstr \"old translation\"");
    }

    [Fact]
    public void Wrong_Context_Does_Not_Match()
    {
        var result = Apply(("", "Welcome", ["Bienvenue"]));

        result.Applied.Should().Be(0);
        result.Unmatched.Should().ContainSingle().Which.MsgId.Should().Be("Welcome");
        result.Text.Should().Contain("old translation");
    }

    [Fact]
    public void Removes_Fuzzy_Flag_When_Translation_Supplied()
    {
        var result = Apply(("", "Save changes", ["Enregistrer"]));

        result.Applied.Should().Be(1);
        result.Text.Should().NotContain("fuzzy");
        result.Text.Should().Contain("msgstr \"Enregistrer\"");
        // The reference comment above the flags line must survive.
        result.Text.Should().Contain("#: src/routes/settings/+page.svelte");
    }

    [Fact]
    public void Applies_Plural_Translation_With_Matching_Form_Count()
    {
        var result = Apply(("", "One item", ["Un élément", "{0} éléments"]));

        result.Applied.Should().Be(1);
        result.Text.Should().Contain("msgstr[0] \"Un élément\"");
        result.Text.Should().Contain("msgstr[1] \"{0} éléments\"");
    }

    [Fact]
    public void Rejects_Plural_With_Wrong_Form_Count()
    {
        var result = Apply(("", "One item", ["Un élément"]));

        result.Applied.Should().Be(0);
        result.Unmatched.Should().ContainSingle().Which.MsgId.Should().Be("One item");
    }

    [Fact]
    public void Matches_Multiline_MsgId_And_Collapses_Multiline_MsgStr()
    {
        var result = Apply(("", "A long message that spans multiple lines", ["Traduction longue"]));

        result.Applied.Should().Be(1);
        result.Text.Should().Contain("msgstr \"Traduction longue\"");
        result.Text.Should().NotContain("an old translation");
        // The multiline msgid itself is untouched.
        result.Text.Should().Contain("\"A long message that \"");
    }

    [Fact]
    public void Never_Touches_Header_Or_Obsolete_Entries()
    {
        var result = Apply(
            ("", "", ["hijack header"]),
            ("", "Obsolete message", ["hijack obsolete"]));

        result.Applied.Should().Be(0);
        result.Text.Should().Contain("Content-Type: text/plain");
        result.Text.Should().Contain("#~ msgstr \"obsolete translation\"");
    }

    [Fact]
    public void Escapes_Special_Characters_In_Translations()
    {
        var result = Apply(("", "Hello", ["Line1\nLine2 \"quoted\" back\\slash"]));

        result.Applied.Should().Be(1);
        result.Text.Should().Contain("msgstr \"Line1\\nLine2 \\\"quoted\\\" back\\\\slash\"");
    }

    [Fact]
    public void Unescapes_MsgId_Before_Matching()
    {
        const string catalog = """
            msgid "Say \"hi\"\nnow"
            msgstr ""
            """;

        var result = PoCatalogEditor.ApplyTranslations(
            catalog,
            new Dictionary<(string, string), IReadOnlyList<string>>
            {
                [("", "Say \"hi\"\nnow")] = ["Dis « salut »"],
            });

        result.Applied.Should().Be(1);
    }

    [Fact]
    public void Leaves_Unrelated_Entries_And_Layout_Intact()
    {
        var result = Apply(("", "Hello", ["Bonjour"]));

        var untouched = Catalog
            .Replace("msgid \"Hello\"\r\nmsgstr \"\"", "")
            .Replace("msgid \"Hello\"\nmsgstr \"\"", "");
        foreach (var line in untouched.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0))
            result.Text.Should().Contain(line);
    }

    [Fact]
    public void Reports_Unknown_MsgIds_As_Unmatched()
    {
        var result = Apply(
            ("", "Hello", ["Bonjour"]),
            ("", "Does not exist", ["N'existe pas"]));

        result.Applied.Should().Be(1);
        result.Unmatched.Should().ContainSingle().Which.MsgId.Should().Be("Does not exist");
    }

    [Fact]
    public void Preserves_Crlf_Line_Endings()
    {
        var crlfCatalog = Catalog.Replace("\n", "\r\n");
        var result = PoCatalogEditor.ApplyTranslations(
            crlfCatalog,
            new Dictionary<(string, string), IReadOnlyList<string>>
            {
                [("", "Hello")] = ["Bonjour"],
            });

        result.Applied.Should().Be(1);
        result.Text.Should().Contain("msgstr \"Bonjour\"\r\n");
    }
}
