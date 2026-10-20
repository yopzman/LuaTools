using CommunityToolkit.Mvvm.ComponentModel;
using LuaToolsGui.Services.CloudSync;

namespace LuaToolsGui.ViewModels;

public sealed partial class CloudSyncAppItemViewModel : ObservableObject
{
    public long AppId { get; }
    public string Name { get; }
    public string AccountId { get; }
    public int FileCount { get; }
    public long TotalBytes { get; }
    public string? ChangeNumber { get; }
    public string? RootTokens { get; }
    public bool InLocalStorage { get; }
    public bool InRemoteStorage { get; }

    public string FormattedSize => FormatBytes(TotalBytes);

    [ObservableProperty]
    private bool _isBusy;

    public CloudSyncAppItemViewModel(CloudSyncApp app)
    {
        AppId = app.AppId;
        Name = app.Name;
        AccountId = app.AccountId;
        FileCount = app.FileCount;
        TotalBytes = app.TotalBytes;
        ChangeNumber = app.ChangeNumber;
        RootTokens = app.RootTokens;
        InLocalStorage = app.InLocalStorage;
        InRemoteStorage = app.InRemoteStorage;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}

public sealed partial class Steam760FileItemViewModel : ObservableObject
{
    public string RelativePath { get; }
    public string FullPath { get; }
    public long Size { get; }
    public string FormattedSize { get; }

    [ObservableProperty]
    private bool _isSelected = true;

    public Steam760FileItemViewModel(CloudSyncService.Steam760File file)
    {
        RelativePath = file.RelativePath;
        FullPath = file.FullPath;
        Size = file.Size;
        FormattedSize = file.Size < 1024 ? $"{file.Size} B" : $"{file.Size / 1024.0:F1} KB";
    }
}
