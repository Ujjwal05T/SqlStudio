using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlStudio.Services;

namespace SqlStudio.ViewModels;

/// <summary>One tab under the editor: either a result grid or the Messages pane.</summary>
public abstract partial class ResultPanelViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string Title { get; set; } = "";
}

public sealed record MessageLine(string Text, bool IsError);

public sealed class MessagesViewModel : ResultPanelViewModel
{
    const int MaxLines = 5000;

    public MessagesViewModel()
    {
        Title = "Messages";
        Lines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public ObservableCollection<MessageLine> Lines { get; } = [];

    public bool IsEmpty => Lines.Count == 0;

    public void Add(string text, bool isError)
    {
        if (Lines.Count == MaxLines)
            Lines.RemoveAt(0);
        Lines.Add(new MessageLine(text, isError));
    }
}
