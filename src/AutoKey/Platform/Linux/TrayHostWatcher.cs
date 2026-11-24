using Tmds.DBus.Protocol;

namespace AutoKey.Platform.Linux;

/// <summary>
/// Tracks whether the desktop shows tray icons. On Linux they're StatusNotifierItems, which only appear while a host
/// owns the <c>org.kde.StatusNotifierWatcher</c> name on the session bus: KDE Plasma and most other desktops have one,
/// GNOME only with the AppIndicator extension. (Tmds.DBus.Protocol comes with Avalonia, which uses it for the tray
/// icon itself.)
/// </summary>
internal sealed class TrayHostWatcher : IDisposable
{
    private const string WatcherName = "org.kde.StatusNotifierWatcher";

    private readonly DBusConnection? _connection;
    private volatile NameOwnerWatcher? _watcher;

    public TrayHostWatcher()
    {
        try
        {
            if (DBusAddress.Session is { } address)
            {
                _connection = new DBusConnection(address);
                _ = WatchAsync(_connection);
            }
        }
        catch (Exception)
        {
            // No usable session bus, so there's no tray either.
        }
    }

    /// <summary>Whether a tray host is running right now (false until the session bus has answered).</summary>
    public bool IsAvailable => _watcher?.GetCurrentOwner() is not null;

    // Disconnecting ends the watch too; the watcher then reports no owner.
    public void Dispose() => _connection?.Dispose();

    private async Task WatchAsync(DBusConnection connection)
    {
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            _watcher = await connection.WatchNameOwnerAsync(WatcherName).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The session bus can't be reached: treat that as having no tray.
        }
    }
}
