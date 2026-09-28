using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Nocturne.Infrastructure.Data;

/// <summary>
/// The assembly holding the migrations of <see cref="NocturneDbContext"/>. It is built beside the
/// API but referenced by no project at compile time, which keeps its designer files out of the
/// <c>dotnet watch</c> workspace, so it is absent from the API's <c>deps.json</c> and has to be
/// loaded from the output directory. Test projects reference it and <c>dotnet ef</c> has already
/// loaded it, so both get it by name.
/// </summary>
public static class NocturneMigrationsAssembly
{
    /// <summary>The migrations assembly name, which <c>dotnet ef -p</c> must match.</summary>
    public const string Name = "Nocturne.Infrastructure.Data.Migrations";

    private static readonly Lazy<Assembly> Assembly = new(Load);

    /// <summary>Points EF at the migrations assembly. Every context that migrates needs it.</summary>
    public static TBuilder UseNocturneMigrations<TBuilder, TExtension>(
        this RelationalDbContextOptionsBuilder<TBuilder, TExtension> builder)
        where TBuilder : RelationalDbContextOptionsBuilder<TBuilder, TExtension>
        where TExtension : RelationalOptionsExtension, new()
        => builder.MigrationsAssembly(Assembly.Value);

    private static Assembly Load()
    {
        try
        {
            return System.Reflection.Assembly.Load(new AssemblyName(Name));
        }
        catch (FileNotFoundException)
        {
            return System.Reflection.Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, Name + ".dll"));
        }
    }
}
