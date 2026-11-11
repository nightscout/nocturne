using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Tests.Controllers.InvalidationKeyFixtures
{
    public sealed class DeclaringController : ControllerBase
    {
        [HttpGet]
        [RemoteQuery]
        public IActionResult Read() => Ok();

        [HttpPost]
        [RemoteCommand(Invalidates = ["Read", nameof(OtherController.OtherQuery), nameof(Write), "Missing"])]
        public IActionResult Bare() => Ok();

        [HttpPost]
        [RemoteCommand(Invalidates = ["Declaring_Read", "Other_OtherQuery"])]
        public IActionResult Qualified() => Ok();

        [HttpPost]
        [RemoteCommand(Invalidates = ["Declaring_Write", "Other_Nothing"])]
        public IActionResult BadlyQualified() => Ok();

        [HttpPost]
        [RemoteForm(Invalidates = ["Missing"])]
        public IActionResult Submit() => Ok();

        [HttpPost]
        [RemoteCommand]
        public IActionResult Write() => Ok();
    }

    public sealed class OtherController : ControllerBase
    {
        [HttpGet]
        [RemoteQuery]
        public IActionResult OtherQuery() => Ok();
    }

    public sealed class SuffixedController : ControllerBase
    {
        [HttpGet]
        [RemoteQuery]
        public Task<IActionResult> ReadAsync() => Task.FromResult<IActionResult>(Ok());

        [HttpPost]
        [RemoteCommand]
        public Task<IActionResult> WriteAsync() => Task.FromResult<IActionResult>(Ok());

        [HttpPost]
        [RemoteForm]
        public Task<IActionResult> SubmitAsync() => Task.FromResult<IActionResult>(Ok());

        [HttpGet]
        public Task<IActionResult> NotRemoteAsync() => Task.FromResult<IActionResult>(Ok());
    }
}

namespace Nocturne.API.Tests.Controllers.InvalidationKeyFixtures.First
{
    public sealed class SharedTagController : ControllerBase
    {
        [HttpPost]
        [RemoteCommand(Invalidates = ["SiblingQuery", "OwnQuery"])]
        public IActionResult Write() => Ok();

        [HttpGet]
        [RemoteQuery]
        public IActionResult OwnQuery() => Ok();
    }
}

namespace Nocturne.API.Tests.Controllers.InvalidationKeyFixtures.Second
{
    public sealed class SharedTagController : ControllerBase
    {
        [HttpGet]
        [RemoteQuery]
        public IActionResult SiblingQuery() => Ok();
    }
}
