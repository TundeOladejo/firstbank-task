using System.Net;
using System.Net.Http.Json;

namespace NovaWallet.Tests;

public class LedgerIntegrationTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/wallets", new { customerId = "c1" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Create_wallet_starts_at_zero_in_ngn()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = await client.CreateWalletAsync("cust-zero");
        var balance = await client.GetFromJsonAsync<BalanceDto>($"/api/wallets/{id}/balance");
        Assert.Equal(0, balance!.BalanceKobo);
        Assert.Equal("NGN", balance.Currency);
    }

    [Fact]
    public async Task Credit_then_transfer_moves_funds_exactly()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var a = await client.CreateWalletAsync("cust-a");
        var b = await client.CreateWalletAsync("cust-b");

        await client.CreditAsync(a, 1_000_000);
        var resp = await client.PostAsJsonAsync("/api/transfers", new { fromWalletId = a, toWalletId = b, amountKobo = 250_000L });
        resp.EnsureSuccessStatusCode();

        Assert.Equal(750_000, await client.GetBalanceAsync(a));
        Assert.Equal(250_000, await client.GetBalanceAsync(b));
    }

    [Fact]
    public async Task Transfer_exceeding_balance_returns_422_and_does_not_move_money()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var a = await client.CreateWalletAsync("cust-a2");
        var b = await client.CreateWalletAsync("cust-b2");
        await client.CreditAsync(a, 100);

        var resp = await client.PostAsJsonAsync("/api/transfers", new { fromWalletId = a, toWalletId = b, amountKobo = 101L });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(100, await client.GetBalanceAsync(a));
        Assert.Equal(0, await client.GetBalanceAsync(b));
    }

    [Fact]
    public async Task Idempotency_replay_does_not_double_process()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var a = await client.CreateWalletAsync("cust-idem-a");
        var b = await client.CreateWalletAsync("cust-idem-b");
        await client.CreditAsync(a, 500_000);

        var key = Guid.NewGuid().ToString();
        var body = new { fromWalletId = a, toWalletId = b, amountKobo = 200_000L };

        var first = await SendTransfer(client, body, key);
        var second = await SendTransfer(client, body, key);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());

        // Only debited once.
        Assert.Equal(300_000, await client.GetBalanceAsync(a));
        Assert.Equal(200_000, await client.GetBalanceAsync(b));
    }

    [Fact]
    public async Task Reusing_idempotency_key_with_different_body_is_rejected()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var a = await client.CreateWalletAsync("cust-idem-c");
        var b = await client.CreateWalletAsync("cust-idem-d");
        await client.CreditAsync(a, 500_000);

        var key = Guid.NewGuid().ToString();
        var r1 = await SendTransfer(client, new { fromWalletId = a, toWalletId = b, amountKobo = 100_000L }, key);
        r1.EnsureSuccessStatusCode();

        var r2 = await SendTransfer(client, new { fromWalletId = a, toWalletId = b, amountKobo = 999_999L }, key);
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
    }

    [Fact]
    public async Task Statement_is_paginated_newest_first()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var a = await client.CreateWalletAsync("cust-stmt");
        await client.CreditAsync(a, 100, "first");
        await client.CreditAsync(a, 200, "second");
        await client.CreditAsync(a, 300, "third");

        var page = await client.GetFromJsonAsync<PagedDto<TxnDto>>($"/api/wallets/{a}/statement?page=1&pageSize=2");
        Assert.Equal(3, page!.TotalCount);
        Assert.Equal(2, page.Items.Count);
        // Newest first: the 300 credit should be first.
        Assert.Equal(300, page.Items[0].AmountKobo);
        Assert.Equal(200, page.Items[1].AmountKobo);
    }

    [Fact]
    public async Task Daily_limit_blocks_transfer_over_cap()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var a = await client.CreateWalletAsync("cust-limit-a");
        var b = await client.CreateWalletAsync("cust-limit-b");
        // Fund above the ₦500,000 (50,000,000 kobo) daily cap.
        await client.CreditAsync(a, 60_000_000);

        var ok = await client.PostAsJsonAsync("/api/transfers", new { fromWalletId = a, toWalletId = b, amountKobo = 50_000_000L });
        ok.EnsureSuccessStatusCode();

        // One kobo more would exceed today's cap.
        var blocked = await client.PostAsJsonAsync("/api/transfers", new { fromWalletId = a, toWalletId = b, amountKobo = 1L });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
    }

    private static Task<HttpResponseMessage> SendTransfer(HttpClient client, object body, string idempotencyKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/transfers")
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(req);
    }
}
