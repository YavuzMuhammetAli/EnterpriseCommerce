using System.Net;
using System.Net.Http.Headers;

namespace EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;

/// <summary>One recorded response, read completely.</summary>
public sealed record BrowserResponse(Uri Url, HttpStatusCode Status, Dictionary<string, string[]> Headers, string Body)
{
    public Uri? Location => Headers.TryGetValue("Location", out var values) ? new Uri(Url, values[0]) : null;

    public IReadOnlyList<string> SetCookies => Headers.TryGetValue("Set-Cookie", out var values) ? values : [];

    public string? Header(string name) => Headers.TryGetValue(name, out var values) ? string.Join(", ", values) : null;

    /// <summary>The Set-Cookie header of a cookie, if this response set or deleted it.</summary>
    public string? SetCookie(string name) => SetCookies.FirstOrDefault(cookie => cookie.StartsWith(name + "=", StringComparison.Ordinal));
}

/// <summary>
/// A minimal browser over in-memory test servers: routes each request to the host its URL names, keeps cookies per
/// host (host-only, Secure, Path) and records every response, so tests can prove what reached the browser.
/// </summary>
public sealed class TestBrowser : IDisposable
{
    private readonly Dictionary<string, HttpMessageInvoker> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public CookieContainer Cookies { get; } = new();

    public List<BrowserResponse> Transcript { get; } = [];

    public TestBrowser Map(Uri origin, HttpMessageHandler handler)
    {
        _hosts[origin.Authority] = new HttpMessageInvoker(handler, disposeHandler: true);
        return this;
    }

    public string? Cookie(Uri origin, string name) => Cookies.GetCookies(origin)[name]?.Value;

    public void SetCookie(Uri origin, string name, string value) =>
        Cookies.Add(new Cookie(name, value, "/", origin.Host) { Secure = true, HttpOnly = true });

    public void RemoveCookie(Uri origin, string name)
    {
        var cookie = Cookies.GetCookies(origin)[name];
        if (cookie is not null)
        {
            cookie.Expired = true;
        }
    }

    public async Task<BrowserResponse> SendAsync(HttpRequestMessage request)
    {
        var url = request.RequestUri!;
        if (!_hosts.TryGetValue(url.Authority, out var invoker))
        {
            throw new InvalidOperationException($"No test server for {url.Authority}.");
        }

        var cookieHeader = Cookies.GetCookieHeader(url);
        if (cookieHeader.Length > 0)
        {
            request.Headers.Add("Cookie", cookieHeader);
        }

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var setCookie in setCookies)
            {
                Cookies.SetCookies(url, setCookie);
            }
        }

        var headers = response.Headers.Concat(response.Content.Headers)
            .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.SelectMany(header => header.Value).ToArray(), StringComparer.OrdinalIgnoreCase);
        var recorded = new BrowserResponse(url, response.StatusCode, headers, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Transcript.Add(recorded);
        return recorded;
    }

    public Task<BrowserResponse> GetAsync(Uri url, Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        configure?.Invoke(request);
        return SendAsync(request);
    }

    /// <summary>A top-level navigation: follows redirects across hosts until a non-redirect, or until <paramref name="stopBefore"/>.</summary>
    public async Task<BrowserResponse> NavigateAsync(Uri url, Func<Uri, bool>? stopBefore = null, int maxRedirects = 15)
    {
        var response = await GetAsync(url, request => request.Headers.Add("Sec-Fetch-Mode", "navigate"));
        for (var hop = 0; hop < maxRedirects && IsRedirect(response.Status) && response.Location is { } next; hop++)
        {
            if (stopBefore?.Invoke(next) == true)
            {
                return response;
            }

            response = await GetAsync(next, request => request.Headers.Add("Sec-Fetch-Mode", "navigate"));
        }

        return response;
    }

    /// <summary>A same-origin script request with Fetch Metadata, as a browser sends from the front end's own page.</summary>
    public Task<BrowserResponse> FetchAsync(HttpMethod method, Uri url, string? xsrfToken, string fetchSite = "same-origin", string? origin = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (fetchSite.Length > 0)
        {
            request.Headers.Add("Sec-Fetch-Site", fetchSite);
        }

        request.Headers.Add("Origin", origin ?? url.GetLeftPart(UriPartial.Authority));
        if (xsrfToken is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return SendAsync(request);
    }

    public static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    public void Dispose()
    {
        foreach (var invoker in _hosts.Values)
        {
            invoker.Dispose();
        }
    }
}
