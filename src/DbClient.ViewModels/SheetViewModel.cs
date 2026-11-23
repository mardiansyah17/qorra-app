using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbClient.ViewModels;

public abstract partial class SheetViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _result = new();

    public Task<bool> Result => _result.Task;

    public virtual bool HasChanges => false;

    protected void Accept() => _result.TrySetResult(true);

    [RelayCommand]
    public void Cancel() => _result.TrySetResult(false);
}

public sealed partial class ConfirmViewModel(string title, string message, string acceptText) : SheetViewModel
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string AcceptText { get; } = acceptText;

    public string? AlternativeText { get; init; }

    public bool ChoseAlternative { get; private set; }

    public bool IsSafe { get; init; }

    public string? InputHint { get; init; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Input { get; set; } = "";

    public string? OptionText { get; init; }

    [ObservableProperty]
    public partial bool Option { get; set; }

    public string? RequiredInput { get; init; }

    public bool IsSecret { get; init; }

    public string? Code { get; init; }

    public bool AllowEmpty { get; init; }

    private bool CanConfirm => InputHint is null || AllowEmpty
        || (RequiredInput is null ? Input.Trim().Length > 0 : Input.Trim() == RequiredInput);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => Accept();

    [RelayCommand]
    private void Alternative()
    {
        ChoseAlternative = true;
        Accept();
    }
}
