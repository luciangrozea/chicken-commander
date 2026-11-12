using System.Collections.Concurrent;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Vfs;

namespace ChickenCommander.Core.Remote;

/// <summary>
/// An open connection. Browsing uses one shared client (serialized by a lock); every file transfer opens
/// its own client, so a long download never blocks the panel and the libraries are never used from two threads.
/// </summary>
public sealed class RemoteSession(RemoteConnection connection, Func<IRemoteClient> clientFactory) : IDisposable
{
    private readonly object gate = new();
    private IRemoteClient? browser;

    public RemoteConnection Connection { get; } = connection;

    /// <summary>Runs an action on the browsing client, reconnecting once if the server dropped the connection.</summary>
    public T Use<T>(Func<IRemoteClient, T> action)
    {
        lock (gate)
        {
            browser ??= OpenClient();
            try
            {
                return action(browser);
            }
            catch (Exception) when (!browser.IsConnected)
            {
                browser.Dispose();
                browser = OpenClient();
                return action(browser);
            }
        }
    }

    public void Use(Action<IRemoteClient> action) => Use<object?>(c =>
    {
        action(c);
        return null;
    });

    /// <summary>A new, connected client for one transfer; the caller disposes it.</summary>
    public IRemoteClient OpenClient()
    {
        var client = clientFactory();
        try
        {
            client.Connect();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            browser?.Dispose();
            browser = null;
        }
    }
}

/// <summary>Open sessions, by connection name.</summary>
public sealed class RemoteSessions
{
    private readonly ConcurrentDictionary<string, RemoteSession> sessions = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public IReadOnlyCollection<RemoteSession> All => sessions.Values.ToList();

    public RemoteSession? Find(string name) => sessions.TryGetValue(name, out var s) ? s : null;

    /// <summary>Session for a panel path like "sftp://name/dir", if connected.</summary>
    public RemoteSession? ForPath(string panelPath) =>
        RemotePath.TryParsePanelPath(panelPath, out _, out var name, out _) ? Find(name) : null;

    public void Add(RemoteSession session)
    {
        if (sessions.TryRemove(session.Connection.Name, out var old)) old.Dispose();
        sessions[session.Connection.Name] = session;
        Changed?.Invoke();
    }

    public void Close(string name)
    {
        if (sessions.TryRemove(name, out var s)) s.Dispose();
        Changed?.Invoke();
    }

    public void CloseAll()
    {
        foreach (var name in sessions.Keys.ToList()) Close(name);
    }
}

/// <summary>Shows remote directories in a panel.</summary>
public sealed class RemoteProvider(RemoteSessions sessions) : IFileSystemProvider
{
    public bool CanHandle(string path) => RemotePath.TryParsePanelPath(path, out _, out _, out _);

    public IReadOnlyList<FileEntry> List(string path, CancellationToken ct = default)
    {
        var (session, remote) = Resolve(path);
        return session.Use(c => c.List(remote, ct))
            .Select(e => e with { FullPath = session.Connection.ToPanelPath(e.FullPath) })
            .ToList();
    }

    public bool DirectoryExists(string path)
    {
        if (!RemotePath.TryParsePanelPath(path, out _, out var name, out var remote) || sessions.Find(name) is not { } session) return false;
        try
        {
            return remote == "/" || session.Use(c => c.DirectoryExists(remote));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    public string? GetParent(string path)
    {
        if (!RemotePath.TryParsePanelPath(path, out var scheme, out var name, out var remote)) return null;
        var parent = RemotePath.Parent(remote);
        return parent is null ? null : $"{scheme}://{name}{parent}";
    }

    public string Normalize(string path) =>
        RemotePath.TryParsePanelPath(path, out var scheme, out var name, out var remote) ? $"{scheme}://{name}{remote}" : path;

    public string Combine(string directory, string name) => directory.TrimEnd('/') + "/" + name;

    public bool IsReadOnly => false;

    /// <summary>Session and remote path for a panel path; throws when the connection is closed.</summary>
    public (RemoteSession Session, string RemotePath) Resolve(string panelPath)
    {
        if (!RemotePath.TryParsePanelPath(panelPath, out _, out var name, out var remote))
            throw new DirectoryNotFoundException(panelPath);
        var session = sessions.Find(name) ?? throw new IOException($"Not connected to \"{name}\". Use Ctrl+N to connect.");
        return (session, remote);
    }
}
