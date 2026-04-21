using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

var exitCode = await MigrationCli.RunAsync(args);
return exitCode;

internal static class MigrationCli
{
    private const string BaselineVersion = "000_baseline_schema";
    private const string BaselineScriptName = "schema.pgsql.sql";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = MigrationOptions.Parse(args);
            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            var repoRoot = ResolveRepoRoot();
            var migrationsDirectory = options.MigrationsDirectory ?? Path.Combine(repoRoot, "database", "migrations");
            var schemaFile = options.SchemaFile ?? Path.Combine(repoRoot, "database", "schema.pgsql.sql");
            var connectionString = ResolveConnectionString(options.ConnectionString);

            if (!Directory.Exists(migrationsDirectory))
            {
                Console.Error.WriteLine($"Migrations directory not found: {migrationsDirectory}");
                return 2;
            }

            var migrationScripts = LoadMigrationScripts(migrationsDirectory);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            return options.Command switch
            {
                MigrationCommand.Status => await PrintStatusAsync(connection, migrationScripts),
                MigrationCommand.Migrate => await ApplyPendingMigrationsAsync(connection, migrationScripts, options.Verbose),
                MigrationCommand.Bootstrap => await BootstrapAsync(connection, schemaFile, migrationScripts, options.Verbose),
                _ => 2
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            PrintHelp();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Migration command failed: {ex.Message}");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Aura database migration tool");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project backend/Aura.DbMigrator -- [status|migrate|bootstrap] [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  status      Show applied and pending migrations.");
        Console.WriteLine("  migrate     Apply pending incremental scripts in database/migrations.");
        Console.WriteLine("  bootstrap   Apply schema.pgsql.sql to an empty database and mark existing migrations as baseline.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --connection <value>      PostgreSQL connection string. Falls back to ConnectionStrings__PgSql.");
        Console.WriteLine("  --migrations-dir <path>   Migration directory. Default: database/migrations");
        Console.WriteLine("  --schema-file <path>      Baseline schema file. Default: database/schema.pgsql.sql");
        Console.WriteLine("  --verbose                 Print detailed execution output.");
        Console.WriteLine("  -h, --help                Show help.");
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var solutionFile = Path.Combine(current.FullName, "Aura.sln");
            var databaseDir = Path.Combine(current.FullName, "database");
            if (File.Exists(solutionFile) && Directory.Exists(databaseDir))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string ResolveConnectionString(string? explicitConnectionString)
    {
        var value = explicitConnectionString;
        if (string.IsNullOrWhiteSpace(value))
        {
            value = Environment.GetEnvironmentVariable("ConnectionStrings__PgSql");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            value = Environment.GetEnvironmentVariable("AURA_PGSQL_CONNECTION");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("PostgreSQL connection string is required. Pass --connection or set ConnectionStrings__PgSql.");
        }

        return value;
    }

    private static List<MigrationScript> LoadMigrationScripts(string migrationsDirectory)
    {
        var regex = new Regex(@"^(?<version>\d+)_.*\.sql$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        return Directory
            .EnumerateFiles(migrationsDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                var fileName = Path.GetFileName(path);
                var match = regex.Match(fileName);
                if (!match.Success)
                {
                    throw new ArgumentException($"Invalid migration file name: {fileName}. Expected format like 001_name.sql.");
                }

                var sql = File.ReadAllText(path, Encoding.UTF8);
                return new MigrationScript(
                    Version: match.Groups["version"].Value,
                    ScriptName: fileName,
                    FullPath: path,
                    Sql: sql,
                    Checksum: ComputeChecksum(sql));
            })
            .OrderBy(x => x.Version, StringComparer.Ordinal)
            .ThenBy(x => x.ScriptName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ComputeChecksum(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    private static async Task<int> PrintStatusAsync(NpgsqlConnection connection, IReadOnlyList<MigrationScript> scripts)
    {
        var tableExists = await HistoryTableExistsAsync(connection);
        var applied = tableExists
            ? await LoadAppliedMigrationsAsync(connection)
            : new Dictionary<string, AppliedMigration>(StringComparer.Ordinal);

        ValidateAppliedChecksums(applied, scripts);

        Console.WriteLine(tableExists
            ? $"schema_migrations exists with {applied.Count} applied record(s)."
            : "schema_migrations does not exist yet. No migration history is recorded.");

        Console.WriteLine();
        Console.WriteLine("Migration status:");
        foreach (var script in scripts)
        {
            if (applied.TryGetValue(script.Version, out var row))
            {
                Console.WriteLine($"  [applied] {script.Version} {script.ScriptName} ({row.ExecutionKind}, {row.AppliedAt:yyyy-MM-dd HH:mm:ss zzz})");
            }
            else
            {
                Console.WriteLine($"  [pending] {script.Version} {script.ScriptName}");
            }
        }

        var pendingCount = scripts.Count(script => !applied.ContainsKey(script.Version));
        Console.WriteLine();
        Console.WriteLine($"Summary: applied {scripts.Count - pendingCount}, pending {pendingCount}.");
        return 0;
    }

    private static async Task<int> ApplyPendingMigrationsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<MigrationScript> scripts,
        bool verbose)
    {
        await EnsureHistoryTableAsync(connection);
        var applied = await LoadAppliedMigrationsAsync(connection);
        ValidateAppliedChecksums(applied, scripts);

        var pending = scripts.Where(script => !applied.ContainsKey(script.Version)).ToList();
        if (pending.Count == 0)
        {
            Console.WriteLine("No pending migrations.");
            return 0;
        }

        foreach (var script in pending)
        {
            Console.WriteLine($"Applying migration {script.Version} {script.ScriptName}...");
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await using (var command = new NpgsqlCommand(script.Sql, connection, transaction))
                {
                    await command.ExecuteNonQueryAsync();
                }

                await InsertHistoryAsync(connection, transaction, script.Version, script.ScriptName, script.Checksum, "migration");
                await transaction.CommitAsync();

                if (verbose)
                {
                    Console.WriteLine($"  Completed: {script.FullPath}");
                }
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        Console.WriteLine($"Migration complete. Applied {pending.Count} script(s).");
        return 0;
    }

    private static async Task<int> BootstrapAsync(
        NpgsqlConnection connection,
        string schemaFile,
        IReadOnlyList<MigrationScript> scripts,
        bool verbose)
    {
        if (!File.Exists(schemaFile))
        {
            Console.Error.WriteLine($"Schema file not found: {schemaFile}");
            return 2;
        }

        if (await HasUserTablesAsync(connection))
        {
            Console.Error.WriteLine("Bootstrap can only run against an empty database.");
            return 2;
        }

        var schemaSql = await File.ReadAllTextAsync(schemaFile, Encoding.UTF8);
        var schemaChecksum = ComputeChecksum(schemaSql);

        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            Console.WriteLine($"Applying baseline schema: {schemaFile}");
            await using (var command = new NpgsqlCommand(schemaSql, connection, transaction))
            {
                await command.ExecuteNonQueryAsync();
            }

            await EnsureHistoryTableAsync(connection, transaction);
            await InsertHistoryAsync(connection, transaction, BaselineVersion, BaselineScriptName, schemaChecksum, "baseline");

            foreach (var script in scripts)
            {
                await InsertHistoryAsync(connection, transaction, script.Version, script.ScriptName, script.Checksum, "baseline");
                if (verbose)
                {
                    Console.WriteLine($"  Registered baseline migration: {script.Version} {script.ScriptName}");
                }
            }

            await transaction.CommitAsync();
            Console.WriteLine("Bootstrap completed.");
            return 0;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static void ValidateAppliedChecksums(
        IReadOnlyDictionary<string, AppliedMigration> applied,
        IReadOnlyList<MigrationScript> scripts)
    {
        foreach (var script in scripts)
        {
            if (!applied.TryGetValue(script.Version, out var row))
            {
                continue;
            }

            if (!string.Equals(row.ScriptName, script.ScriptName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Migration version {script.Version} is recorded as {row.ScriptName}, but the current file is {script.ScriptName}.");
            }

            if (!string.Equals(row.Checksum, script.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Migration version {script.Version} has a checksum mismatch. Do not rewrite applied script {script.ScriptName}.");
            }
        }
    }

    private static async Task<bool> HistoryTableExistsAsync(NpgsqlConnection connection)
    {
        const string sql = """
            SELECT EXISTS (
              SELECT 1
              FROM information_schema.tables
              WHERE table_schema = 'public' AND table_name = 'schema_migrations'
            )
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task<bool> HasUserTablesAsync(NpgsqlConnection connection)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_type = 'BASE TABLE'
              AND table_name <> 'schema_migrations'
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
        return count > 0;
    }

    private static async Task EnsureHistoryTableAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction = null)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
              version VARCHAR(64) PRIMARY KEY,
              script_name VARCHAR(255) NOT NULL,
              checksum VARCHAR(64) NOT NULL,
              execution_kind VARCHAR(32) NOT NULL DEFAULT 'migration',
              applied_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Dictionary<string, AppliedMigration>> LoadAppliedMigrationsAsync(NpgsqlConnection connection)
    {
        const string sql = """
            SELECT version, script_name, checksum, execution_kind, applied_at
            FROM schema_migrations
            ORDER BY version
            """;
        var result = new Dictionary<string, AppliedMigration>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new AppliedMigration(
                Version: reader.GetString(0),
                ScriptName: reader.GetString(1),
                Checksum: reader.GetString(2),
                ExecutionKind: reader.GetString(3),
                AppliedAt: reader.GetFieldValue<DateTimeOffset>(4));
            result[row.Version] = row;
        }

        return result;
    }

    private static async Task InsertHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string version,
        string scriptName,
        string checksum,
        string executionKind)
    {
        const string sql = """
            INSERT INTO schema_migrations(version, script_name, checksum, execution_kind)
            VALUES(@version, @script_name, @checksum, @execution_kind)
            ON CONFLICT (version) DO NOTHING;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("script_name", scriptName);
        command.Parameters.AddWithValue("checksum", checksum);
        command.Parameters.AddWithValue("execution_kind", executionKind);
        await command.ExecuteNonQueryAsync();
    }
}

internal sealed record MigrationScript(
    string Version,
    string ScriptName,
    string FullPath,
    string Sql,
    string Checksum);

internal sealed record AppliedMigration(
    string Version,
    string ScriptName,
    string Checksum,
    string ExecutionKind,
    DateTimeOffset AppliedAt);

internal enum MigrationCommand
{
    Status,
    Migrate,
    Bootstrap
}

internal sealed record MigrationOptions
{
    public MigrationCommand Command { get; private init; } = MigrationCommand.Status;
    public string? ConnectionString { get; private init; }
    public string? MigrationsDirectory { get; private init; }
    public string? SchemaFile { get; private init; }
    public bool Verbose { get; private init; }
    public bool ShowHelp { get; private init; }

    public static MigrationOptions Parse(string[] args)
    {
        var options = new MigrationOptions();
        if (args.Length == 0)
        {
            return options with { Command = MigrationCommand.Status };
        }

        var index = 0;
        if (!args[0].StartsWith("-", StringComparison.Ordinal))
        {
            options = options with { Command = ParseCommand(args[0]) };
            index = 1;
        }

        while (index < args.Length)
        {
            var token = args[index];
            switch (token)
            {
                case "-h":
                case "--help":
                    options = options with { ShowHelp = true };
                    index++;
                    break;
                case "--connection":
                    options = options with { ConnectionString = ReadNextValue(args, ref index, token) };
                    break;
                case "--migrations-dir":
                    options = options with { MigrationsDirectory = ReadNextValue(args, ref index, token) };
                    break;
                case "--schema-file":
                    options = options with { SchemaFile = ReadNextValue(args, ref index, token) };
                    break;
                case "--verbose":
                    options = options with { Verbose = true };
                    index++;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {token}");
            }
        }

        return options;
    }

    private static MigrationCommand ParseCommand(string value) => value.ToLowerInvariant() switch
    {
        "status" => MigrationCommand.Status,
        "migrate" => MigrationCommand.Migrate,
        "bootstrap" => MigrationCommand.Bootstrap,
        _ => throw new ArgumentException($"Unknown command: {value}")
    };

    private static string ReadNextValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Option {option} requires a value.");
        }

        index += 2;
        return args[index - 1];
    }
}
