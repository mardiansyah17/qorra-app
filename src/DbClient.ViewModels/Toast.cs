using CommunityToolkit.Mvvm.ComponentModel;

namespace DbClient.ViewModels;

public enum ToastKind { Info, Success, Error }

public sealed partial class Toast(string title, string? detail, ToastKind kind) : ObservableObject
{
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public ToastKind Kind { get; } = kind;
    public string? ActionText { get; init; }
    public Action? Action { get; init; }
    public string? LinkText { get; init; }
    public string? LinkUrl { get; init; }

    [ObservableProperty] public partial bool IsClosing { get; set; }

    [ObservableProperty] public partial double? Progress { get; set; }
}
