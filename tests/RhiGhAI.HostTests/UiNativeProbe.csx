#r "C:\Program Files\Rhino 8\System\Eto.dll"
#r "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.30\PresentationFramework.dll"
#r "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.30\PresentationCore.dll"
#r "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.30\WindowsBase.dll"
using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation.Peers;
if (!new[] { "RhiGhAI-acceptance.3dm", "RhiGhAI-ui-retry.3dm" }.Contains(System.IO.Path.GetFileName(__rhino_doc__.Path))) throw new Exception("Separate acceptance slot only");
var lines=new List<string>();
var rootPath=@"C:\Rhino 8\Rhino 8\rhino plugin\artifacts";
var host=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="RhiGhAI.Rhino");
var panel=(Eto.Forms.Panel)Rhino.UI.Panels.GetPanel(new Guid("E7C5B31B-ADCA-4511-A57D-D3BB37CCF725"), __rhino_doc__);
var flags=BindingFlags.Instance|BindingFlags.NonPublic;
var native=(FrameworkElement)panel.ControlObject;
void Check(bool ok,string msg) { if(!ok) throw new Exception(msg); }
void CheckButton(object eto) {
 eto.GetType().GetMethod("Invalidate",BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly).Invoke(eto,null);
 var control=(Button)eto.GetType().GetProperty("ControlObject").GetValue(eto);
 control.ApplyTemplate();
 var surface=(Border)control.Template.FindName("Surface",control);
 var ring=(Border)control.Template.FindName("FocusRing",control);
 Check(surface!=null && surface.CornerRadius.TopLeft==8 && ring!=null,"Rounded native template/focus ring missing");
 var peer=new ButtonAutomationPeer(control);
 Check(peer.GetAutomationControlType()==AutomationControlType.Button && !string.IsNullOrEmpty(peer.GetName()),"Button automation role/name missing");
 Check(control.Focusable && control.IsTabStop,"Native focus/Tab disabled");
 bool enabled=control.IsEnabled; control.IsEnabled=false;
 Check(Math.Abs(surface.Opacity-0.45)<1e-9,"Disabled trigger missing"); control.IsEnabled=enabled;
 lines.Add("PASS native button: "+control.Content+"; rounded template, automation, focus/Tab, disabled trigger");
}
panel.GetType().GetMethod("SetBusy",flags).Invoke(panel,new object[]{true});
CheckButton(panel.GetType().GetField("_send",flags).GetValue(panel));
panel.GetType().GetMethod("SetBusy",flags).Invoke(panel,new object[]{false});
foreach(var name in new[]{"_send","_newChat","_settings"}) CheckButton(panel.GetType().GetField(name,flags).GetValue(panel));
var plugin=host.GetType("RhiGhAI.Rhino.RhiGhAIPlugIn").GetProperty("Instance").GetValue(null);
var service=plugin.GetType().GetProperty("Service").GetValue(plugin);
var dialog=(Eto.Forms.Dialog)Activator.CreateInstance(host.GetType("RhiGhAI.Rhino.UI.RhiGhAISettingsDialog"),new object[]{service,null,null});
foreach(var field in dialog.GetType().GetFields(flags).Where(f=>f.FieldType.Name=="RhiGhAIButton")) {var button=field.GetValue(dialog);if(button!=null)CheckButton(button);}
dialog.Dispose();
// Actual native control render, not an operating-system screenshot. Omit account text in this artifact.
var footer=(Eto.Forms.Label)panel.GetType().GetField("_footer",flags).GetValue(panel);
var nativeFooter=footer.ControlObject;
var textProperty=nativeFooter.GetType().GetProperty("Text");
var footerText=textProperty.GetValue(nativeFooter);
textProperty.SetValue(nativeFooter,"Codex / ChatGPT / connected");
try {
 ((FrameworkElement)nativeFooter).InvalidateVisual();
 native.Dispatcher.Invoke((Action)(() => {}), System.Windows.Threading.DispatcherPriority.Render);
 native.UpdateLayout();
 int width=(int)Math.Ceiling(native.ActualWidth),height=(int)Math.Ceiling(native.ActualHeight);
 var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(native);
 var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
 using(var stream=System.IO.File.Create(System.IO.Path.Combine(rootPath,"UI-native-panel.png")))encoder.Save(stream);
 lines.Add("PASS actual panel native render "+width+"x"+height+" DIP at 96-DPI bitmap; this is not screen/DPI/input acceptance");
} finally {textProperty.SetValue(nativeFooter,footerText);}
var connection=service.GetType().GetProperty("Connection").GetValue(service);
lines.Add("Loaded "+host.GetName().Version+" from "+host.Location);
lines.Add("Connection ready="+connection.GetType().GetProperty("Ready").GetValue(connection));
System.IO.File.WriteAllLines(System.IO.Path.Combine(rootPath,"UI_NATIVE_PROBE.txt"),lines);
Console.WriteLine("UI_NATIVE_PROBE_OK");
