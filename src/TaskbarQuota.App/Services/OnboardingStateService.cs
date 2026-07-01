using System.IO;

namespace TaskbarQuota;

internal static class OnboardingStateService
{
    private static string StatePath => Path.Combine(AppStorage.AppDataDirectory, "onboarding-dismissed");

    public static bool IsDismissed()
    {
        try { return File.Exists(StatePath); }
        catch { return false; }
    }

    public static void Dismiss()
    {
        try
        {
            Directory.CreateDirectory(AppStorage.AppDataDirectory);
            File.WriteAllText(StatePath, "1");
        }
        catch
        {
            // Best effort; showing onboarding again is harmless.
        }
    }
}
