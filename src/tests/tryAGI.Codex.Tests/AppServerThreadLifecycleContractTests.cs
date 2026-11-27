using System.Text.Json;

namespace tryAGI.Codex.Tests;

public sealed class AppServerThreadLifecycleContractTests
{
    [Fact]
    public void LifecycleRequestsUseExactWireNamesAndRetainExtensionData()
    {
        var archive = CodexAppServerMethods.ThreadArchive;
        var unarchive = CodexAppServerMethods.ThreadUnarchive;
        var rename = CodexAppServerMethods.ThreadSetName;
        Assert.Equal("thread/archive", archive.Name);
        Assert.Equal("thread/unarchive", unarchive.Name);
        Assert.Equal("thread/name/set", rename.Name);
        Assert.Contains(archive.Name, CodexAppServerProtocol.ClientRequests);
        Assert.Contains(unarchive.Name, CodexAppServerProtocol.ClientRequests);
        Assert.Contains(rename.Name, CodexAppServerProtocol.ClientRequests);
        var raw = JsonSerializer.Deserialize(
            """{"threadId":"thread-1","future":{"version":2}}""", archive.ParamsType);
        Assert.NotNull(raw);
        Assert.Equal("thread-1", raw.ThreadId);
        var encoded = JsonSerializer.SerializeToElement(raw, unarchive.ParamsType);
        Assert.Equal(2, encoded.GetProperty("future").GetProperty("version").GetInt32());
        var name = JsonSerializer.SerializeToElement(new CodexAppServerThreadSetNameParams("thread-1", "New name"), rename.ParamsType);
        Assert.Equal("thread-1", name.GetProperty("threadId").GetString());
        Assert.Equal("New name", name.GetProperty("name").GetString());
        Assert.False(name.TryGetProperty("ThreadId", out _));
    }

    [Fact]
    public void LifecycleNotificationsPreserveUnknownFieldsAndRejectOtherMethods()
    {
        using var raw = JsonDocument.Parse("""{"threadId":"thread-1","future":{"kept":true}}""");
        var methods = new[] { CodexAppServerNotifications.ThreadArchived,
            CodexAppServerNotifications.ThreadUnarchived, CodexAppServerNotifications.ThreadClosed };
        foreach (var method in methods)
        {
            Assert.Contains(method.Method, CodexAppServerProtocol.ServerNotifications);
            Assert.False(method.TryRead(new CodexAppServerNotification("other", raw.RootElement), out _));
            Assert.True(method.TryRead(new CodexAppServerNotification(method.Method, raw.RootElement), out var value));
            Assert.NotNull(value);
            Assert.Equal("thread-1", value.ThreadId);
            var encoded = JsonSerializer.SerializeToElement(value, method.PayloadType);
            Assert.True(encoded.GetProperty("future").GetProperty("kept").GetBoolean());
        }
        var name = CodexAppServerNotifications.ThreadNameUpdated;
        Assert.Contains(name.Method, CodexAppServerProtocol.ServerNotifications);
        Assert.True(name.TryRead(new CodexAppServerNotification(name.Method, raw.RootElement), out var named));
        Assert.NotNull(named);
        Assert.Null(named.ThreadName);
        var serialized = JsonSerializer.SerializeToElement(named, name.PayloadType);
        Assert.False(serialized.TryGetProperty("threadName", out _));
        Assert.True(serialized.GetProperty("future").GetProperty("kept").GetBoolean());
    }
}
