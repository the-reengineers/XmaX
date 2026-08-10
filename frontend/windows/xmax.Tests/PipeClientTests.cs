using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using XmaX.Services;

namespace XmaX.Tests;

/// <summary>
/// Tests for PipeClient and IPC protocol format.
/// </summary>
public class PipeClientTests
{
    // ===== PipeClient lifecycle tests =====

    [Fact]
    public void IsConnected_Initially_ReturnsFalse()
    {
        using var client = new PipeClient();
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var client = new PipeClient();
        client.Dispose();
        client.Dispose(); // Double-dispose should not throw
    }

    [Fact]
    public async Task SendCommand_AfterDispose_ThrowsObjectDisposedException()
    {
        var client = new PipeClient();
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.SendCommandAsync("ping"));
    }

    [Fact]
    public void Disconnect_WhenNotConnected_DoesNotThrow()
    {
        using var client = new PipeClient();
        client.Disconnect();
    }

    // ===== Protocol format tests (no pipe I/O) =====

    [Fact]
    public void ProtocolMessage_Response_ParsesCorrectly()
    {
        var responseJson = """{"type":"response","id":"req_1","ok":true,"data":{"stapm":45}}""";
        var msg = JsonNode.Parse(responseJson)?.AsObject();

        Assert.NotNull(msg);
        Assert.Equal("response", msg["type"]?.GetValue<string>());
        Assert.Equal("req_1", msg["id"]?.GetValue<string>());
        Assert.True(msg["ok"]?.GetValue<bool>());
        Assert.Equal(45, msg["data"]?["stapm"]?.GetValue<int>());
    }

    [Fact]
    public void ProtocolMessage_Event_ParsesCorrectly()
    {
        var eventJson = """{"type":"event","event":"button_press","data":{"count":5}}""";
        var msg = JsonNode.Parse(eventJson)?.AsObject();

        Assert.NotNull(msg);
        Assert.Equal("event", msg["type"]?.GetValue<string>());
        Assert.Equal("button_press", msg["event"]?.GetValue<string>());
        Assert.Equal(5, msg["data"]?["count"]?.GetValue<int>());
    }

    [Fact]
    public void ProtocolMessage_Command_SerializesCorrectly()
    {
        var cmd = new JsonObject
        {
            ["type"] = "command",
            ["method"] = "get_metrics",
            ["id"] = "req_1"
        };

        var json = cmd.ToJsonString();
        var parsed = JsonNode.Parse(json)?.AsObject();

        Assert.NotNull(parsed);
        Assert.Equal("command", parsed["type"]?.GetValue<string>());
        Assert.Equal("get_metrics", parsed["method"]?.GetValue<string>());
        Assert.Equal("req_1", parsed["id"]?.GetValue<string>());
    }

    [Fact]
    public void ProtocolMessage_CommandWithPayload_SerializesCorrectly()
    {
        var cmd = new JsonObject
        {
            ["type"] = "command",
            ["method"] = "set_fan",
            ["id"] = "req_2"
        };
        cmd["mode"] = "auto";

        var json = cmd.ToJsonString();
        var parsed = JsonNode.Parse(json)?.AsObject();

        Assert.NotNull(parsed);
        Assert.Equal("command", parsed["type"]?.GetValue<string>());
        Assert.Equal("set_fan", parsed["method"]?.GetValue<string>());
        Assert.Equal("auto", parsed["mode"]?.GetValue<string>());
    }

    [Fact]
    public void ErrorResponse_ParsesCorrectly()
    {
        var errorJson = """{"type":"response","id":"req_5","ok":false,"error":"tdp_out_of_range"}""";
        var msg = JsonNode.Parse(errorJson)?.AsObject();

        Assert.NotNull(msg);
        Assert.False(msg["ok"]?.GetValue<bool>());
        Assert.Equal("tdp_out_of_range", msg["error"]?.GetValue<string>());
    }

    [Fact]
    public void ProtocolError_ParsesCorrectly()
    {
        var errorJson = """{"type":"error","error":"parse_error"}""";
        var msg = JsonNode.Parse(errorJson)?.AsObject();

        Assert.NotNull(msg);
        Assert.Equal("error", msg["type"]?.GetValue<string>());
        Assert.Equal("parse_error", msg["error"]?.GetValue<string>());
        Assert.Null(msg["id"]);
    }

    [Fact]
    public void RequestId_MonotonicIncrease()
    {
        var counter = 0;
        var ids = new HashSet<string>();

        for (int i = 0; i < 100; i++)
        {
            var id = $"req_{Interlocked.Increment(ref counter)}";
            Assert.True(ids.Add(id), $"Duplicate request ID: {id}");
        }

        Assert.Equal(100, ids.Count);
        Assert.Equal(100, counter);
    }
}
