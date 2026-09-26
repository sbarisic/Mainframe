using System.Text;
using System.Runtime.InteropServices;
using Mainframe.Core;
using Mainframe.Storage;
using Microsoft.Data.Sqlite;

namespace Mainframe.Tests;

public sealed class EncryptionTests
{
    private static class PlainSqlite
    {
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr database, int flags, IntPtr vfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr context, IntPtr error);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int sqlite3_close(IntPtr database);
    }

    [Fact]
    public void QualifiedEngineEncryptsMainAndWalAndRejectsWrongPasswords()
    {
        string root = Path.Combine(Path.GetTempPath(), "Mainframe-Cipher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "volume.mfv");
        const string password = "correct horse \u03bb battery";
        const string sentinel = "mainframe-encrypted-filename-and-content";
        try
        {
            using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Password = password, Pooling = false }.ToString()))
            {
                database.Open();
                NativeSqlite.Verify(database);
                using SqliteCommand command = database.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE entries(name TEXT); INSERT INTO entries VALUES ($n)";
                command.Parameters.AddWithValue("$n", sentinel);
                command.ExecuteNonQuery();
                foreach (string file in new[]
                {
                    path,
                    path + "-wal"
                })
                {
                    using var raw = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var data = new MemoryStream();
                    raw.CopyTo(data);
                    Assert.DoesNotContain(sentinel, Encoding.UTF8.GetString(data.ToArray()));
                }
            }

            // Windows' ordinary SQLite engine cannot interpret an encrypted container.
            Assert.Equal(0, PlainSqlite.sqlite3_open_v2(path, out IntPtr plain, 1, IntPtr.Zero));
            try
            {
                Assert.Equal(26, PlainSqlite.sqlite3_exec(plain, "SELECT name FROM entries", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
            }
            finally
            {
                Assert.Equal(0, PlainSqlite.sqlite3_close(plain));
            }

            foreach (string secret in new[]
            {
                "",
                "wrong password"
            })
            {
                using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Password = secret, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
                Assert.Throws<SqliteException>(() =>
                {
                    database.Open();
                    using SqliteCommand command = database.CreateCommand();
                    command.CommandText = "SELECT name FROM entries";
                    command.ExecuteScalar();
                });
            }

            using var reopened = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Password = password, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            reopened.Open();
            using SqliteCommand read = reopened.CreateCommand();
            read.CommandText = "SELECT name FROM entries";
            Assert.Equal(sentinel, read.ExecuteScalar());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void EmptyPasswordStillCreatesAnEncryptedContainer()
    {
        string root = Path.Combine(Path.GetTempPath(), "Mainframe-EmptyPassword-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "volume.mfv");
        try
        {
            string id = EncryptedVolume.Create(path, "");
            Assert.Equal(0, PlainSqlite.sqlite3_open_v2(path, out IntPtr plain, 1, IntPtr.Zero));
            try
            {
                Assert.Equal(26, PlainSqlite.sqlite3_exec(plain, "SELECT * FROM sqlite_master", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
            }
            finally
            {
                Assert.Equal(0, PlainSqlite.sqlite3_close(plain));
            }
            Assert.Equal("UNLOCK_FAILED", Assert.Throws<KernelOperationException>(() => EncryptedVolume.Mount(path, "\0")).Code);
            using EncryptedVolume volume = EncryptedVolume.Mount(path, "");
            Assert.Equal(id, volume.Id);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
