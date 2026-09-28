using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Services.Reply;

namespace BotAgent.ConcurrencyStressProbe;

public static class Program
{
    private const int TenantCount = 50;
    private const int TotalEvents = 1_000;
    private const int NoiseEvents = 200;
    private const int GlobalMaxConcurrency = 8;
    private const int P95TargetMilliseconds = 500;

    public static async Task<int> Main()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "botagent-concurrency-stress-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", dataRoot);
        var checks = 0;
        var failures = 0;
        var errors = new ConcurrentQueue<string>();
        var startedAt = Stopwatch.GetTimestamp();
        var latencySamples = new ConcurrentBag<double>();
        var processedByTenant = new ConcurrentDictionary<string, ConcurrentQueue<long>>();
        var queues = Enumerable.Range(0, TenantCount)
            .ToDictionary(index => TenantKey(index), _ => new TenantReplyQueue());
        var acceptedTimestamps = new ConcurrentDictionary<(string Tenant, long Sequence), long>();
        var accepted = 0L;
        var coalesced = 0L;
        var preservedOverflow = 0L;
        var maxQueueDepth = 0;
        var counters = new StressCounters();
        var injected = 0;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            AppDatabase.Initialize();
            AppDatabase.Write(connection => AppDatabase.Exec(connection,
                "CREATE TABLE IF NOT EXISTS synthetic_stress_events (tenant_key TEXT NOT NULL, sequence_no INTEGER NOT NULL, PRIMARY KEY (tenant_key, sequence_no))"));

            foreach (var tenantIndex in Enumerable.Range(0, TenantCount))
            {
                var tenant = TenantKey(tenantIndex);
                processedByTenant[tenant] = new ConcurrentQueue<long>();
            }

            var eventPlan = BuildEventPlan();
            foreach (var item in eventPlan)
            {
                injected++;
                var queue = queues[item.Tenant];
                var offer = queue.Enqueue(
                    new PendingReply(item.Sequence, Proactive: false, MustPreserve: item.MustPreserve),
                    TenantReplyQueue.DefaultCapacity);
                switch (offer)
                {
                    case PendingReplyOffer.Accepted:
                        Interlocked.Increment(ref accepted);
                        acceptedTimestamps[(item.Tenant, item.Sequence)] = Stopwatch.GetTimestamp();
                        break;
                    case PendingReplyOffer.PreservedOverflow:
                        Interlocked.Increment(ref accepted);
                        Interlocked.Increment(ref preservedOverflow);
                        acceptedTimestamps[(item.Tenant, item.Sequence)] = Stopwatch.GetTimestamp();
                        break;
                    case PendingReplyOffer.AcceptedAfterCoalescing:
                    case PendingReplyOffer.CoalescedOld:
                        Interlocked.Increment(ref coalesced);
                        acceptedTimestamps[(item.Tenant, item.Sequence)] = Stopwatch.GetTimestamp();
                        break;
                    case PendingReplyOffer.CoalescedIncoming:
                        Interlocked.Increment(ref coalesced);
                        break;
                }

                var depth = queue.Count;
                if (depth > maxQueueDepth)
                {
                    maxQueueDepth = depth;
                }
            }

            var injectionMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            Check(ref checks, ref failures, injected == TotalEvents, "注入事件总量为 1000");
            Check(ref checks, ref failures, injectionMilliseconds < 3_000, "在 3 秒内完成合成事件注入");
            Check(ref checks, ref failures, queues[TenantKey(0)].Count <= TenantReplyQueue.DefaultCapacity, "噪声租户队列保持有界");
            Check(ref checks, ref failures, maxQueueDepth <= TenantReplyQueue.DefaultCapacity, "普通轮次不会突破单租户队列上限");
            Check(ref checks, ref failures, coalesced > 0, "噪声租户发生可观察的安全合并");

            using var globalGate = new SemaphoreSlim(GlobalMaxConcurrency, GlobalMaxConcurrency);
            var workers = queues.Select(pair => ProcessTenantAsync(
                pair.Key,
                pair.Value,
                globalGate,
                processedByTenant,
                acceptedTimestamps,
                latencySamples,
                errors,
                counters)).ToArray();
            await Task.WhenAll(workers);
            var persistence = processedByTenant.Select(pair => PersistTenantAsync(pair.Key, pair.Value, errors, counters)).ToArray();
            await Task.WhenAll(persistence);

            var processed = processedByTenant.Values.Sum(queue => queue.Count);
            var allOrdered = processedByTenant.All(pair => IsStrictlyIncreasing(pair.Value));
            var otherTenantsProgressed = processedByTenant.Skip(1).All(pair => pair.Value.Count > 0);
            var sqliteRows = AppDatabase.Scalar<long>("SELECT COUNT(1) FROM synthetic_stress_events");
            var p95 = Percentile(latencySamples, 0.95);

            Check(ref checks, ref failures, processed == accepted, "已接受事件全部完成处理");
            Check(ref checks, ref failures, allOrdered, "单租户处理顺序严格递增");
            Check(ref checks, ref failures, otherTenantsProgressed, "非噪声租户仍能推进");
            Check(ref checks, ref failures, counters.MaxActiveWorkers > 1 && counters.MaxActiveWorkers <= GlobalMaxConcurrency, "跨租户并行且不超过全局并发上限");
            Check(ref checks, ref failures, errors.IsEmpty && counters.Unhandled == 0 && counters.SqliteBusy == 0, "无未处理异常或 SQLITE_BUSY");
            Check(ref checks, ref failures, sqliteRows == processed, "合成事件持久化行数与处理数一致");
            Check(ref checks, ref failures, p95 <= P95TargetMilliseconds, "P95 调度延迟不超过声明的测试基线");

            var result = new
            {
                probe = "ConcurrencyStressProbe",
                status = failures == 0 ? "pass" : "fail",
                tenants = TenantCount,
                injected,
                noiseTenantEvents = NoiseEvents,
                accepted,
                processed,
                coalesced,
                preservedOverflow,
                maxQueueDepth,
                queueLimit = TenantReplyQueue.DefaultCapacity,
                globalMaxConcurrency = GlobalMaxConcurrency,
                maxActiveWorkers = counters.MaxActiveWorkers,
                p95SchedulingMilliseconds = Math.Round(p95, 3),
                declaredP95BaselineMilliseconds = P95TargetMilliseconds,
                sqliteBusy = counters.SqliteBusy,
                unhandled = counters.Unhandled,
                checks,
                failures
            };
            Console.WriteLine(JsonSerializer.Serialize(result));
            if (!errors.IsEmpty)
            {
                Console.Error.WriteLine("synthetic worker errors: " + string.Join(" | ", errors.Take(5)));
            }
            Console.WriteLine($"通过 {checks - failures}，失败 {failures}");
            return failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            var result = new
            {
                probe = "ConcurrencyStressProbe",
                status = "fail",
                error = ex.GetType().Name,
                checks,
                failures = failures + 1
            };
            Console.WriteLine(JsonSerializer.Serialize(result));
            Console.Error.WriteLine("失败：" + ex.Message);
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(dataRoot))
                {
                    Directory.Delete(dataRoot, recursive: true);
                }
            }
            catch
            {
                // 临时目录清理失败不改变探针断言结果。
            }
        }
    }

    private static async Task ProcessTenantAsync(
        string tenant,
        TenantReplyQueue queue,
        SemaphoreSlim globalGate,
        ConcurrentDictionary<string, ConcurrentQueue<long>> processedByTenant,
        ConcurrentDictionary<(string Tenant, long Sequence), long> acceptedTimestamps,
        ConcurrentBag<double> latencySamples,
        ConcurrentQueue<string> errors,
        StressCounters counters)
    {
        while (queue.TryDequeue(out var item))
        {
            try
            {
                await globalGate.WaitAsync();
                var active = Interlocked.Increment(ref counters.ActiveWorkers);
                UpdateMax(ref counters.MaxActiveWorkers, active);
                try
                {
                    if (item.TriggerMessageId is long sequence && acceptedTimestamps.TryGetValue((tenant, sequence), out var enqueuedAt))
                    {
                        latencySamples.Add(ElapsedMilliseconds(enqueuedAt));
                    }

                    await Task.Delay(1);
                    processedByTenant[tenant].Enqueue(item.TriggerMessageId ?? 0);
                }
                finally
                {
                    Interlocked.Decrement(ref counters.ActiveWorkers);
                    globalGate.Release();
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("SQLITE_BUSY", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref counters.SqliteBusy);
                }
                else
                {
                    Interlocked.Increment(ref counters.Unhandled);
                }

                errors.Enqueue(tenant + ":" + ex.GetType().Name);
            }
        }
    }

    private static Task PersistTenantAsync(
        string tenant,
        ConcurrentQueue<long> processed,
        ConcurrentQueue<string> errors,
        StressCounters counters)
        => Task.Run(() =>
        {
            try
            {
                AppDatabase.Write(connection =>
                {
                    foreach (var sequence in processed)
                    {
                        AppDatabase.Exec(connection,
                            "INSERT INTO synthetic_stress_events(tenant_key, sequence_no) VALUES ($tenant, $sequence)",
                            ("$tenant", tenant),
                            ("$sequence", sequence));
                    }
                });
            }
            catch (Exception ex)
            {
                if (IsSqliteBusy(ex))
                {
                    Interlocked.Increment(ref counters.SqliteBusy);
                }
                else
                {
                    Interlocked.Increment(ref counters.Unhandled);
                }

                errors.Enqueue(tenant + ":" + ex.GetType().Name);
            }
        });

    private static bool IsSqliteBusy(Exception ex)
        => ex.ToString().Contains("SQLITE_BUSY", StringComparison.OrdinalIgnoreCase)
            || ex.ToString().Contains("database is locked", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<SyntheticEvent> BuildEventPlan()
    {
        var result = new List<SyntheticEvent>(TotalEvents);
        for (var tenantIndex = 0; tenantIndex < TenantCount; tenantIndex++)
        {
            var count = tenantIndex == 0 ? NoiseEvents : (TotalEvents - NoiseEvents) / (TenantCount - 1);
            if (tenantIndex > 0 && tenantIndex <= (TotalEvents - NoiseEvents) % (TenantCount - 1))
            {
                count++;
            }

            for (var sequence = 1; sequence <= count; sequence++)
            {
                result.Add(new SyntheticEvent(TenantKey(tenantIndex), sequence, sequence % 37 == 0));
            }
        }

        return result;
    }

    private static string TenantKey(int index) => "group:synthetic-" + index.ToString("D2");

    private static bool IsStrictlyIncreasing(IEnumerable<long> values)
    {
        var first = true;
        var previous = 0L;
        foreach (var value in values)
        {
            if (!first && value <= previous)
            {
                return false;
            }

            first = false;
            previous = value;
        }

        return true;
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        var index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static double ElapsedMilliseconds(long startedAt)
        => (Stopwatch.GetTimestamp() - startedAt) * 1_000d / Stopwatch.Frequency;

    private static void UpdateMax(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    private static void Check(ref int checks, ref int failures, bool condition, string name)
    {
        checks++;
        if (!condition)
        {
            failures++;
            Console.Error.WriteLine("断言失败：" + name);
        }
    }

    private readonly record struct SyntheticEvent(string Tenant, long Sequence, bool MustPreserve);

    private sealed class StressCounters
    {
        public int ActiveWorkers;
        public int MaxActiveWorkers;
        public long SqliteBusy;
        public long Unhandled;
    }
}
