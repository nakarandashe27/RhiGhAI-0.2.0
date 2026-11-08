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
if (host.GetName().Version.ToString() != "0.2.4.0") throw new Exception("Expected loaded RhiGhAI 0.2.4.");
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
if (phase == 2) {
// Free-form operations: every one executes, the results are valid Breps, one Undo removes them all.
var formPlan = @"{""schemaVersion"":1,""targetHost"":""rhino"",""summary"":""Free form"",""assumptions"":[],""operations"":[
{""kind"":""createCircle"",""id"":""base"",""center"":{""x"":0,""y"":0,""z"":0},""normal"":{""x"":0,""y"":0,""z"":1},""radius"":10,""layer"":""Form""},
{""kind"":""copy"",""id"":""top"",""references"":[""base""],""translation"":{""x"":0,""y"":0,""z"":30},""rotation"":{""axis"":{""x"":0,""y"":0,""z"":1},""angleDegrees"":45,""center"":{""x"":0,""y"":0,""z"":0}},""scale"":{""factor"":0.6,""center"":{""x"":0,""y"":0,""z"":0}},""layer"":""Form""},
{""kind"":""loft"",""id"":""tower"",""curveIds"":[""base"",""top""],""closed"":false,""cap"":true,""layer"":""Form""},
{""kind"":""createCurve"",""id"":""blob"",""points"":[{""x"":60,""y"":40,""z"":0},{""x"":70,""y"":45,""z"":0},{""x"":68,""y"":55,""z"":0},{""x"":55,""y"":52,""z"":0}],""closed"":true,""layer"":""Form""},
{""kind"":""copy"",""id"":""blobTop"",""references"":[""blob""],""translation"":{""x"":0,""y"":0,""z"":20},""rotation"":{""axis"":{""x"":0,""y"":0,""z"":1},""angleDegrees"":30,""center"":{""x"":62,""y"":48,""z"":0}},""scale"":null,""layer"":""Form""},
{""kind"":""loft"",""id"":""blobLoft"",""curveIds"":[""blob"",""blobTop""],""closed"":false,""cap"":true,""layer"":""Form""},
{""kind"":""createCurve"",""id"":""rail"",""points"":[{""x"":40,""y"":0,""z"":0},{""x"":40,""y"":0,""z"":20},{""x"":50,""y"":0,""z"":40}],""closed"":false,""layer"":""Form""},
{""kind"":""createCircle"",""id"":""profile"",""center"":{""x"":40,""y"":0,""z"":0},""normal"":{""x"":0,""y"":0,""z"":1},""radius"":2,""layer"":""Form""},
{""kind"":""sweep1"",""id"":""swept"",""railId"":""rail"",""profileIds"":[""profile""],""closed"":false,""cap"":false,""layer"":""Form""},
{""kind"":""createCurve"",""id"":""tubeRail"",""points"":[{""x"":60,""y"":0,""z"":0},{""x"":70,""y"":10,""z"":10},{""x"":80,""y"":0,""z"":20}],""closed"":false,""layer"":""Form""},
{""kind"":""pipe"",""id"":""tube"",""railId"":""tubeRail"",""radius"":1.5,""cap"":true,""layer"":""Form""},
{""kind"":""createArc"",""id"":""arc"",""start"":{""x"":100,""y"":0,""z"":0},""mid"":{""x"":105,""y"":0,""z"":10},""end"":{""x"":100,""y"":0,""z"":20},""layer"":""Form""},
{""kind"":""revolve"",""id"":""vase"",""profileId"":""arc"",""axisStart"":{""x"":95,""y"":0,""z"":0},""axisEnd"":{""x"":95,""y"":0,""z"":20},""angleDegrees"":360,""layer"":""Form""},
{""kind"":""createBox"",""id"":""block"",""origin"":{""x"":130,""y"":0,""z"":0},""size"":{""x"":20,""y"":20,""z"":20},""layer"":""Form""},
{""kind"":""filletEdges"",""id"":""rounded"",""brepId"":""block"",""radius"":2,""layer"":""Form""}]}";
var formSnapshot = snapshot();
var formResult = execute(formSnapshot, compile(formPlan, formSnapshot));
var formIds = ((System.Collections.IEnumerable)formResult.GetType().GetProperty("CreatedOrChangedIds").GetValue(formResult)).Cast<Guid>().ToArray();
if (formIds.Length != 6) throw new Exception("Free form created " + formIds.Length + " objects, expected 6 (2 lofts, sweep, pipe, revolve, fillet).");
foreach (var id in formIds) {
    var created = __rhino_doc__.Objects.FindId(id);
    if (created == null || !(created.Geometry is Brep) || !created.Geometry.IsValid) throw new Exception("Free form result is not a valid Brep.");
}
// Execution order is topological, not textual: find the loft by where it must be.
if (!formIds.Select(id => (Brep)__rhino_doc__.Objects.FindId(id).Geometry).Any(b => {
        var box = b.GetBoundingBox(true);
        return b.IsSolid && Math.Abs(box.Max.Z - 30) < 1e-6 && box.Max.X < 10 + 1e-6;
    }))
    throw new Exception("Capped loft is not a 30-high solid.");
// A loft through a closed smooth curve and its rotated copy must close too (no seam slit).
if (!formIds.Select(id => (Brep)__rhino_doc__.Objects.FindId(id).Geometry).Any(b => {
        var box = b.GetBoundingBox(true);
        return b.IsSolid && Math.Abs(box.Max.Z - 20) < 1e-6 && box.Min.Y > 30;
    }))
    throw new Exception("Loft through closed smooth curves is not a solid.");
if (!__rhino_doc__.Undo()) throw new Exception("Free form Undo failed.");
if (formIds.Any(id => __rhino_doc__.Objects.FindId(id) != null)) throw new Exception("Free form objects remained after one Undo.");
// A fillet that cannot succeed fails before anything is written.
var badFillet = @"{""schemaVersion"":1,""targetHost"":""rhino"",""summary"":""Bad fillet"",""assumptions"":[],""operations"":[{""kind"":""createBox"",""id"":""b"",""origin"":{""x"":0,""y"":0,""z"":0},""size"":{""x"":10,""y"":10,""z"":10},""layer"":""FilletProbe""},{""kind"":""filletEdges"",""id"":""f"",""brepId"":""b"",""radius"":50,""layer"":""FilletProbe""}]}";
var badSnapshot = snapshot();
try { execute(badSnapshot, compile(badFillet, badSnapshot)); throw new Exception("Expected fillet rejection."); }
catch (Exception error) { if (!error.Message.Contains("FilletFailed")) throw; }
if (__rhino_doc__.Layers.FindName("FilletProbe") != null || __rhino_doc__.Objects.Any(o => !o.IsDeleted)) throw new Exception("Failed fillet left something behind.");
phase = 3;
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
phase = 4;
System.IO.File.WriteAllText(reportPath, "RHINO_ACCEPTANCE_OK panel+singleUndo selectedMove+singleUndo freeForm+singleUndo filletReject layerRollback+unrelatedUndoPreserved\nLoaded: " + host.Location);
} catch (Exception error) {
    phase = 4;
    System.IO.File.WriteAllText(reportPath, "FAILED: " + error.ToString());
}
};
System.Threading.Tasks.Task.Run(async () => {
    for (int tick = 0; tick < 30 && phase < 4; tick++) {
        await System.Threading.Tasks.Task.Delay(500);
        RhinoApp.InvokeOnUiThread((Action)(() => handler(null, EventArgs.Empty)));
    }
});
Console.WriteLine("RHINO_ACCEPTANCE_SCHEDULED");
