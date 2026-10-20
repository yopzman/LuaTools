using System;
using System.Reflection;
using System.Threading;
using LuaToolsGui.Services;
using LuaToolsGui.Services.CloudSync;
using LuaToolsGui.ViewModels;
using LuaToolsGui.Views;
using Xunit;

namespace LuaToolsGui.Tests;

public class CloudSyncTests
{
    [Fact]
    public void TestSymbolRegularEnum()
    {
        bool cloudSyncExists = Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>("CloudSync24", out var symbol);
        Assert.True(cloudSyncExists, "SymbolRegular.CloudSync24 should exist in WPF-UI");
    }

    [Fact]
    public void TestVersionAttribute()
    {
        var info = typeof(MainViewModel).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        int plus = info?.IndexOf('+') ?? -1;
        var ver = plus >= 0 ? info![..plus] : info;
        Assert.Equal("1.3.2-dev", ver);
    }

    [Fact]
    public void TestReadConfig()
    {
        var cfg = CloudRedirectConfigHelper.ReadConfig();
        Assert.NotNull(cfg);
        Assert.NotNull(cfg.Provider);
    }

    [Fact]
    public void TestInstantiateCloudSyncViewOnSta()
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = System.Windows.Application.Current ?? new System.Windows.Application();
                app.Resources["BoolToVis"] = new System.Windows.Controls.BooleanToVisibilityConverter();
                app.Resources["InverseBoolToVis"] = new InverseBoolToVisibilityConverter();
                app.Resources["NullToCollapsed"] = new NullToCollapsedConverter();

                // Load resource dictionaries if needed
                var toast = new ToastService();
                var gh = new GithubProxy();
                var crService = new CloudRedirectService(gh);
                var cliService = new CloudSyncCliService(crService);
                var oauth = new CloudSyncOAuthService();
                var settings = new SettingsService();
                var steam = new SteamService(settings);
                var appList = new SteamAppListCache();
                var csService = new CloudSyncService(steam, appList, cliService, oauth);
                var vm = new CloudSyncViewModel(csService, toast);

                var view = new CloudSyncView(vm);
                Assert.NotNull(view);

                var win = new System.Windows.Window { Content = view };
                win.Show();

                // Call InitializeAsync as Loaded would do
                vm.InitializeAsync().GetAwaiter().GetResult();
                win.Close();
            }
            catch (Exception e)
            {
                ex = e;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (ex != null)
        {
            throw new Exception("CloudSyncView creation failed: " + ex.ToString(), ex);
        }
    }
}
