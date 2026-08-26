using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

var corePath = @"C:\Rhino 8\Rhino 8\rhino plugin\artifacts\package-input\RhiGhAI\RhiGhAI.Core.dll";
var rhinoPath = @"C:\Rhino 8\Rhino 8\rhino plugin\artifacts\package-input\RhiGhAI\RhiGhAI.Rhino.rhp";
var core = Assembly.LoadFrom(corePath);
var host = Assembly.LoadFrom(rhinoPath);
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
    var validation = Activator.CreateInstance(validationType, rhinoHost, allowed);
    try {
        return compiler.GetMethod("Compile").Invoke(null, new[] { plan, validation });
    } catch (TargetInvocationException error) {
        throw new Exception(error.InnerException == null ? error.ToString() : error.InnerException.ToString());
    }
};
Func<object> snapshot = () => snapshotBuilder.GetMethod("Capture").Invoke(null, new object[] { __rhino_doc__ });
Func<object, object, object> execute = (snap, graph) => {
    try {
        return executor.GetMethod("Execute").Invoke(null, new object[] { __rhino_doc__, snap, graph, CancellationToken.None });
    } catch (TargetInvocationException error) {
        throw new Exception(error.InnerException == null ? error.ToString() : error.InnerException.ToString());
    }
};

EventHandler handler = null;
__rhino_doc__.Strings.SetString("RhiGhAI.Acceptance", "PENDING");
handler = (sender, args) => {
if (__rhino_doc__.IsCommandRunning || __rhino_doc__.UndoRecordingIsActive) return;
RhinoApp.Idle -= handler;
try {
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

var seedId = __rhino_doc__.Objects.AddBrep(Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(10, 10, 10))));
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
__rhino_doc__.Strings.SetString("RhiGhAI.Acceptance", "RHINO_ACCEPTANCE_OK panel+singleUndo selectedMove+singleUndo");
} catch (Exception error) {
    __rhino_doc__.Strings.SetString("RhiGhAI.Acceptance", "FAILED: " + error.ToString());
}
};
RhinoApp.Idle += handler;
Console.WriteLine("RHINO_ACCEPTANCE_SCHEDULED");
