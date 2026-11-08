#r "C:\Users\YOUR_USER\AppData\Roaming\McNeel\Rhinoceros\8.0\Plug-ins\RhiGhAI\RhiGhAI.Core.dll"
#r "C:\Users\YOUR_USER\AppData\Roaming\McNeel\Rhinoceros\8.0\Plug-ins\RhiGhAI\RhiGhAI.Rhino.rhp"
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Rhino;
using RhiGhAI.Rhino.Services;
using RhiGhAI.Core.Contracts;
using RhiGhAI.Core.Providers;

// Actual installed service and native executor; only the provider response is a fault fixture.
// Never run in a working model. No saved settings, credentials, or real provider are changed.
public class FaultProvider : IPlanProvider {
 public ProviderKind Kind => ProviderKind.Codex;
 public int Calls, Interrupts;
 public Func<CancellationToken,Task<string>> Response;
 public TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
 public Task<ProviderStatus> ConnectAsync(CancellationToken token) => Task.FromResult(new ProviderStatus(true,"fixture","fixture",null,Array.Empty<ProviderModel>()));
 public Task<string> RequestJsonAsync(PlanRequest request,CancellationToken token) { Interlocked.Increment(ref Calls);Started.TrySetResult(true);return Response(token); }
 public void ResetConversation() { }
 public void Interrupt() => Interlocked.Increment(ref Interrupts);
 public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
var doc=__rhino_doc__;
if(Path.GetFileName(doc.Path)!="RhiGhAI-ui-retry.3dm" || doc.Objects.Any(o=>!o.IsDeleted)) throw new Exception("Empty separate retry document required");
var serial=doc.RuntimeSerialNumber;
var report=Path.Combine(Path.GetDirectoryName(doc.Path),"SERVICE_FAULT_ACCEPTANCE.txt");
var providerField=typeof(RhiGhAIService).GetField("_provider",BindingFlags.Instance|BindingFlags.NonPublic);
var settingsProperty=typeof(RhiGhAIService).GetProperty("Settings");
var valid=@"{""schemaVersion"":1,""targetHost"":""rhino"",""summary"":""Fault fixture"",""assumptions"":[],""operations"":[{""kind"":""createBox"",""id"":""probe"",""origin"":{""x"":0,""y"":0,""z"":0},""size"":{""x"":10,""y"":20,""z"":3},""layer"":""ServiceFaultProbe""}]}";
Task OnUi(Action action) {var tcs=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);RhinoApp.InvokeOnUiThread((Action)(()=>{try{action();tcs.SetResult(true);}catch(Exception e){tcs.SetException(e);}}));return tcs.Task;}
void Require(bool value,string message) {if(!value)throw new Exception(message);}
Task Within(Task task,int seconds=45) => Task.Run(async()=> {if(await Task.WhenAny(task,Task.Delay(TimeSpan.FromSeconds(seconds)))!=task)throw new Exception("Probe deadline exceeded");await task;});
File.WriteAllText(report,"PENDING\n");
Task.Run(async()=> {
 var passed=new List<string>();
 try {
  await Task.Delay(1000);
  string[] initialLayers=null;
  await OnUi(()=>{Require(!doc.IsCommandRunning,"Command running");if(doc.UndoRecordingIsActive)doc.EndUndoRecord(doc.CurrentUndoRecordSerialNumber);initialLayers=doc.Layers.Where(l=>!l.IsDeleted).Select(l=>l.Name).OrderBy(x=>x).ToArray();});
  async Task Case(string name,FaultProvider provider,Action<ServiceMessage[],bool[],FaultProvider> check,Func<RhiGhAIService,FaultProvider,TaskCompletionSource<bool>,Task> during=null,bool success=false) {
   var service=new RhiGhAIService();
   var original=(IPlanProvider)providerField.GetValue(service);
   providerField.SetValue(service,provider);
   await original.DisposeAsync();
   settingsProperty.SetValue(service,service.Settings with {RetryMax=5,TimeoutSeconds=30});
   var messages=new List<ServiceMessage>();var busy=new List<bool>();
   var code=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
   service.Message+=(sender,m)=>{lock(messages)messages.Add(m);if(m.Kind=="code")code.TrySetResult(true);};
   service.BusyChanged+=(sender,b)=>{lock(busy)busy.Add(b);};
   Task turn=null;
   try {
    await OnUi(()=> {Require(doc.RuntimeSerialNumber==serial && RhinoDoc.ActiveDoc.RuntimeSerialNumber==serial,"Wrong active test document");turn=service.SendAsync(doc,"Provider fault fixture: "+name,TargetHost.Rhino,"fixture","low");});
    if(during!=null){await Within(provider.Started.Task,10);await during(service,provider,code);}
    await Within(turn);
    ServiceMessage[] ms;bool[] bs;lock(messages)ms=messages.ToArray();lock(busy)bs=busy.ToArray();
    Require(bs.SequenceEqual(new[]{true,false}),name+": busy lifecycle");
    check(ms,bs,provider);
    await OnUi(()=> {
     if(success){Require(doc.Objects.Count(o=>!o.IsDeleted)==1,name+": expected one box");Require(doc.Undo(),name+": Undo failed");}
     Require(!doc.Objects.Any(o=>!o.IsDeleted),name+": late or retained geometry");
     Require(initialLayers.SequenceEqual(doc.Layers.Where(l=>!l.IsDeleted).Select(l=>l.Name).OrderBy(x=>x)),name+": retained layer");
    });
    await service.RefreshAsync(CancellationToken.None); // gate is released and reusable
    passed.Add("PASS "+name+"; calls="+provider.Calls+"; busy true/false; no retained geometry/layer; refresh reusable");
    File.WriteAllLines(report,passed);
   } finally {service.Stop();if(turn!=null)await Within(turn);await service.DisposeAsync();}
  }
  foreach(var failure in new[]{"Unauthorized","RateLimit","Transport"}) {
   var provider=new FaultProvider {Response=token=>Task.FromException<string>(failure=="Transport"?(Exception)new IOException("Fixture transport failure"):new ProviderException(failure,"Fixture provider failure"))};
   await Case(failure,provider,(ms,bs,p)=>Require(p.Calls==1 && ms.Any(m=>m.Kind=="error" && m.Code==(failure=="Transport"?"IOException":failure)) && !ms.Any(m=>m.Kind=="result"),failure+": wrong retry/error"));
  }
  var timeout=new FaultProvider {Response=async token=>{await Task.Delay(Timeout.Infinite,token);return valid;}};
  var timer=System.Diagnostics.Stopwatch.StartNew();
  await Case("configured 30-second timeout",timeout,(ms,bs,p)=>Require(p.Calls==1 && ms.Any(m=>m.Kind=="system" && m.Text.Contains("остановлена")) && !ms.Any(m=>m.Kind=="result"),"Timeout lifecycle"));
  Require(timer.Elapsed.TotalSeconds>=29 && timer.Elapsed.TotalSeconds<40,"Timeout elapsed outside range");
  var stop=new FaultProvider {Response=async token=>{await Task.Delay(Timeout.Infinite,token);return valid;}};
  await Case("Stop and settings/provider change during turn",stop,(ms,bs,p)=>Require(p.Calls==1 && p.Interrupts>=1 && !ms.Any(m=>m.Kind=="result"),"Stop failed"),async(s,p,c)=>{
   var settings=s.Settings;bool rejected=false;
   try{await Within(s.SaveSettingsAsync(settings with {Provider=ProviderKind.OpenAiCompatible},null,CancellationToken.None),5);}catch(InvalidOperationException){rejected=true;}
   Require(rejected && s.Settings==settings,"Overlapping settings swap was accepted");s.Stop();
  });
  var lateReply=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
  var late=new FaultProvider {Response=token=>lateReply.Task};
  await Case("provider ignores cancellation and replies after Stop",late,(ms,bs,p)=>Require(p.Calls==1 && !ms.Any(m=>m.Kind=="result"),"Late response committed"),async(s,p,c)=>{s.Stop();await Task.Delay(400);lateReply.SetResult(valid);});
  var queuedReply=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
  var queued=new FaultProvider {Response=token=>queuedReply.Task};
  await Case("Stop while native commit is queued on UI",queued,(ms,bs,p)=>Require(ms.Any(m=>m.Kind=="code") && !ms.Any(m=>m.Kind=="result"),"Queued cancellation failed"),async(s,p,c)=>{
   await OnUi(()=> {
    queuedReply.SetResult(valid);
    Require(c.Task.Wait(3000),"Plan did not reach validated code stage");
    var cancellation=Task.Run(async()=>{await Task.Delay(150);s.Stop();});
    Require(cancellation.Wait(3000),"Stop did not complete while UI was occupied");
   });
   await OnUi(()=>{}); // flush queued native callback, then assert it made no mutation
  });
  var repeated=new FaultProvider {Response=token=>Task.FromResult("invalid JSON")};
  await Case("same invalid JSON twice stops repair",repeated,(ms,bs,p)=>Require(p.Calls==2 && ms.Any(m=>m.Kind=="error" && m.Text.Contains("повторила")) && !ms.Any(m=>m.Kind=="result"),"Repeated error retried beyond twice"));
  var repaired=new FaultProvider();repaired.Response=token=>Task.FromResult(repaired.Calls==1?"invalid JSON":valid);
  await Case("invalid JSON repaired once and committed with one Undo",repaired,(ms,bs,p)=>Require(p.Calls==2 && ms.Any(m=>m.Kind=="error" && m.Code=="InvalidPlanJson") && ms.Count(m=>m.Kind=="result")==1,"Repair failed"),success:true);
  var staleReply=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
  var stale=new FaultProvider {Response=token=>staleReply.Task};
  string originalPath=doc.Path;string renamed=Path.Combine(Path.GetDirectoryName(originalPath),"RhiGhAI-ui-retry-renamed.3dm");
  try {
   await Case("Save As while provider response is pending",stale,(ms,bs,p)=>Require(p.Calls==1 && ms.Any(m=>m.Kind=="error" && m.Code=="StaleDocument") && !ms.Any(m=>m.Kind=="result"),"Stale Save As did not reject"),async(s,p,c)=>{
    await OnUi(()=>{Require(RhinoApp.RunScript("_-SaveAs \""+renamed+"\" _Enter _Enter",false),"Test SaveAs command failed");Require(doc.Path!=originalPath,"SaveAs did not change path");});
    staleReply.SetResult(valid);
   });
  } finally {await OnUi(()=>{if(doc.Path!=originalPath)Require(RhinoApp.RunScript("_-SaveAs \""+originalPath+"\" _Enter _Enter",false),"Restore test path failed");});}
  passed.Add("COMPLETE 10 scenarios; actual service/native Rhino; provider failures are injected fixtures, not account logout/quota tests.");
  File.WriteAllLines(report,passed);
 } catch(Exception e){File.WriteAllLines(report,passed.Concat(new[]{"FAILED: "+e}));}
});
Console.WriteLine("SERVICE_FAULT_SCHEDULED");
