namespace EnterpriseCommerce.Catalog.Domain.Common;

/// <summary>
/// Validation errors keyed by the request field they concern (for example <c>variants[2].sku</c>). Domain factories
/// collect every error instead of stopping at the first, so a client can fix a request in one round trip.
/// </summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool IsEmpty => _errors.Count == 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            messages = [];
            _errors[field] = messages;
        }

        messages.Add(message);
    }

    public bool Contains(string field) => _errors.ContainsKey(field);

    public IReadOnlyDictionary<string, string[]> ToDictionary() =>
        _errors.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray(), StringComparer.Ordinal);
}
