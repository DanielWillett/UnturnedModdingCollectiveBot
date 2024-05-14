﻿using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using UnturnedModdingCollective.API;
using UnturnedModdingCollective.Models;

namespace UnturnedModdingCollective.Services;
public class BotDbContext : DbContext
{
    private readonly IConfiguration _configuration;
    private readonly ISecretProvider _secretProvider;
    public DbSet<ReviewRequest> ReviewRequests => Set<ReviewRequest>();
    public BotDbContext(IConfiguration configuration, ISecretProvider secretProvider)
    {
        _configuration = configuration;
        _secretProvider = secretProvider;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ValueTask<string> connStringTask = GetConnectionString(_configuration, _secretProvider);

        string connectionString;
        if (connStringTask.IsCompleted)
        {
            connectionString = connStringTask.Result;
        }
        else
        {
            connectionString = Task.Run(connStringTask.AsTask).Result;
        }

#if DEBUG
        optionsBuilder.EnableSensitiveDataLogging(true);
#endif

        optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
    }

    internal static async ValueTask<string> GetConnectionString(IConfiguration configuration, ISecretProvider secretProvider)
    {
        IConfigurationSection sqlConfig = configuration.GetSection("SQL");

        string? connString = sqlConfig["ConnectionString"];
        if (!string.IsNullOrWhiteSpace(connString))
        {
            connString = await secretProvider.GetSecret(connString);
            if (!string.IsNullOrWhiteSpace(connString))
                return connString;
        }

        string? server = sqlConfig["Host"];
        string? database = sqlConfig["Database"];
        string? username = sqlConfig["Username"];
        string? password = sqlConfig["Password"];
        string? port = await secretProvider.GetSecret(sqlConfig["Port"]!);

        MySqlConnectionStringBuilder builder = new MySqlConnectionStringBuilder
        {
            ApplicationName = "uncreated-web",
            AllowUserVariables = true,
            Server = server == null ? null : await secretProvider.GetSecret(server),
            Database = database == null ? null : await secretProvider.GetSecret(database),
            UserID = username == null ? null : await secretProvider.GetSecret(username),
            Password = password == null ? null : await secretProvider.GetSecret(password),
            Port = port == null ? (ushort)3306 : ushort.Parse(port)
        };

        return builder.ConnectionString;
    }
}