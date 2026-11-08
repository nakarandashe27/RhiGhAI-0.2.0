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
using RhiGhAI.Core.Providers;
using RhiGhAI.Core.Contracts;
using RhiGhAI.Rhino.Services;
public class DocumentSwitchProvider : IPlanProvider {
 public ProviderKind Kind=>ProviderKind.Codex;
 public int Calls;
 public TaskCompletionSource<bool> Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public TaskCompletionSource<string> Reply=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public Task<ProviderStatus> ConnectAsync(CancellationToken token)=>Task.FromResult(new ProviderStatus(true,"fixture","fixture",null,Array.Empty<ProviderModel>()));
 public Task<string> RequestJsonAsync(PlanRequest request,CancellationToken token){Interlocked.Increment(ref Calls);Started.SetResult(true);return Reply.Task;}
 public void ResetConversation(){}
 public void Interrupt(){}
 public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
var initial=__rhino_doc__;
if(Path.GetFileName(initial.Path)!="RhiGhAI-ui-retry.3dm" || initial.Objects.Any(o=>!o.IsDeleted))throw new Exception("Separate empty test document only");
var originalPath=initial.Path;
var report=Path.Combine(Path.GetDirectoryName(originalPath),"RHINO_DOCUMENT_SWITCH.txt");
var targetPath=Path.Combine(Path.GetDirectoryName(originalPath),"RhiGhAI-open-target-"+Guid.NewGuid().ToString("N")+".3dm");
File.Copy(originalPath,targetPath,false);
var valid=@"{""schemaVersion"":1,""targetHost"":""rhino"",""summary"":""Late document fixture"",""assumptions"":[],""operations"":[{""kind"":""createBox"",""id"":""late"",""origin"":{""x"":0,""y"":0,""z"":0},""size"":{""x"":10,""y"":20,""z"":3},""layer"":""LateDocumentFixture""}]}";
File.WriteAllText(report,"PENDING");
Task OnUi(Action action){var tcs=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);RhinoApp.InvokeOnUiThread((Action)(()=>{try{action();tcs.SetResult(true);}catch(Exception e){tcs.SetException(e);}}));return tcs.Task;}
void Check(bool condition,string message){if(!condition)throw new Exception(message);}
async Task Within(Task task){if(await Task.WhenAny(task,Task.Delay(45000))!=task)throw new Exception("Document probe deadline exceeded");await task;}
Task.Run(async()=>{
 var lines=new List<string>();
 try{
  await Task.Delay(1000);
  await OnUi(()=>{if(initial.UndoRecordingIsActive)initial.EndUndoRecord(initial.CurrentUndoRecordSerialNumber);});
  foreach(var action in new[]{"New","Open"}){
   RhinoDoc sourceDoc=null;uint sourceSerial=0;
   await OnUi(()=>{sourceDoc=RhinoDoc.ActiveDoc;Check(Path.GetFileName(sourceDoc.Path)=="RhiGhAI-ui-retry.3dm" && !sourceDoc.Objects.Any(o=>!o.IsDeleted),"Wrong source doc");sourceSerial=sourceDoc.RuntimeSerialNumber;});
   var service=new RhiGhAIService();var provider=new DocumentSwitchProvider();
   var field=typeof(RhiGhAIService).GetField("_provider",BindingFlags.Instance|BindingFlags.NonPublic);
   var previous=(IPlanProvider)field.GetValue(service);field.SetValue(service,provider);await previous.DisposeAsync();
   var messages=new List<ServiceMessage>();service.Message+=(sender,m)=>{lock(messages)messages.Add(m);};Task turn=null;
   try{
    await OnUi(()=>turn=service.SendAsync(sourceDoc,"Document switch fixture "+action,TargetHost.Rhino,"fixture","low"));
    await Within(provider.Started.Task);
    await OnUi(()=>{
     RhinoDoc changed;
     if(action=="New")changed=RhinoDoc.Create(originalPath);
     else{bool already;changed=RhinoDoc.Open(targetPath,out already);}
     Check(changed!=null && RhinoDoc.ActiveDoc.RuntimeSerialNumber!=sourceSerial,"Native "+action+" did not change active document");
    });
    provider.Reply.SetResult(valid);
    await Within(turn);
    ServiceMessage[] observed;lock(messages)observed=messages.ToArray();
    Check(provider.Calls==1 && observed.Any(m=>m.Kind=="error" && m.Code=="StaleDocument") && !observed.Any(m=>m.Kind=="result"),action+": wrong stale response lifecycle");
    await OnUi(()=>{
     var active=RhinoDoc.ActiveDoc;
     Check(!active.Objects.Any(o=>!o.IsDeleted) && !active.Layers.Any(l=>!l.IsDeleted && l.Name=="LateDocumentFixture"),action+": late mutation");
     bool already;var restored=RhinoDoc.Open(originalPath,out already);
     Check(restored!=null && !restored.Objects.Any(o=>!o.IsDeleted) && !restored.Layers.Any(l=>!l.IsDeleted && l.Name=="LateDocumentFixture"),"Restore test document failed");
    });
    lines.Add("PASS native "+action+" while actual service waits for provider: one request; StaleDocument; no mutation in replacement or reopened source.");
    File.WriteAllLines(report,lines);
   }finally{provider.Reply.TrySetResult(valid);service.Stop();if(turn!=null)await Within(turn);await service.DisposeAsync();}
  }
  lines.Add("COMPLETE 2 document-switch scenarios. Only test-owned empty documents; provider response fixture.");
  File.WriteAllLines(report,lines);
 }catch(Exception e){File.WriteAllLines(report,lines.Concat(new[]{"FAILED: "+e}));}
});
Console.WriteLine("DOCUMENT_SWITCH_SCHEDULED");
