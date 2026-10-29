namespace WinfoMain.SystemInfo;

/// <summary>
/// Represents an installed application from the Windows Registry.
/// </summary>
public record InstalledApp(
    string Name,
    string? Version,
    string? Publisher,
    string? InstallPath,
    string? InstallDate);
