// Tadka — Sharding Demo (standalone teaching sample, not wired into the app).
//
// The cohort defers sharding (ADR-017) because 1 lakh orders/day fits one Postgres.
// But the *skill* — shard-key design, consistent hashing, vnodes, rebalancing cost —
// is an architect must-have. This simulation lets you SEE it, failure-first:
//
//   DEMO 1  naive hash % N         -> growing 4->5 shards moves ~80% of keys (disaster)
//   DEMO 2  consistent hashing, NO vnodes -> lumpy, uneven shards
//   DEMO 3  consistent hashing + vnodes    -> even; dropping a shard moves only ~1/N
//   DEMO 4  skewed shard key        -> hashing balances KEYS, not TRAFFIC (hot shard)
//
// Deterministic (fixed inputs) so the numbers are reproducible. No dependencies.

using System.Text;

const int KeyCount = 100_000;

Console.WriteLine("=== Tadka Sharding Demo — why 'just hash % N' is a trap ===");
Console.WriteLine($"Routing {KeyCount:N0} order keys across shards.\n");

// Reproducible keys: order-0000000 .. order-0099999
string[] keys = new string[KeyCount];
for (int i = 0; i < KeyCount; i++) keys[i] = $"order-{i:D7}";

Demo1_NaiveModulo(keys);
Demo2_ConsistentNoVnodes(keys);
Demo3_ConsistentWithVnodes(keys);
Demo4_SkewedShardKey();

Console.WriteLine("\n=== Takeaways ===");
Console.WriteLine(" 1. hash % N rebalances almost EVERYTHING when N changes -> never reshard naively.");
Console.WriteLine(" 2. Consistent hashing without vnodes is even-ish at best and lumpy in practice.");
Console.WriteLine(" 3. Vnodes (~150/shard) give smooth load AND small rebalances (~1/N on add/remove).");
Console.WriteLine(" 4. Hashing balances KEYS, not TRAFFIC: a low-cardinality/skewed shard key => hot shard.");
Console.WriteLine("    Pick a high-cardinality, evenly-distributed shard key; mitigate hot keys separately.");
return;

// ---------------------------------------------------------------------------

static void Demo1_NaiveModulo(string[] keys)
{
    Console.WriteLine("--- DEMO 1: naive hash % N (the resharding disaster) ---");

    int[] dist4 = new int[4];
    foreach (var k in keys) dist4[(int)(Hash(k) % 4)]++;
    PrintDistribution("hash % 4", dist4);

    // Grow 4 -> 5 shards and count how many keys land on a DIFFERENT shard.
    int moved = 0;
    foreach (var k in keys)
    {
        ulong h = Hash(k);
        if (h % 4 != h % 5) moved++;
    }
    double pct = 100.0 * moved / keys.Length;
    Console.WriteLine($"  Grow 4 -> 5 shards: {moved:N0} of {keys.Length:N0} keys move = {pct:F1}% reshuffled.");
    Console.WriteLine("  => Almost the whole dataset moves. This is why you NEVER shard with modulo.\n");
}

static void Demo2_ConsistentNoVnodes(string[] keys)
{
    Console.WriteLine("--- DEMO 2: consistent hashing, NO virtual nodes (lumpy) ---");
    var ring = BuildRing(shardCount: 4, vnodesPerShard: 1);
    int[] dist = new int[4];
    foreach (var k in keys) dist[Locate(ring, Hash(k))]++;
    PrintDistribution("4 shards, 1 point each", dist);
    Console.WriteLine("  => 3-4 ring points = uneven slices. One shard can carry far more than its share.\n");
}

static void Demo3_ConsistentWithVnodes(string[] keys)
{
    Console.WriteLine("--- DEMO 3: consistent hashing + virtual nodes (even + cheap rebalance) ---");
    const int vnodes = 150;

    var ring4 = BuildRing(shardCount: 4, vnodesPerShard: vnodes);
    int[] dist4 = new int[4];
    int[] assignedBefore = new int[keys.Length];
    for (int i = 0; i < keys.Length; i++)
    {
        int s = Locate(ring4, Hash(keys[i]));
        assignedBefore[i] = s;
        dist4[s]++;
    }
    PrintDistribution($"4 shards, {vnodes} vnodes each", dist4);

    // Remove shard 3 (4 -> 3) and count how many keys move.
    var ring3 = BuildRing(shardCount: 3, vnodesPerShard: vnodes);
    int moved = 0;
    for (int i = 0; i < keys.Length; i++)
    {
        int after = Locate(ring3, Hash(keys[i]));
        // Map old shard ids 0..3 vs new 0..2: a key "moved" if it was NOT on shard 3
        // yet changed owner, OR it was on the removed shard 3.
        bool wasOnRemoved = assignedBefore[i] == 3;
        if (wasOnRemoved || !SameOwner(assignedBefore[i], after, removed: 3)) moved++;
    }
    double pct = 100.0 * moved / keys.Length;
    Console.WriteLine($"  Drop 1 of 4 shards: {moved:N0} of {keys.Length:N0} keys move = {pct:F1}%.");
    Console.WriteLine($"  => ONLY the removed shard's keys relocate (~1/N). The other 3 shards: untouched.");
    Console.WriteLine($"     Contrast DEMO 1, where growing by one shard reshuffled ~80%.\n");
}

