using System.Net;

namespace tryAGI.Codex;

/// <summary>Explicitly selected credential and billing boundary, never inferred from a model name.</summary>
public enum CodexProviderAuthMode
{
    /// <summary>ChatGPT subscription authorization.</summary>
    ChatGptSubscription,
    /// <summary>Explicit OpenAI API billing authorization.</summary>
    OpenAiApiKey,
    /// <summary>Host-managed custom gateway credentials.</summary>
    CustomCredential,
    /// <summary>Explicit Amazon Bedrock credentials.</summary>
    AmazonBedrock,
    /// <summary>Explicit Google Cloud credentials.</summary>
    GoogleCloud,
    /// <summary>No provider credential.</summary>
    None,
}

/// <summary>Host-owned provider registration. No credentials are stored in route metadata.</summary>
public sealed record CodexProviderRoute(string Id, CodexProviderAuthMode AuthMode, Uri BaseUri);

/// <summary>Typed failures during provider selection and network authorization.</summary>
public enum CodexProviderRouteFailure
{
    /// <summary>No matching configured provider exists.</summary>
    UnknownProvider,
    /// <summary>The selected provider does not accept the requested credential mode.</summary>
    AuthenticationMismatch,
    /// <summary>The caller must name the paid or custom provider.</summary>
    ExplicitPaidProviderRequired,
    /// <summary>The destination is outside the allowed route or network policy.</summary>
    DestinationDenied,
    /// <summary>No effective network policy was supplied.</summary>
    NetworkPolicyUnavailable,
    /// <summary>The effective network policy was revoked.</summary>
    NetworkPolicyRevoked,
}

/// <summary>A safe provider routing or network authorization error.</summary>
public sealed class CodexProviderRouteException(CodexProviderRouteFailure failure)
    : InvalidOperationException("Codex provider route denied: " + failure)
{
    /// <summary>The classified denial.</summary>
    public CodexProviderRouteFailure Failure { get; } = failure;
}

/// <summary>Resolves only explicitly registered provider routes. This adapter never obtains credentials.</summary>
public sealed class CodexProviderRouting
{
    private readonly Dictionary<string, CodexProviderRoute> routes;
    private readonly string defaultProviderId;

