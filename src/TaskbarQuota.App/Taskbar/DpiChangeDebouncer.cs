namespace TaskbarQuota.Taskbar;

/// <summary>Rejects one-off DPI readings while accepting a stable changed value promptly.</summary>
internal sealed class DpiChangeDebouncer
{
    private readonly int requiredSamples;
    private uint candidate;
    private int samples;

    public DpiChangeDebouncer(int requiredSamples = 2)
    {
        this.requiredSamples = requiredSamples < 1 ? 1 : requiredSamples;
    }

    public bool Observe(uint appliedDpi, uint observedDpi)
    {
        if (observedDpi == 0 || observedDpi == appliedDpi)
        {
            Reset();
            return false;
        }

        if (candidate != observedDpi)
        {
            candidate = observedDpi;
            samples = 1;
        }
        else
        {
            samples++;
        }

        return samples >= requiredSamples;
    }

    public void Reset()
    {
        candidate = 0;
        samples = 0;
    }
}
