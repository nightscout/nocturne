namespace Nocturne.Connectors.MyLife.Services;

public class MyLifeSessionStore
{
    public string ServiceUrl { get; private set; } = string.Empty;
    public string AuthToken { get; private set; } = string.Empty;
    public string UserId { get; private set; } = string.Empty;
    public string PatientId { get; private set; } = string.Empty;

    public void SetSession(string serviceUrl, string authToken, string userId, string patientId)
    {
        ServiceUrl = serviceUrl;
        AuthToken = authToken;
        UserId = userId;
        PatientId = patientId;
    }

    public void Clear()
    {
        ServiceUrl = string.Empty;
        AuthToken = string.Empty;
        UserId = string.Empty;
        PatientId = string.Empty;
    }
}