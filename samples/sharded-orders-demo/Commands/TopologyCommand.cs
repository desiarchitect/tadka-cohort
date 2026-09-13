namespace Tadka.Samples.ShardedOrdersDemo.Commands;

static class TopologyCommand
{
    public static async Task RunAsync(Args args, ClusterState state)
    {
        Console.WriteLine("=== Cluster Topology ===");
        Console.WriteLine($"Routing mode : {Router.Describe(state)}");
        Console.WriteLine($"Shard key    : {state.ShardKey ?? "(not seeded yet)"}");

        long total = 0;
        foreach (var id in state.ShardIds)
        {
            bool up = await ShardStore.PingAsync(id);
            if (!up)
            {
                Console.WriteLine($"  shard {id}  {ShardCatalog.HostLabel(id),-32}  UNREACHABLE");
                continue;
            }
            try
            {
                long count = await ShardStore.CountAsync(id);
                total += count;
                Console.WriteLine($"  shard {id}  {ShardCatalog.HostLabel(id),-32}  rows: {count,7:N0}");
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01") // undefined_table
            {
                Console.WriteLine($"  shard {id}  {ShardCatalog.HostLabel(id),-32}  no schema yet (run `init`)");
            }
        }
        Console.WriteLine($"Total rows: {total:N0}");
    }
}
