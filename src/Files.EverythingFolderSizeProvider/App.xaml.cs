using System;
using System.Linq;
using Microsoft.UI.Xaml;

namespace Files.EverythingFolderSizeProvider;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var isBridge = Environment.GetCommandLineArgs().Any(x => string.Equals(x, "--bridge", StringComparison.OrdinalIgnoreCase)) ||
                       args.Arguments.Contains("--bridge", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (isBridge)
                await EverythingBridge.RunAsync();
        }
        catch
        {
        }
        finally
        {
            Exit();
        }
    }
}
