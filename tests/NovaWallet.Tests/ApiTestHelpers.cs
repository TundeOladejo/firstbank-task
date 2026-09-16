using System.Net.Http.Json;

namespace NovaWallet.Tests;

public record WalletDto(Guid WalletId, string CustomerId, string Currency, long BalanceKobo, DateTimeOffset CreatedAt);
public record BalanceDto(Guid WalletId, string Currency, long BalanceKobo);
public record TransferDto(Guid TransferId, Guid FromWalletId, Guid ToWalletId, long AmountKobo, long FromBalanceKobo, DateTimeOffset CompletedAt);
public record TxnDto(Guid Id, string Type, long AmountKobo, long BalanceAfterKobo, Guid? CounterpartyWalletId, Guid? TransferId, string? Reference, DateTimeOffset CreatedAt);
public record PagedDto<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);

public static class ApiTestHelpers
{
    public static async Task<Guid> CreateWalletAsync(this HttpClient client, string customerId)
    {
        var resp = await client.PostAsJsonAsync("/api/wallets", new { customerId });
        resp.EnsureSuccessStatusCode();
        var wallet = await resp.Content.ReadFromJsonAsync<WalletDto>();
        return wallet!.WalletId;
    }

    public static async Task CreditAsync(this HttpClient client, Guid walletId, long amountKobo, string? reference = null)
    {
        var resp = await client.PostAsJsonAsync($"/api/wallets/{walletId}/credits", new { amountKobo, reference });
        resp.EnsureSuccessStatusCode();
    }

    public static async Task<long> GetBalanceAsync(this HttpClient client, Guid walletId)
    {
        var balance = await client.GetFromJsonAsync<BalanceDto>($"/api/wallets/{walletId}/balance");
        return balance!.BalanceKobo;
    }
}
