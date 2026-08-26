using System.Collections.Immutable;
using RhiGhAI.Core.Contracts;

namespace RhiGhAI.Core.Graph;

public sealed record OperationNode(
    string NodeId,
    string OperationKind,
    int SchemaVersion,
    TargetHost TargetHost,
    TaskOperation TypedOperation,
    ImmutableArray<string> Dependencies,
    string ExpectedResultType);

public sealed record OperationGraph(
    int SchemaVersion,
    TargetHost TargetHost,
    ImmutableArray<OperationNode> Nodes,
    string GraphHash,
    string Summary);

/// <summary>
/// What the compiler is allowed to trust from outside the plan. The host is not part of it: the
/// schema pins targetHost to "rhino" and <see cref="TaskPlanCompiler"/> rejects anything else, so
/// carrying an expected host here only produced a second copy of the same check.
/// </summary>
public sealed record ValidationContext(IReadOnlySet<string> AllowedReferences);
