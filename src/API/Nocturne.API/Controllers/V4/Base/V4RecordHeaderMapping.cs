using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Base;

/// <summary>
/// Copies the <see cref="IV4RecordHeader"/> of a request onto the model it becomes.
/// </summary>
public static class V4RecordHeaderMapping
{
    /// <summary>
    /// Sets the model's timestamp (as UTC), offset, device, app and data source from the request.
    /// </summary>
    /// <param name="model">The model being mapped.</param>
    /// <param name="request">The request that supplies the header.</param>
    /// <returns><paramref name="model"/>, so a mapping reads as one expression.</returns>
    public static TModel WithHeaderFrom<TModel>(this TModel model, IV4RecordHeader request)
        where TModel : IV4Record
    {
        model.Timestamp = request.Timestamp.UtcDateTime;
        model.UtcOffset = request.UtcOffset;
        model.Device = request.Device;
        model.App = request.App;
        model.DataSource = request.DataSource;
        return model;
    }
}
