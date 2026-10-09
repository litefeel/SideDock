using SideDock;
using System.Text.Json;

namespace SideDock.Tests;

public sealed class ExternalLinkNavigationTests
{
    [Theory]
    [InlineData("https://developer.android.com/tools/releases/platform-tools", "https://developer.android.com/tools/releases/platform-tools")]
    [InlineData("HTTP://Example.COM/path?q=1#section", "http://example.com/path?q=1#section")]
    public void NewWindowAcceptsHttpUrlsWithoutChangingTheirDestination(string href, string expected)
    {
        Assert.True(MainWindow.TryGetExternalHttpUrl(href, out var url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/relative-path")]
    [InlineData("about:blank")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,test")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("custom:launch")]
    public void NewWindowRejectsInvalidOrNonHttpUrls(string? href)
    {
        Assert.False(MainWindow.TryGetExternalHttpUrl(href, out var url));
        Assert.Equal(string.Empty, url);
    }

    [Fact]
    public void InterceptedLinkMessageAcceptsTheCitationDestination()
    {
        const string href = "https://developer.android.com/tools/releases/platform-tools";
        var message = JsonSerializer.Serialize(new { type = "sideDock.openExternalBlankLink", href });

        Assert.True(MainWindow.TryGetExternalBlankLinkUrl(message, out var url));
        Assert.Equal(href, url);
    }

    [Theory]
    [InlineData("invalid JSON")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"other\",\"href\":\"https://example.com\"}")]
    [InlineData("{\"type\":\"sideDock.openExternalBlankLink\",\"href\":7}")]
    [InlineData("{\"type\":\"sideDock.openExternalBlankLink\",\"href\":\"javascript:alert(1)\"}")]
    public void InterceptedLinkMessageRejectsUnrelatedOrInvalidRequests(string message)
    {
        Assert.False(MainWindow.TryGetExternalBlankLinkUrl(message, out var url));
        Assert.Equal(string.Empty, url);
    }
}
