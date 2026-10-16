using Azure.Core;

namespace AirlineDemo.Api.Investigation;

/// <summary>
/// Resolves the real credential on the first token request, never when the chat client is built, so registering or
/// resolving the client resolves no credential and makes no token request.
/// </summary>
public sealed class LazyTokenCredential(Func<TokenCredential> create) : TokenCredential
{
    private readonly Lazy<TokenCredential> _inner = new(create, LazyThreadSafetyMode.ExecutionAndPublication);

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        _inner.Value.GetToken(requestContext, cancellationToken);

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        _inner.Value.GetTokenAsync(requestContext, cancellationToken);
}
