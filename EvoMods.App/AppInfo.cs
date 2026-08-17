using System.Reflection;

using EvoMods.Core.Game;

namespace EvoMods.App;

/// <summary>Facts about this build and this machine that more than one page needs.</summary>
internal static class AppInfo
{
    /// <summary>The version as published, without the git hash the build appends.</summary>
    public static string Version { get; } =
        Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion.Split('+')[0]
        ?? "unknown";

    /// <summary>Every Assetto Corsa EVO install found, best first. Never throws.</summary>
    public static List<string> FindGames(out string? error)
    {
        try
        {
            error = null;
            return GameLocator.FindAll();
        }
        catch (Exception e)
        {
            error = e.Message;
            return [];
        }
    }

    /// <summary>The install to act on, or null when there is none to act on.</summary>
    public static string? GameRoot => FindGames(out _).FirstOrDefault();
}
