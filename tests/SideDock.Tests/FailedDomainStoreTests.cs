using Microsoft.Extensions.Logging.Abstractions;

namespace SideDock.Tests;

public sealed class FailedDomainStoreTests
{
    [Theory]
    [InlineData("https://API.Example.COM:8443/path", "https://api.example.com")]
    [InlineData("https://example.com./resource", "https://example.com")]
    [InlineData("http://BÜCHER.example/", "http://xn--bcher-kva.example")]
    [InlineData("ws://Socket.Example.COM:8080/events", "ws://socket.example.com")]
    [InlineData("WSS://socket.example.com/events", "wss://socket.example.com")]
    [InlineData("custom+tcp://Service.Example.COM:9000/path", "custom+tcp://service.example.com")]
    [InlineData("tcp://[2001:db8::1]:9000/path", "tcp://[2001:db8::1]")]
    public void TryNormalizeEndpointReturnsCanonicalProtocolAndHost(string url, string expected)
    {
        Assert.True(FailedDomainStore.TryNormalizeEndpoint(url, out var endpoint));
        Assert.Equal(expected, endpoint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("about:blank")]
    [InlineData("data:text/plain,test")]
    [InlineData("file:///c:/temp/test.html")]
    public void TryNormalizeEndpointRejectsUrlsWithoutHosts(string? url)
    {
        Assert.False(FailedDomainStore.TryNormalizeEndpoint(url, out _));
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
    public void RecordsPersistMergeAndSortProtocolQualifiedCounts()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "failed-domains.txt");
        File.WriteAllText(
            path,
            "bad row\nhttps://api.example.com\t2\nws://cdn.example.com\t3\nHTTPS://API.example.com\t4\nhttps://zero.example\t0\n");

        var store = new FailedDomainStore(path, NullLogger.Instance);
        store.Record("ws://cdn.example.com");
        store.Record("ws://cdn.example.com");
        store.Record("ws://cdn.example.com");
        Assert.Equal(7, store.Record("ws://cdn.example.com").FailureCount);
        Assert.Equal(1, store.Record("https://cdn.example.com").FailureCount);

        Assert.Equal(
            ["ws://cdn.example.com\t7", "https://api.example.com\t6", "https://cdn.example.com\t1"],
            File.ReadAllLines(path));

        var reloaded = new FailedDomainStore(path, NullLogger.Instance);
        Assert.Equal(7, reloaded.Record("https://api.example.com").FailureCount);
        Assert.Equal(
            ["https://api.example.com\t7", "ws://cdn.example.com\t7", "https://cdn.example.com\t1"],
            File.ReadAllLines(path));
    }

    [Fact]
    public void RecordReportsOnlyNewProtocolQualifiedEntries()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "failed-domains.txt");
        var store = new FailedDomainStore(path, NullLogger.Instance);

        Assert.Equal(
            new FailedDomainRecordResult(1, IsNew: true),
            store.Record("https://example.com"));
        Assert.Equal(
            new FailedDomainRecordResult(2, IsNew: false),
            store.Record("https://example.com"));

        foreach (var endpoint in new[]
                 {
                     "http://example.com",
                     "ws://example.com",
                     "wss://example.com"
                 })
        {
            Assert.True(store.Record(endpoint).IsNew);
        }

        var reloaded = new FailedDomainStore(path, NullLogger.Instance);
        Assert.Equal(
            new FailedDomainRecordResult(3, IsNew: false),
            reloaded.Record("https://example.com"));

        Assert.True(reloaded.Clear());
        Assert.Equal(
            new FailedDomainRecordResult(1, IsNew: true),
            reloaded.Record("https://example.com"));
    }

    [Fact]
    public void LoadingClearsLegacyEntriesAndKeepsNewFormatEntries()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "failed-domains.txt");
        File.WriteAllText(
            path,
            "legacy.example.com\t5\nhttps://keep.example.com\t2\nWS://socket.example.com\t3\ninvalid row\n");

        var store = new FailedDomainStore(path, NullLogger.Instance);

        Assert.Equal(
            ["ws://socket.example.com\t3", "https://keep.example.com\t2"],
            File.ReadAllLines(path));
        Assert.Collection(
            store.Snapshot(),
            entry => Assert.Equal(new KeyValuePair<string, long>("ws://socket.example.com", 3), entry),
            entry => Assert.Equal(new KeyValuePair<string, long>("https://keep.example.com", 2), entry));
    }

    [Fact]
    public void ClearRemovesMemoryAndFileEntries()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "failed-domains.txt");
        var store = new FailedDomainStore(path, NullLogger.Instance);
        store.Record("https://example.com");

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
