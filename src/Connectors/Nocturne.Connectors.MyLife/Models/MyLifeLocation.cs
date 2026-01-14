namespace Nocturne.Connectors.MyLife.Models;

public class MyLifeLocation
{
    public MyLifeCountry? Country20 { get; set; }
    public string? Id { get; set; }
    public string? Login { get; set; }
    public string? CountryIcoAlpha2 { get; set; }
    public string? LastAccess { get; set; }
}

public class MyLifeCountry
{
    public string? RestServiceUrl { get; set; }
    public int Id { get; set; }
    public string? IsoAlpha2 { get; set; }
    public string? IsoNum3 { get; set; }
    public string? Name { get; set; }
    public int DistributorId { get; set; }
    public bool AllowRegistration { get; set; }
    public string? LoginUrl { get; set; }
    public string? ServiceUrl { get; set; }
    public string? RegisterUrl { get; set; }
    public bool OwnResponsibility { get; set; }
}