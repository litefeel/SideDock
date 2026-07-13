using Microsoft.Extensions.Logging.Abstractions;

namespace SideDock.Tests;

public sealed class FailedDomainStoreTests
{
    [Theory]
    [InlineData("https://API.Example.COM:8443/path", "api.example.com")]
    [InlineData("https://example.com./resource", "example.com")]
    [InlineData("http://xn--bcher-kva.example/", "xn--bcher-kva.example")]
    public void TryNormalizeHostReturnsCanonicalHttpHost(string url, string expected)
    {
        Assert.True(FailedDomainStore.TryNormalizeHost(url, out var host));
        Assert.Equal(expected, host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("about:blank")]
    [InlineData("data:text/plain,test")]
    [InlineData("file:///c:/temp/test.html")]
    public void TryNormalizeHostRejectsUnsupportedUrls(string? url)
    {
        Assert.False(FailedDomainStore.TryNormalizeHost(url, out _));
    }

    [Theory]
    [InlineData("net::ERR_NAME_NOT_RESOLVED")]
    [InlineData("net::ERR_CONNECTION_REFUSED")]
    [InlineData("net::ERR_PROXY_CONNECTION_FAILED")]
    [InlineData("net::ERR_TUNNEL_CONNECTION_FAILED")]
    [InlineData("net::ERR_TIMED_OUT")]
    [InlineData("net::ERR_CERT_AUTHORITY_INVALID")]
    [InlineData("net::ERR_SSL_PROTOCOL_ERROR")]
    public void IsConnectionFailureAcceptsProxyRelevantErrors(string error)
    {
        Assert.True(FailedDomainStore.IsConnectionFailure(error, canceled: false));
    }

    [Theory]
    [InlineData("net::ERR_ABORTED", false, null)]
    [InlineData("net::ERR_BLOCKED_BY_CLIENT", false, null)]
    [InlineData("net::ERR_CONNECTION_RESET", true, null)]
    [InlineData("net::ERR_CONNECTION_RESET", false, "inspector")]
    public void IsConnectionFailureRejectsCanceledOrBlockedErrors(string error, bool canceled, string? blockedReason)
    {
        Assert.False(FailedDomainStore.IsConnectionFailure(error, canceled, blockedReason));
    }

    [Fact]
    public void RecordsPersistsMergesAndSortsCounts()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "failed-domains.txt");
        File.WriteAllText(path, "bad row\napi.example.com\t2\ncdn.example.com\t3\nAPI.example.com\t4\nzero.example\t0\n");

        var store = new FailedDomainStore(path, NullLogger.Instance);
        store.Record("cdn.example.com");
        store.Record("cdn.example.com");
        store.Record("cdn.example.com");
        Assert.Equal(7, store.Record("cdn.example.com"));

        Assert.Equal(
            ["cdn.example.com\t7", "api.example.com\t6"],
            File.ReadAllLines(path));

        var reloaded = new FailedDomainStore(path, NullLogger.Instance);
        Assert.Equal(7, reloaded.Record("api.example.com"));
        Assert.Equal(
            ["api.example.com\t7", "cdn.example.com\t7"],
            File.ReadAllLines(path));
    }

    [Fact]
    public void ClearRemovesMemoryAndFileEntries()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "failed-domains.txt");
        var store = new FailedDomainStore(path, NullLogger.Instance);
        store.Record("example.com");

        store.Clear();

        Assert.Empty(store.Snapshot());
        Assert.Empty(File.ReadAllLines(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"SideDock.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
