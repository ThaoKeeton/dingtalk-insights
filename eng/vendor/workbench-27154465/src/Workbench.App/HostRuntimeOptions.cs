using Microsoft.UI.Xaml;
using Workbench.Core;
using Workbench.WinUI.Sdk;

namespace Workbench.App;

internal static class HostRuntimeOptions
{
    public static PluginManagerOptions Create(RunOptions options)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Plugins");
        if (options.SmokeTest)
        {
            // Curate a new root: never enumerate installed plugins or read normal user settings.
            var isolated = Path.Combine(options.UserRoot, "bundled");
            foreach (var id in RunOptions.SampleIds)
                CopySample(Path.Combine(bundled, id), Path.Combine(isolated, id));
            bundled = isolated;
        }
        var projectionNames = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Where(name => name.StartsWith("Microsoft.Windows.", StringComparison.Ordinal) && name.EndsWith(".Projection", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.WindowsAppRuntime.", StringComparison.Ordinal))
            .Append("Microsoft.InteractiveExperiences.Projection").Distinct().ToArray();
        return new()
        {
            PluginsRoot = Path.Combine(options.UserRoot, "Plugins"),
            UserRoot = options.UserRoot,
            AdditionalDiscoveryRoots = options.RuntimeProbe is not null ? [] : [bundled],
            SharedAssemblies = [typeof(WinUiView).Assembly, typeof(FrameworkElement).Assembly,
                typeof(WinRT.ComWrappersSupport).Assembly, typeof(Windows.Foundation.IAsyncAction).Assembly],
            SharedAssemblyNames = projectionNames
        };
    }

    private static void CopySample(string source, string destination)
    {
        HostRuntime.RejectLinks(source);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Bundled smoke package is missing: {source}");
        HostRuntime.RejectLinks(destination);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            HostRuntime.RejectLinks(file);
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        if (Directory.EnumerateDirectories(source).Any())
            throw new IOException("Bundled smoke samples must be flat code-only packages.");
    }
}
