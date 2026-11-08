using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

if (__rhino_doc__.Objects.Any(o => !o.IsDeleted) ||
    !new[] { "RhiGhAI-acceptance.3dm", "RhiGhAI-ui-retry.3dm" }.Contains(System.IO.Path.GetFileName(__rhino_doc__.Path)))
    throw new Exception("Acceptance requires an empty, separate document saved as RhiGhAI-acceptance.3dm. Do not run in a working model.");
var core = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "RhiGhAI.Core");
var host = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "RhiGhAI.Rhino");
if (host.GetName().Version.ToString() != "0.2.2.0") throw new Exception("Expected loaded RhiGhAI 0.2.2.");
var reportPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(__rhino_doc__.Path), "RHINO_ACCEPTANCE.txt");
var planJson = core.GetType("RhiGhAI.Core.Contracts.TaskPlanJson");
var compiler = core.GetType("RhiGhAI.Core.Graph.TaskPlanCompiler");
var targetHostType = core.GetType("RhiGhAI.Core.Contracts.TargetHost");
var validationType = core.GetType("RhiGhAI.Core.Graph.ValidationContext");
var snapshotBuilder = host.GetType("RhiGhAI.Rhino.Execution.RhinoSnapshotBuilder");
var executor = host.GetType("RhiGhAI.Rhino.Execution.RhinoPlanExecutor");
object rhinoHost = Enum.Parse(targetHostType, "Rhino");

Func<string, object, object> compile = (json, snapshot) => {
    var plan = planJson.GetMethod("Parse").Invoke(null, new object[] { json });
    var allowed = snapshot.GetType().GetProperty("AllowedReferences").GetValue(snapshot);
    var validation = Activator.CreateInstance(validationType, allowed);
    try {
        return compiler.GetMethod("Compile").Invoke(null, new[] { plan, validation });
    } catch (TargetInvocationException error) {
        throw new Exception(error.InnerException == null ? error.ToString() : (error.InnerException.GetType().GetProperty("Code")?.GetValue(error.InnerException) + ": " + error.InnerException.ToString()));
    }
};
Func<object> snapshot = () => snapshotBuilder.GetMethod("Capture").Invoke(null, new object[] { __rhino_doc__ });
Func<object, object, object> execute = (snap, graph) => {
    try {
        return executor.GetMethod("Execute").Invoke(null, new object[] { __rhino_doc__, snap, graph, CancellationToken.None });
    } catch (TargetInvocationException error) {
        throw new Exception(error.InnerException == null ? error.ToString() : (error.InnerException.GetType().GetProperty("Code")?.GetValue(error.InnerException) + ": " + error.InnerException.ToString()));
    }
};

