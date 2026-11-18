using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gem.Services;

/// <summary>
/// Ensures only a single running Gem instance per user session and forwards
/// command-line arguments (.torrent files and magnet: URIs) over a local named pipe.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private bool _ownsMutex;
    private CancellationTokenSource? _listenCts;
    private Task? _listenTask;
    private bool _disposed;

    public SingleInstanceService(string? scopeKey = null)
    {
        string rawScope = string.IsNullOrWhiteSpace(scopeKey)
            ? $"{Environment.UserDomainName}\\{Environment.UserName}"
            : scopeKey;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawScope));
        string suffix = Convert.ToHexString(hash[..8]);

        _mutexName = $@"Local\Gem.SingleInstance.{suffix}";
        _pipeName = $"Gem.Ipc.{suffix}";
    }

    public bool TryAcquirePrimary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_mutex is not null)
            return _ownsMutex;

        _mutex = new Mutex(initiallyOwned: true, name: _mutexName, createdNew: out bool createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
        }

        return createdNew;
    }

    public void StartListening(Action<string[]> onArgumentsReceived)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(onArgumentsReceived);

        if (_listenCts is not null)
            return;

        _listenCts = new CancellationTokenSource();
        CancellationToken ct = _listenCts.Token;
        _listenTask = Task.Run(() => ListenLoopAsync(onArgumentsReceived, ct), ct);
    }

    public bool NotifyPrimaryInstance(string[] args, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(3);
        DateTime deadlineUtc = DateTime.UtcNow + effectiveTimeout;
        string payload = JsonSerializer.Serialize(args ?? []);

        while (true)
        {
            try
            {
                int remainingMs = (int)Math.Clamp((deadlineUtc - DateTime.UtcNow).TotalMilliseconds, 50, effectiveTimeout.TotalMilliseconds);
                using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                client.Connect(remainingMs);

                using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };
                writer.WriteLine(payload);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadlineUtc)
                    return false;

                Thread.Sleep(60);
            }
            catch
            {
                return false;
            }
        }
    }

    private async Task ListenLoopAsync(Action<string[]> onArgumentsReceived, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                    continue;

                string[]? args = JsonSerializer.Deserialize<string[]>(line);
                onArgumentsReceived(args ?? []);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _listenCts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _listenTask?.Wait(300);
        }
        catch
        {
        }

        _listenCts?.Dispose();
        _listenCts = null;

        if (_mutex is not null)
        {
            if (_ownsMutex)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch
                {
                }
            }

            _mutex.Dispose();
            _mutex = null;
            _ownsMutex = false;
        }
    }
}
