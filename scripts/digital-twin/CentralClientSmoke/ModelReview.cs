using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using MesControlAgv.Wpf.DigitalTwin;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static class ModelReview
{
    // Diagnostic-only asset substitution and fixed camera, never a device source.
    public static int Run(string modelPath,string outputPath)
    {
        var bytes=File.ReadAllBytes(Path.GetFullPath(modelPath));
        var output=Path.GetFullPath(outputPath);Directory.CreateDirectory(output);
        var exit=1;var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Startup+=async(_,_)=>{
            Window? window=null;WebView2? browser=null;
            try{
                var packagedAssets=Path.Combine(AppContext.BaseDirectory,"DigitalTwin","Web");
                var assets=Path.Combine(output,"review-assets");
                foreach(var file in Directory.EnumerateFiles(packagedAssets,"*",SearchOption.AllDirectories)){
                    var target=Path.Combine(assets,Path.GetRelativePath(packagedAssets,file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target,overwrite:false);
                }
                File.WriteAllBytes(Path.Combine(assets,"lab606.glb"),bytes);
                // Highlight-only GLBs add a second primitive, making the device
                // a Group. Adapt only this copied diagnostic viewer's anchor.
                var scriptPath=Path.Combine(assets,"viewer.js");
                File.WriteAllText(scriptPath,File.ReadAllText(scriptPath).Replace(
                    "object.geometry.computeBoundingBox();\n    const box = object.geometry.boundingBox;",
                    "object.updateMatrixWorld(true); const box = new THREE.Box3().setFromObject(object,true).applyMatrix4(object.matrixWorld.clone().invert());")
                    .Replace("object.geometry.computeBoundingBox();\r\n    const box = object.geometry.boundingBox;",
                    "object.updateMatrixWorld(true); const box = new THREE.Box3().setFromObject(object,true).applyMatrix4(object.matrixWorld.clone().invert());"));
                File.AppendAllText(Path.Combine(assets,"viewer.js"),"\n"+ReviewCamera);
                var env=await CoreWebView2Environment.CreateAsync(userDataFolder:Path.Combine(output,"webview-profile"));
                browser=new WebView2();window=new Window{Content=browser,Width=1600,Height=1000,Left=-10000,Top=0,ShowActivated=false,ShowInTaskbar=false};
                window.Show();await browser.EnsureCoreWebView2Async(env);var core=browser.CoreWebView2;
                core.SetVirtualHostNameToFolderMapping(DigitalTwinScene.Host,assets,CoreWebView2HostResourceAccessKind.DenyCors);
                core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
                core.WebResourceRequested+=(_,e)=>{
                    if(!DigitalTwinScene.IsLocalResource(e.Request.Uri)&&!e.Request.Uri.StartsWith("blob:"))
                    {e.Response=env.CreateWebResourceResponse(Stream.Null,403,"Local only","");return;}
                };
                core.NewWindowRequested+=(_,e)=>e.Handled=true;
                core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
                core.NavigationStarting+=(_,e)=>e.Cancel=!DigitalTwinScene.IsViewerPage(e.Uri);
                core.Navigate(DigitalTwinScene.Page);
                var until=DateTime.UtcNow.AddSeconds(45);
                while(await core.ExecuteScriptAsync("window.digitalTwin?.inspect().ready===true")!="true")
                {if(DateTime.UtcNow>until)throw new TimeoutException("Diagnostic model did not load");await Task.Delay(100);}
                if(await core.ExecuteScriptAsync("typeof window.reviewView==='function'")!="true")throw new InvalidOperationException("Diagnostic resource substitution failed");
                foreach(var name in new[]{"overview","robot","box"}){
                    await core.ExecuteScriptAsync($"window.reviewView('{name}')");await Task.Delay(250);
                    using var file=File.Create(Path.Combine(output,name+".png"));await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
                }
                File.WriteAllText(Path.Combine(output,"scene.json"),await core.ExecuteScriptAsync("window.digitalTwin.inspect()"));
                Console.WriteLine("Model review captures: "+output);exit=0;
            }catch(Exception ex){Console.Error.WriteLine(ex);File.WriteAllText(Path.Combine(output,"error.txt"),ex.ToString());}
            finally{browser?.Dispose();window?.Close();app.Shutdown();}
        };app.Run();return exit;
    }
    private const string ReviewCamera="""
        window.reviewView=kind=>{
          model.traverse(o=>{if(o.isMesh&&o.material.name==='REMOVAL_REVIEW'){
            o.material.polygonOffset=true;o.material.polygonOffsetFactor=-2;o.material.polygonOffsetUnits=-2;
          }});
          document.getElementById('labels').hidden=true;
          const robot=objects.get('agv-composite-a');
          robot.position.set(21.4465,-.025,-4.6631);robot.rotation.set(0,-.01769265,0);robot.updateMatrixWorld(true);
          const views={overview:[[27,10,2],[17.8,.4,-6.8]],robot:[[18,2.2,-6.6],[15.98,.62,-8.58]],box:[[17.15,1.7,-4.85],[18.05,1.02,-6.55]]};
          const [from,target]=views[kind];controls.target.set(...target);camera.position.set(...from);camera.lookAt(controls.target);
          controls.update();scheduleRender();
        };
        """;
}
