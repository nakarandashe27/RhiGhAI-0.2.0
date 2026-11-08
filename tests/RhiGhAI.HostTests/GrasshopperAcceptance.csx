#r "C:\Program Files\Rhino 8\Plug-ins\Grasshopper\Grasshopper.dll"
#r "C:\Program Files\Rhino 8\Plug-ins\Grasshopper\GH_IO.dll"
#r "C:\Users\YOUR_USER\AppData\Roaming\McNeel\Rhinoceros\8.0\Plug-ins\RhiGhAI\RhiGhAI.Core.dll"
#r "C:\Users\YOUR_USER\AppData\Roaming\McNeel\Rhinoceros\8.0\Plug-ins\RhiGhAI\RhiGhAI.Grasshopper.gha"
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using RhiGhAI.Core.Contracts;
using RhiGhAI.Core.Graph;
using RhiGhAI.Grasshopper;
if (!new[] { "RhiGhAI-acceptance.3dm", "RhiGhAI-ui-retry.3dm" }.Contains(System.IO.Path.GetFileName(__rhino_doc__.Path))) throw new Exception("Separate acceptance slot only");
var report = @"C:\Rhino 8\Rhino 8\rhino plugin\artifacts\GH_ACCEPTANCE.txt";
var lines = new List<string>();
var catalog = GrasshopperBridge.LoadCatalog();
var original = Instances.ActiveCanvas.Document;
var doc = new GH_Document();
Instances.DocumentServer.AddDocument(doc);
Instances.ActiveCanvas.Document = doc;
void Check(bool condition, string message) { if(!condition) throw new Exception(message); }
GhGraphPlan Plan(int count) => GhGraphCompiler.Compile(new GhGraphEnvelope(1,"Host test",Array.Empty<string>(),new GhNodeSpec[] {
 new("n","Number Slider",new GhValueSpec[] {new("min","1"),new("max","20"),new("value",count.ToString()),new("decimals","0")}),
 new("series","Series",new GhValueSpec[] {new("Start","0"),new("Step","10")}),
 new("point","Construct Point",Array.Empty<GhValueSpec>())
},new GhWireSpec[] {new("n","","series","Count"),new("series","Series","point","X coordinate")}),catalog);
var ownership="host-acceptance";
try {
var first=GrasshopperBridge.Emit(ownership,Plan(5),doc);
Check(first.ObjectCount==3 && first.WireCount==2 && doc.ObjectCount==4,"Native counts");
var firstIds=doc.Objects.Select(o=>o.InstanceGuid).ToHashSet();
var pt=doc.Objects.OfType<IGH_Component>().Single(c=>c.Name=="Construct Point");
Check(pt.Params.Output[0].VolatileDataCount==5,"Point output count");
lines.Add("PASS native sliders+components+wires+group, 5 solved points");
GrasshopperBridge.Emit(ownership,Plan(7),doc);
Check(doc.ObjectCount==4,"Replacement should not pile up");
Check(doc.Objects.OfType<IGH_Component>().Single(c=>c.Name=="Construct Point").Params.Output[0].VolatileDataCount==7,"Updated solve");
doc.UndoServer.PerformUndo(); doc.NewSolution(false);
Check(doc.Objects.Select(o=>o.InstanceGuid).ToHashSet().SetEquals(firstIds),"One Undo restores first graph");
lines.Add("PASS replacement+singleUndo restores old graph and group");
GrasshopperBridge.Emit(ownership,Plan(9),doc);
Check(doc.ObjectCount==4,"Update after Undo duplicated the restored graph");
lines.Add("PASS update after Undo keeps only one owned graph");
var undoIds=doc.Objects.Select(o=>o.InstanceGuid).ToHashSet();
doc.UndoServer.PerformUndo(); doc.UndoServer.PerformRedo(); doc.NewSolution(false);
Check(doc.Objects.Select(o=>o.InstanceGuid).ToHashSet().SetEquals(undoIds),"Redo graph mismatch");
GrasshopperBridge.Emit(ownership,Plan(9),doc);
Check(doc.ObjectCount==4,"Update after Redo duplicated graph");
lines.Add("PASS update after Redo keeps one owned graph");
var before=doc.Objects.Select(o=>o.InstanceGuid).ToHashSet();
using(var cancel=new CancellationTokenSource()) {
GH_Document.SolutionEndEventHandler cancelEnd=(sender,args)=>cancel.Cancel();
doc.SolutionEnd+=cancelEnd;
try { GrasshopperBridge.Emit(ownership,Plan(11),doc,cancel.Token); throw new Exception("Expected cancel"); }
catch(OperationCanceledException) { }
finally { doc.SolutionEnd-=cancelEnd; }
Check(doc.Objects.Select(o=>o.InstanceGuid).ToHashSet().SetEquals(before),"Cancelled solve left mutations");
}
lines.Add("PASS cancellation after solution rolls back exact graph");
var bad=GhGraphCompiler.Compile(new GhGraphEnvelope(1,"Division failure",Array.Empty<string>(),new GhNodeSpec[] {
new("one","Number Slider",new GhValueSpec[] {new("min","0"),new("max","10"),new("value","1")}),
new("zero","Number Slider",new GhValueSpec[] {new("min","0"),new("max","10"),new("value","0")}),
new("divide","Division",Array.Empty<GhValueSpec>())},new GhWireSpec[] {new("one","","divide","A"),new("zero","","divide","B")}),catalog);
try { GrasshopperBridge.Emit(ownership,bad,doc); throw new Exception("Division did not raise a runtime error"); }
catch(TaskPlanValidationException e) {Check(e.Code=="GrasshopperSolutionFailed",e.ToString());}
Check(doc.Objects.Select(o=>o.InstanceGuid).ToHashSet().SetEquals(before),"Runtime-error rollback changed previous graph");
lines.Add("PASS runtime error rolls back previous graph");
using(var cancel=new CancellationTokenSource()) {
var sentinel = new GH_Panel(); sentinel.CreateAttributes(); sentinel.UserText="unrelated user action";
bool injected=false;
GH_Document.SolutionEndEventHandler userAction=(sender,args)=> {
 if(injected) return; injected=true;
 doc.UndoUtil.RecordAddObjectEvent("unrelated sentinel",sentinel); doc.AddObject(sentinel,false); cancel.Cancel();
};
doc.SolutionEnd+=userAction;
try { GrasshopperBridge.Emit(ownership,Plan(11),doc,cancel.Token); throw new Exception("Expected cancel with user callback"); }
catch(OperationCanceledException) { }
finally { doc.SolutionEnd-=userAction; }
Check(doc.FindObject(sentinel.InstanceGuid,false)!=null,"Rollback undid an unrelated callback action");
Check(doc.Objects.Where(o=>o.InstanceGuid!=sentinel.InstanceGuid).Select(o=>o.InstanceGuid).ToHashSet().SetEquals(before),"Rollback did not restore own graph with callback");
doc.RemoveObject(sentinel,false);
}
lines.Add("PASS unrelated solution-callback Undo action preserved during own rollback");
var limit=doc.UndoServer.MaxRecords;
doc.UndoServer.MaxRecords=0;
GrasshopperBridge.Emit(ownership,Plan(10),doc);
Check(doc.ObjectCount==4,"MaxRecords zero duplicated graph");
doc.UndoServer.PerformUndo(); doc.NewSolution(false);
Check(doc.Objects.Select(o=>o.InstanceGuid).ToHashSet().SetEquals(before),"MaxRecords zero could not Undo");
lines.Add("PASS MaxRecords=0 retains native Undo (zero means unbounded in this host)");
doc.UndoServer.MaxRecords=1;
GrasshopperBridge.Emit(ownership,Plan(10),doc);
GrasshopperBridge.Emit(ownership,Plan(12),doc);
Check(doc.ObjectCount==4,"MaxRecords=1 duplicated graph");
doc.UndoServer.PerformUndo(); doc.NewSolution(false);
Check(doc.Objects.OfType<GH_NumberSlider>().Single().CurrentValue==10,"One Undo under MaxRecords=1 did not restore first update");
lines.Add("PASS MaxRecords=1 replacement and one Undo; host did not auto-trim count synchronously");
doc.UndoServer.MaxRecords=limit;
var savedIds=doc.Objects.Select(o=>o.InstanceGuid).ToHashSet();
var path=@"C:\Rhino 8\Rhino 8\rhino plugin\artifacts\RhiGhAI-acceptance.gh";
var archive=new GH_IO.Serialization.GH_Archive();
Check(archive.AppendObject(doc,"Definition") && archive.WriteToFile(path,true,false),"Save GH");
var read=new GH_IO.Serialization.GH_Archive();var reopened=new GH_Document();
Check(read.ReadFromFile(path) && read.ExtractObject(reopened,"Definition"),"Reopen GH");
Check(reopened.Objects.Select(o=>o.InstanceGuid).ToHashSet().SetEquals(savedIds),"Reopen changed saved guids");
Instances.DocumentServer.AddDocument(reopened); Instances.ActiveCanvas.Document=reopened;
GrasshopperBridge.Emit(ownership,Plan(3),reopened);
Check(reopened.ObjectCount==8 && savedIds.All(id=>reopened.FindObject(id,false)!=null),"Reopen ownership removed saved user graph");
lines.Add("PASS saved/reopened graph belongs to user, next emit preserves it");
try { GrasshopperBridge.Emit(ownership,Plan(4),doc); throw new Exception("Stale document accepted"); }
catch(GrasshopperExecutionException e) {Check(e.Code=="StaleGrasshopperDocument",e.ToString());}
lines.Add("PASS switched GH document rejected before mutation");
GrasshopperBridge.Forget(ownership);
Instances.ActiveCanvas.Document=original;
Instances.DocumentServer.RemoveDocument(reopened);
System.IO.File.WriteAllLines(report,lines);
Console.WriteLine("GH_ACCEPTANCE_OK");
} catch(Exception e) { lines.Add("FAILED: "+e); System.IO.File.WriteAllLines(report,lines); Console.WriteLine("GH_ACCEPTANCE_FAILED"); }
finally { Instances.ActiveCanvas.Document=original; Instances.DocumentServer.RemoveDocument(doc); }
