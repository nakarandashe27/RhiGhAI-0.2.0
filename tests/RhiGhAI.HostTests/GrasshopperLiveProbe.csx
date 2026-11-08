#r "C:\Program Files\Rhino 8\Plug-ins\Grasshopper\Grasshopper.dll"
#r "C:\Program Files\Rhino 8\Plug-ins\Grasshopper\GH_IO.dll"
#r "C:\Users\YOUR_USER\AppData\Roaming\McNeel\Rhinoceros\8.0\Plug-ins\RhiGhAI\RhiGhAI.Core.dll"
#r "C:\Users\YOUR_USER\AppData\Roaming\McNeel\Rhinoceros\8.0\Plug-ins\RhiGhAI\RhiGhAI.Grasshopper.gha"
#r "C:\Users\YOUR_USER\AppData\Roaming\McNeel\Rhinoceros\8.0\Plug-ins\RhiGhAI\RhiGhAI.Rhino.rhp"
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Rhino;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using RhiGhAI.Core.Contracts;
using RhiGhAI.Core.Providers;
using RhiGhAI.Grasshopper;
using RhiGhAI.Rhino.Services;
var rhinoDoc=__rhino_doc__;
if(Path.GetFileName(rhinoDoc.Path)!="RhiGhAI-ui-retry.3dm" || rhinoDoc.Objects.Any(o=>!o.IsDeleted))throw new Exception("Separate empty test document required");
var serial=rhinoDoc.RuntimeSerialNumber;var documentPath=rhinoDoc.Path;
var folder=Path.GetDirectoryName(documentPath);
var report=Path.Combine(folder,"GH_SERVICE_LIVE.txt");
File.WriteAllText(report,"PENDING");
Task OnUi(Action action){var tcs=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);RhinoApp.InvokeOnUiThread((Action)(()=>{try{action();tcs.SetResult(true);}catch(Exception e){tcs.SetException(e);}}));return tcs.Task;}
void Check(bool condition,string message){if(!condition)throw new Exception(message);}
Task.Run(async()=>{
 var lines=new List<string>();GH_Document original=null;GH_Document doc=null;var reopened=new List<GH_Document>();
 var service=new RhiGhAIService();
 try {
  var field=typeof(RhiGhAIService).GetField("_provider",BindingFlags.Instance|BindingFlags.NonPublic);
  var previous=(IPlanProvider)field.GetValue(service);field.SetValue(service,new CodexPlanProvider());await previous.DisposeAsync();
  typeof(RhiGhAIService).GetProperty("Settings").SetValue(service,service.Settings with {Provider=ProviderKind.Codex,RetryMax=3,TimeoutSeconds=180});
  await service.RefreshAsync(CancellationToken.None);
  Check(service.Connection.Ready && service.Connection.Models.Any(m=>m.Id=="gpt-6.1-sol" && m.Efforts.Contains("low")),"Live subscription/model unavailable");
  await OnUi(()=>{GrasshopperBridge.LoadCatalog();original=Instances.ActiveCanvas.Document;doc=new GH_Document();Instances.DocumentServer.AddDocument(doc);Instances.ActiveCanvas.Document=doc;});
  await service.StartNewConversationAsync(serial,documentPath);
  async Task Scenario(string name,string prompt,Action<GH_Document> verify,Action<GH_Document> edit,Action<GH_Document> verifyEdited){
   var messages=new List<ServiceMessage>();EventHandler<ServiceMessage> handler=(sender,m)=>{lock(messages)messages.Add(m);};service.Message+=handler;
   Guid[] before=null;Task turn=null;
   try {
    await OnUi(()=>{before=doc.Objects.Select(o=>o.InstanceGuid).ToArray();turn=service.SendAsync(rhinoDoc,prompt,TargetHost.Grasshopper,"gpt-6.1-sol","low");});
    await turn;
    ServiceMessage[] ms;lock(messages)ms=messages.ToArray();
    Check(ms.Count(m=>m.Kind=="result")==1,"Live service failed: "+string.Join(";",ms.Where(m=>m.Kind=="error").Select(m=>m.Code+":"+m.Text)));
    await OnUi(()=>{
     Check(ReferenceEquals(Instances.ActiveCanvas.Document,doc),"Wrong active GH definition");
     Check(doc.Objects.OfType<GH_Group>().Count()==1,"Owned graphs accumulated");
     Check(doc.Objects.OfType<IGH_Component>().All(c=>!c.RuntimeMessages(GH_RuntimeMessageLevel.Error).Any()),"Runtime GH error");
     Check(doc.Objects.OfType<GH_NumberSlider>().All(s=>s.SourceCount==0 && doc.Objects.OfType<IGH_Component>().Any(c=>c.Params.Input.Any(p=>p.Sources.Contains(s)))),"Unconnected slider");
     verify(doc);
     var ids=doc.Objects.Select(o=>o.InstanceGuid).ToHashSet();
     var path=Path.Combine(folder,"GH-live-"+name+".gh");var archive=new GH_IO.Serialization.GH_Archive();
     Check(archive.AppendObject(doc,"Definition") && archive.WriteToFile(path,true,false),"Save live GH graph failed");
     var read=new GH_IO.Serialization.GH_Archive();var copy=new GH_Document();
     Check(read.ReadFromFile(path) && read.ExtractObject(copy,"Definition"),"Reopen live GH graph failed");
     Instances.DocumentServer.AddDocument(copy);reopened.Add(copy);Instances.ActiveCanvas.Document=copy;copy.NewSolution(false);
     Check(copy.Objects.Select(o=>o.InstanceGuid).ToHashSet().SetEquals(ids),"Reopen GUID mismatch");verify(copy);
     edit(copy);copy.NewSolution(false);verifyEdited(copy);
     Instances.ActiveCanvas.Document=doc;
     doc.UndoServer.PerformUndo();doc.NewSolution(false);
     Check(doc.Objects.Select(o=>o.InstanceGuid).ToHashSet().SetEquals(before),"Single GH Undo did not restore prior graph");
     doc.UndoServer.PerformRedo();doc.NewSolution(false);verify(doc);
    });
    lines.Add("PASS "+name+": actual service -> gpt-6.1-sol/low -> native GH; semantic output; one owned group; single Undo/Redo; save/reopen GUIDs; local slider recalculation. Repair errors="+ms.Count(m=>m.Kind=="error"));
    File.WriteAllLines(report,lines);
   }finally{service.Message-=handler;}
  }
  IGH_Component Component(GH_Document d,string name)=>d.Objects.OfType<IGH_Component>().Single(c=>c.Name==name);
  GH_NumberSlider Slider(GH_Document d,string component,string port)=> (GH_NumberSlider)Component(d,component).Params.Input.Single(p=>p.Name==port).Sources.Single();
  void Points(GH_Document d,int count){var points=Component(d,"Construct Point").Params.Output[0].VolatileData.AllData(true).Cast<GH_Point>().Select(p=>p.Value).OrderBy(p=>p.X).ToArray();Check(points.Length==count,"Point count");for(int i=0;i<count;i++)Check(Math.Abs(points[i].X-i*100)<1e-6 && Math.Abs(points[i].Y)<1e-6 && Math.Abs(points[i].Z)<1e-6,"Point positions");}
  void Bounds(GH_Document d,string component,double x,double y,double z){var data=Component(d,component).Params.Output[0].VolatileData.AllData(true).OfType<IGH_GeometricGoo>().ToArray();Check(data.Length==1 && data[0].IsValid,"Expected one valid geometric output");var diagonal=data[0].Boundingbox.Diagonal;Check(Math.Abs(diagonal.X-x)<1e-5 && Math.Abs(diagonal.Y-y)<1e-5 && Math.Abs(diagonal.Z-z)<1e-5,"Output bounds: "+diagonal);}
  await Scenario("points","Создай редактируемый Grasshopper граф: 5 точек по оси X от 0 с шагом 100 мм. Два независимых Number Slider: целочисленный count=5 (1..20) и step=100 (1..500). Series Start=0, затем Construct Point, Y=Z=0. Count и Step подключи напрямую к слайдерам. Ничего другого.",d=>Points(d,5),d=>Slider(d,"Series","Count").SetSliderValue(7m),d=>Points(d,7));
  await Scenario("circle","Полностью замени предыдущий граф одним редактируемым кругом Circle в XY с центром 0,0,0 и радиусом 50 мм. Radius подключи напрямую к Number Slider value=50, min=1, max=200. Только один круг и один слайдер радиуса; никаких Series или Construct Point.",d=>Bounds(d,"Circle",100,100,0),d=>Slider(d,"Circle","Radius").SetSliderValue(75m),d=>Bounds(d,"Circle",150,150,0));
  await Scenario("extrusion","Полностью замени предыдущий граф редактируемой прямоугольной экструзией. Rectangle в XY, X Size=100, Y Size=50, Radius=0. X Size и Y Size напрямую из двух Number Slider с min=1, max=500. Extrude Base из Rectangle, Direction из Unit Z. Unit Z Factor напрямую из третьего Number Slider высоты 30 (1..200). Получить ровно одну экструзию по Z высотой 30 мм. Не добавляй круги, Series, скрипты или bake.",d=>Bounds(d,"Extrude",100,50,30),d=>Slider(d,"Unit Z","Factor").SetSliderValue(45m),d=>Bounds(d,"Extrude",100,50,45));
  lines.Add("COMPLETE 3 actual subscription AI requests through service, distinct editable native definitions; no Rhino baked geometry.");
  await OnUi(()=>Check(!rhinoDoc.Objects.Any(o=>!o.IsDeleted),"Unexpected Rhino geometry"));
  File.WriteAllLines(report,lines);
 }catch(Exception e){File.WriteAllLines(report,lines.Concat(new[]{"FAILED: "+e}));}
 finally{
  await service.DisposeAsync();
  await OnUi(()=>{Instances.ActiveCanvas.Document=original;foreach(var copy in reopened)Instances.DocumentServer.RemoveDocument(copy);if(doc!=null)Instances.DocumentServer.RemoveDocument(doc);});
 }
});
Console.WriteLine("GH_SERVICE_LIVE_SCHEDULED");
