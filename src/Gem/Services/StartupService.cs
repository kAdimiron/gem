using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Gem.Services;

public interface IStartupService
{
    bool IsStartupEnabled();
    bool SetStartupEnabled(bool enabled);
}

public sealed class WindowsStartupService : IStartupService
{
    public const string DefaultRunSubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultStartupApprovedSubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string DefaultValueName = "Gem";

    private readonly string _runSubKeyPath;
    private readonly string _valueName;
    private readonly Func<string?> _executablePathProvider;
    private readonly bool _manageStartupApproved;

    public WindowsStartupService(
        string runSubKeyPath = DefaultRunSubKeyPath,
        string valueName = DefaultValueName,
        Func<string?>? executablePathProvider = null)
    {
        _runSubKeyPath = runSubKeyPath;
        _valueName = valueName;
        _executablePathProvider = executablePathProvider ?? ResolveExecutablePath;
        _manageStartupApproved = string.Equals(runSubKeyPath, DefaultRunSubKeyPath, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsStartupEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_runSubKeyPath, writable: false);
            if (key?.GetValue(_valueName) is not string value || string.IsNullOrWhiteSpace(value))
                return false;

            if (_manageStartupApproved && IsDisabledInStartupApproved())
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool SetStartupEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_runSubKeyPath, writable: true);
                key?.DeleteValue(_valueName, throwOnMissingValue: false);
                ClearStartupApprovedOverride();
                return true;
            }

            string? exePath = _executablePathProvider();
            if (string.IsNullOrWhiteSpace(exePath))
                return false;

            using RegistryKey runKey = Registry.CurrentUser.CreateSubKey(_runSubKeyPath, writable: true);
            runKey.SetValue(_valueName, BuildLaunchCommand(exePath), RegistryValueKind.String);
            ClearStartupApprovedOverride();
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string BuildLaunchCommand(string executablePath)
    {
        string trimmed = executablePath.Trim().Trim('"');
        return $"\"{trimmed}\"";
    }

    internal static string? ResolveExecutablePath()
    {
        string? candidate = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            try
            {
                candidate = Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch
            {
                candidate = null;
            }
        }

        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        string fileName = Path.GetFileName(candidate);
        if (string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return null;

        return File.Exists(candidate) && candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    private bool IsDisabledInStartupApproved()
    {
        try
        {
            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(DefaultStartupApprovedSubKeyPath, writable: false);
            if (approved?.GetValue(_valueName) is byte[] bytes && bytes.Length >= 4)
            {
                // In Windows StartupApproved\Run, 0x02 indicates enabled; 0x03/0x01 indicates disabled by user.
                return bytes[0] != 0x02;
            }
        }
        catch
        {
        }

        return false;
    }

    private void ClearStartupApprovedOverride()
    {
        if (!_manageStartupApproved)
            return;

        try
        {
            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(DefaultStartupApprovedSubKeyPath, writable: true);
            approved?.DeleteValue(_valueName, throwOnMissingValue: false);
        }
        catch
        {
        }
    }
}
