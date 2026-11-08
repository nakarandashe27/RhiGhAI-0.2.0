#r "C:\Program Files\Rhino 8\Plug-ins\Grasshopper\Grasshopper.dll"
#r "C:\Users\YOUR_USER\AppData\Roaming\McNeel\Rhinoceros\8.0\Plug-ins\RhiGhAI\RhiGhAI.Core.dll"
#r "C:\Users\YOUR_USER\AppData\Roaming\McNeel\Rhinoceros\8.0\Plug-ins\RhiGhAI\RhiGhAI.Grasshopper.gha"
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Grasshopper;
using Grasshopper.Kernel;
using RhiGhAI.Core.Contracts;
using RhiGhAI.Grasshopper;
if(Path.GetFileName(__rhino_doc__.Path)!="RhiGhAI-ui-retry.3dm")throw new Exception("Separate retry document only");
var catalog=GrasshopperBridge.LoadCatalog();
var constructor=typeof(GhComponentCatalog).GetConstructors(BindingFlags.NonPublic|BindingFlags.Instance).Single();
var lines=new List<string>();
foreach(var name in new[]{"Rectangle","Circle"}) {
 if(!catalog.TryFind(name,out var selected))throw new Exception("Missing "+name);
 var emitted=Instances.ComponentServer.EmitObject(selected.ComponentGuid);
 if(emitted is not IGH_Component component || selected.IsParameter)throw new Exception("Parameter won "+name);
 if(!selected.Inputs.Select(p=>p.Name).SequenceEqual(component.Params.Input.Select(p=>p.Name)))throw new Exception("Ports differ "+name);
 var advertised=catalog.Describe(name,900).Split('\n').Where(line=>line.StartsWith(name+" |",StringComparison.Ordinal)).ToArray();
 if(advertised.Length!=1 || !advertised[0].Contains(string.Join(", ",selected.Inputs.Select(p=>p.Name))))throw new Exception("Prompt disagrees with selected "+name);
 var parameterProxy=Instances.ComponentServer.ObjectProxies.First(p=>p.Desc.Name==name && p.CreateInstance() is IGH_Param);
 var param=(IGH_Param)parameterProxy.CreateInstance();
 var parameter=new GhComponentSpec(parameterProxy.Guid,name,param.NickName,param.Category,param.SubCategory,param.Description,new[]{new GhPortSpec(param.Name,param.NickName)},new[]{new GhPortSpec(param.Name,param.NickName)},IsParameter:true);
 foreach(var order in new[]{new[]{parameter,selected},new[]{selected,parameter}}) {
  var fixture=(GhComponentCatalog)constructor.Invoke(new object[]{order});
  if(!fixture.TryFind(name,out var found) || found.ComponentGuid!=selected.ComponentGuid)throw new Exception("Library order changes "+name);
 }
 lines.Add("PASS "+name+": native component wins floating parameter in both enumeration orders; actual ports="+string.Join(",",selected.Inputs.Select(p=>p.Name))+"; exactly one matching prompt entry.");
}
File.WriteAllLines(Path.Combine(Path.GetDirectoryName(__rhino_doc__.Path),"GH_CATALOG_ACCEPTANCE.txt"),lines);
Console.WriteLine("GH_CATALOG_ACCEPTANCE_OK");
