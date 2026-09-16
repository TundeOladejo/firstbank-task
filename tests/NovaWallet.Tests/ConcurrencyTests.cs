using System.Net;
using System.Net.Http.Json;

namespace NovaWallet.Tests;

/// <summary>
/// The core assessment requirement: the balance must never go negative and money must never be
/// double-spent under concurrent load. These tests fire many simultaneous transfers against a
/// single wallet funded for only a fraction of them, then assert the invariants hold exactly.
/// </summary>
public class ConcurrencyTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    [Fact]
    public async Task Concurrent_transfers_never_overspend_and_never_go_negative()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var source = await client.CreateWalletAsync("cust-race-src");
        var sink = await client.CreateWalletAsync("cust-race-sink");

        // Fund the source for exactly 50 transfers of 1,000 kobo. We then fire 100 concurrent
        // transfers: at most 50 may succeed, the rest must fail with insufficient funds.
        const int concurrent = 100;
        const long amount = 1_000;
        const int affordable = 50;
        await client.CreditAsync(source, amount * affordable);

        var tasks = Enumerable.Range(0, concurrent).Select(async i =>
        {
            // Each request gets its own client with a distinct subject so the per-subject rate
            // limiter (a separate concern) does not mask the ledger concurrency being tested.
            var c = await factory.CreateAuthenticatedClientAsync($"race-{i}");
            var resp = await c.PostAsJsonAsync("/api/transfers",
                new { fromWalletId = source, toWalletId = sink, amountKobo = amount });
            return resp.StatusCode;
        });

        var results = await Task.WhenAll(tasks);

        var succeeded = results.Count(s => s == HttpStatusCode.Created);
        var rejected = results.Count(s => s == HttpStatusCode.UnprocessableEntity);

        Assert.Equal(affordable, succeeded);
        Assert.Equal(concurrent - affordable, rejected);

        // Invariants: source drained to exactly zero, sink holds exactly what left the source,
        // and no money was created or destroyed.
        Assert.Equal(0, await client.GetBalanceAsync(source));
        Assert.Equal(amount * affordable, await client.GetBalanceAsync(sink));
    }

    [Fact]
    public async Task Concurrent_replays_of_same_idempotency_key_process_once()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var a = await client.CreateWalletAsync("cust-idem-race-a");
        var b = await client.CreateWalletAsync("cust-idem-race-b");
        await client.CreditAsync(a, 1_000_000);

        var key = Guid.NewGuid().ToString();
        var body = new { fromWalletId = a, toWalletId = b, amountKobo = 300_000L };

        // Fire the same idempotent request many times at once.
        var tasks = Enumerable.Range(0, 25).Select(async i =>
        {
            var c = await factory.CreateAuthenticatedClientAsync($"idem-race-{i}");
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/transfers") { Content = JsonContent.Create(body) };
            req.Headers.Add("Idempotency-Key", key);
            var resp = await c.SendAsync(req);
            return resp.StatusCode;
        });

        var results = await Task.WhenAll(tasks);

        // Every response is a success (either the original 201 or a replayed 201); none error.
        Assert.All(results, s => Assert.Equal(HttpStatusCode.Created, s));

        // Crucially, the money moved exactly once regardless of the 25 concurrent attempts.
        Assert.Equal(700_000, await client.GetBalanceAsync(a));
        Assert.Equal(300_000, await client.GetBalanceAsync(b));
    }
}