int phase = 0;
EventHandler handler = null;
System.IO.File.WriteAllText(reportPath, "PENDING");
handler = (sender, args) => {
if (__rhino_doc__.IsCommandRunning) return;
// RhinoCode's test command may leave its automatic record open. End only that test
// record in this explicitly isolated test document before exercising our own transaction.
if (__rhino_doc__.UndoRecordingIsActive)
    __rhino_doc__.EndUndoRecord(__rhino_doc__.CurrentUndoRecordSerialNumber);
try {
if (phase == 0) {
var boxPlan = @"{""schemaVersion"":1,""targetHost"":""rhino"",""summary"":""Panel"",""assumptions"":[],""operations"":[{""kind"":""createBox"",""id"":""panel"",""origin"":{""x"":0,""y"":0,""z"":0},""size"":{""x"":2400,""y"":1200,""z"":18},""layer"":""Panels""}]}";
var before = snapshot();
var boxResult = execute(before, compile(boxPlan, before));
var ids = ((System.Collections.IEnumerable)boxResult.GetType().GetProperty("CreatedOrChangedIds").GetValue(boxResult)).Cast<Guid>().ToArray();
if (ids.Length != 1) throw new Exception("Box acceptance created unexpected object count.");
var panel = __rhino_doc__.Objects.FindId(ids[0]);
if (panel == null) throw new Exception("Panel missing after execute.");
var bounds = panel.Geometry.GetBoundingBox(true);
if (Math.Abs(bounds.Diagonal.X - 2400) > 1e-8 || Math.Abs(bounds.Diagonal.Y - 1200) > 1e-8 || Math.Abs(bounds.Diagonal.Z - 18) > 1e-8) throw new Exception("Panel dimensions mismatch.");
if (__rhino_doc__.Layers[panel.Attributes.LayerIndex].Name != "Panels") throw new Exception("Panel layer mismatch.");
if (!__rhino_doc__.Undo()) throw new Exception("Panel Undo failed.");
if (__rhino_doc__.Objects.FindId(ids[0]) != null) throw new Exception("Panel remained after one Undo.");

phase = 1;
return;
}
if (phase == 1) {
var seedUndo = __rhino_doc__.BeginUndoRecord("acceptance seed");
var seedId = __rhino_doc__.Objects.AddBrep(Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(10, 10, 10))));
__rhino_doc__.EndUndoRecord(seedUndo);
var seed = __rhino_doc__.Objects.FindId(seedId);
if (seed == null) throw new Exception("Seed missing.");
seed.Select(true);
var moveSnapshot = snapshot();
var movePlan = string.Format(@"{{""schemaVersion"":1,""targetHost"":""rhino"",""summary"":""Move selected"",""assumptions"":[],""operations"":[{{""kind"":""transform"",""id"":""move"",""references"":[""selection:{0}""],""translation"":{{""x"":0,""y"":0,""z"":500}},""layer"":""Raised""}}]}}", seedId.ToString("D"));
execute(moveSnapshot, compile(movePlan, moveSnapshot));
var moved = __rhino_doc__.Objects.FindId(seedId);
if (moved == null) throw new Exception("Moved object missing.");
if (Math.Abs(moved.Geometry.GetBoundingBox(true).Min.Z - 500) > 1e-8) throw new Exception("Move distance mismatch.");
if (__rhino_doc__.Layers[moved.Attributes.LayerIndex].Name != "Raised") throw new Exception("Move layer mismatch.");
if (!__rhino_doc__.Undo()) throw new Exception("Move Undo failed.");
var restored = __rhino_doc__.Objects.FindId(seedId);
if (restored == null) throw new Exception("Object missing after move Undo.");
if (Math.Abs(restored.Geometry.GetBoundingBox(true).Min.Z) > 1e-8) throw new Exception("Move Undo did not restore geometry.");
__rhino_doc__.Objects.Delete(seedId, true);
phase = 2;
return;
}
// Reject after a layer is created but before object addition. Undo must remove that layer,
// and leave a previous unrelated user action untouched.
var sentinelUndo = __rhino_doc__.BeginUndoRecord("acceptance sentinel");
var sentinelId = __rhino_doc__.Objects.AddPoint(99, 99, 99);
__rhino_doc__.EndUndoRecord(sentinelUndo);
var failingSnapshot = snapshot();
var failingPlan = string.Format(@"{{""schemaVersion"":1,""targetHost"":""rhino"",""summary"":""Fail after layer"",""assumptions"":[],""operations"":[{{""kind"":""ensureLayer"",""id"":""a"",""name"":""RollbackProbe""}},{{""kind"":""transform"",""id"":""z"",""references"":[""selection:{0}""],""translation"":{{""x"":0,""y"":0,""z"":1}},""layer"":null}}]}}", Guid.Empty.ToString("D"));
var invalidPlan = planJson.GetMethod("Parse").Invoke(null, new object[] { failingPlan });
var allowedForFailure = new HashSet<string> { "selection:" + Guid.Empty.ToString("D") };
var failureContext = Activator.CreateInstance(validationType, allowedForFailure);
var failingGraph = compiler.GetMethod("Compile").Invoke(null, new[] { invalidPlan, failureContext });
try { execute(failingSnapshot, failingGraph); throw new Exception("Expected transform rejection."); }
catch (Exception error) { if (!error.Message.Contains("UnsupportedReference")) throw; }
if (__rhino_doc__.Layers.FindName("RollbackProbe") != null) throw new Exception("Failed attempt left a layer.");
if (__rhino_doc__.Objects.FindId(sentinelId) == null) throw new Exception("Rollback undid an unrelated user action.");
__rhino_doc__.Objects.Delete(sentinelId, true);
phase = 3;
System.IO.File.WriteAllText(reportPath, "RHINO_ACCEPTANCE_OK panel+singleUndo selectedMove+singleUndo layerRollback+unrelatedUndoPreserved\nLoaded: " + host.Location);
} catch (Exception error) {
    phase = 3;
    System.IO.File.WriteAllText(reportPath, "FAILED: " + error.ToString());
}
};
System.Threading.Tasks.Task.Run(async () => {
    for (int tick = 0; tick < 30 && phase < 3; tick++) {
        await System.Threading.Tasks.Task.Delay(500);
        RhinoApp.InvokeOnUiThread((Action)(() => handler(null, EventArgs.Empty)));
    }
});
Console.WriteLine("RHINO_ACCEPTANCE_SCHEDULED");
