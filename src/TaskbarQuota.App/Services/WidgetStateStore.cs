using System;
using System.Globalization;
using System.IO;
using TaskbarQuota.Taskbar;
using TaskbarQuota.Usage;

namespace TaskbarQuota;

internal sealed class WidgetStateStore
{
    private const string VisibilityPolicyFileName = "widget-visibility-policy.txt";
    private const string BackgroundCliFileName = "widget-background-cli.txt";
    private const string LastProviderFileName = "last-widget-provider.txt";

    private readonly string _directory;

    public WidgetStateStore(string directory)
    {
        _directory = directory;
    }

    public WidgetVisibilityMode LoadVisibilityMode()
        => LoadEnum(
            VisibilityPolicyFileName,
            WidgetVisibilityMode.ShowWhileAnySupportedAiToolIsOpen);

    public bool LoadKeepVisibleWhileBackgroundCliAgentRunning()
        => LoadInt(BackgroundCliFileName) == 1;

    public ProviderId? LoadLastProvider()
    {
        try
        {
            string path = Path.Combine(_directory, LastProviderFileName);
            if (!File.Exists(path))
                return null;

            string value = File.ReadAllText(path).Trim();
            return Enum.TryParse(value, ignoreCase: false, out ProviderId provider)
                && Enum.IsDefined(provider)
                ? provider
                : null;
        }
        catch
        {
            return null;
        }
    }

    public bool SaveVisibilityMode(WidgetVisibilityMode mode)
        => Save(VisibilityPolicyFileName, ((int)mode).ToString(CultureInfo.InvariantCulture));

    public bool SaveKeepVisibleWhileBackgroundCliAgentRunning(bool enabled)
        => Save(BackgroundCliFileName, enabled ? "1" : "0");

    public bool SaveLastProvider(ProviderId provider)
        => Save(LastProviderFileName, provider.ToString());

    private TEnum LoadEnum<TEnum>(string fileName, TEnum fallback)
        where TEnum : struct, Enum
    {
        int? value = LoadInt(fileName);
        return value is int defined && Enum.IsDefined(typeof(TEnum), defined)
            ? (TEnum)Enum.ToObject(typeof(TEnum), defined)
            : fallback;
    }

    private int? LoadInt(string fileName)
    {
        try
        {
            string path = Path.Combine(_directory, fileName);
            if (!File.Exists(path))
                return null;

            return int.TryParse(
                File.ReadAllText(path),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value)
                ? value
                : null;
        }
        catch
        {
            return null;
        }
    }

    private bool Save(string fileName, string value)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, fileName), value);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
