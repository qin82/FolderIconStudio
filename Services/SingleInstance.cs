using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace FolderIconStudio.Services;

/// <summary>
/// Keeps a single instance and forwards right-click requests to it through a named pipe.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\FolderIconStudio.Mutex";
    private const string PipeName = "FolderIconStudio.Pipe";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsPrimary = createdNew;
        if (createdNew) _ = ListenAsync(_cancellation.Token);
    }

    public bool IsPrimary { get; }
    public event Action<string>? FolderRequested;
    public event Action? OpenRequested;

    public static bool TrySend(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(message.ReplaceLineEndings(" "));
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token);

                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(token);
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("icon|", StringComparison.Ordinal))
                {
                    FolderRequested?.Invoke(line[5..]);
                }
                else if (line == "open|")
                {
                    OpenRequested?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                try
                {
                    await Task.Delay(150, CancellationToken.None);
                }
                catch (Exception)
                {
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _mutex.Dispose();
        _cancellation.Dispose();
    }
}
