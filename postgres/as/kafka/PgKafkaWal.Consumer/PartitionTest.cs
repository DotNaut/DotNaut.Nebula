using Npgsql.Replication;
using Npgsql.Replication.PgOutput;
using Npgsql.Replication.PgOutput.Messages;

namespace PgKafkaWal.Consumer;

/// <summary>
/// Reads all pending messages from a specific replication slot/publication and prints them.
/// Usage: dotnet run -- --partition-test
/// </summary>
static class PartitionTest
{
    const string ConnectionString = "Host=localhost;Port=5432;Username=repuser;Password=secret;Database=mydb";

    public static async Task RunAsync()
    {
        Console.WriteLine("=== Partition Replication Test ===\n");

        await ReadSlotAsync("slot_part_0", "pub_part_0", "Partition 0 (bench_part_0)");
        await ReadSlotAsync("slot_part_1", "pub_part_1", "Partition 1 (bench_part_1)");
        await ReadSlotAsync("slot_part_all", "pub_part_all", "All partitions (bench_part via root)");
    }

    static async Task ReadSlotAsync(string slotName, string pubName, string label)
    {
        Console.WriteLine($"--- {label} [{slotName} / {pubName}] ---");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        try
        {
            await using var conn = new LogicalReplicationConnection(ConnectionString);
            await conn.Open(cts.Token);

            var slot = new PgOutputReplicationSlot(slotName);
            var options = new PgOutputReplicationOptions(pubName, PgOutputProtocolVersion.V1);

            var relations = new Dictionary<uint, RelationMessage>();
            int count = 0;

            await foreach (var message in conn.StartReplication(slot, options, cts.Token))
            {
                switch (message)
                {
                    case RelationMessage rel:
                        relations[rel.RelationId] = rel;
                        Console.WriteLine($"  TABLE: {rel.Namespace}.{rel.RelationName}");
                        break;

                    case InsertMessage insert:
                    {
                        var rel = relations[insert.Relation.RelationId];
                        var values = new List<string>();
                        int i = 0;
                        await foreach (var value in insert.NewRow)
                        {
                            var colName = rel.Columns[i].ColumnName;
                            var val = value.IsDBNull ? "NULL" : (await value.Get(cts.Token))?.ToString() ?? "NULL";
                            values.Add($"{colName}={val}");
                            i++;
                        }
                        Console.WriteLine($"  INSERT: {string.Join(", ", values)}");
                        count++;
                        break;
                    }
                }

                conn.SetReplicationStatus(message.WalEnd);
            }
        }
        catch (OperationCanceledException)
        {
            // timeout = no more messages
        }

        Console.WriteLine();
    }
}
