namespace LogSphere.Sdk;

/// <summary>Ambient correlation, trace and business-entity context flowing with the async call chain.
/// Every event submitted through the SDK automatically attaches the active values.</summary>
public static class CorrelationContext
{
    private sealed class Holder
    {
        public string? CorrelationId;
        public string? TraceId;
        public string? SpanId;
        public string? UserId;
        public string? UserName;
        public string? SessionId;
        public string? BusinessEntityType;
        public string? BusinessEntityId;
        public string? ClientIp;
        public string? UserAgent;
    }

    private static readonly AsyncLocal<Holder?> Current = new();

    private static Holder Ensure() => Current.Value ??= new Holder();

    public static string? CorrelationId { get => Current.Value?.CorrelationId; set => Ensure().CorrelationId = value; }
    public static string? TraceId { get => Current.Value?.TraceId; set => Ensure().TraceId = value; }
    public static string? SpanId { get => Current.Value?.SpanId; set => Ensure().SpanId = value; }
    public static string? UserId { get => Current.Value?.UserId; set => Ensure().UserId = value; }
    public static string? UserName { get => Current.Value?.UserName; set => Ensure().UserName = value; }
    public static string? SessionId { get => Current.Value?.SessionId; set => Ensure().SessionId = value; }
    public static string? BusinessEntityType => Current.Value?.BusinessEntityType;
    public static string? BusinessEntityId => Current.Value?.BusinessEntityId;
    /// <summary>Caller IP of the current request (set by the middleware; X-Forwarded-For aware).
    /// Attached to every event — including audits — as http.clientIp.</summary>
    public static string? ClientIp { get => Current.Value?.ClientIp; set => Ensure().ClientIp = value; }
    public static string? UserAgent { get => Current.Value?.UserAgent; set => Ensure().UserAgent = value; }

    public static string EnsureCorrelationId() =>
        Ensure().CorrelationId ??= Guid.NewGuid().ToString("N");

    /// <summary>Scopes subsequent logs to a business entity: using var _ = CorrelationContext.BeginBusinessScope("Payment", id);</summary>
    public static IDisposable BeginBusinessScope(string entityType, string entityId)
    {
        var holder = Ensure();
        var previous = (holder.BusinessEntityType, holder.BusinessEntityId);
        holder.BusinessEntityType = entityType;
        holder.BusinessEntityId = entityId;
        return new Scope(() =>
        {
            holder.BusinessEntityType = previous.BusinessEntityType;
            holder.BusinessEntityId = previous.BusinessEntityId;
        });
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose();
        }
    }
}
