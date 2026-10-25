using System.Reflection;
using FluentAssertions;
using Nocturne.API.Controllers.V4.Base;
using OpenApi.Remote.Attributes;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Base;

/// <summary>
/// No page renders the list reads with the arguments a generated command refreshes them with, so a
/// delete or restore declaring them would pay two API reads that refresh nothing. A call site
/// refreshes its own query (<c>refresh()</c> or the realtime store). Without a declared
/// <c>Invalidates</c> the server never calls <c>requested()</c>, so <c>.updates(...)</c> on these
/// commands does nothing.
/// </summary>
[Trait("Category", "Unit")]
public class V4CrudInvalidationTests
{
    [Theory]
    [InlineData(nameof(V4CrudControllerBase<,,,>.Delete))]
    [InlineData(nameof(V4CrudControllerBase<,,,>.Restore))]
    [InlineData(nameof(V4CrudControllerBase<,,,>.BulkRestore))]
    public void DeletingOrRestoring_DeclaresNoBlanketRefresh(string write)
    {
        var command = typeof(V4CrudControllerBase<,,,>).GetMethod(write)!.GetCustomAttribute<RemoteCommandAttribute>();

        command.Should().NotBeNull();
        command!.Invalidates.Should().BeNullOrEmpty();
    }
}
