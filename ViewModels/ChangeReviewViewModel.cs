using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlStudio.ViewModels;

/// <summary>What the user sees before edits are saved: the exact T-SQL, and (on PROD servers) a deliberate "yes, change production".</summary>
public sealed partial class ChangeReviewViewModel : ViewModelBase
{
    readonly Func<bool, string> _script;

    public ChangeReviewViewModel(Func<bool, string> script, string summary, bool isProd, string serverName, string tableName)
    {
        _script = script;
        Summary = summary;
        IsProd = isProd;
        ServerName = serverName;
        TableName = tableName;
        Script = script(true);
    }

    public string Summary { get; }
    public bool IsProd { get; }
    public string ServerName { get; }
    public string TableName { get; }
    public string Heading => $"Save changes to {TableName}";
    public string ProdText => $"{ServerName} is marked PROD. These changes will be made to production data.";

    [ObservableProperty]
    public partial string Script { get; set; }

    /// <summary>Also require each changed cell to still hold the value it had when the result was loaded.</summary>
    [ObservableProperty]
    public partial bool CheckOldValues { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool Acknowledged { get; set; }

    public bool CanSave => !IsProd || Acknowledged;

    partial void OnCheckOldValuesChanged(bool value) => Script = _script(value);
}
