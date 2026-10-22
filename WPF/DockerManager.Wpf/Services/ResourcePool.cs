namespace DockerManager.Wpf.Services;

/// <summary>Anything that can be cached and reused between operations.</summary>
public interface IPooledResource
{
    /// <summary>False once the resource is broken and must be discarded instead of reused.</summary>
    bool IsUsable { get; }
}

/// <summary>
/// Small async-safe cache of reusable resources keyed by connection identity.
/// Resources that report themselves unusable, or that were idle for too long, are disposed.
/// </summary>
public sealed class ResourcePool<T> where T : class, IPooledResource
{
    private readonly Dictionary<string, (T Resource, DateTime IdleSince)> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeSpan _idleLimit;

    public ResourcePool(TimeSpan idleLimit)
    {
        _idleLimit = idleLimit > TimeSpan.Zero ? idleLimit : TimeSpan.FromMinutes(5);
    }

    /// <summary>Returns a cached resource when possible, otherwise creates one with <paramref name="connect"/>.</summary>
    public async Task<T> RentAsync(string key, Func<CancellationToken, Task<T>> connect, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connect);
        T? cached = null;
        lock (_gate)
        {
            PruneLocked();
            if (_cache.TryGetValue(key, out var entry))
            {
                _cache.Remove(key);
                cached = entry.Resource;
            }
        }
        return cached ?? await connect(ct).ConfigureAwait(false);
    }

    /// <summary>Puts a healthy resource back; unusable resources are disposed and dropped.</summary>
    public void Return(string key, T resource)
    {
        if (!resource.IsUsable)
        {
            (resource as IDisposable)?.Dispose();
            return;
        }
        lock (_gate) { _cache[key] = (resource, DateTime.UtcNow); }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var entry in _cache.Values) (entry.Resource as IDisposable)?.Dispose();
            _cache.Clear();
        }
    }

    private void PruneLocked()
    {
        var now = DateTime.UtcNow;
        var expired = _cache.Where(kv => now - kv.Value.IdleSince > _idleLimit).Select(kv => kv.Key).ToList();
        foreach (var key in expired)
        {
            (_cache[key].Resource as IDisposable)?.Dispose();
            _cache.Remove(key);
        }
    }
}