    /// <summary>Creates a host-owned provider registry with a configured default.</summary>
    public CodexProviderRouting(IEnumerable<CodexProviderRoute> routes, string defaultProviderId)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProviderId);
        this.routes = new(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(route.Id);
            if (!Enum.IsDefined(route.AuthMode) || !ValidBase(route))
                throw new CodexProviderRouteException(CodexProviderRouteFailure.DestinationDenied);
            this.routes.Add(route.Id, route);
        }
        if (!this.routes.ContainsKey(defaultProviderId))
            throw new CodexProviderRouteException(CodexProviderRouteFailure.UnknownProvider);
        this.defaultProviderId = defaultProviderId;
    }

    /// <summary>Uses the configured default only for the requested credential mode.
    /// Paid API or cloud routes must be explicitly named by the caller.</summary>
    public CodexProviderRoute Resolve(string? requestedProviderId, CodexProviderAuthMode authMode)
    {
        if (!routes.TryGetValue(requestedProviderId ?? defaultProviderId, out var route))
            throw new CodexProviderRouteException(CodexProviderRouteFailure.UnknownProvider);
        if (authMode != route.AuthMode)
            throw new CodexProviderRouteException(CodexProviderRouteFailure.AuthenticationMismatch);
        if (requestedProviderId is null && authMode is CodexProviderAuthMode.OpenAiApiKey
            or CodexProviderAuthMode.AmazonBedrock or CodexProviderAuthMode.GoogleCloud
            or CodexProviderAuthMode.CustomCredential)
            throw new CodexProviderRouteException(CodexProviderRouteFailure.ExplicitPaidProviderRequired);
        return route;
    }

    /// <summary>Checks the exact registered origin and path boundary before attaching credentials.</summary>
    public static void ValidateDestination(CodexProviderRoute route, Uri destination)
    {
        if (!ValidBase(route) || !ValidUri(destination) ||
            !string.Equals(route.BaseUri.IdnHost, destination.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            route.BaseUri.Port != destination.Port ||
            !destination.AbsolutePath.StartsWith(route.BaseUri.AbsolutePath, StringComparison.Ordinal))
            throw new CodexProviderRouteException(CodexProviderRouteFailure.DestinationDenied);
    }

    private static bool ValidBase(CodexProviderRoute route)
    {
        var uri = route.BaseUri;
        if (!Enum.IsDefined(route.AuthMode) || !ValidUri(uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Query.Length != 0 ||
            !uri.AbsolutePath.EndsWith('/')) return false;

        return route.AuthMode switch
        {
            CodexProviderAuthMode.ChatGptSubscription => uri.IdnHost == "chatgpt.com"
                && uri.IsDefaultPort && uri.AbsolutePath == "/backend-api/codex/",
            CodexProviderAuthMode.OpenAiApiKey => uri.IdnHost == "api.openai.com"
                && uri.IsDefaultPort && uri.AbsolutePath == "/v1/",
            // Custom/cloud registrations cannot silently impersonate either native OpenAI route.
            _ => !IsOpenAiHost(uri.IdnHost),
        };
    }

    private static bool IsOpenAiHost(string host) => new[] { "openai.com", "openai.org", "chatgpt.com",
        "openai-staging.com", "chatgpt-staging.com" }.Any(domain =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    private static bool ValidUri(Uri uri) => uri.IsAbsoluteUri
        && uri.Scheme is "https" or "wss"
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.IdnHost.Length > 0 && !uri.IdnHost.EndsWith('.')
        && !uri.AbsolutePath.Contains("%2f", StringComparison.OrdinalIgnoreCase)
        && !uri.AbsolutePath.Contains("%5c", StringComparison.OrdinalIgnoreCase)
        && !uri.AbsolutePath.Contains("%25", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A host-owned, immutable network-policy snapshot, revocable by its owner.</summary>
public sealed class CodexProviderNetworkPolicy
{
    private readonly HashSet<string>? allowedHosts;
    private readonly CancellationToken revoked;

    private CodexProviderNetworkPolicy(HashSet<string>? allowedHosts, CancellationToken revoked)
    {
        this.allowedHosts = allowedHosts;
        this.revoked = revoked;
    }

    /// <summary>An explicit unrestricted destination policy, still constrained by the selected provider route.</summary>
    public static CodexProviderNetworkPolicy Unrestricted(CancellationToken revoked = default) => new(null, revoked);

    /// <summary>Allows only exact normalized TLS hosts. An empty collection denies all destinations.</summary>
    public static CodexProviderNetworkPolicy Restricted(IEnumerable<string> hosts, CancellationToken revoked = default)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts)
        {
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.EndsWith('.'))
                throw new ArgumentException("A valid exact host is required.", nameof(hosts));
            normalized.Add(new Uri("https://" + (host.Contains(':') && !host.StartsWith('[') ? "[" + host + "]" : host)).IdnHost);
        }
        return new(normalized, revoked);
    }

    /// <summary>Checks a destination against the current snapshot.</summary>
    public void Validate(Uri destination)
    {
        if (revoked.IsCancellationRequested)
            throw new CodexProviderRouteException(CodexProviderRouteFailure.NetworkPolicyRevoked);
        if (!destination.IsAbsoluteUri || destination.Scheme is not ("https" or "wss") ||
            destination.UserInfo.Length != 0 || destination.Fragment.Length != 0 ||
            destination.IdnHost.EndsWith('.') ||
            (allowedHosts is not null && !allowedHosts.Contains(destination.IdnHost)))
            throw new CodexProviderRouteException(CodexProviderRouteFailure.DestinationDenied);
    }

    /// <summary>Creates a route-bound HTTPS transport. Redirects are disabled and the host may supply a proxy.
    /// Existing transports must opt in to this adapter explicitly.</summary>
    public HttpMessageHandler CreateHttpHandler(CodexProviderRoute route, IWebProxy? proxy = null)
        => new PolicyHandler(this, route, new SocketsHttpHandler { AllowAutoRedirect = false, Proxy = proxy });

    internal sealed class PolicyHandler(CodexProviderNetworkPolicy policy, CodexProviderRoute route, HttpMessageHandler inner)
        : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var destination = request.RequestUri ?? throw new CodexProviderRouteException(CodexProviderRouteFailure.DestinationDenied);
            CodexProviderRouting.ValidateDestination(route, destination);
            policy.Validate(destination);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, policy.revoked);
            try
            {
                var response = await base.SendAsync(request, linked.Token).ConfigureAwait(false);
                try
                {
                    policy.Validate(destination);
                    // Keep the policy alive throughout SSE and other streamed response bodies.
                    response.Content = new CodexRevocableHttpContent(response.Content, policy.revoked);
                    policy.Validate(destination);
                    return response;
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }
            catch (OperationCanceledException) when (policy.revoked.IsCancellationRequested)
            {
                throw new CodexProviderRouteException(CodexProviderRouteFailure.NetworkPolicyRevoked);
            }
        }
    }
}
