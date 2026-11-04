using Microsoft.Data.SqlClient;
using SqlStudio.Models;

namespace SqlStudio.Services;

public static class ConnectionFactory
{
    public static string BuildConnectionString(ConnectionProfile profile, string? password, string? database = null)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = profile.Server.Trim(),
            ApplicationName = "SqlStudio",
            Encrypt = profile.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = profile.TrustServerCertificate,
            ConnectTimeout = 15,
            Pooling = false, // each query tab owns one session so USE / temp tables behave like SSMS
        };

        var db = database ?? profile.DefaultDatabase;
        if (!string.IsNullOrWhiteSpace(db))
            builder.InitialCatalog = db;

        switch (profile.Auth)
        {
            case AuthMode.SqlLogin:
                builder.UserID = profile.Username;
                builder.Password = password ?? "";
                break;
            case AuthMode.EntraInteractive:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                builder.UserID = profile.Username;
                break;
        }

        return builder.ConnectionString;
    }
}
