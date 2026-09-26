using Mainframe.Protocol;
using Microsoft.Data.Sqlite;

namespace Mainframe.Core;

public sealed partial class KernelStore
{
    public VolumeInfo[] ListVolumeMounts()
    {
        using SqliteConnection db = Connect(directory);
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT id,path,mount FROM volume_mounts ORDER BY mount";
        using SqliteDataReader reader = command.ExecuteReader();
        var mounts = new List<VolumeInfo>();
        while (reader.Read())
        {
            mounts.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), "locked", ""));
        }

        return mounts.ToArray();
    }

    public void SaveVolumeMount(VolumeInfo volume)
    {
        using SqliteConnection db = Connect(directory);
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "INSERT INTO volume_mounts VALUES($m,$i,$p) ON CONFLICT(mount) DO UPDATE SET id=$i,path=$p";
        command.Parameters.AddWithValue("$m", volume.Mount);
        command.Parameters.AddWithValue("$i", volume.Id);
        command.Parameters.AddWithValue("$p", volume.Path);
        command.ExecuteNonQuery();
    }

    public void RemoveVolumeMount(string mount)
    {
        using SqliteConnection db = Connect(directory);
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "DELETE FROM volume_mounts WHERE mount=$m";
        command.Parameters.AddWithValue("$m", mount);
        command.ExecuteNonQuery();
    }
}
