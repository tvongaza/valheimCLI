using System.Collections;
using System.Text.Json;
using valheimCLI.Extensions;
using Xunit;

namespace valheimCLI.Tests;

public class ExtensionRegistryTests
{
    private static ExtensionRegistry Registry(OperationGate? gate = null, Func<ExtensionCommand, string?>? check = null) => new(gate ?? new(), check ?? (_ => null));
    private static IEnumerator Done(ExtensionContext context) { context.Succeed(new Dictionary<string, object?> { ["count"] = 7 }); yield break; }
    private static ExtensionCommand Command(string name = "query", Func<ExtensionContext, IEnumerator>? run = null, bool readOnly = false) => new(name, "test", run ?? Done, readOnly);
    [Fact] public void DuplicateCommandRegistrationIsAtomic()
    {
        using var registry = Registry();
        Assert.Throws<ArgumentException>(() => registry.Register("roads", "1", 1, Command(), Command()));
        Assert.Empty(registry.Registrations);
        registry.Register("roads", "1", 1, Command());
    }
    [Fact] public void UnknownApiIsRefused() { using var r = Registry(); Assert.Throws<NotSupportedException>(() => r.Register("roads", "1", 2)); }
    [Fact] public void LateDisposeCannotUnregisterNewOwner()
    {
        using var r = Registry(); var old = r.Register("roads", "1", 1, Command()); old.Dispose();
        var current = r.Register("roads", "2", 1, Command()); old.Dispose(); Assert.Same(current, Assert.Single(r.Registrations));
    }
    [Fact] public void QueuedHandlerCannotRunAfterUnload()
    {
        using var r = Registry(); bool ran = false; ExtensionResult? result = null;
        var owner = r.Register("roads", "1", 1, Command(run: c => { ran = true; return Done(c); }));
        r.Begin("roads/query", [], 1, () => false, x => result = x); owner.Dispose(); r.Tick();
        Assert.False(ran); Assert.Equal("extension_unloaded", result!.Code); Assert.Empty(r.Registrations);
    }
    [Fact] public void MutationWaitsForExistingCoreOperation()
    {
        var gate = new OperationGate(); using var r = Registry(gate); gate.TryAcquire(99, "teleport");
        r.Register("roads", "1", 1, Command()); ExtensionResult? result = null;
        r.Begin("roads/query", [], 1, () => false, x => result = x); r.Tick(); Assert.Null(result);
        gate.Release(99); r.Tick(); Assert.True(result!.Ok); Assert.True(gate.IsFree);
    }
    [Fact] public void CancellationHoldsGateAndOwnerUntilEffectsSettle()
    {
        bool settled = false, abandoned = false, cleaned = false; var gate = new OperationGate(); using var r = Registry(gate);
        IEnumerator Run(ExtensionContext c) { c.WaitForQuiescence(() => settled); while (true) yield return null; }
        var owner = r.Register("roads", "1", 1, Command(run: Run)); owner.OnDispose(() => cleaned = true);
        ExtensionResult? result = null; r.Begin("roads/query", [], 1, () => abandoned, x => result = x); r.Tick();
        abandoned = true; owner.Dispose(); r.Tick(); Assert.False(gate.IsFree); Assert.False(cleaned); Assert.Null(result);
        Assert.Throws<InvalidOperationException>(() => r.Register("roads", "2", 1));
        settled = true; r.Tick(); Assert.True(gate.IsFree); Assert.True(cleaned); Assert.Equal("extension_unloaded", result!.Code);
    }
    [Fact] public void ThrowingHandlerCleanupCannotReleaseTheGateOrRunTwice()
    {
        var gate = new OperationGate(); using var r = Registry(gate); bool cancel = false; int disposals = 0;
        IEnumerator Run(ExtensionContext context)
        {
            try { while (true) yield return null; }
            finally { disposals++; throw new InvalidOperationException("restore failed"); }
        }
        var owner = r.Register("roads", "1", 1, Command(run: Run));
        r.Begin("roads/query", [], 1, () => cancel, _ => {}); r.Tick(); cancel = true; owner.Dispose(); r.Tick(); r.Tick();
        Assert.Equal(1, disposals); Assert.False(gate.IsFree); Assert.Contains("restart required", owner.CleanupError);
        Assert.Throws<InvalidOperationException>(() => r.Register("roads", "2", 1));
    }
    [Fact] public void CleanupFaultBlocksReplacement()
    {
        using var r = Registry(); var owner = r.Register("roads", "1", 1); owner.OnDispose(() => throw new Exception("restore failed")); owner.Dispose();
        Assert.Contains("restore failed", owner.CleanupError); Assert.Throws<InvalidOperationException>(() => r.Register("roads", "2", 1));
    }
    [Fact] public void ThrowingReplyIsOnlyInvokedOnce()
    {
        using var r = Registry(); var owner = r.Register("roads", "1", 1, Command()); int replies = 0;
        r.Begin("roads/query", [], 1, () => false, _ => { replies++; throw new Exception("transport gone"); }); r.Tick(); owner.Dispose();
        Assert.Equal(1, replies); Assert.Empty(r.Registrations);
    }
    [Fact] public void PreconditionIsCheckedAfterGateBecomesAvailable()
    {
        var gate = new OperationGate(); string? refusal = null; using var r = Registry(gate, _ => refusal);
        r.Register("roads", "1", 1, Command()); gate.TryAcquire(99, "teleport"); ExtensionResult? result = null;
        r.Begin("roads/query", [], 1, () => false, x => result = x); r.Tick(); refusal = "world unloaded"; gate.Release(99); r.Tick();
        Assert.Equal("extension_precondition", result!.Code); Assert.True(gate.IsFree);
    }
    [Fact] public void MissingResultIsAnError()
    {
        IEnumerator Run(ExtensionContext c) { yield break; }
        using var r = Registry(); r.Register("roads", "1", 1, Command(run: Run)); ExtensionResult? result = null;
        r.Begin("roads/query", [], 1, () => false, x => result = x); r.Tick(); Assert.Equal("missing_result", result!.Code);
    }
    [Fact] public void OffThreadRegistrationIsRejected()
    {
        using var r = Registry(); Exception? error = null; var t = new Thread(() => { try { r.Register("roads", "1", 1); } catch (Exception ex) { error = ex; } });
        t.Start(); t.Join(); Assert.IsType<InvalidOperationException>(error);
    }
    [Fact] public void ResultsAreStructuredAndEscaped()
    {
        var result = new ExtensionResult(true, "", "line\n\"\\", new Dictionary<string, object?> { ["point"] = new[] { 1.0, 2.0 }, ["count"] = 7 });
        using var json = JsonDocument.Parse(ExtensionJson.Result(result)); Assert.Equal(7, json.RootElement.GetProperty("data").GetProperty("count").GetInt32());
        Assert.Equal(result.Message, json.RootElement.GetProperty("message").GetString());
    }
    [Fact] public void UnsupportedAndRecursiveResultsFail()
    {
        Assert.Throws<ArgumentException>(() => ExtensionJson.Write(new object())); Assert.Throws<ArgumentException>(() => ExtensionJson.Write(double.NaN));
        var cycle = new List<object>(); cycle.Add(cycle); Assert.Throws<ArgumentException>(() => ExtensionJson.Write(cycle));
    }
    [Fact] public void CleanupRunsInReverseEvenIfOneFails()
    {
        using var r = Registry(); var owner = r.Register("roads", "1", 1); var order = new List<int>();
        owner.OnDispose(() => order.Add(1)); owner.OnDispose(() => { order.Add(2); throw new Exception("failed"); }); owner.Dispose();
        Assert.Equal(new[] { 2, 1 }, order);
    }
}
