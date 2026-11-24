using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace EnterpriseCommerce.Inventory.Infrastructure.ServiceClients;

/// <summary>Why an internal call gave no usable answer: a bounded set, safe to log.</summary>
internal enum InternalCallFailure
{
    None,
    NotConfigured,
    NoToken,
    NotReached,
    UnexpectedStatus,
}

/// <summary>The outcome of an internal call: the parsed <c>200</c> body, or why there is none.</summary>
internal sealed record InternalCallResult(JsonDocument? Body, InternalCallFailure Failure, int? StatusCode, Exception? Exception) : IDisposable
{
    public void Dispose() => Body?.Dispose();
}

/// <summary>
/// One authenticated <c>POST</c> to another service's internal API with a <c>svc-inventory</c> service token
/// (ADR-025 section 2, ADR-027 section 2): the token is renewed and the call retried once after a <c>401</c>, and the
/// <c>X-Correlation-ID</c> is propagated. It never throws for a remote failure: the caller maps every failure to its own
/// fail-closed exception. It never logs; the caller logs without the body or the token.
/// </summary>
internal static class InternalApiCaller
{
    private const string CorrelationHeader = "X-Correlation-ID";

    public static async Task<InternalCallResult> PostAsync(
        HttpClient httpClient,
        InventoryServiceTokenProvider tokens,
        string scope,
        Uri? baseAddress,
        bool serviceClientSpecified,
        string relativePath,
        object body,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (baseAddress is null || !serviceClientSpecified)
        {
            return new InternalCallResult(null, InternalCallFailure.NotConfigured, null, null);
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            string accessToken;
            try
            {
                accessToken = await tokens.GetAsync(scope, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return new InternalCallResult(null, InternalCallFailure.NoToken, null, exception);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, relativePath)) { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (correlationId is { Length: > 0 })
            {
                request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);
            }

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return new InternalCallResult(null, InternalCallFailure.NotReached, null, exception);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
                {
                    // The cached service token may have been revoked or expired early.
                    tokens.Invalidate(scope);
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return new InternalCallResult(null, InternalCallFailure.UnexpectedStatus, (int)response.StatusCode, null);
                }

                try
                {
                    var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                    return new InternalCallResult(json, InternalCallFailure.None, 200, null);
                }
                catch (JsonException exception)
                {
                    return new InternalCallResult(null, InternalCallFailure.UnexpectedStatus, 200, exception);
                }
            }
        }

        return new InternalCallResult(null, InternalCallFailure.UnexpectedStatus, (int)HttpStatusCode.Unauthorized, null);
    }
}
