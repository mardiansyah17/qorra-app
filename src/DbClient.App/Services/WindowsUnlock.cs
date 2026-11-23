using System.Windows;
using System.Windows.Interop;
using Windows.Security.Credentials.UI;

namespace DbClient.App.Services;

internal static class WindowsUnlock
{
    public static async Task<bool> IsAvailableAsync()
    {
        try { return await UserConsentVerifier.CheckAvailabilityAsync() == UserConsentVerifierAvailability.Available; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { return false; }
    }

    public static async Task<bool> VerifyAsync(string message)
    {
        if (!await IsAvailableAsync()) return true;
        try
        {
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
            var hwnd = owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
            var result = hwnd != IntPtr.Zero
                ? await UserConsentVerifierInterop.RequestVerificationForWindowAsync(hwnd, message)
                : await UserConsentVerifier.RequestVerificationAsync(message);
            return result == UserConsentVerificationResult.Verified;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
