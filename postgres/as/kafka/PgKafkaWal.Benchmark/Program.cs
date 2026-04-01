using System.Diagnostics;
using Npgsql;
using Npgsql.Replication;
using Npgsql.Replication.PgOutput;
using Npgsql.Replication.PgOutput.Messages;

namespace PgKafkaWal.Benchmark;

class Program
{
    const string ConnectionString = "Host=localhost;Port=5432;Username=repuser;Password=secret;Database=mydb";
    const string SuperuserConnectionString = "Host=localhost;Port=5432;Username=postgres;Database=mydb";

    static async Task Main(string[] args)
    {
        var config = ParseArgs(args);

        Console.WriteLine("=== PostgreSQL Logical Replication Benchmark ===");
        Console.WriteLine($"  Mode:       {(config.Partitioned ? "PARTITIONED (each reader = own partition)" : "DUPLICATE (all readers = same data)")}");
        Console.WriteLine($"  Messages:   {config.Messages}");
        Console.WriteLine($"  Writers:    {config.Writers}");
        Console.WriteLine($"  Readers:    {config.Readers}");
        Console.WriteLine($"  Batch size: {config.BatchSize}");
        Console.WriteLine($"  Ack every:  {(config.AckInterval == 0 ? "NEVER" : config.AckInterval == 1 ? "every message" : $"every {config.AckInterval} messages")}{(config.SyncAck ? " (SYNC — network round-trip)" : " (local only)")}");
        Console.WriteLine();

        string[] pubNames;
        string[] slotNames;
        string insertSql;
        // How many messages each reader expects
        int[] expectedPerReader;

        if (config.Partitioned)
        {
            await SetupPartitionedAsync(config.Readers);
            pubNames = Enumerable.Range(0, config.Readers).Select(i => $"bench_pub_p{i}").ToArray();
            slotNames = Enumerable.Range(0, config.Readers).Select(i => $"bench_slot_p{i}").ToArray();
            // region = sequential id, hash partition distributes across N partitions
            // Each writer sends (writerId, seqNum) — we use region for partition routing
            insertSql = "INSERT INTO bench_part (region, payload, sent_at) VALUES ($1, $2, $3)";
            // With hash partitioning, distribution is roughly even but not exact
            // Each reader signals done when it sees its share; we'll use approximate targets
            int perReader = config.Messages / config.Readers;
            expectedPerReader = new int[config.Readers];
            Array.Fill(expectedPerReader, perReader);
            // last reader picks up remainder
            expectedPerReader[config.Readers - 1] += config.Messages % config.Readers;
        }
        else
        {
            await SetupFlatAsync(config.Readers);
            string pubName = "my_pub";
            pubNames = Enumerable.Repeat(pubName, config.Readers).ToArray();
            slotNames = config.Readers == 1
                ? ["bench_slot"]
                : Enumerable.Range(0, config.Readers).Select(i => $"bench_slot_{i}").ToArray();
            insertSql = "INSERT INTO bench (payload, sent_at) VALUES ($1, $2)";
            expectedPerReader = Enumerable.Repeat(config.Messages, config.Readers).ToArray();
        }

        // Per-reader tracking
        var perReaderReceived = new int[config.Readers];
        var perReaderLatencies = new List<long>[config.Readers];
        var perReaderDone = new TaskCompletionSource[config.Readers];
        for (int i = 0; i < config.Readers; i++)
        {
            perReaderLatencies[i] = new List<long>(config.Messages / config.Readers + 1000);
            perReaderDone[i] = new TaskCompletionSource();
        }

        // For partitioned mode: track total received across all readers
        int totalReceived = 0;
        var allDone = new TaskCompletionSource();

        var allReadersReady = new CountdownEvent(config.Readers);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // --- Reader tasks ---
        var readerTasks = new Task[config.Readers];
        for (int r = 0; r < config.Readers; r++)
        {
            int readerId = r;
            string slotName = slotNames[r];
            string pubName = pubNames[r];
            // Table name to match in replication stream
            string matchTable = config.Partitioned ? $"bench_part_p{readerId}" : "bench";

            readerTasks[r] = Task.Run(async () =>
            {
                try
                {
                    await using var conn = new LogicalReplicationConnection(ConnectionString);
                    await conn.Open(cts.Token);

                    var slot = new PgOutputReplicationSlot(slotName);
                    var options = new PgOutputReplicationOptions(pubName, PgOutputProtocolVersion.V1);

                    allReadersReady.Signal();

                    var relations = new Dictionary<uint, RelationMessage>();
                    int sentAtColIndex = -1;
                    int ackCounter = 0;

                    await foreach (var message in conn.StartReplication(slot, options, cts.Token))
                    {
                        if (message is RelationMessage rel)
                        {
                            relations[rel.RelationId] = rel;
                            if (rel.RelationName.StartsWith("bench"))
                            {
                                for (int c = 0; c < rel.Columns.Count; c++)
                                {
                                    if (rel.Columns[c].ColumnName == "sent_at")
                                    { sentAtColIndex = c; break; }
                                }
                            }
                        }
                        else if (message is InsertMessage insert)
                        {
                            var relation = relations.GetValueOrDefault(insert.Relation.RelationId);
                            if (relation != null && relation.RelationName.StartsWith("bench") && sentAtColIndex >= 0)
                            {
                                long sentTicks = 0;
                                int col = 0;
                                await foreach (var value in insert.NewRow)
                                {
                                    if (col == sentAtColIndex && !value.IsDBNull)
                                        sentTicks = Convert.ToInt64(await value.Get(cts.Token));
                                    col++;
                                }

                                var nowTicks = DateTime.UtcNow.Ticks;
                                Interlocked.Increment(ref perReaderReceived[readerId]);
                                lock (perReaderLatencies[readerId])
                                    perReaderLatencies[readerId].Add(nowTicks - sentTicks);

                                if (config.Partitioned)
                                {
                                    // Partitioned: signal when ALL readers combined reach total
                                    var total = Interlocked.Increment(ref totalReceived);
                                    if (total >= config.Messages)
                                        allDone.TrySetResult();
                                }
                                else
                                {
                                    // Duplicate: each reader must see all messages
                                    if (perReaderReceived[readerId] >= expectedPerReader[readerId])
                                        perReaderDone[readerId].TrySetResult();
                                }
                            }
                            else
                            {
                                await foreach (var _ in insert.NewRow) { }
                            }
                        }

                        ackCounter++;
                        if (config.AckInterval > 0 && ackCounter % config.AckInterval == 0)
                        {
                            conn.SetReplicationStatus(message.WalEnd);
                            if (config.SyncAck)
                                await conn.SendStatusUpdate(cts.Token);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"Reader {readerId} error: {ex.Message}");
                    if (config.Partitioned)
                        allDone.TrySetException(ex);
                    else
                        perReaderDone[readerId].TrySetException(ex);
                }
            }, cts.Token);
        }

        allReadersReady.Wait(cts.Token);
        Console.WriteLine($"{config.Readers} reader(s) connected. Starting writers...\n");

        // --- Writer tasks ---
        var writeSw = Stopwatch.StartNew();

        var writerTasks = new Task[config.Writers];
        int messagesPerWriter = config.Messages / config.Writers;
        int writerRemainder = config.Messages % config.Writers;

        for (int w = 0; w < config.Writers; w++)
        {
            int count = messagesPerWriter + (w < writerRemainder ? 1 : 0);
            int writerId = w;
            writerTasks[w] = Task.Run(() =>
                WriterAsync(writerId, count, config.BatchSize, insertSql, config.Partitioned, cts.Token));
        }

        await Task.WhenAll(writerTasks);
        writeSw.Stop();

        var writeElapsed = writeSw.Elapsed;
        Console.WriteLine($"All writers finished in {writeElapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"Write throughput: {config.Messages / writeElapsed.TotalSeconds:F0} msg/s\n");

        // --- Wait for readers ---
        Console.WriteLine("Waiting for readers to receive all messages...");
        var readSw = Stopwatch.StartNew();

        try
        {
            var timeout = Task.Delay(TimeSpan.FromSeconds(60));
            Task waitTask;

            if (config.Partitioned)
            {
                waitTask = allDone.Task;
            }
            else
            {
                waitTask = Task.WhenAll(perReaderDone.Select(t => t.Task));
            }

            var completed = await Task.WhenAny(waitTask, timeout);
            readSw.Stop();

            if (completed == timeout)
            {
                if (config.Partitioned)
                    Console.WriteLine($"Timeout! Total received: {totalReceived}/{config.Messages}");
                for (int r = 0; r < config.Readers; r++)
                    Console.WriteLine($"  Reader {r}: {perReaderReceived[r]} msgs");
                cts.Cancel();
                return;
            }

            await waitTask;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            cts.Cancel();
            return;
        }

        cts.Cancel();

        // --- Results ---
        int totalMsgs = config.Partitioned ? totalReceived : config.Messages;
        var totalElapsed = writeElapsed + readSw.Elapsed;

        Console.WriteLine();
        Console.WriteLine("=== Results ===");
        if (config.Partitioned)
        {
            Console.WriteLine($"  Total messages:     {totalMsgs} (split across {config.Readers} partitions)");
            for (int r = 0; r < config.Readers; r++)
                Console.WriteLine($"    Partition {r}: {perReaderReceived[r]} msgs ({100.0 * perReaderReceived[r] / totalMsgs:F1}%)");
        }
        else
        {
            Console.WriteLine($"  Total messages:     {config.Messages} (x{config.Readers} readers = {config.Messages * config.Readers} total delivered)");
        }

        Console.WriteLine($"  Write time:         {writeElapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"  Write throughput:   {config.Messages / writeElapsed.TotalSeconds:F0} msg/s");
        Console.WriteLine($"  Read catchup time:  {readSw.Elapsed.TotalMilliseconds:F0} ms (slowest reader)");
        Console.WriteLine($"  End-to-end time:    {totalElapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"  E2E throughput:     {totalMsgs / totalElapsed.TotalSeconds:F0} msg/s");

        if (!config.Partitioned && config.Readers > 1)
            Console.WriteLine($"  Combined delivery:  {config.Messages * config.Readers / totalElapsed.TotalSeconds:F0} msg/s (all readers, duplicated)");

        // Per-reader latency
        for (int r = 0; r < config.Readers; r++)
        {
            var lats = perReaderLatencies[r].Where(x => x > 0).OrderBy(x => x).ToArray();
            if (lats.Length == 0) continue;

            Console.WriteLine();
            Console.WriteLine($"  Reader {r} latency ({perReaderReceived[r]} msgs):");
            Console.WriteLine($"    Min:  {TicksToMs(lats.First()):F2} ms");
            Console.WriteLine($"    Avg:  {TicksToMs(lats.Average()):F2} ms");
            Console.WriteLine($"    P50:  {TicksToMs(Percentile(lats, 50)):F2} ms");
            Console.WriteLine($"    P95:  {TicksToMs(Percentile(lats, 95)):F2} ms");
            Console.WriteLine($"    P99:  {TicksToMs(Percentile(lats, 99)):F2} ms");
            Console.WriteLine($"    Max:  {TicksToMs(lats.Last()):F2} ms");
        }

        foreach (var t in readerTasks)
            try { await t; } catch (OperationCanceledException) { }
    }

    static async Task WriterAsync(int writerId, int count, int batchSize, string insertSql, bool partitioned, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(ct);

        int sent = 0;
        while (sent < count)
        {
            int chunk = Math.Min(batchSize, count - sent);
            var batch = new NpgsqlBatch(conn);

            for (int i = 0; i < chunk; i++)
            {
                var cmd = new NpgsqlBatchCommand(insertSql);
                if (partitioned)
                {
                    // region determines which partition gets the row
                    cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = sent + i });
                    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = $"w{writerId}-{sent + i}" });
                    cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = DateTime.UtcNow.Ticks });
                }
                else
                {
                    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = $"w{writerId}-{sent + i}" });
                    cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = DateTime.UtcNow.Ticks });
                }
                batch.BatchCommands.Add(cmd);
            }

            await batch.ExecuteNonQueryAsync(ct);
            sent += chunk;
        }
    }

    // --- Setup for flat (non-partitioned) mode ---
    static async Task SetupFlatAsync(int readerCount)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS bench (
                    id       BIGSERIAL PRIMARY KEY,
                    payload  TEXT,
                    sent_at  BIGINT NOT NULL
                )
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "TRUNCATE bench";
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT 1 FROM pg_publication_tables WHERE pubname = 'my_pub' AND tablename = 'bench'";
            var result = await cmd.ExecuteScalarAsync();
            if (result is null)
                throw new Exception("Table 'bench' not in publication 'my_pub'. Run as superuser:\n  ALTER PUBLICATION my_pub ADD TABLE bench;");
        }

        await DropSlotsAsync(conn, "bench_slot%");

        await using var replConn = new LogicalReplicationConnection(ConnectionString);
        await replConn.Open();

        if (readerCount == 1)
            await replConn.CreatePgOutputReplicationSlot("bench_slot", temporarySlot: false);
        else
            for (int i = 0; i < readerCount; i++)
                await replConn.CreatePgOutputReplicationSlot($"bench_slot_{i}", temporarySlot: false);

        Console.WriteLine($"Setup complete: bench table ready, {readerCount} slot(s) created.");
    }

    // --- Setup for partitioned mode ---
    static async Task SetupPartitionedAsync(int partitionCount)
    {
        // Need superuser to create publications
        await using var su = new NpgsqlConnection(SuperuserConnectionString);
        await su.OpenAsync();

        // Drop old publications and slots first
        await DropSlotsAsync(su, "bench_slot_p%");

        for (int i = 0; i < partitionCount; i++)
        {
            await ExecIgnoreAsync(su, $"DROP PUBLICATION IF EXISTS bench_pub_p{i}");
        }

        // Drop and recreate partitioned table
        await ExecIgnoreAsync(su, "DROP TABLE IF EXISTS bench_part CASCADE");

        await using (var cmd = su.CreateCommand())
        {
            cmd.CommandText = $"""
                CREATE TABLE bench_part (
                    id       BIGSERIAL,
                    region   INT NOT NULL,
                    payload  TEXT,
                    sent_at  BIGINT NOT NULL
                ) PARTITION BY HASH (region)
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Create N partitions + N publications
        for (int i = 0; i < partitionCount; i++)
        {
            await using (var cmd = su.CreateCommand())
            {
                cmd.CommandText = $"CREATE TABLE bench_part_p{i} PARTITION OF bench_part FOR VALUES WITH (MODULUS {partitionCount}, REMAINDER {i})";
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = su.CreateCommand())
            {
                cmd.CommandText = $"GRANT ALL ON TABLE bench_part_p{i} TO repuser";
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = su.CreateCommand())
            {
                cmd.CommandText = $"CREATE PUBLICATION bench_pub_p{i} FOR TABLE bench_part_p{i}";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        await using (var cmd = su.CreateCommand())
        {
            cmd.CommandText = "GRANT ALL ON TABLE bench_part TO repuser";
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = su.CreateCommand())
        {
            cmd.CommandText = "GRANT USAGE, SELECT ON SEQUENCE bench_part_id_seq TO repuser";
            await cmd.ExecuteNonQueryAsync();
        }

        // Create N replication slots
        await using var replConn = new LogicalReplicationConnection(ConnectionString);
        await replConn.Open();

        for (int i = 0; i < partitionCount; i++)
            await replConn.CreatePgOutputReplicationSlot($"bench_slot_p{i}", temporarySlot: false);

        Console.WriteLine($"Setup complete: bench_part with {partitionCount} partitions, {partitionCount} publications, {partitionCount} slots.");
    }

    static async Task DropSlotsAsync(NpgsqlConnection conn, string pattern)
    {
        var slotsToDrop = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT slot_name FROM pg_replication_slots WHERE slot_name LIKE '{pattern}'";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                slotsToDrop.Add(reader.GetString(0));
        }

        foreach (var slot in slotsToDrop)
            await ExecIgnoreAsync(conn, $"SELECT pg_drop_replication_slot('{slot}')");
    }

    static async Task ExecIgnoreAsync(NpgsqlConnection conn, string sql)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException) { }
    }

    static double TicksToMs(double ticks) => ticks / TimeSpan.TicksPerMillisecond;
    static long Percentile(long[] sorted, int p) => sorted[(int)((p / 100.0) * (sorted.Length - 1))];

    static BenchConfig ParseArgs(string[] args)
    {
        var config = new BenchConfig();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--partitioned") { config.Partitioned = true; continue; }
            if (args[i] == "--sync-ack") { config.SyncAck = true; continue; }
            if (i + 1 >= args.Length) continue;
            switch (args[i])
            {
                case "--messages": config.Messages = int.Parse(args[i + 1]); i++; break;
                case "--writers": config.Writers = int.Parse(args[i + 1]); i++; break;
                case "--readers": config.Readers = int.Parse(args[i + 1]); i++; break;
                case "--batch-size": config.BatchSize = int.Parse(args[i + 1]); i++; break;
                case "--ack-interval": config.AckInterval = int.Parse(args[i + 1]); i++; break;
            }
        }
        return config;
    }
}

class BenchConfig
{
    public int Messages { get; set; } = 10_000;
    public int Writers { get; set; } = 4;
    public int Readers { get; set; } = 1;
    public int BatchSize { get; set; } = 100;
    public bool Partitioned { get; set; } = false;
    public int AckInterval { get; set; } = 1; // SetReplicationStatus every N messages (0 = never)
    public bool SyncAck { get; set; } = false; // true = SendStatusUpdate (network round-trip)
}
