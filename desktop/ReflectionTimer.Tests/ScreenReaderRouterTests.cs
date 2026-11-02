using ReflectionTimer.Accessible;

// Fake outputs only. No installed reader, actual speech or user profile.
internal static class ScreenReaderRouterTests
{
    internal static void Run(Action<bool,string> check)
    {
        var context=SynchronizationContext.Current;
        var auto=WindowsFormsSynchronizationContext.AutoInstall;
        try {
            WindowsFormsSynchronizationContext.AutoInstall=false;
            SynchronizationContext.SetSynchronizationContext(null);
            RunAsync(check).GetAwaiter().GetResult();
        } finally {
            WindowsFormsSynchronizationContext.AutoInstall=auto;
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private static async Task RunAsync(Action<bool,string> check)
    {
        using var owner=new Control();
        var calls=new List<string>();
        var accepts=false;var throws=false;
        var jaws=true;var windows=true;var safe=true;
        Task<bool> Nvda(string text,bool extra) {
            calls.Add("NVDA:"+text+":"+extra);
            if(throws)throw new IOException("Synthetic unavailable reader");
            return Task.FromResult(accepts);
        }
        var router=new ScreenReaderAnnouncementRouter((text,_)=>{calls.Add("JAWS:"+text);return jaws;},
            (_,text,_)=>{calls.Add("UIA:"+text);return windows;},Nvda,()=>safe,
            text=>{calls.Add("NVDA notice:"+text);return Task.FromResult(true);});
        accepts=true;
        check(await router.TryAnnounceAsync(owner,"Paused",false)&&calls.SequenceEqual(new[]{"NVDA:Paused:False"}),
            "A running NVDA receives feedback before JAWS or UIA without a second notification");
        calls.Clear();
        check(await router.TryAnnounceAsync(owner,"Settings saved",true,true)&&calls.SequenceEqual(new[]{"NVDA notice:Settings saved"}),
            "General confirmations use NVDA's separate notice queue rather than replacing playback or view messages");
        calls.Clear();
        await router.TryAnnounceAsync(owner,"Ready",false);await router.TryAnnounceAsync(owner,"Ready",false);
        check(calls.Count==2&&calls.All(c=>c=="NVDA:Ready:False"),"Repeated requested status stays announceable through the NVDA route");
        calls.Clear();accepts=false;
        check(await router.TryAnnounceAsync(owner,"Resumed",false)&&calls.SequenceEqual(new[]{"NVDA:Resumed:False","JAWS:Resumed"}),
            "An unavailable NVDA permits the existing JAWS route exactly once");
        calls.Clear();jaws=false;
        check(await router.TryAnnounceAsync(owner,"Focus on",false)&&calls.SequenceEqual(new[]{"NVDA:Focus on:False","JAWS:Focus on","UIA:Focus on"}),
            "Readers without direct APIs retain standard Windows notifications");
        calls.Clear();windows=false;
        check(!await router.TryAnnounceAsync(owner,"Fallback",true)&&calls.Count==3,"Declined reader routes permit one visible Web live-region fallback");
        calls.Clear();throws=true;windows=true;
        check(await router.TryAnnounceAsync(owner,"Recovered",false)&&calls.Last()=="UIA:Recovered","A failed NVDA call cannot suppress other readers");
        calls.Clear();throws=false;safe=false;
        check(await router.TryAnnounceAsync(owner,"Private status",false)&&calls.Count==0,"Secure desktop suppression does not call any reader or leak a DOM fallback");
        safe=true;
        var pending=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        router=new((_,_)=>{calls.Add("JAWS");return true;},(_,_,_)=>false,(_,_)=>pending.Task,()=>safe);
        var request=router.TryAnnounceAsync(owner,"Timer paused",false);
        check(!request.IsCompleted&&calls.Count==0,"Waiting for a reader does not block the caller or prematurely notify a second reader");
        pending.SetResult(true);check(await request&&calls.Count==0,"An asynchronously accepted NVDA request never reaches the fallback reader");
        pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        request=router.TryAnnounceAsync(owner,"Older status",false);pending.SetCanceled();
        check(await request&&calls.Count==0,"Superseded NVDA feedback stays cancelled rather than speaking obsolete text through JAWS");
        pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        request=router.TryAnnounceAsync(owner,"While locking",false);safe=false;pending.SetResult(false);
        check(await request&&calls.Count==0,"Locking during asynchronous reader work suppresses later fallback speech");
        safe=true;owner.Dispose();
        check(!await router.TryAnnounceAsync(owner,"After close",false)&&calls.Count==0,"Disposed providers cannot enqueue or deliver new feedback");
    }
}
