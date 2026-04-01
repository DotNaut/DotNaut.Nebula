using Npgsql.Replication;
using Npgsql.Replication.PgOutput;
using Npgsql.Replication.PgOutput.Messages;

namespace PgKafkaWal.Consumer;

class Program
{
    // --- Configuration ---
    const string ConnectionString = "Host=localhost;Port=5432;Username=repuser;Password=secret;Database=mydb";
    const string PublicationName = "my_pub";
    const string SlotName = "my_slot";

    static async Task Main(string[] args)
    {
        if (args.Contains("--partition-test"))
        {
            await PartitionTest.RunAsync();
            return;
        }

        Console.WriteLine("PostgreSQL Logical Replication Listener");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("\nStopping...");
        };

        try
        {
            await ListenAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Listener stopped.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
        }
    }

    static async Task ListenAsync(CancellationToken ct)
    {
        await using var conn = new LogicalReplicationConnection(ConnectionString);
        await conn.Open(ct);

        Console.WriteLine("Connected. Starting replication stream...\n");

        var slot = new PgOutputReplicationSlot(SlotName);
        var options = new PgOutputReplicationOptions(PublicationName, PgOutputProtocolVersion.V1);

        // RelationMessage is sent before the first DML for each table — cache it
        var relations = new Dictionary<uint, RelationMessage>();

        await foreach (var message in conn.StartReplication(slot, options, ct))
        {
            switch (message)
            {
                case BeginMessage begin:
                    Console.WriteLine($"--- BEGIN (xid={begin.TransactionXid}) ---");
                    break;

                case RelationMessage relation:
                    relations[relation.RelationId] = relation;
                    break;

                case InsertMessage insert:
                {
                    var rel = relations[insert.Relation.RelationId];
                    var values = await ReadTupleAsync(rel, insert.NewRow, ct);
                    Console.WriteLine($"  INSERT {rel.Namespace}.{rel.RelationName}");
                    PrintValues(values);
                    break;
                }

                case FullUpdateMessage fullUpdate:
                {
                    var rel = relations[fullUpdate.Relation.RelationId];
                    var oldValues = await ReadTupleAsync(rel, fullUpdate.OldRow, ct);
                    var newValues = await ReadTupleAsync(rel, fullUpdate.NewRow, ct);
                    Console.WriteLine($"  UPDATE {rel.Namespace}.{rel.RelationName}");
                    Console.WriteLine($"    old: {FormatValues(oldValues)}");
                    Console.WriteLine($"    new: {FormatValues(newValues)}");
                    break;
                }

                case DefaultUpdateMessage update:
                {
                    var rel = relations[update.Relation.RelationId];
                    var newValues = await ReadTupleAsync(rel, update.NewRow, ct);
                    Console.WriteLine($"  UPDATE {rel.Namespace}.{rel.RelationName}");
                    PrintValues(newValues);
                    break;
                }

                case FullDeleteMessage fullDelete:
                {
                    var rel = relations[fullDelete.Relation.RelationId];
                    var oldRow = await ReadTupleAsync(rel, fullDelete.OldRow, ct);
                    Console.WriteLine($"  DELETE {rel.Namespace}.{rel.RelationName}");
                    PrintValues(oldRow);
                    break;
                }

                case KeyDeleteMessage keyDelete:
                {
                    var rel = relations[keyDelete.Relation.RelationId];
                    var keys = await ReadTupleAsync(rel, keyDelete.Key, ct);
                    Console.WriteLine($"  DELETE {rel.Namespace}.{rel.RelationName} (by key)");
                    PrintValues(keys);
                    break;
                }

                case TruncateMessage:
                    Console.WriteLine("  TRUNCATE");
                    break;

                case CommitMessage:
                    Console.WriteLine($"--- COMMIT ---\n");
                    break;
            }

            // Acknowledge WAL position so PostgreSQL can reclaim WAL space
            conn.SetReplicationStatus(message.WalEnd);
        }
    }

    /// <summary>
    /// Reads all column values from a ReplicationTuple.
    /// Must be consumed within the same loop iteration — Npgsql recycles message instances.
    /// </summary>
    static async ValueTask<Dictionary<string, object?>> ReadTupleAsync(
        RelationMessage relation, ReplicationTuple tuple, CancellationToken ct)
    {
        var result = new Dictionary<string, object?>();
        int i = 0;

        await foreach (var value in tuple)
        {
            var name = relation.Columns[i].ColumnName;

            if (value.IsDBNull)
                result[name] = null;
            else if (value.IsUnchangedToastedValue)
                result[name] = "<unchanged TOAST>";
            else
                result[name] = await value.Get(ct);

            i++;
        }

        return result;
    }

    static void PrintValues(Dictionary<string, object?> values)
    {
        foreach (var (key, val) in values)
            Console.WriteLine($"    {key} = {val ?? "NULL"}");
    }

    static string FormatValues(Dictionary<string, object?> values)
        => string.Join(", ", values.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));
}
