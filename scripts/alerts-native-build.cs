// scripts/alerts-native-build.cs
//
// Builds the nocturne_alerts native library for a Linux container image and writes the
// `dotnet publish` properties that pack it, one per line, to <properties-file>.
//
// Usage:
//   dotnet run scripts/alerts-native-build.cs <linux-x64|linux-arm64> <properties-file>

#:project Shared/Shared.csproj

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run scripts/alerts-native-build.cs <linux-x64|linux-arm64> <properties-file>");
    return 2;
}

var properties = AlertsNativeBuild.BuildForContainer(Directory.GetCurrentDirectory(), args[0]);
if (properties is null)
    return 1;

File.WriteAllLines(args[1], properties);
return 0;
