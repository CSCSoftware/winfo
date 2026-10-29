namespace WinfoMain.Browser;

public record BrowserProfile(
    string ProcessName,
    string AddressBarName,
    string? AddressBarAutomationId,
    string WindowClassName);
