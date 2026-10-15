using System.Reflection;
using System.Reflection.Emit;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;

namespace AirlineDemo.Api.Tests;

/// <summary>
/// Covers FR-003 / AC-002 (eligibility and approved wording), AC-005 (replay, conflict, atomicity) and AC-008.
/// Story criteria: AC-01 eligibility and baseline, AC-02 rejected-state outcomes, AC-03 event identity and atomicity,
/// AC-04 no external dispatch. Each test is tagged with the criterion it verifies.
/// </summary>
public class MockRequestDeliveryTests
{
    private const string Quote = "Return readiness is pending reserve approval.";
    private const string TypedValue = "pending reserve approval";
    private const string Text = "Return readiness is pending reserve approval.";
    private const string ScenarioTime = "2026-10-14T00:00:00Z";
    private const string BasisId = "basis-7";
    private const int BasisVersion = 3;

    private static readonly ProcessingCoverage Complete = new(Inventory: 2, Processed: 2, Failed: 0, Partial: 0);

    private static IReadOnlyDictionary<string, BasisDocument> Basis() => new Dictionary<string, BasisDocument>
    {
        ["return-status"] = BasisDocument.Create("return-status", [Quote, "Page two text."]),
    };

    private static ValidatedClaim Validated(ClaimKind kind, string quote = Quote)
    {
        var claim = new InvestigationClaim(Text, [new DocumentWitness("return-status", 1, quote)]);
        return ClaimValidator.Validate(claim, kind, Basis(), TypedValue);
    }

    private static DeliveryFinding Finding(
        IEnumerable<ValidatedClaim> claims,
        ProcessingCoverage? coverage = null,
        bool ambiguous = false,
        string basisId = BasisId,
        int basisVersion = BasisVersion) =>
        new(coverage ?? Complete, claims.ToArray(), ambiguous, basisId, basisVersion);

    private static DeliveryFinding Gap() => Finding([Validated(ClaimKind.MissingRequirement)]);

    private static DeliveryEnvelope Env(
        string eventId = "evt-1",
        string reportId = "rep-1",
        string missing = "a signed return-readiness record",
        int basisVersion = BasisVersion) =>
        new(eventId, "case-1", reportId, BasisId, basisVersion, ScenarioTime, "Engine", "ENG-123", missing);

    // AC-01: eligible gap delivers one request with approved wording; baseline creates none.
    [Fact]
    public void Clear_validated_gap_delivers_exactly_one_request_with_approved_wording()
    {
        var inbox = new MockRequestInbox();

        var result = inbox.Submit(Env(), Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
        Assert.Equal(201, result.HttpStatus);
        var receipt = Assert.Single(inbox.Requests);
        Assert.Equal(
            $"Synthetic demonstration request. For Engine ENG-123, please provide a signed return-readiness record. " +
            $"Evidence basis {BasisId} v{BasisVersion}. Reference {receipt.RequestId}.",
            receipt.Body);
        Assert.Equal(BasisId, receipt.BasisId);
        Assert.Equal(BasisVersion, receipt.BasisVersion);
        Assert.Equal(MockRequestTemplate.Version, receipt.TemplateVersion);
        Assert.Single(inbox.Audit);
    }

    [Fact]
    public void Complete_baseline_creates_no_request()
    {
        var inbox = new MockRequestInbox();

        var result = inbox.Submit(Env(), Finding([Validated(ClaimKind.Baseline)]), ScenarioTime);

        Assert.Equal(DeliveryOutcome.CompleteBaseline, result.Outcome);
        Assert.Empty(inbox.Requests);
        Assert.Empty(inbox.Audit);
    }

    // AC-02: each rejected state asserts its exact outcome; the complete eligible Gap() fixture is the control.
    [Theory]
    [InlineData("incomplete coverage", DeliveryOutcome.Blocked)]
    [InlineData("failed finding", DeliveryOutcome.Blocked)]
    [InlineData("stale basis", DeliveryOutcome.Review)]
    [InlineData("ambiguous identity", DeliveryOutcome.Review)]
    [InlineData("conflict", DeliveryOutcome.Review)]
    [InlineData("downgraded", DeliveryOutcome.Review)]
    [InlineData("two gaps", DeliveryOutcome.Review)]
    [InlineData("no supported claims", DeliveryOutcome.Blocked)]
    [InlineData("gap with unsupported claim", DeliveryOutcome.Review)]
    [InlineData("gap with baseline claim", DeliveryOutcome.Review)]
    public void Ambiguous_conflicting_incomplete_failed_unsupported_or_stale_findings_create_no_request(
        string scenario, DeliveryOutcome expected)
    {
        var inbox = new MockRequestInbox();
        var finding = scenario switch
        {
            "incomplete coverage" => Finding([Validated(ClaimKind.MissingRequirement)], new ProcessingCoverage(2, 1, 1, 0)),
            "failed finding" => Finding([Validated(ClaimKind.MissingRequirement)], new ProcessingCoverage(2, 2, 1, 0)),
            "stale basis" => Finding([Validated(ClaimKind.MissingRequirement)], basisVersion: BasisVersion + 1),
            "ambiguous identity" => Finding([Validated(ClaimKind.MissingRequirement)], ambiguous: true),
            "conflict" => Finding([Validated(ClaimKind.Conflict)]),
            "downgraded" => Finding([Validated(ClaimKind.MissingRequirement, "Return readiness is cleared.")]),
            "two gaps" => Finding([Validated(ClaimKind.MissingRequirement), Validated(ClaimKind.MissingRequirement)]),
            "gap with unsupported claim" => Finding(
                [Validated(ClaimKind.MissingRequirement), Validated(ClaimKind.Baseline, "Return readiness is cleared.")]),
            "gap with baseline claim" => Finding([Validated(ClaimKind.MissingRequirement), Validated(ClaimKind.Baseline)]),
            "no supported claims" => Finding([]),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };

        var result = inbox.Submit(Env(), finding, ScenarioTime);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Receipt);
        Assert.Empty(inbox.Requests);
        Assert.Empty(inbox.Audit);
    }

