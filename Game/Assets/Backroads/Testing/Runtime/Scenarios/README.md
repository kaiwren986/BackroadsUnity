# Autotest scenarios by lane

Each lane keeps its player autotest kinds in its own subfolder here (`World/` is lane W, `EngineSim/` is lane V; see `OWNERS.json`).

A scenario is a class in the `Backroads.Testing` assembly that implements `IAutotestScenario`:

```csharp
public sealed class ShotsScenario : IAutotestScenario
{
    public string Kind => "shots";

    public IEnumerator Run(AutotestContext ctx)
    {
        var p = ctx.Params<MyParams>();   // fields of StreamingAssets/Autotest/<id>.json beyond the common ones
        yield return ctx.Screenshot("shot_000");
        ctx.Metric("chunks_ms", 812);
        if (bad) ctx.Fail("why");
    }
}
```

`AutotestRunner` finds it by `kind`, runs it, and writes `result.json` (status, failures, notes, evidence, metrics, frame-time percentiles, GPU name) and a contact sheet of every PNG in the evidence. The scenario JSON goes in `Game/Assets/StreamingAssets/Autotest/<id>.json`; `tools\ci\autotest.ps1 -Scenario <id>` runs it.
