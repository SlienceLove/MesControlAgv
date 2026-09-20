using Microsoft.Web.WebView2.Core;

internal static class ReferenceVisibilityCheck
{
    public static async Task CheckAsync(CoreWebView2 core,bool enabled)
    {
        var expected=enabled?"true":"false";
        var expression=$$"""
            (()=>{
              const enabled={{expected}},g=window.digitalTwin.inspect().ground;
              const visible=id=>!document.getElementById(id).hidden;
              return g.referenceEnabled===enabled &&
                ['source','hint','ground-status','map-reference'].every(id=>visible(id)===enabled) &&
                g.overlayVisible===(enabled&&g.enabled&&(g.aligned||g.schematic)) &&
                g.routesVisible===(enabled&&g.enabled&&g.routesEnabled&&(g.aligned||g.schematic)) &&
                (enabled||g.stationLabelsVisible===0) &&
                Array.from(document.querySelectorAll('.device-label')).every(e=>!e.hidden);
            })()
            """;
        var until=DateTime.UtcNow.AddSeconds(10);
        while(await core.ExecuteScriptAsync(expression)!="true")
        {
            if(DateTime.UtcNow>until)throw new TimeoutException("Reference master visibility or device labels incorrect: "+enabled);
            await Task.Delay(50);
        }
    }
}
