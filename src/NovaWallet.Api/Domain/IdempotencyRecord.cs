namespace NovaWallet.Api.Domain;

/// <summary>
/// Persists the outcome of an idempotent operation keyed by the client-supplied
/// Idempotency-Key. Replaying the same key returns the stored response; reusing a key
/// with a different request payload is rejected as a conflict.
/// </summary>
public class IdempotencyRecord
{
    public Guid Id { get; set; }

    /// <summary>The client-supplied Idempotency-Key header value.</summary>
    public string Key { get; set; } = default!;

    /// <summary>SHA-256 of the canonical request payload, used to detect key reuse with a different body.</summary>
    public string RequestHash { get; set; } = default!;

    /// <summary>HTTP status code that was returned for the original request.</summary>
    public int ResponseStatusCode { get; set; }

    /// <summary>Serialized response body returned for the original request.</summary>
    public string ResponseBody { get; set; } = default!;

    public DateTimeOffset CreatedAt { get; set; }
}
