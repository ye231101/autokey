namespace AutoKey.Platform;

internal sealed class NoOpScope : IDisposable
{
    public static readonly NoOpScope Instance = new();

    public void Dispose()
    {
    }
}
