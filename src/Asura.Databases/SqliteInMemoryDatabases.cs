using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Asura.Application;
using Microsoft.Data.Sqlite;

namespace Asura.Databases;

/// <summary>
/// The application-facing handle to <see cref="SqliteInMemoryDatabases"/>,
/// so presentation code can serve a database from memory without referencing
/// the engine layer.
/// </summary>
public sealed class SqliteInMemoryDatabaseRegistry : IInMemoryDatabaseRegistry
{
    public string Register(byte[] database) => SqliteInMemoryDatabases.Register(database);

    public void Unregister(string connectionString) =>
        SqliteInMemoryDatabases.Unregister(connectionString);
}

/// <summary>
/// SQLite databases served from memory instead of a file. A previewed remote
/// database is downloaded as bytes and must stay bytes — writing it to disk to
/// satisfy an engine that "opens paths" would put the user's data on disk in
/// the clear — so the engine is handed the buffer itself through SQLite's own
/// deserialize API.
///
/// A registration pins the buffer and yields a connection string; every
/// connection opened from that string is an in-memory database deserialized
/// from the same pinned bytes, read-only at the pager level. Connections are
/// opened per query elsewhere in this layer, and the deserialize borrows the
/// buffer rather than copying it, so each open costs an allocation of nothing.
/// </summary>
public static class SqliteInMemoryDatabases
{
    /// <summary>
    /// The marker the SQLite driver recognizes in place of a file path. Namespaced
    /// so no real path or user input can collide with it.
    /// </summary>
    internal const string TokenPrefix = "asura-memory:";

    private const int SQLITE_DESERIALIZE_READONLY = 4;

    private static readonly ConcurrentDictionary<string, PinnedDatabase> Registered = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a database image and returns the connection string that
    /// serves it. The image is pinned until <see cref="Unregister"/>.
    /// </summary>
    public static string Register(byte[] database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var token = Guid.NewGuid().ToString("n");
        Registered[token] = new PinnedDatabase(database);
        return $"Data Source={TokenPrefix}{token}";
    }

    /// <summary>
    /// Releases a registration. Its connection string stops working, and the
    /// buffer is unpinned as soon as the last connection borrowing it closes —
    /// a query racing the preview's close finishes against valid memory.
    /// </summary>
    public static void Unregister(string connectionString)
    {
        if (TryResolveToken(connectionString, out var token)
            && Registered.TryRemove(token, out var pinned))
        {
            pinned.Release();
        }
    }

    /// <summary>
    /// Borrows an existing preview image for private worker transfer without a
    /// second parent buffer. Ordinary file targets return null; a closed token
    /// fails rather than becoming a path or selecting another registration.
    /// </summary>
    public static Stream? OpenSnapshot(string connectionString)
    {
        if (!TryResolveToken(connectionString, out var token)) { return null; }
        if (!Registered.TryGetValue(token, out var pinned) || !pinned.TryBeginBorrow())
        {
            throw new InvalidOperationException("This in-memory database preview has been closed.");
        }
        try { return pinned.OpenBorrowedStream(); }
        catch { pinned.EndBorrow(); throw; }
    }

    /// <summary>
    /// A connection for a registered token, or null when the connection string
    /// is an ordinary one. Called by the SQLite driver for every string it is
    /// asked to open.
    /// </summary>
    internal static SqliteConnection? TryCreateConnection(string connectionString)
    {
        if (!TryResolveToken(connectionString, out var token))
        {
            return null;
        }

        // Pooling would keep physical :memory: connections — each with its own
        // deserialized schema — alive past their registration. Every open is a
        // fresh deserialize instead, which borrows the pinned buffer for free.
        var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        // The borrow is held by the connection itself, not looked up again on
        // close: unregistering removes the token from the registry, and the
        // buffer must still be returned by whoever already borrowed it.
        PinnedDatabase? borrowed = null;
        connection.StateChange += (sender, args) =>
        {
            if (sender is not SqliteConnection changed)
            {
                return;
            }

            if (args.CurrentState is System.Data.ConnectionState.Closed
                or System.Data.ConnectionState.Broken)
            {
                Interlocked.Exchange(ref borrowed, null)?.EndBorrow();
                return;
            }

            if (args.CurrentState is not System.Data.ConnectionState.Open)
            {
                return;
            }

            if (!Registered.TryGetValue(token, out var pinned)
                || !pinned.TryBeginBorrow())
            {
                changed.Close();
                throw new InvalidOperationException(
                    "This in-memory database preview has been closed.");
            }

            borrowed = pinned;
            pinned.DeserializeInto(changed);
        };
        return connection;
    }

    private static bool TryResolveToken(string connectionString, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        var marker = connectionString.IndexOf(
            TokenPrefix,
            StringComparison.Ordinal);
        if (marker < 0)
        {
            return false;
        }

        var start = marker + TokenPrefix.Length;
        var end = start;
        while (end < connectionString.Length
            && char.IsAsciiLetterOrDigit(connectionString[end]))
        {
            end++;
        }

        token = connectionString[start..end];
        return token.Length > 0;
    }

    /// <summary>
    /// The image, pinned so SQLite can borrow it in place. Borrowing (rather
    /// than SQLITE_DESERIALIZE_FREEONCLOSE with a copy) is what makes opening
    /// a connection per query affordable for a large database — so the pin
    /// may only be freed once released *and* no connection is inside it.
    /// </summary>
    private sealed class PinnedDatabase(byte[] bytes)
    {
        private readonly object _gate = new();
        private GCHandle _pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        private int _borrows;
        private bool _released;

        public Stream OpenBorrowedStream() => new SnapshotStream(bytes, this);

        public bool TryBeginBorrow()
        {
            lock (_gate)
            {
                if (_released || !_pin.IsAllocated)
                {
                    return false;
                }

                _borrows++;
                return true;
            }
        }

        public void EndBorrow()
        {
            lock (_gate)
            {
                _borrows--;
                FreeIfDone();
            }
        }

        public void Release()
        {
            lock (_gate)
            {
                _released = true;
                FreeIfDone();
            }
        }

        public void DeserializeInto(SqliteConnection connection)
        {
            var rc = SQLitePCL.raw.sqlite3_deserialize(
                connection.Handle!,
                "main",
                _pin.AddrOfPinnedObject(),
                bytes.Length,
                bytes.Length,
                SQLITE_DESERIALIZE_READONLY);
            if (rc != SQLitePCL.raw.SQLITE_OK)
            {
                throw new SqliteException(
                    "The database image could not be opened from memory.",
                    rc);
            }
        }

        private void FreeIfDone()
        {
            if (_released && _borrows == 0 && _pin.IsAllocated)
            {
                _pin.Free();
            }
        }
    }

    private sealed class SnapshotStream(byte[] bytes, PinnedDatabase owner) : MemoryStream(bytes, writable: false)
    {
        private PinnedDatabase? _owner = owner;

        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally
            {
                if (disposing) { Interlocked.Exchange(ref _owner, null)?.EndBorrow(); }
            }
        }
    }
}
