using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NovaWallet.Api.Persistence;
using Testcontainers.PostgreSql;

namespace NovaWallet.Tests;

/// <summary>
/// Spins up a throwaway PostgreSQL container and boots the real API against it. Using a real
/// database (not the in-memory provider) is essential: the concurrency guarantees rely on
/// PostgreSQL row locks (SELECT ... FOR UPDATE) and the xmin concurrency token, neither of which
/// the in-memory provider emulates.
/// </summary>
public class PostgresApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("novawallet")
        .WithUsername("novawallet")
        .WithPassword("novawallet")
        // Headroom for the concurrency load test, which holds many connections blocked on row locks.
        .WithCommand("-c", "max_connections=300")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Point the app at the container and mark the env as Testing so Program skips its own
        // startup migration; the fixture applies migrations explicitly in InitializeAsync.
        builder.UseEnvironment("Testing");
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_db.GetConnectionString())
        {
            MaxPoolSize = 200
        }.ConnectionString;
        builder.UseSetting("ConnectionStrings:Postgres", cs);
    }

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        using var scope = Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        await ctx.Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await _db.DisposeAsync();
    }

    /// <summary>Creates an HttpClient with a valid bearer token attached.</summary>
    public async Task<HttpClient> CreateAuthenticatedClientAsync(string subject = "test-subject")
    {
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/token", new { subject, customerId = (string?)null });
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private record TokenDto(string AccessToken, string TokenType, DateTimeOffset ExpiresAt);
}
