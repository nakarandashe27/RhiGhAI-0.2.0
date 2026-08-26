using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Rhino;
using Rhino.PlugIns;
using Rhino.UI;
using RhiGhAI.Rhino.Services;
using RhiGhAI.Rhino.UI;

namespace RhiGhAI.Rhino;

/// <summary>Rhino host entry point. Functional registration is added by roadmap vertical slices.</summary>
[Guid("5A25E338-4474-4A49-AF20-B6A11B9C8D9B")]
public sealed class RhiGhAIPlugIn : PlugIn
{
    private const string BridgeAssembly = "RhiGhAI.Grasshopper";

    static RhiGhAIPlugIn()
    {
        // The bridge assembly ships as RhiGhAI.Grasshopper.gha so Grasshopper can register the
        // component, but Rhino's plug-in loader only probes for .dll and fails the whole task with
        // "Could not load file or assembly 'RhiGhAI.Grasshopper'". Point it at the real file.
        AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(typeof(RhiGhAIPlugIn).Assembly) ?? AssemblyLoadContext.Default;
        context.Resolving += ResolveBridge;
    }

    public static RhiGhAIPlugIn? Instance { get; private set; }
    public RhiGhAIService Service { get; } = new();

    public RhiGhAIPlugIn()
    {
        Instance = this;
    }

    private static Assembly? ResolveBridge(AssemblyLoadContext context, AssemblyName name)
    {
        if (!string.Equals(name.Name, BridgeAssembly, StringComparison.Ordinal))
        {
            return null;
        }

        // Reuse the copy Grasshopper already loaded, so both hosts agree on one component type.
        Assembly? loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(item => string.Equals(item.GetName().Name, BridgeAssembly, StringComparison.Ordinal));
        if (loaded is not null)
        {
            return loaded;
        }

        string? directory = Path.GetDirectoryName(typeof(RhiGhAIPlugIn).Assembly.Location);
        if (directory is null)
        {
            return null;
        }

        string path = Path.Combine(directory, BridgeAssembly + ".gha");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        if (!OperatingSystem.IsWindows() || RhinoApp.Version.Major != 8 || RhinoApp.Version.Minor < 20)
        {
            errorMessage = "RhiGhAI 0.1.5 requires Rhino 8.20 or newer on Windows.";
            return LoadReturnCode.ErrorNoDialog;
        }

        Panels.RegisterPanel(this, typeof(RhiGhAIPanel), "RhiGhAI", null);
        return LoadReturnCode.Success;
    }

    protected override void OnShutdown()
    {
        Service.Shutdown();
        base.OnShutdown();
    }
}
