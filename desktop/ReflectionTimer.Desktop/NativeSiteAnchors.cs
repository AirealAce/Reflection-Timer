using ReflectionTimer.Core;

namespace ReflectionTimer.Accessible;

// Native metadata has no opener history. Anchor each target's first committed
// website for a running capture; never learn unrelated later navigation as links.
internal sealed class NativeSiteAnchors
{
    private readonly object gate=new();
    private readonly Dictionary<string,string> hosts=[];
    private Guid? session;
    private string? selection;
    private bool active;
    private long generation;
    internal long Configure(FocusModeSettings settings,TimerState timer)
    {
        lock(gate){
            var next=settings.Enabled&&settings.TargetOnSiteLinks&&timer.IsRunning&&timer.SessionId is not null;
            if(session!=timer.SessionId||selection!=settings.SelectionKey||active!=next){
                hosts.Clear();generation++;
            }
            session=timer.SessionId;selection=settings.SelectionKey;active=next;
            return generation;
        }
    }
    internal long Generation {get{lock(gate)return generation;}}
    internal bool CanRead(long token,string key){lock(gate)return active&&token==generation&&hosts.Count<512&&!hosts.ContainsKey(key);}
    internal void Record(long token,string key,string host)
    {
        if(!FocusSites.TryCanonicalHost(host,out var canonical))return;
        lock(gate)if(active&&token==generation&&hosts.Count<512)hosts.TryAdd(key,canonical);
    }
    internal string[] Hosts(IReadOnlyList<FocusTarget> targets)
    {
        lock(gate)return active?targets.Select(t=>hosts.GetValueOrDefault(t.Key)).OfType<string>().Distinct().ToArray():[];
    }
}
