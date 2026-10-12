using System;
using System.Reflection;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace NewQ.App.Updates;

/// <summary>
/// Updates from the GitHub releases of the repository (Velopack). Only the installed app updates itself; a build run
/// from Visual Studio or the dist folder reports itself as not installed and never checks.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Public repository whose releases carry the installer and the update packages.</summary>
    public const string RepositoryUrl = "https://github.com/emanuelepelandi/NewQ";

    private readonly UpdateManager _manager = new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));

    /// <summary>Installed with the setup (updates possible).</summary>
    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>The version running now.</summary>
    public string CurrentVersion => _manager.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    /// <summary>A newer release, or null.</summary>
    public Task<UpdateInfo?> CheckAsync() => _manager.CheckForUpdatesAsync();

    /// <summary>Downloads the update (only the differences when possible); progress 0-100.</summary>
    public Task DownloadAsync(UpdateInfo update, Action<int> progress) => _manager.DownloadUpdatesAsync(update, progress);

    /// <summary>Closes NewQ, installs the downloaded update and starts the new version.</summary>
    public void ApplyAndRestart(UpdateInfo update) => _manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
}
