using MySqlConnector;

namespace VIPCore;

/// <summary>Единственная ответственность этого объекта - преобразовать настройки БД в connection string.</summary>
internal static class DatabaseConnectionStringBuilder
{
    public static string Build(VipDb connection, int maxPoolSize)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Database = connection.Database,
            UserID = connection.User,
            Password = connection.Password,
            Server = connection.Host,
            Port = (uint)connection.Port,
            Pooling = true,
            MinimumPoolSize = 0,
            MaximumPoolSize = (uint)Math.Clamp(maxPoolSize, 1, 1000),
            ConnectionIdleTimeout = 30,
            ConnectionTimeout = 10,
            ConnectionReset = true,
            ConnectionLifeTime = 300
        };

        return builder.ConnectionString;
    }
}
