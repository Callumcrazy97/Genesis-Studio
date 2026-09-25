using System.Collections.Concurrent;

Console.WriteLine("DevProfiler .NET sample starting.");
var allocations = new ConcurrentBag<byte[]>();

Task worker = Task.Run(() =>
{
    for (int batch = 0; batch < 14; batch++)
    {
        double value = CalculateBatch(batch + 100, 900_000);
        Console.WriteLine($"Worker {batch}: {value:F4}");
        Thread.Sleep(60);
    }
});

for (int batch = 0; batch < 20; batch++)
{
    double value = CalculateBatch(batch, 1_100_000);
    if (batch % 4 == 0)
        allocations.Add(new byte[2 * 1024 * 1024]);
    Console.WriteLine($"Main {batch}: {value:F4}");
    Thread.Sleep(75);
}

await worker;
Console.WriteLine("DevProfiler .NET sample complete.");

static double CalculateBatch(int seed, int iterations)
{
    double total = 0;
    for (int index = 0; index < iterations; index++)
        total += Math.Sin(seed + index * 0.001) * Math.Cos(index * 0.0007);
    return total;
}
