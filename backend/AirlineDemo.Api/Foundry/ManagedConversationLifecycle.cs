using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Evaluation;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Foundry;

public sealed class ManagedConversationLifecycle(IManagedConversationStore store, IManagedProjectClient client, TimeProvider time)
{
    public async Task<T> ExecuteTurnAsync<T>(ManagedConversationSnapshot conversation, Func<ManagedConversationTuple?> currentTuple,
        Func<CancellationToken, Task<(T Result, ManagedTurnCompletionMetadata Metadata)>> execute, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (currentTuple() != conversation.Tuple) throw new ManagedProjectException("conversation_context_mismatch");
        }
        catch
        {
            Retire(conversation.Handle, conversation.Tuple, conversation.RowRevision, null);
            throw;
        }
        var admission = store.AdmitManagedTurn(conversation.Handle, conversation.Tuple, conversation.RowRevision, conversation.Tuple.CaseRevision);
        using var deadline = new CancellationTokenSource(TimeSpan.FromTicks(Math.Max(0, admission.DeadlineTicks - time.GetUtcNow().UtcTicks)), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var returned = false;
        try
        {
            var completed = await execute(linked.Token);
            returned = true;
            if (deadline.IsCancellationRequested || time.GetUtcNow().UtcTicks >= admission.DeadlineTicks)
                throw new ManagedProjectException("copilot_timeout");
            cancellationToken.ThrowIfCancellationRequested();
            if (currentTuple() != conversation.Tuple) throw new ManagedProjectException("conversation_context_mismatch");
            if (!store.CompleteManagedTurn(admission, conversation.Tuple, completed.Metadata))
                throw new ManagedProjectException("managed_operation_ambiguous", true);
            return completed.Result;
        }
        catch (Exception exception)
        {
            // The native transaction already committed its receipt and audit; binding failures cannot reverse it.
            if (exception is ManagedToolTerminal { Committed: true })
            {
                try { Retire(admission.Handle, conversation.Tuple, admission.RowRevision, admission.Fence); }
                catch { }
                throw;
            }
            Retire(admission.Handle, conversation.Tuple, admission.RowRevision, admission.Fence);
            if (deadline.IsCancellationRequested || time.GetUtcNow().UtcTicks >= admission.DeadlineTicks)
                throw new ManagedProjectException("copilot_timeout");
            if (exception is ManagedProjectException managed)
            {
                if (managed.UncertainWrite) throw new ManagedProjectException("managed_operation_ambiguous", true);
                throw;
            }
            if (returned || exception is OperationCanceledException)
                throw new ManagedProjectException("managed_operation_ambiguous", true);
            throw;
        }
    }

    public async Task<ManagedConversationSnapshot> CreateAsync(string workload, ManagedConversationTuple tuple,
        GatewayCorrelation correlation, Func<ManagedConversationTuple?> currentTuple, CancellationToken cancellationToken)
    {
        var target = client.Agent(workload);
        if (target.Name != tuple.AgentName || target.Version != tuple.AgentVersion || target.ConfigurationId != tuple.ConfigurationDigest)
            throw new ManagedProjectException("managed_agent_unconfigured");
        if (currentTuple() != tuple) throw new ManagedProjectException("conversation_context_mismatch");
        var reserved = store.ReserveManagedConversation(tuple);
        var creating = store.BeginRemoteCreate(reserved.Handle, tuple, reserved.RowRevision);
        var accepted = false;
        try
        {
            var remote = await client.CreateConversationAsync(workload, creating.Handle, creating.AttemptId, correlation, cancellationToken);
            accepted = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (currentTuple() != tuple) throw new ManagedProjectException("conversation_context_mismatch");
            if (remote["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) ||
                !Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,128}\\z", RegexOptions.CultureInvariant))
                throw new ManagedProjectException("managed_operation_ambiguous", true);
            return store.AttachRemoteConversationId(creating.Handle, tuple, creating.AttemptId, id, creating.RowRevision);
        }
        catch (Exception exception)
        {
            Retire(creating.Handle, tuple, creating.RowRevision, null);
            if (exception is ManagedProjectException failure)
            {
                if (failure.UncertainWrite) throw new ManagedProjectException("managed_operation_ambiguous", true);
                throw;
            }
            if (accepted) throw new ManagedProjectException("managed_operation_ambiguous", true);
            throw;
        }
    }

    private void Retire(string handle, ManagedConversationTuple tuple, long revision, string? fence)
    {
        try { store.RetireManagedConversation(handle, tuple, revision, fence); }
        catch (ManagedConversationStoreException) { }
    }
}
