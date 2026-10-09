using System.IO.Pipes;
using System.Text;

namespace Contexo.Desktop.Platform.Common;

/// <summary>
/// One running Contexo per user. A named <see cref="Mutex"/> decides who is first; a later instance asks the first one to show
/// its window through a named pipe (works on Windows and macOS; a named EventWaitHandle does not exist on macOS) and then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string ShowCommand = "show";

    private readonly string _pipeName;
    private readonly Mutex? _mutex;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listener;

    private SingleInstance(string name, Mutex? mutex)
    {
        _pipeName = name + ".activate";
        _mutex = mutex;
    }

    /// <summary>True for the first instance; false when another one already runs.</summary>
    public bool IsPrimary => _mutex is not null;

    /// <summary>Raised on a background thread when a second instance asked to show the window.</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>Default name; includes the user name so two users on one machine can each run Contexo.</summary>
    public static string DefaultName => $"Contexo.SingleInstance.{Environment.UserName}";

    public static SingleInstance Acquire(string? name = null)
    {
        name ??= DefaultName;
        try
        {
            // On Unix a plain name is private to the login session; "Global\" makes it machine wide (the name already contains the user).
            var mutexName = OperatingSystem.IsWindows() ? name : "Global\\" + name;
            var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
            if (createdNew)
            {
                return new SingleInstance(name, mutex);
            }

            mutex.Dispose();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Cannot tell; treat as "another instance exists" so we never start two writers on the database.
        }

        return new SingleInstance(name, null);
    }

    /// <summary>First instance only: starts listening for activation requests.</summary>
    public void StartListening()
    {
        if (!IsPrimary || _listener is not null)
        {
            return;
        }

        _listener = Task.Run(() => ListenAsync(_cts.Token));
    }

    /// <summary>Later instance: asks the first instance to show its window. Returns false when it could not be reached.</summary>
    public async Task<bool> NotifyPrimaryAsync(TimeSpan timeout)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(timeout);
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes(ShowCommand + "\n");
            await client.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
            await client.FlushAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == ShowCommand)
                {
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Client went away mid-message; wait for the next one.
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Listener was cancelled.
        }

        _cts.Dispose();
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released from a different thread than the one that acquired it; closing the handle is enough.
            }

            _mutex.Dispose();
        }
    }
}
