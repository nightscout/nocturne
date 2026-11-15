namespace Nocturne.Connectors.Oura.Export;

/// <summary>An upload that cannot be read as an Oura data export. <see cref="Code"/> is stable for clients.</summary>
public sealed class OuraExportException(string code, Exception? inner = null) : Exception(code, inner)
{
    public const string NotAZip = "not_a_zip";
    public const string NotAnOuraExport = "not_an_oura_export";
    public const string TooLarge = "export_too_large";

    public string Code { get; } = code;
}