    // AC-03: exact replay returns the original receipt and leaves one request.
    [Fact]
    public void Exact_replay_returns_original_receipt_without_a_second_request()
    {
        var inbox = new MockRequestInbox();
        var first = inbox.Submit(Env(), Gap(), ScenarioTime);

        var replay = inbox.Submit(Env(), Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Replayed, replay.Outcome);
        Assert.Equal(200, replay.HttpStatus);
        Assert.Equal(first.Receipt, replay.Receipt);
        Assert.Single(inbox.Requests);
    }

    [Fact]
    public void Changed_envelope_under_same_event_id_is_rejected_with_409()
    {
        var inbox = new MockRequestInbox();
        inbox.Submit(Env(), Gap(), ScenarioTime);
        var auditBefore = inbox.Audit.Count;

        var changed = inbox.Submit(Env(missing: "a different record"), Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Conflict, changed.Outcome);
        Assert.Equal(409, changed.HttpStatus);
        Assert.Null(changed.Receipt);
        Assert.Single(inbox.Requests);
        Assert.Equal(auditBefore, inbox.Audit.Count);
    }

    [Fact]
    public void Envelopes_differing_only_by_unit_separator_inside_fields_are_not_exact_replays()
    {
        var inbox = new MockRequestInbox();
        var first = new DeliveryEnvelope("evt-1", "case-1", "rep-1", BasisId, BasisVersion, ScenarioTime,
            "Engine\u001fENG-123", "serial-x", "a signed return-readiness record");
        var second = new DeliveryEnvelope("evt-1", "case-1", "rep-1", BasisId, BasisVersion, ScenarioTime,
            "Engine", "ENG-123\u001fserial-x", "a signed return-readiness record");
        inbox.Submit(first, Gap(), ScenarioTime);

        var collided = inbox.Submit(second, Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Conflict, collided.Outcome);
        Assert.Equal(409, collided.HttpStatus);
        Assert.Null(collided.Receipt);
        Assert.Single(inbox.Requests);
    }

    [Fact]
    public void Reused_report_id_under_a_new_event_is_rejected_with_409()
    {
        var inbox = new MockRequestInbox();
        inbox.Submit(Env(eventId: "evt-1", reportId: "rep-1"), Gap(), ScenarioTime);
        var auditBefore = inbox.Audit.Count;

        var reused = inbox.Submit(Env(eventId: "evt-2", reportId: "rep-1"), Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Conflict, reused.Outcome);
        Assert.Equal(409, reused.HttpStatus);
        Assert.Null(reused.Receipt);
        Assert.Single(inbox.Requests);
        Assert.Equal(auditBefore, inbox.Audit.Count);
    }

