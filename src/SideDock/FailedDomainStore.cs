using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SideDock;

internal readonly record struct FailedDomainRecordResult(long FailureCount, bool IsNew);

internal sealed class FailedDomainStore
{
    private readonly object _syncRoot = new();
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Dictionary<string, long> _counts = new(StringComparer.OrdinalIgnoreCase);

    public FailedDomainStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
        Load();
    }

    public string Path => _path;

    public static bool IsConnectionFailure(string? errorText, bool canceled, string? blockedReason = null)
    {
        if (canceled || !string.IsNullOrWhiteSpace(blockedReason) || string.IsNullOrWhiteSpace(errorText))
        {
            return false;
        }

        var error = errorText.Trim().ToUpperInvariant();
        return error.Contains("NAME_NOT_RESOLVED", StringComparison.Ordinal)
            || error.Contains("DNS_", StringComparison.Ordinal)
            || error.Contains("CONNECTION_", StringComparison.Ordinal)
            || error.Contains("PROXY_", StringComparison.Ordinal)
            || error.Contains("TUNNEL_CONNECTION_FAILED", StringComparison.Ordinal)
            || error.Contains("TIMED_OUT", StringComparison.Ordinal)
            || error.Contains("ADDRESS_UNREACHABLE", StringComparison.Ordinal)
            || error.Contains("INTERNET_DISCONNECTED", StringComparison.Ordinal)
            || error.Contains("NETWORK_CHANGED", StringComparison.Ordinal)
            || error.Contains("SSL_", StringComparison.Ordinal)
            || error.Contains("CERT_", StringComparison.Ordinal)
            || error.Contains("QUIC_PROTOCOL_ERROR", StringComparison.Ordinal);
    }

    public static bool TryNormalizeEndpoint(string? url, out string endpoint)
    {
        endpoint = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Scheme)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0)
        {
            return false;
        }

        if (uri.HostNameType == UriHostNameType.IPv6)
        {
            host = $"[{host.Trim('[', ']')}]";
        }

        endpoint = $"{uri.Scheme.ToLowerInvariant()}://{host}";
        return true;
    }

    public FailedDomainRecordResult Record(string endpoint)
    {
        lock (_syncRoot)
        {
            var isNew = !_counts.TryGetValue(endpoint, out var current);
            var updated = current == long.MaxValue ? long.MaxValue : current + 1;
            _counts[endpoint] = updated;
            SaveLocked();
            return new FailedDomainRecordResult(updated, isNew);
        }
    }

    public bool EnsureFileExists()
    {
        lock (_syncRoot)
        {
            return SaveLocked();
        }
    }

    public bool Clear()
    {
        lock (_syncRoot)
        {
            _counts.Clear();
            return SaveLocked();
        }
    }

    internal IReadOnlyList<KeyValuePair<string, long>> Snapshot()
    {
        lock (_syncRoot)
        {
            return GetSortedEntries().ToArray();
        }
    }

    private void Load()
    {
        lock (_syncRoot)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return;
                }

                var foundLegacyEntry = false;
                foreach (var line in File.ReadLines(_path))
                {
                    var parts = line.Split('\t');
                    if (parts.Length == 2 && !parts[0].Contains("://", StringComparison.Ordinal))
                    {
                        foundLegacyEntry = true;
                        continue;
                    }

                    if (parts.Length != 2
                        || !TryNormalizeEndpoint(parts[0].Trim(), out var endpoint)
                        || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                        || count <= 0)
                    {
                        continue;
                    }

                    _counts.TryGetValue(endpoint, out var existing);
                    _counts[endpoint] = existing > long.MaxValue - count ? long.MaxValue : existing + count;
                }

                if (foundLegacyEntry)
                {
                    SaveLocked();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load failed domains. Path={FailedDomainsPath}", _path);
            }
        }
    }

    private IEnumerable<KeyValuePair<string, long>> GetSortedEntries()
    {
        return _counts
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
    }

    private bool SaveLocked()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            var lines = GetSortedEntries().Select(entry => $"{entry.Key}\t{entry.Value.ToString(CultureInfo.InvariantCulture)}");
            File.WriteAllLines(temporaryPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, _path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save failed domains. Path={FailedDomainsPath}", _path);
            return false;
        }
    }
}
