namespace VAppCore;

/// <summary>
/// Async-local scope that disables <see cref="AuditLogInterceptor{TUserKey, TTenantKey}"/> writes
/// for the duration of a using block. Use during bulk imports / migrations / seeders where
/// audit rows would either flood the table or be misleading (the changes weren't user-driven).
/// </summary>
public static class AuditSuppression
{
    private static readonly AsyncLocal<int> _depth = new();

    public static bool IsSuppressed => _depth.Value > 0;

    public static IDisposable Suppress()
    {
        _depth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _depth.Value--;
        }
    }
}