    [Fact]
    public void Scenario_time_mismatch_is_rejected_with_400_before_acceptance()
    {
        var inbox = new MockRequestInbox();

        var result = inbox.Submit(Env(), Gap(), "2026-01-01T00:00:00Z");

        Assert.Equal(DeliveryOutcome.ScenarioTimeMismatch, result.Outcome);
        Assert.Equal(400, result.HttpStatus);
        Assert.Null(result.Receipt);
        Assert.Empty(inbox.Requests);
        Assert.Empty(inbox.Audit);
    }

    [Fact]
    public async Task Concurrent_replay_creates_exactly_one_request()
    {
        var inbox = new MockRequestInbox();

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => inbox.Submit(Env(), Gap(), ScenarioTime))));

        var delivered = results.Single(r => r.Outcome == DeliveryOutcome.Delivered);
        Assert.Equal(1, results.Count(r => r.Outcome == DeliveryOutcome.Delivered));
        Assert.Equal(31, results.Count(r => r.Outcome == DeliveryOutcome.Replayed));
        Assert.All(results, r => Assert.Equal(delivered.Receipt, r.Receipt));
        Assert.Single(inbox.Requests);
        Assert.Single(inbox.Audit);
    }

    [Fact]
    public void Injected_write_failure_leaves_no_partial_receipt_or_audit_and_retry_delivers_once()
    {
        var inbox = new MockRequestInbox { InjectWriteFailure = true };

        Assert.Throws<InvalidOperationException>(() => inbox.Submit(Env(), Gap(), ScenarioTime));
        Assert.Empty(inbox.Requests);
        Assert.Empty(inbox.Audit);

        inbox.InjectWriteFailure = false;
        var retry = inbox.Submit(Env(), Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Delivered, retry.Outcome);
        Assert.Single(inbox.Requests);
        Assert.Single(inbox.Audit);
    }

    [Fact]
    public void Correction_under_new_event_id_is_a_new_decision_and_does_not_inherit_approval()
    {
        var inbox = new MockRequestInbox();
        inbox.Submit(Env(eventId: "evt-1", reportId: "rep-1"), Gap(), ScenarioTime);

        var correction = inbox.Submit(Env(eventId: "evt-2", reportId: "rep-2", missing: "a corrected record"), Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Delivered, correction.Outcome);
        Assert.Equal(2, inbox.Requests.Count);
        Assert.NotEqual(inbox.Requests[0].RequestId, inbox.Requests[1].RequestId);
    }

    [Fact]
    public void Envelopes_differing_only_by_unpaired_surrogates_are_not_exact_replays()
    {
        var inbox = new MockRequestInbox();
        var first = new DeliveryEnvelope("evt-1", "case-1", "rep-1", BasisId, BasisVersion, ScenarioTime,
            "Engine\uD800", "ENG-123", "a signed return-readiness record");
        var second = new DeliveryEnvelope("evt-1", "case-1", "rep-1", BasisId, BasisVersion, ScenarioTime,
            "Engine\uD801", "ENG-123", "a signed return-readiness record");
        inbox.Submit(first, Gap(), ScenarioTime);

        var collided = inbox.Submit(second, Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Conflict, collided.Outcome);
        Assert.Equal(409, collided.HttpStatus);
        Assert.Null(collided.Receipt);
        Assert.Single(inbox.Requests);
    }

    // AC-04: delivery persists only through the in-memory mock inbox; no external dispatch.
    [Fact]
    public void Delivery_has_no_external_messaging_dependency()
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var apiAssembly = typeof(MockRequestInbox).Assembly;
        var roots = new[] { typeof(MockRequestInbox), typeof(MockDeliveryPolicy), typeof(MockRequestTemplate) }
            .SelectMany(t => t.GetMembers(All).OfType<MethodBase>());

        var (violations, visited) = WalkDispatchGraph(roots, apiAssembly);

        Assert.Empty(violations);
        Assert.Contains(visited, m => m.Name == nameof(MockRequestInbox.Submit));

        var result = new MockRequestInbox().Submit(Env(), Gap(), ScenarioTime);
        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
    }

    [Fact]
    public void No_dispatch_walk_reports_each_forbidden_construct_it_reaches()
    {
        var testAssembly = typeof(MockRequestDeliveryTests).Assembly;

        Assert.Empty(WalkDispatchGraph(new[] { ((Action)PlainHelperCaller).Method }, testAssembly).Violations);
        Assert.NotEmpty(WalkDispatchGraph(new[] { ((Action<IFakeSender>)InterfaceCaller).Method }, testAssembly).Violations);
        Assert.NotEmpty(WalkDispatchGraph(new[] { ((Action)AsyncCaller).Method }, testAssembly).Violations);
        Assert.NotEmpty(WalkDispatchGraph(new[] { ((Action)ReflectionCaller).Method }, testAssembly).Violations);
        Assert.NotEmpty(WalkDispatchGraph(new[] { ((Action)ProcessCaller).Method }, testAssembly).Violations);

        Assert.Contains(WalkDispatchGraph(new[] { ((Action)PInvokeCaller).Method }, testAssembly).Violations, v => v.Contains("native", StringComparison.Ordinal));
        Assert.Contains(WalkDispatchGraph(new[] { ((Action<Func<string>>)DelegateInvokeCaller).Method }, testAssembly).Violations, v => v.Contains("reflective or dynamic invocation", StringComparison.Ordinal));
        Assert.Contains(WalkDispatchGraph(new[] { ((Action<Action>)DelegateDynamicInvokeCaller).Method }, testAssembly).Violations, v => v.Contains("DynamicInvoke", StringComparison.Ordinal));
        Assert.Contains(WalkDispatchGraph(new[] { EmitCalliCaller() }, testAssembly).Violations, v => v.Contains("calli", StringComparison.Ordinal));
    }

    [Fact]
    public void No_dispatch_walk_reports_concrete_virtual_overrides()
    {
        var testAssembly = typeof(MockRequestDeliveryTests).Assembly;

        var declared = WalkDispatchGraph(new[] { ((Action<FakeVirtualBase>)VirtualCaller).Method }, testAssembly).Violations;
        Assert.Contains(declared, v => v.Contains("dispatches to API interface/abstract/virtual", StringComparison.Ordinal) && v.Contains(nameof(FakeVirtualBase.Run), StringComparison.Ordinal));

        var overridingFramework = WalkDispatchGraph(new[] { ((Action<object>)FrameworkVirtualCaller).Method }, testAssembly).Violations;
        Assert.Contains(overridingFramework, v => v.Contains("dispatches to API interface/abstract/virtual System.Object.ToString", StringComparison.Ordinal));
    }

    private class FakeVirtualBase { public virtual void Run() { } }
    private sealed class FakeToStringOverride { public override string ToString() => "x"; }

    private static void VirtualCaller(FakeVirtualBase value) => value.Run();
    private static void FrameworkVirtualCaller(object value) => _ = value.ToString();

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getpid")]
    private static extern int NativeGetPid();

    private static void PInvokeCaller() => _ = NativeGetPid();
    private static void DelegateInvokeCaller(Func<string> callback) => _ = callback();
    private static void DelegateDynamicInvokeCaller(Action callback) => callback.DynamicInvoke();

    /// <summary>Emits a static method that loads a function pointer and calls it with <c>calli</c>, which C# cannot produce without unsafe code.</summary>
    private static MethodInfo EmitCalliCaller()
    {
        var module = AssemblyBuilder
            .DefineDynamicAssembly(new AssemblyName("CalliFixture"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("CalliFixture");
        var type = module.DefineType("CalliCaller", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var method = type.DefineMethod("Call", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldftn, typeof(GC).GetMethod(nameof(GC.Collect), Type.EmptyTypes)!);
        il.EmitCalli(OpCodes.Calli, CallingConventions.Standard, typeof(void), Type.EmptyTypes, null);
        il.Emit(OpCodes.Ret);
        return type.CreateType().GetMethod("Call")!;
    }

    private static void PlainHelperCaller() => _ = string.Empty.Trim();
    private static void InterfaceCaller(IFakeSender sender) => sender.Send("x");
    private static void AsyncCaller() => _ = AsyncHelper();
    private static async System.Threading.Tasks.Task AsyncHelper() => await System.Threading.Tasks.Task.Yield();
    private static void ReflectionCaller() => _ = Activator.CreateInstance(typeof(object));
    private static void ProcessCaller() => System.Diagnostics.Process.Start("x");

    /// <summary>Walks the IL call graph from <paramref name="roots"/> and collects every forbidden construct it reaches, plus the methods visited.</summary>
    private static (List<string> Violations, HashSet<MethodBase> Visited) WalkDispatchGraph(IEnumerable<MethodBase> roots, Assembly apiAssembly)
    {
        var violations = new List<string>();
        var visited = new HashSet<MethodBase>();
        var pending = new Queue<MethodBase>();
        foreach (var root in roots)
        {
            if (visited.Add(root)) pending.Enqueue(root);
        }

        while (pending.Count > 0)
        {
            var method = pending.Dequeue();
            var where = $"{method.DeclaringType?.FullName}.{method.Name}";
            if (IsNativeOrBodylessApiMethod(method, apiAssembly)) violations.Add($"{where} is native or has no IL body");
            if (IsStateMachineMethod(method)) violations.Add($"{where} is async or an iterator; its generated state machine is not walked");

            foreach (var callee in CalledMethods(method))
            {
                if (callee is null)
                {
                    violations.Add($"{where} uses calli (indirect call through a function pointer)");
                    continue;
                }
                var owner = callee.DeclaringType;
                var ownerAssembly = owner?.Assembly.GetName().Name ?? string.Empty;
                if (owner?.Assembly != apiAssembly && !AllowedFrameworkAssemblies.Contains(ownerAssembly))
                    violations.Add($"{where} calls {owner?.FullName}.{callee.Name} in {ownerAssembly}");
                if (IsNativeOrBodylessApiMethod(callee, apiAssembly))
                    violations.Add($"{where} calls native or bodyless {owner?.FullName}.{callee.Name}");
                if (IsReflectiveOrDynamicInvocation(callee))
                    violations.Add($"{where} uses reflective or dynamic invocation via {owner?.FullName}.{callee.Name}");
                if (IsUnresolvedApiDispatch(callee, apiAssembly))
                    violations.Add($"{where} dispatches to API interface/abstract/virtual {owner?.FullName}.{callee.Name}; its implementations are not visible to the IL walk");
                if (owner?.Assembly == apiAssembly && visited.Add(callee)) pending.Enqueue(callee);
            }
        }

        return (violations, visited);
    }

    /// <summary>True for async and iterator methods, whose bodies live in compiler-generated state machines the IL walk does not follow.</summary>
    private static bool IsStateMachineMethod(MethodBase method) =>
        method.IsDefined(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute), false)
        || method.IsDefined(typeof(System.Runtime.CompilerServices.IteratorStateMachineAttribute), false);

    /// <summary>Framework assemblies a delivery path may use for pure computation. Anything else (Process, sockets, HTTP, third-party SDKs) fails the test.</summary>
    private static readonly HashSet<string> AllowedFrameworkAssemblies = new(StringComparer.Ordinal)
    {
        "System.Private.CoreLib", "System.Runtime", "System.Runtime.Extensions", "System.Collections",
        "System.Collections.Concurrent", "System.Linq", "System.Memory", "System.Threading",
        "System.Threading.Tasks", "System.Text.RegularExpressions", "System.Text.Json", "System.Security.Cryptography",
    };

    private static readonly Dictionary<int, OpCode> OpCodeByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(op => (int)(ushort)op.Value);

    /// <summary>True for P/Invoke methods anywhere, and for concrete API methods with no IL body (extern or runtime-implemented); such methods are opaque to the IL walk.</summary>
    private static bool IsNativeOrBodylessApiMethod(MethodBase method, Assembly apiAssembly)
    {
        if (method.Attributes.HasFlag(MethodAttributes.PinvokeImpl)) return true;
        return method.DeclaringType?.Assembly == apiAssembly
            && !method.IsAbstract
            && method.GetMethodBody() is null;
    }

    private interface IFakeSender { void Send(string message); }

    [Fact]
    public void No_external_messaging_guards_flag_reflection_and_interface_dispatch()
    {
        var testAssembly = typeof(MockRequestDeliveryTests).Assembly;

        Assert.True(IsReflectiveOrDynamicInvocation(typeof(MethodBase).GetMethod(nameof(MethodBase.Invoke), new[] { typeof(object), typeof(object[]) })!));
        Assert.True(IsReflectiveOrDynamicInvocation(typeof(Activator).GetMethod(nameof(Activator.CreateInstance), Type.EmptyTypes)!));
        Assert.True(IsReflectiveOrDynamicInvocation(typeof(Delegate).GetMethod(nameof(Delegate.DynamicInvoke))!));
        Assert.True(IsReflectiveOrDynamicInvocation(typeof(Func<string>).GetMethod("Invoke")!));
        Assert.False(IsReflectiveOrDynamicInvocation(typeof(string).GetMethod(nameof(string.Trim), Type.EmptyTypes)!));

        var send = typeof(IFakeSender).GetMethod(nameof(IFakeSender.Send))!;
        Assert.True(IsUnresolvedApiDispatch(send, testAssembly));
        Assert.False(IsUnresolvedApiDispatch(send, typeof(MockRequestInbox).Assembly));

        var hashCode = typeof(object).GetMethod(nameof(GetHashCode), Type.EmptyTypes)!;
        Assert.True(IsUnresolvedApiDispatch(hashCode, typeof(DeliveryEnvelope).Assembly));
        Assert.False(IsUnresolvedApiDispatch(typeof(string).GetMethod(nameof(string.Trim), Type.EmptyTypes)!, typeof(DeliveryEnvelope).Assembly));
    }

    /// <summary>True for calls that can invoke arbitrary code without a visible call edge: reflection, Activator, dynamic delegate invocation, and marshalled function pointers.</summary>
    private static bool IsReflectiveOrDynamicInvocation(MethodBase callee)
    {
        var owner = callee.DeclaringType;
        if (owner is null) return false;
        var ns = owner.Namespace ?? string.Empty;
        return ns == "System.Reflection" || ns.StartsWith("System.Reflection.", StringComparison.Ordinal)
            || owner == typeof(System.Runtime.InteropServices.Marshal)
            || owner == typeof(System.Runtime.InteropServices.NativeLibrary)
            || ns == "System.Runtime.Loader"
            || owner == typeof(Type) || owner.IsSubclassOf(typeof(Type))
            || owner == typeof(Activator)
            || owner == typeof(AppDomain)
            || (typeof(Delegate).IsAssignableFrom(owner) && callee.Name is "Invoke" or "DynamicInvoke" or "BeginInvoke" or "CreateDelegate");
    }

    /// <summary>True for calls whose concrete target is chosen at runtime (interface, abstract, or virtual dispatch) and that the IL walk therefore cannot see: API-declared interface/abstract/virtual methods, and any virtual method the API overrides.</summary>
    private static bool IsUnresolvedApiDispatch(MethodBase callee, Assembly apiAssembly)
    {
        if (callee.DeclaringType?.Assembly == apiAssembly
            && (callee.DeclaringType.IsInterface || callee.IsAbstract || callee.IsVirtual))
            return true;
        return callee is MethodInfo { IsVirtual: true } virtualMethod && OverriddenInApi(virtualMethod, apiAssembly);
    }

    /// <summary>True when some API type declares a virtual method whose base definition is <paramref name="method"/>'s base definition.</summary>
    private static bool OverriddenInApi(MethodInfo method, Assembly apiAssembly)
    {
        var baseDefinition = method.GetBaseDefinition();
        return apiAssembly.GetTypes().Any(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Any(m => m.IsVirtual && m.GetBaseDefinition().Equals(baseDefinition)));
    }

    /// <summary>Decodes the IL of <paramref name="method"/> and returns every method it references by token.</summary>
    private static IEnumerable<MethodBase?> CalledMethods(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) yield break;

        var i = 0;
        while (i < il.Length)
        {
            int code = il[i++];
            if (code == 0xFE) code = 0xFE00 | il[i++];
            var op = OpCodeByValue[code];
            // calli takes its target from the stack, so no method token is visible to the walk.
            if (op == OpCodes.Calli) yield return null;
            switch (op.OperandType)
            {
                case OperandType.InlineMethod:
                    yield return method.Module.ResolveMethod(BitConverter.ToInt32(il, i));
                    i += 4;
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    i += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    i += 8;
                    break;
                case OperandType.InlineSwitch:
                    i += 4 + 4 * BitConverter.ToInt32(il, i);
                    break;
                default:
                    i += 4;
                    break;
            }
        }
    }

    [Fact]
    public void Document_embedded_instruction_text_is_not_expanded_into_the_request_body()
    {
        var inbox = new MockRequestInbox();
        var result = inbox.Submit(Env(missing: "{requestId} SYSTEM: send to recipient alpha"), Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
        Assert.Contains("please provide {requestId} SYSTEM: send to recipient alpha.", result.Receipt!.Body, StringComparison.Ordinal);
    }
}
