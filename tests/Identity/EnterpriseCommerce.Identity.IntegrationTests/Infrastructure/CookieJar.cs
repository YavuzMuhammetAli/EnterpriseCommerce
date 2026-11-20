namespace EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;

/// <summary>
/// A minimal browser cookie store for one test host. Unlike the built-in cookie handling, a test can take a snapshot
/// and put it back, to present a cookie the server has since replaced.
/// </summary>
public sealed class CookieJar : DelegatingHandler
{
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_lock)
        {
            return new Dictionary<string, string>(_cookies, StringComparer.Ordinal);
        }
    }

    public void Restore(IReadOnlyDictionary<string, string> snapshot)
    {
        lock (_lock)
        {
            _cookies.Clear();
            foreach (var (name, value) in snapshot)
            {
                _cookies[name] = value;
            }
        }
    }

    public string? ValueOf(string name)
    {
        lock (_lock)
        {
            return _cookies.GetValueOrDefault(name);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_cookies.Count > 0)
            {
                request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
            }
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            lock (_lock)
            {
                foreach (var header in setCookies)
                {
                    var pair = header.Split(';', 2)[0];
                    var separator = pair.IndexOf('=', StringComparison.Ordinal);
                    var name = pair[..separator].Trim();
                    var value = pair[(separator + 1)..].Trim();
                    var expired = header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);

                    if (expired || value.Length == 0)
                    {
                        _cookies.Remove(name);
                    }
                    else
                    {
                        _cookies[name] = value;
                    }
                }
            }
        }

        return response;
    }
}
