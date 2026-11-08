#r "C:\Users\YOUR_USER\AppData\Roaming\McNeel\Rhinoceros\8.0\Plug-ins\RhiGhAI\RhiGhAI.Core.dll"
#r "C:\Users\YOUR_USER\AppData\Roaming\McNeel\Rhinoceros\8.0\Plug-ins\RhiGhAI\RhiGhAI.Rhino.rhp"
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rhino;
using RhiGhAI.Rhino;
using RhiGhAI.Rhino.Services;
using RhiGhAI.Core.Contracts;
var doc=__rhino_doc__;
if (System.IO.Path.GetFileName(doc.Path)!="RhiGhAI-acceptance.3dm" || doc.Objects.Any(o=>!o.IsDeleted)) throw new Exception("Empty separate test doc only");
var serial=doc.RuntimeSerialNumber;var path=doc.Path;
var service=RhiGhAIPlugIn.Instance.Service;
var report=@"C:\Rhino 8\Rhino 8\rhino plugin\artifacts\SERVICE_LIVE.txt";
System.IO.File.WriteAllText(report,"PENDING");
Task OnUi(Action action) { var tcs=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);RhinoApp.InvokeOnUiThread((Action)(()=> {try {action(); tcs.SetResult(true);}catch(Exception e){tcs.SetException(e);}}));return tcs.Task; }
Task.Run(async () => {
var messages=new List<ServiceMessage>();
EventHandler<ServiceMessage> handler=(sender,message)=> { lock(messages)messages.Add(message); };
service.Message+=handler;
try {
 await Task.Delay(1000);
 await OnUi(()=> {if(doc.IsCommandRunning)throw new Exception("Rhino command running");if(doc.UndoRecordingIsActive)doc.EndUndoRecord(doc.CurrentUndoRecordSerialNumber);});
 await service.StartNewConversationAsync(serial,path);
 Task send=null;
 await OnUi(()=> {send=service.SendAsync(doc,"Create exactly one box using createBox: origin 0,0,0, size 100,50,5 in millimeters, layer IntegrationProbe. Summary Integration probe. No other geometry. Never use tools.",TargetHost.Rhino,"gpt-6.1-sol","low");});
 await send;
 ServiceMessage[] observed;lock(messages)observed=messages.ToArray();
 if(observed.Any(m=>m.Kind=="error") || !observed.Any(m=>m.Kind=="result")) throw new Exception("Service failed; error codes="+string.Join(",",observed.Where(m=>m.Kind=="error").Select(m=>m.Code)));
 await OnUi(()=> {
 var objects=doc.Objects.Where(o=>!o.IsDeleted).ToArray();if(objects.Length!=1)throw new Exception("Unexpected count="+objects.Length);
 var o=objects[0];var diagonal=o.Geometry.GetBoundingBox(true).Diagonal;
 if(Math.Abs(diagonal.X-100)>1e-8||Math.Abs(diagonal.Y-50)>1e-8||Math.Abs(diagonal.Z-5)>1e-8)throw new Exception("Dimensions differ");
 if(doc.Layers[o.Attributes.LayerIndex].Name!="IntegrationProbe")throw new Exception("Layer differs");
 if(!doc.Undo() || doc.Objects.Any(obj=>!obj.IsDeleted))throw new Exception("One Undo did not remove service result");
 });
 System.IO.File.WriteAllText(report,"PASS actual installed service -> gpt-6.1-sol/low -> strict plan -> native box 100x50x5/layer IntegrationProbe -> one Undo. No API key.\n"+"Messages="+string.Join(",",observed.Select(m=>m.Kind)));
} catch(Exception e) {System.IO.File.WriteAllText(report,"FAILED: "+e);}
finally {service.Message-=handler;}
});
Console.WriteLine("SERVICE_LIVE_SCHEDULED");
