using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

internal interface IReflectionPromptWindow
{
    Guid ReflectionId { get; }
    // Freeze editing and durably save the current draft; this never submits it.
    Task PrepareHandoffAsync();
    void ResumeEditing();
    void CloseAfterSave();
}

// Serialize both navigation and completion. Drafts must be durable before the
// host replaces the one visible editor; merely browsing never sends anything.
internal sealed class ReflectionPromptCoordinator(PreviewSession session,
    Func<IReadOnlyList<IReflectionPromptWindow>> openWindows, Func<Guid,bool,Task> show, Action queued)
{
    private readonly SemaphoreSlim gate = new(1,1);
    internal async Task OpenAsync(Guid id, bool activate, Func<bool>? stopping = null, bool sessionCompleted = false)
    {
        await gate.WaitAsync();
        try { await OpenCoreAsync(id,activate,stopping,sessionCompleted); }
        finally {gate.Release();}
    }
    internal async Task NavigateAsync(Guid from, int direction, Func<bool>? stopping = null)
    {
        if(direction is not -1 and not 1)throw new ArgumentException("Choose Prev or Next.");
        await gate.WaitAsync();
        try {
            var prompts=session.Engine.Snapshot.Prompts;
            var index=prompts.FindIndex(p=>p.Id==from);
            if(index<0||index+direction<0||index+direction>=prompts.Count)return;
            await OpenCoreAsync(prompts[index+direction].Id,true,stopping,false);
        } finally {gate.Release();}
    }
    // Ending and sending from a check-in leaves no new popup to open. Apply the
    // usual completion policy to older drafts without reopening the sent one.
    internal async Task CompleteSubmittedAsync(Guid id, Func<bool>? stopping = null)
    {
        await gate.WaitAsync();
        try {
            var state=session.Engine.Snapshot;
            var sent=state.Outbox.SingleOrDefault(o=>o.Id==id);
            if(stopping?.Invoke()==true||!state.AutoSendIncompleteReflections||sent is null||sent.IsCheckIn)return;
            await AutoSendPriorAsync(state.Prompts.Where(p=>!p.IsCheckIn&&p.IsTest==sent.IsTest&&p.CompletedAt<=sent.SubmittedAt.ToUnixTimeMilliseconds()),stopping);
        } finally {gate.Release();}
    }
    private async Task AutoSendPriorAsync(IEnumerable<ReflectionPrompt> prompts, Func<bool>? stopping)
    {
        foreach(var prior in prompts) {
            if(stopping?.Invoke()==true)return;
            var window=openWindows().FirstOrDefault(w=>w.ReflectionId==prior.Id);
            try {
                if(window is not null)await window.PrepareHandoffAsync();
                if(session.AutoSendReflection(prior.Id))queued();
                window?.CloseAfterSave();
            } catch {window?.ResumeEditing();throw;}
        }
    }
    private async Task OpenCoreAsync(Guid id,bool activate,Func<bool>? stopping,bool sessionCompleted)
    {
        if(stopping?.Invoke()==true)return;
        var state=session.Engine.Snapshot;
        var target=state.Prompts.SingleOrDefault(p=>p.Id==id);
        if(target is null)return;
        if(sessionCompleted&&!target.IsCheckIn&&state.AutoSendIncompleteReflections) {
            // Only earlier completed sessions, not the new prompt, future queued
            // arrivals, active check-ins, or reflections from a different test mode.
            await AutoSendPriorAsync(state.Prompts.TakeWhile(p=>p.Id!=id).Where(p=>!p.IsCheckIn&&p.IsTest==target.IsTest),stopping);
        }
        var prepared=new List<IReflectionPromptWindow>();
        try {
            foreach(var previous in openWindows().Where(w=>w.ReflectionId!=id).ToArray()) {
                prepared.Add(previous);
                await previous.PrepareHandoffAsync();
            }
            if(stopping?.Invoke()==true||!session.Engine.Snapshot.Prompts.Any(p=>p.Id==id))return;
            // The host preloads the target, then closes previous editors before
            // showing it. Failed preparation leaves the current editor available.
            await show(id,activate);
        } finally {foreach(var previous in prepared)previous.ResumeEditing();}
    }
    internal async Task ExclusivelyAsync(Func<Task> action)
    {
        await gate.WaitAsync();
        try {await action();} finally {gate.Release();}
    }
}
