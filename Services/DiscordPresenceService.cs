using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace MinecraftProjectLauncher.Services;

/// <summary>Windows Discord IPC client for the Rich Presence SET_ACTIVITY command.</summary>
public sealed class DiscordPresenceService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private string? _applicationId;

    public async Task SetActivityAsync(string applicationId, string details, string state, long? startedAt = null)
    {
        await _gate.WaitAsync();
        try
        {
            try
            {
                await EnsureConnectedAsync(applicationId);
                var activity = new Dictionary<string, object?>
                {
                    ["name"] = "SAPVP-AI Launcher",
                    ["type"] = 0,
                    ["details"] = Limit(details, 128),
                    ["state"] = Limit(state, 128),
                    ["assets"] = new Dictionary<string, string>
                    {
                        ["large_image"] = DiscordPresenceConfiguration.LargeImageKey,
                        ["large_text"] = "SAPVP-AI Launcher",
                        ["small_image"] = DiscordPresenceConfiguration.SmallImageKey,
                        ["small_text"] = "Minecraft Java"
                    }
                };
                if (startedAt is not null) activity["timestamps"] = new { start = startedAt.Value };
                await SendCommandAsync("SET_ACTIVITY", new { pid = Environment.ProcessId, activity });
            }
            catch
            {
                await DisconnectAsync();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task ClearActivityAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_pipe is not { IsConnected: true }) return;
            try { await SendCommandAsync("SET_ACTIVITY", new { pid = Environment.ProcessId, activity = (object?)null }); }
            finally { await DisconnectAsync(); }
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureConnectedAsync(string applicationId)
    {
        if (_pipe is { IsConnected: true } && string.Equals(_applicationId, applicationId, StringComparison.Ordinal)) return;
        if (_pipe is not null)
        {
            try { if (_pipe.IsConnected) await SendCommandAsync("SET_ACTIVITY", new { pid = Environment.ProcessId, activity = (object?)null }); }
            catch { }
            await DisconnectAsync();
        }

        Exception? lastError = null;
        for (var index = 0; index <= 9; index++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
                await pipe.ConnectAsync(timeout.Token);
                _pipe = pipe;
                _applicationId = applicationId;
                await WriteFrameAsync(pipe, 0, JsonSerializer.SerializeToUtf8Bytes(new { v = 1, client_id = applicationId }));
                var ready = await ReadFrameAsync(pipe, TimeSpan.FromSeconds(4));
                EnsureNoRpcError(ready.Payload);
                using var readyJson = JsonDocument.Parse(ready.Payload);
                if (!readyJson.RootElement.TryGetProperty("evt", out var evt) || evt.GetString() != "READY")
                    throw new IOException("O Discord não confirmou a conexão RPC.");
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException)
            {
                lastError = ex;
                await pipe.DisposeAsync();
                if (ReferenceEquals(_pipe, pipe)) { _pipe = null; _applicationId = null; }
            }
        }
        throw new IOException("Discord Desktop não está aberto ou não aceitou a conexão Rich Presence.", lastError);
    }

    private async Task SendCommandAsync(string command, object args)
    {
        if (_pipe is not { IsConnected: true } pipe) throw new IOException("A conexão com Discord foi encerrada.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { cmd = command, args, nonce = Guid.NewGuid().ToString("N") });
        await WriteFrameAsync(pipe, 1, payload);
        var response = await ReadFrameAsync(pipe, TimeSpan.FromSeconds(4));
        EnsureNoRpcError(response.Payload);
    }

    private static async Task WriteFrameAsync(Stream stream, int opcode, byte[] payload)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), payload.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static async Task<(int Opcode, byte[] Payload)> ReadFrameAsync(Stream stream, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var header = new byte[8];
        await ReadExactlyAsync(stream, header, cancellation.Token);
        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length is < 0 or > 1024 * 1024) throw new IOException("Resposta RPC do Discord inválida.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellation.Token);
        return (opcode, payload);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new EndOfStreamException("Discord encerrou a conexão RPC.");
            offset += read;
        }
    }

    private static void EnsureNoRpcError(byte[] payload)
    {
        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        if (root.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
        {
            var message = root.TryGetProperty("data", out var data) && data.TryGetProperty("message", out var text)
                ? text.GetString() : null;
            throw new IOException(message ?? "Discord retornou um erro de Rich Presence.");
        }
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

    private async Task DisconnectAsync()
    {
        if (_pipe is not null) await _pipe.DisposeAsync();
        _pipe = null;
        _applicationId = null;
    }

    public async ValueTask DisposeAsync()
    {
        await ClearActivityAsync();
        await _gate.WaitAsync();
        try { await DisconnectAsync(); }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
