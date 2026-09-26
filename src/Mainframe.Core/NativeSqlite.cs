using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Mainframe.Core;

public static class NativeSqlite
{
    public const string CipherVersion = "4.19.0";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetKey(IntPtr database, byte[] key, int length);
    private static SetKey s_setKey = null!;
#pragma warning disable CA2255
    [ModuleInitializer]
    public static void Initialize()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("The qualified database engine requires Windows x64.");
        }

        Assembly provider = typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly;
        IntPtr library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "sqlite3.dll"));
        s_setKey = Marshal.GetDelegateForFunctionPointer<SetKey>(NativeLibrary.GetExport(library, "sqlite3_key"));
        NativeLibrary.SetDllImportResolver(provider, (name, _, _) => name == "sqlite3" ? library : IntPtr.Zero);
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
        SQLitePCL.raw.FreezeProvider();
    }

#pragma warning restore CA2255
    internal static void ApplyPassword(SqliteConnection connection, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        byte[] key = new UTF8Encoding(false, true).GetBytes(password);
        try
        {
            if (key.Length == 0 || password.Contains('\0'))
            {
                // SQLCipher requires nonempty key material. A NUL-prefixed base64
                // encoding covers formerly disallowed passwords without colliding
                // with existing UTF-8 passwords or truncating embedded NULs.
                byte[] encoded = new byte[1 + System.Buffers.Text.Base64.GetMaxEncodedToUtf8Length(key.Length)];
                System.Buffers.Text.Base64.EncodeToUtf8(key, encoded.AsSpan(1), out _, out _);
                CryptographicOperations.ZeroMemory(key);
                key = encoded;
            }

            bool held = false;
            try
            {
                connection.Handle!.DangerousAddRef(ref held);
                if (s_setKey(connection.Handle.DangerousGetHandle(), key, key.Length) != SQLitePCL.raw.SQLITE_OK)
                {
                    throw new InvalidOperationException("Unable to initialize encrypted database.");
                }
            }
            finally
            {
                if (held)
                {
                    connection.Handle!.DangerousRelease();
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static void Verify(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA cipher_version";
        if (command.ExecuteScalar() is not string version || !version.StartsWith(CipherVersion + " ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The qualified SQLCipher engine is unavailable.");
        }

        command.CommandText = "PRAGMA compile_options";
        using SqliteDataReader reader = command.ExecuteReader();
        bool memoryOnly = false;
        while (reader.Read())
        {
            memoryOnly |= reader.GetString(0) == "TEMP_STORE=3";
        }

        if (!memoryOnly)
        {
            throw new InvalidOperationException("SQLCipher must use memory-only temporary storage.");
        }
    }
}
