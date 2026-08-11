using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace AppLockerOverlay;

/// <summary>
/// Beta: best-effort USB mass storage disable via USBSTOR (HKLM, needs elevation) plus optional Device Manager handling from the hub process.
/// Does not disable USB keyboards/mice (that would lock you out). Not a security boundary against kernel malware.
/// </summary>
internal static class UsbInputLockdownBeta
{
    private const string UsbStorServiceKey = @"SYSTEM\CurrentControlSet\Services\USBSTOR";

    /// <summary>USBSTOR Start: 3 = manual, 4 = disabled.</summary>
    private const int UsbStorStartManual = 3;

    private const int UsbStorStartDisabled = 4;

    internal static bool IsProcessElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(id);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryReadUsbStorStart(out int startValue, out string? error)
    {
        startValue = 0;
        error = null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(UsbStorServiceKey, writable: false);
            if (k is null)
            {
                error = "USBSTOR registry key was not found.";
                return false;
            }

            var o = k.GetValue("Start");
            if (o is int i)
            {
                startValue = i;
                return true;
            }

            if (o is not null && int.TryParse(o.ToString(), out var parsed))
            {
                startValue = parsed;
                return true;
            }

            error = "USBSTOR Start value was missing or not a number.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool TryWriteUsbStorStart(int startValue, out string? error)
    {
        error = null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(UsbStorServiceKey, writable: true);
            if (k is null)
            {
                error = "Could not open USBSTOR for write (run as Administrator).";
                return false;
            }

            k.SetValue("Start", startValue, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Captures current Start, sets disabled (4). Updates <paramref name="cfg"/>.</summary>
    internal static void TryEnableUsbMassStorage(LockerConfig cfg, out bool elevated, out bool storageApplied, out string? detail)
    {
        elevated = IsProcessElevated();
        storageApplied = false;
        detail = null;

        if (!elevated)
        {
            detail = "Not elevated — USB mass storage (USBSTOR) was not changed.";
            return;
        }

        if (!TryReadUsbStorStart(out var current, out var readErr))
        {
            detail = readErr ?? "Could not read USBSTOR.";
            return;
        }

        if (cfg.UsbStorStartBeforeLockdown < 0)
        {
            cfg.UsbStorStartBeforeLockdown = current;
        }

        if (current == UsbStorStartDisabled)
        {
            storageApplied = true;
            detail = "USBSTOR was already disabled.";
            return;
        }

        if (!TryWriteUsbStorStart(UsbStorStartDisabled, out var writeErr))
        {
            detail = writeErr ?? "Could not write USBSTOR.";
            return;
        }

        storageApplied = true;
        detail = "USBSTOR set to disabled (4). A reboot or replug may be needed for all devices to notice.";
    }

    /// <summary>Restores USBSTOR Start from <see cref="LockerConfig.UsbStorStartBeforeLockdown"/> when possible.</summary>
    internal static void TryDisableUsbMassStorage(LockerConfig cfg, out string? detail)
    {
        detail = null;
        if (!IsProcessElevated())
        {
            detail = "Not elevated — could not restore USBSTOR. Run as Administrator to undo the service change.";
            cfg.UsbStorStartBeforeLockdown = -1;
            return;
        }

        if (cfg.UsbStorStartBeforeLockdown < 0)
        {
            detail = "No saved USBSTOR value to restore.";
            return;
        }

        var restore = cfg.UsbStorStartBeforeLockdown;
        if (restore is < 0 or > 4)
        {
            restore = UsbStorStartManual;
        }

        if (!TryWriteUsbStorStart(restore, out var err))
        {
            detail = err ?? "Restore failed.";
            return;
        }

        cfg.UsbStorStartBeforeLockdown = -1;
        detail = "USBSTOR restored.";
    }

    internal static bool LooksLikeDeviceManagerWindow(string? mainWindowTitle)
    {
        if (string.IsNullOrEmpty(mainWindowTitle))
        {
            return false;
        }

        return mainWindowTitle.Contains("Device Manager", StringComparison.OrdinalIgnoreCase);
    }

    internal static void TryCloseProcess(Process proc)
    {
        try
        {
            if (!proc.CloseMainWindow())
            {
                Thread.Sleep(400);
            }

            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: false);
            }
        }
        catch (Win32Exception)
        {
            try
            {
                proc.Kill(entireProcessTree: false);
            }
            catch
            {
                // ignored
            }
        }
        catch
        {
            try
            {
                proc.Kill(entireProcessTree: false);
            }
            catch
            {
                // ignored
            }
        }
    }
}
