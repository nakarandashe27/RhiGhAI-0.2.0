namespace RhiGhAI.HostTests;

using Xunit;

public sealed class HostTestContract
{
    [Fact(Skip = "Runs only inside the Rhino host integration harness.")]
    public void RhinoHostIsAvailable()
    {
    }
}
