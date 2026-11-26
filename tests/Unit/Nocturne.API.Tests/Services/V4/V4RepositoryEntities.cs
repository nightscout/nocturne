using Nocturne.Infrastructure.Data.Repositories.V4;

namespace Nocturne.API.Tests.Services.V4;

internal static class V4RepositoryEntities
{
    /// <summary>The entity <paramref name="repository"/> reads through <see cref="V4RepositoryBase{TModel, TEntity}"/>.</summary>
    public static Type Of(Type repository)
    {
        for (var type = repository; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(V4RepositoryBase<,>))
                return type.GetGenericArguments()[1];
        }

        throw new InvalidOperationException($"{repository.Name} does not read through V4RepositoryBase");
    }
}