static void Demo4_SkewedShardKey()
{
    Console.WriteLine("--- DEMO 4: skewed shard key (hashing balances keys, not traffic) ---");
    // Shard by CITY. Bangalore is 80% of orders; 4 other cities split the rest.
    // Even perfect hashing sends all Bangalore orders to ONE shard.
    (string city, int orders)[] cities =
    {
        ("Bangalore", 80_000), ("Hyderabad", 8_000), ("Pune", 6_000),
        ("Chennai", 4_000), ("Delhi", 2_000),
    };
    var ring = BuildRing(shardCount: 4, vnodesPerShard: 150);
    int[] dist = new int[4];
    foreach (var (city, orders) in cities) dist[Locate(ring, Hash(city))] += orders;
    PrintDistribution("shard by city (Bangalore = 80%)", dist);
    Console.WriteLine("  => One shard holds ~80% of traffic. Great hashing can't fix a bad shard KEY.");
    Console.WriteLine("     Lesson: shard by a high-cardinality key (e.g. order_id / customer_id), not city.\n");
}

// --- consistent-hashing ring -------------------------------------------------

static (ulong point, int shard)[] BuildRing(int shardCount, int vnodesPerShard)
{
    var points = new List<(ulong point, int shard)>(shardCount * vnodesPerShard);
    for (int s = 0; s < shardCount; s++)
        for (int v = 0; v < vnodesPerShard; v++)
            points.Add((Hash($"shard-{s}#vnode-{v}"), s));
    points.Sort((a, b) => a.point.CompareTo(b.point));
    return points.ToArray();
}

// First ring point clockwise from the key's hash (wraps around).
static int Locate((ulong point, int shard)[] ring, ulong keyHash)
{
    int lo = 0, hi = ring.Length - 1, ans = 0; // default wrap to first point
    while (lo <= hi)
    {
        int mid = (lo + hi) / 2;
        if (ring[mid].point >= keyHash) { ans = mid; hi = mid - 1; }
        else lo = mid + 1;
    }
    if (lo == ring.Length) ans = 0; // past the last point -> wrap to first
    return ring[ans].shard;
}

// Owner is "the same" if the new owner equals the old, accounting for the fact
// that shard ids above the removed one are unaffected in our 0..N-1 labelling.
static bool SameOwner(int before, int after, int removed)
    => before != removed && before == after;

// --- helpers -----------------------------------------------------------------

static void PrintDistribution(string label, int[] dist)
{
    int total = dist.Sum();
    int max = dist.Max(), min = dist.Min();
    double spread = min == 0 ? double.PositiveInfinity : (double)max / min;
    Console.WriteLine($"  {label}:");
    for (int i = 0; i < dist.Length; i++)
    {
        double pct = 100.0 * dist[i] / total;
        string bar = new string('#', (int)Math.Round(pct / 2));
        Console.WriteLine($"    shard {i}: {dist[i],7:N0}  {pct,5:F1}%  {bar}");
    }
    Console.WriteLine($"    spread (max/min): {spread:F2}x  (1.00x = perfectly even)");
}

// Stable 64-bit hash — FNV-1a then a splitmix64 avalanche finalizer so even
// structured strings ("shard-0#vnode-1") spread evenly around the ring.
// Deterministic across runs/machines.
static ulong Hash(string s)
{
    const ulong offset = 14695981039346656037UL;
    const ulong prime = 1099511628211UL;
    ulong h = offset;
    foreach (byte b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= prime; }
    // splitmix64 finalizer (avalanche)
    h ^= h >> 30; h *= 0xbf58476d1ce4e5b9UL;
    h ^= h >> 27; h *= 0x94d049bb133111ebUL;
    h ^= h >> 31;
    return h;
}
