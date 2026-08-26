using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.UI;
using RhiGhAI.Rhino.UI;

namespace RhiGhAI.Rhino.Commands;

[Guid("AF036B4B-FB19-4ED7-8208-FA8739F04BD0")]
public sealed class RhiGhAICommand : Command
{
    public override string EnglishName => "RhiGhAI";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        Panels.OpenPanel(typeof(RhiGhAIPanel).GUID);
        return Result.Success;
    }
}
