using System.Collections;
using System.Collections.Generic;
using BepInEx;
using UnityEngine;
using valheimCLI;
using valheimCLI.Extensions;

[BepInPlugin("testing.cli.reload-probe", "CLI Reload Probe", "0.1.0")]
public sealed class ReloadProbe : BaseUnityPlugin
{
    private ExtensionRegistration? _registration;
    private IEnumerator Start()
    {
        float deadline = Time.realtimeSinceStartup + 30;
        while (valheimCLIPlugin.Instance?.Extensions == null)
        {
            if (Time.realtimeSinceStartup > deadline) { Logger.LogError("Stable CLI core is not available."); yield break; }
            yield return null;
        }
        _registration = valheimCLIPlugin.Instance.Extensions.Register("example.probe", "0.1.0", 1,
            new ExtensionCommand("hello", "Read an example structured observation", Hello, readOnly: true));
    }
    private static IEnumerator Hello(ExtensionContext context)
    {
        context.Succeed(new Dictionary<string, object?> { ["source"] = "reload-probe", ["complete"] = true, ["value"] = "A" });
        yield break;
    }
    private void OnDestroy() => _registration?.Dispose();
}
