using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NovaWallet.Tests;

/// <summary>
/// Verifies that every error path returns the same RFC 7807 shape: application/problem+json with a
/// machine-readable title (error code), plus the correlationId / traceId / timestamp extensions.
/// A consistent error contract is what lets clients branch reliably on failures.
/// </summary>
public class ErrorHandlingTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private static async Task<JsonElement> ReadProblem(HttpResponseMessage resp)
    {
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement;
    }

    private static void AssertHasCommonExtensions(JsonElement p)
    {
        Assert.True(p.TryGetProperty("correlationId", out _), "missing correlationId");
        Assert.True(p.TryGetProperty("traceId", out _), "missing traceId");
        Assert.True(p.TryGetProperty("timestamp", out _), "missing timestamp");
        Assert.True(p.TryGetProperty("type", out var type));
        Assert.StartsWith("https://novawallet.example/problems/", type.GetString());
    }

    [Fact]
    public async Task Model_validation_error_uses_problem_shape_with_field_errors()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        // Missing customerId -> model validation failure.
        var resp = await client.PostAsJsonAsync("/api/wallets", new { });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var p = await ReadProblem(resp);
        AssertHasCommonExtensions(p);
        Assert.Equal("validation_error", p.GetProperty("title").GetString());
        Assert.True(p.TryGetProperty("errors", out var errors));
        // Field-level map should mention the offending field (lower-cased).
        Assert.Contains(errors.EnumerateObject(), e => e.Name.Equals("customerId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Malformed_json_body_returns_consistent_400()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var content = new StringContent("{ this is not json ", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/api/wallets", content);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var p = await ReadProblem(resp);
        AssertHasCommonExtensions(p);
        // Either the malformed_request code (our handler) or validation_error, but always problem+json.
        var title = p.GetProperty("title").GetString();
        Assert.Contains(title, new[] { "malformed_request", "validation_error" });
    }

    [Fact]
    public async Task Domain_error_returns_problem_json_with_code()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var a = await client.CreateWalletAsync("err-a");
        var b = await client.CreateWalletAsync("err-b");
        await client.CreditAsync(a, 100);

        var resp = await client.PostAsJsonAsync("/api/transfers",
            new { fromWalletId = a, toWalletId = b, amountKobo = 5_000L });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var p = await ReadProblem(resp);
        AssertHasCommonExtensions(p);
        Assert.Equal("insufficient_funds", p.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Same_wallet_transfer_is_rejected_with_specific_code()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var a = await client.CreateWalletAsync("err-same");

        var resp = await client.PostAsJsonAsync("/api/transfers",
            new { fromWalletId = a, toWalletId = a, amountKobo = 1_000L });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var p = await ReadProblem(resp);
        Assert.Equal("same_wallet_transfer", p.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Not_found_wallet_returns_problem_json()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var resp = await client.GetAsync($"/api/wallets/{Guid.NewGuid()}/balance");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var p = await ReadProblem(resp);
        AssertHasCommonExtensions(p);
        Assert.Equal("wallet_not_found", p.GetProperty("title").GetString());
    }
}
