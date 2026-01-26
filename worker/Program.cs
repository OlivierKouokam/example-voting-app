using System;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Newtonsoft.Json;
using Npgsql;
using StackExchange.Redis;

namespace Worker
{
    public class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                // --- Récupération des variables d'environnement avec valeurs par défaut ---
                var redisHost = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "redis";
                var redisPort = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379");

                var pgHost = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "db";
                var pgPort = int.Parse(Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432");
                var pgUser = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
                var pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres";
                var pgDb = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "postgres";

                var sleepMs = int.Parse(Environment.GetEnvironmentVariable("WORKER_SLEEP_MS") ?? "100");
                var keepAliveMs = int.Parse(Environment.GetEnvironmentVariable("KEEPALIVE_MS") ?? "1000");

                // --- Connexion PostgreSQL ---
                var pgsql = OpenDbConnection($"Server={pgHost};Port={pgPort};Username={pgUser};Password={pgPassword};Database={pgDb}");

                // --- Connexion Redis ---
                var redisConn = OpenRedisConnection(redisHost, redisPort);
                var redis = redisConn.GetDatabase();

                // Keep alive command pour PostgreSQL
                var keepAliveCommand = pgsql.CreateCommand();
                keepAliveCommand.CommandText = "SELECT 1";

                var definition = new { vote = "", voter_id = "" };

                while (true)
                {
                    Thread.Sleep(sleepMs);

                    // Reconnexion Redis si nécessaire
                    if (redisConn == null || !redisConn.IsConnected)
                    {
                        Console.WriteLine("Reconnecting Redis");
                        redisConn = OpenRedisConnection(redisHost, redisPort);
                        redis = redisConn.GetDatabase();
                    }

                    string json = redis.ListLeftPopAsync("votes").Result;
                    if (json != null)
                    {
                        var vote = JsonConvert.DeserializeAnonymousType(json, definition);
                        Console.WriteLine($"Processing vote for '{vote.vote}' by '{vote.voter_id}'");

                        // Reconnexion PostgreSQL si nécessaire
                        if (!pgsql.State.Equals(System.Data.ConnectionState.Open))
                        {
                            Console.WriteLine("Reconnecting DB");
                            pgsql = OpenDbConnection($"Server={pgHost};Port={pgPort};Username={pgUser};Password={pgPassword};Database={pgDb}");
                        }

                        UpdateVote(pgsql, vote.voter_id, vote.vote);
                    }
                    else
                    {
                        keepAliveCommand.ExecuteNonQuery();
                        Thread.Sleep(keepAliveMs);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        private static NpgsqlConnection OpenDbConnection(string connectionString)
        {
            NpgsqlConnection connection;
            while (true)
            {
                try
                {
                    connection = new NpgsqlConnection(connectionString);
                    connection.Open();
                    break;
                }
                catch (Exception)
                {
                    Console.Error.WriteLine("Waiting for db...");
                    Thread.Sleep(1000);
                }
            }

            Console.Error.WriteLine("Connected to db");

            var command = connection.CreateCommand();
            command.CommandText = @"CREATE TABLE IF NOT EXISTS votes (
                                        id VARCHAR(255) NOT NULL UNIQUE,
                                        vote VARCHAR(255) NOT NULL
                                    )";
            command.ExecuteNonQuery();

            return connection;
        }

        private static ConnectionMultiplexer OpenRedisConnection(string host, int port)
        {
            var ipAddress = GetIp(host);
            Console.WriteLine($"Found redis at {ipAddress}:{port}");

            while (true)
            {
                try
                {
                    Console.Error.WriteLine("Connecting to redis...");
                    return ConnectionMultiplexer.Connect($"{ipAddress}:{port}");
                }
                catch (RedisConnectionException)
                {
                    Console.Error.WriteLine("Waiting for redis...");
                    Thread.Sleep(1000);
                }
            }
        }

        private static string GetIp(string hostname)
            => Dns.GetHostEntryAsync(hostname)
                .Result
                .AddressList
                .First(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToString();

        private static void UpdateVote(NpgsqlConnection connection, string voterId, string vote)
        {
            var command = connection.CreateCommand();
            try
            {
                command.CommandText = "INSERT INTO votes (id, vote) VALUES (@id, @vote)";
                command.Parameters.AddWithValue("@id", voterId);
                command.Parameters.AddWithValue("@vote", vote);
                command.ExecuteNonQuery();
            }
            catch (DbException)
            {
                command.CommandText = "UPDATE votes SET vote = @vote WHERE id = @id";
                command.Parameters.AddWithValue("@id", voterId);
                command.Parameters.AddWithValue("@vote", vote);
                command.ExecuteNonQuery();
            }
            finally
            {
                command.Dispose();
            }
        }
    }
}

