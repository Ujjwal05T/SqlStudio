using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlStudio.Models;
using SqlStudio.Services;

namespace SqlStudio.ViewModels;

/// <summary>Shared plumbing for dialogs that run a T-SQL script on a server and report progress (backup, restore).</summary>
public abstract partial class ServerJobViewModel : ViewModelBase, IQuerySink
{
    static readonly Regex Percent = new(@"(\d+) percent processed", RegexOptions.Compiled);
    CancellationTokenSource? _cts;

    protected ServerJobViewModel(ConnectionProfile profile, ICredentialStore credentials)
    {
        Profile = profile;
        Credentials = credentials;
    }

    public ConnectionProfile Profile { get; }

    /// <summary>Set by the dialog: asks the user to confirm a destructive step. Returns true to go ahead.</summary>
    public Func<string, string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Buttons name the action ("Replace database" / "Cancel"), not "OK".</summary>
    protected async Task<bool> ConfirmedAsync(string title, string message, string confirmLabel, string cancelLabel = "Cancel") =>
        Confirm is null || await Confirm(title, message, confirmLabel, cancelLabel);
    protected ICredentialStore Credentials { get; }
    public ObservableCollection<MessageLine> Log { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool LastRunFailed { get; set; }

    public bool IsIdle => !IsRunning;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnRunningChanged();
    }

    /// <summary>Lets a dialog refresh its own commands when a run starts or ends.</summary>
    protected virtual void OnRunningChanged()
    {
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    void Cancel() => _cts?.Cancel();

    /// <summary>Runs the script on its own connection to master. Returns true when it finished without errors.</summary>
    protected async Task<bool> RunScriptAsync(string script, string doneText)
    {
        IsRunning = true;
        LastRunFailed = false;
        Progress = 0;
        Log.Clear();
        StatusText = "Running…";
        _cts = new CancellationTokenSource();
        try
        {
            await using var connection = await ServerSession.OpenAsync(Profile, Credentials, "master", _cts.Token);
            var token = _cts.Token;
            var summary = await Task.Run(() => QueryExecutor.ExecuteAsync(connection, script, this, 1000, token));

            var ok = summary is { Errors: 0, Cancelled: false };
            LastRunFailed = !ok;
            if (ok)
                Progress = 100;
            StatusText = ok
                ? $"{doneText} in {summary.Elapsed:hh\\:mm\\:ss}"
                : summary.Cancelled ? "Cancelled" : "Failed. See the log for details.";
            return ok;
        }
        catch (Exception ex)
        {
            LastRunFailed = true;
            StatusText = "Failed. See the log for details.";
            Log.Add(new MessageLine(ex.Message, true));
            return false;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    /// <summary>A follow-up step after a script (e.g. downloading the file). Keeps the log and honours Stop.</summary>
    protected async Task<bool> RunStepAsync(Func<CancellationToken, Task<string>> work, string runningText)
    {
        IsRunning = true;
        LastRunFailed = false;
        Progress = 0;
        StatusText = runningText;
        _cts = new CancellationTokenSource();
        try
        {
            StatusText = await Task.Run(() => work(_cts.Token));
            Progress = 100;
            return true;
        }
        catch (OperationCanceledException)
        {
            LastRunFailed = true;
            StatusText = "Cancelled";
            return false;
        }
        catch (Exception ex)
        {
            LastRunFailed = true;
            StatusText = "Failed. See the log for details.";
            Log.Add(new MessageLine(ex.Message, true));
            return false;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    /// <summary>Adds a line to the log from any thread.</summary>
    protected void AddLog(string text, bool isError = false) =>
        Dispatcher.UIThread.Post(() => Log.Add(new MessageLine(text, isError)));

    protected void SetProgress(double value) => Dispatcher.UIThread.Post(() => Progress = value);

    /// <summary>Runs one connection-level command on a fresh connection, for quick lookups and cleanup.</summary>
    protected async Task<T> WithConnectionAsync<T>(Func<Microsoft.Data.SqlClient.SqlConnection, Task<T>> work)
    {
        await using var connection = await ServerSession.OpenAsync(Profile, Credentials, "master");
        return await work(connection);
    }

    void IQuerySink.Message(string text, bool isError) => Dispatcher.UIThread.Invoke(() =>
    {
        if (Percent.Match(text) is { Success: true } m)
        {
            Progress = double.Parse(m.Groups[1].Value);
            return; // progress shows in the bar, not as 20 log lines
        }
        Log.Add(new MessageLine(text, isError));
    });

    void IQuerySink.ResultSetReady(ResultSet set)
    {
    }
}
