using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;

namespace QuotaBar.Core;

/// <summary>Owns upstream's host and Windows adapters. No account policy or credential store is reimplemented.</summary>
public sealed class CodexSwitcherService : IAsyncDisposable
{
    readonly string _hostPath, _dataPath;
    readonly string? _activeHome;
    readonly Func<string, string?, Task>? _platformHandler;
    readonly SemaphoreSlim _startGate = new(1);
    CoreClient? _client;
    AccountLease? _lease;
    EventWaitHandle? _activation;
    RegisteredWaitHandle? _activationListener;
    string? _error;
    bool _commandBusy, _disposed;
    public event Action? Changed;
    public event Action? ActivationRequested;
    public AccountSnapshot State => _client?.State ?? AccountSnapshot.Empty;
    public string? Error => _error ?? State.Error;
    public bool IsBusy => _commandBusy || State.IsMutating || State.IsAddingAccount;

    public CodexSwitcherService(string? hostPath = null, string? dataPath = null, string? activeHome = null,
        Func<string, string?, Task>? platformHandler = null)
    {
        _hostPath = hostPath ?? Path.Combine(AppContext.BaseDirectory, "switcher-runtime", "SwitcherHost.exe");
        _dataPath = Path.GetFullPath(dataPath ?? Environment.GetEnvironmentVariable("CODEX_SWITCHER_DATA_HOME") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex Account Switcher"));
        _activeHome = activeHome;
        _platformHandler = platformHandler;
    }

    public async Task InitializeAsync()
    {
        await _startGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client?.IsConnected == true) return;
            if (_client != null)
            {
                _client.Changed -= OnChanged;
                await _client.DisposeAsync(); _client = null;
                _activationListener?.Unregister(null); _activationListener = null;
                _activation?.Dispose(); _activation = null;
                _lease?.Dispose(); _lease = null;
            }
            if (!File.Exists(_hostPath)) throw new IOException("The account engine is missing. Prepare the Switcher runtime and rebuild Quota Bar.");
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_dataPath.ToUpperInvariant())));
            _lease = new AccountLease(@"Local\CodexAccountSwitcher-" + key);
            _activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexAccountSwitcher-Activate-" + key);
            _activationListener = ThreadPool.RegisterWaitForSingleObject(_activation,
                (_, _) => ActivationRequested?.Invoke(), null, Timeout.Infinite, false);
            _client = new CoreClient(_hostPath, _dataPath, _activeHome, _platformHandler);
            _client.Changed += OnChanged;
            await _client.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(40));
            _error = null;
            Changed?.Invoke();
        }
        catch
        {
            if (_client != null) { _client.Changed -= OnChanged; await _client.DisposeAsync(); _client = null; }
            _activationListener?.Unregister(null); _activationListener = null;
            _activation?.Dispose(); _activation = null;
            _lease?.Dispose(); _lease = null;
            throw;
        }
        finally { _startGate.Release(); }
    }

    void OnChanged() => Changed?.Invoke();

    public IReadOnlyList<ProviderSnapshot> Providers => _error is { } error
        ? new[] { ProviderSnapshot.Fail("codex", "codex", "Codex", ProviderStatus.Error, error) }
        : CodexSnapshotMapper.Map(State, DateTimeOffset.Now, ReadFetchTimes());

    Dictionary<Guid, DateTimeOffset> ReadFetchTimes()
    {
        var times = new Dictionary<Guid, DateTimeOffset>();
        try
        {
            var file = Path.Combine(_dataPath, "usage-cache.json");
            if (!File.Exists(file)) return times;
            using var cache = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var entry in cache.RootElement.GetProperty("entries").EnumerateArray())
                times[entry.GetProperty("profileID").GetGuid()] = entry.GetProperty("fetchedAt").GetDateTimeOffset();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or FormatException) { }
        return times;
    }

    public async Task RefreshAsync()
    {
        try { await InitializeAsync(); await _client!.CommandAsync("refresh"); _error = null; }
        catch (Exception e) { _error = e is IOException ? e.Message : "Codex accounts could not start. Close the standalone Account Switcher and retry."; Changed?.Invoke(); }
    }

    public async Task CommandAsync(string command, Guid? accountId = null, bool? value = null, string? language = null)
    {
        if (_commandBusy || State.IsMutating || (State.IsAddingAccount && command != "cancelAdd")) return;
        _commandBusy = true;
        Changed?.Invoke();
        try
        {
            await InitializeAsync();
            _error = null;
            await _client!.CommandAsync(command, accountId, value, language);
        }
        finally { _commandBusy = false; Changed?.Invoke(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _startGate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _activationListener?.Unregister(null); _activation?.Dispose();
            if (_client != null) { _client.Changed -= OnChanged; await _client.DisposeAsync(); _client = null; }
            _lease?.Dispose(); _lease = null;
        }
        finally { _startGate.Release(); }
    }

    // A named mutex must be released on its owning OS thread, regardless of async continuations.
    sealed class AccountLease : IDisposable
    {
        readonly ManualResetEventSlim _release = new(false);
        readonly Thread _thread;
        public AccountLease(string name)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() =>
            {
                try
                {
                    using var mutex = new Mutex(true, name, out var first);
                    if (!first) { ready.SetException(new IOException("Close Codex Account Switcher before using its accounts in Quota Bar.")); return; }
                    ready.SetResult();
                    _release.Wait();
                    mutex.ReleaseMutex();
                }
                catch (Exception e) { ready.TrySetException(e); }
            }) { IsBackground = true, Name = "Codex account ownership" };
            _thread.Start();
            try { ready.Task.GetAwaiter().GetResult(); }
            catch { _thread.Join(); _release.Dispose(); throw; }
        }
        public void Dispose() { _release.Set(); _thread.Join(); _release.Dispose(); }
    }
}
