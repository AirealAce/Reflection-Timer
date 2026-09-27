using System.Net;
using System.Text.Json;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

static class ServiceTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        var directory=Path.Combine(Path.GetTempPath(),"ReflectionTimer-Services-"+Guid.NewGuid().ToString("N"));
        var state=PreviewSession.SampleState(DateTimeOffset.Now);state.StartAtLogin=true;
        state.Outbox=state.Outbox.Select(o=>o with { LocalOnly=false }).ToList(); // Upgrade a 0.1 profile.
        var session=new PreviewSession(new MemoryStore { State=state }, isolatedProfile: true);
        var handler=new Receiver(); var audio=new SilentAudio();
        using var services=new PreviewServices(session.Engine,directory,new SheetsClient(handler),audio,speech:new SilentSpeech());
        var connection=new ConnectionSettings { SheetUrl="https://docs.google.com/spreadsheets/d/abcdefghijklmnopqrstuvwxyz/edit",
            WebAppUrl="https://script.google.com/macros/s/syntheticReceiver/exec",ApiToken=new string('a',64),SheetMode="fixed",SheetName="QA only" };
        JsonElement Data(object value)=>JsonSerializer.SerializeToElement(value,PreviewSession.Json);
        try {
            await services.Sync(true);
            check(handler.Actions.Count==0,"Disconnected preview makes no delivery requests");
            await services.CheckAndSave(connection,false);
            check(session.Engine.Snapshot.StartAtLogin,"Saving connection preserves the preview startup preference");
            check(handler.Actions.Count==0 && session.Engine.Snapshot.Outbox.All(o=>o.LocalOnly && o.SheetUrl==""),"Connection save preserves migrated local entries and makes no request while paused");
            await services.CheckAndSave(connection,true);
            check(handler.Actions.SequenceEqual(["ping"]),"Enabling delivery authenticates a receiver before saving");
            await services.Sync();
            check(handler.Actions.Count==1,"Connected preview never attempts to upload its sample entries");
            var prompt=session.Engine.TestPrompt();session.Execute("queue",Data(new { id=prompt,text="Synthetic connected reflection",reason="" }));
            await services.Sync();
            check(session.Engine.Snapshot.Outbox.Single(o=>o.Id==prompt).Status==DeliveryStatus.Sent && handler.Actions.Count(a=>a=="appendReflection")==1,"New connected reflection is delivered once using the shared protocol");
            check(handler.LastWrite.GetProperty("deliveryProtocol").GetString()==SheetsClient.DeliveryProtocol && handler.LastWrite.GetProperty("requestId").GetString()==prompt.ToString(),"Delivery retains the stable request ID and retry protocol");
            var blank=session.Engine.TestPrompt();session.AutoSendReflection(blank);await services.Sync();
            check(handler.LastWrite.GetProperty("autoSent").GetBoolean()&&handler.LastWrite.GetProperty("message").GetString()=="[auto-sent]"&&session.Engine.Snapshot.Outbox.Single(o=>o.Id==blank).Message=="","Blank auto-sent entries use a marker accepted by existing receivers while retaining the empty response locally");
            session.Engine.Start(300,false,50);session.Engine.EndEarly();var early=session.Engine.Snapshot.Prompts.Last();
            session.Engine.SaveDraft(early.Id,"Last words","Appointment");session.AutoSendReflection(early.Id);await services.Sync();
            check(handler.LastWrite.GetProperty("endedEarly").GetBoolean()&&handler.LastWrite.GetProperty("autoSent").GetBoolean()&&handler.LastWrite.GetProperty("message").GetString()=="[auto-sent]\nLast words"&&handler.LastWrite.GetProperty("earlyEndReason").GetString()=="Appointment","Sheets request retains both ended-early and auto-sent status plus the original response and reason");
            var full=session.Engine.TestPrompt();session.Engine.SaveDraft(full,new string('x',5000));session.AutoSendReflection(full);
            var beforeFull=handler.Actions.Count(a=>a=="appendReflection");await services.Sync();
            check(handler.Actions.Count(a=>a=="appendReflection")==beforeFull&&session.Engine.Snapshot.Outbox.Single(o=>o.Id==full) is {Status:DeliveryStatus.NeedsReview,ErrorKind:"receiver_update_required"}&&session.Engine.Snapshot.Outbox.Single(o=>o.Id==full).Message.Length==5000,"An old receiver cannot cause a full-length automatic response to be truncated or lost");
            handler.SupportsAutoSent=true;session.Engine.RetryUpload(full);await services.Sync();
            check(handler.LastWrite.GetProperty("message").GetString()!.Length==5012&&session.Engine.Snapshot.Outbox.Single(o=>o.Id==full).Status==DeliveryStatus.Sent,"Updated receivers receive every character plus the automatic-send marker under the same entry ID");
            try { session.Execute("simulate",Data(new { id=prompt })); throw new Exception("Connected simulation allowed"); } catch(ArgumentException) { }
            check(session.Engine.Snapshot.Outbox.Single(o=>o.Id==prompt).SavedTab=="QA only","Connected entries cannot be relabeled as simulated success");
            var local=session.Engine.Snapshot.Outbox.First();
            using(var client=new SheetsClient(handler)) {
                var calls=handler.Actions.Count; var result=await client.Upload(connection,local);
                check(!result.Success && handler.Actions.Count==calls,"HTTP client itself rejects explicitly local entries");
            }
            var pending=session.Engine.TestPrompt();session.Execute("queue",Data(new {id=pending,text="Another synthetic reflection",reason=""}));
            handler.WriteError="write_uncertain";await services.Sync();
            try { session.Execute("retry",Data(new{id=pending}));throw new Exception("Unconfirmed retry accepted"); } catch(ArgumentException) { }
            check(session.Engine.Snapshot.Outbox.Single(o=>o.Id==pending).Status==DeliveryStatus.NeedsReview,"Uncertain writes require explicit sheet review before a new request");
            session.Execute("retry",Data(new{id=pending,confirmed=true}));
            check(session.Engine.Snapshot.Outbox.All(o=>o.Id!=pending),"Confirmed uncertain retry receives a new request ID");
            handler.WriteError="";var writes=handler.Actions.Count(a=>a=="appendReflection");
            handler.OnPing=()=>services.SaveConnection(connection,false);
            await services.Sync();handler.OnPing=null;
            check(handler.Actions.Count(a=>a=="appendReflection")==writes,"Pausing while verification is in flight prevents the next write");
            handler.OnPing=()=>services.SaveConnection(connection,false);
            try { await services.CheckAndSave(connection,true);throw new Exception("Stale enable accepted"); } catch(ArgumentException) { }
            handler.OnPing=null;
            check(!session.Engine.Snapshot.ExtensionDisabledConfirmed,"A stale connection check cannot undo a newer pause");
            var report=JsonSerializer.Serialize(services.Log.Report(session.Engine.Snapshot));
            check(!report.Contains(connection.ApiToken) && !report.Contains(connection.SheetUrl) && !report.Contains("Synthetic connected reflection"),"Diagnostic report excludes credentials and reflection text");
            var before=audio.Count;await services.Play(SoundEvent.Success,true);
            check(audio.Count==before+1,"Sound preview uses the shared audio backend");
            session.Engine.SetAppVolume(0);before=audio.Count;await services.Play(SoundEvent.LowTime,true);
            check(audio.Count==before,"Muted audio never opens a playback backend");
            var connectionBefore=JsonSerializer.Serialize(session.Engine.Snapshot);var actionsBefore=handler.Actions.Count;
            await services.CheckConnection(connection);
            check(handler.Actions.Count==actionsBefore+1&&handler.Actions.Last()=="ping"&&JsonSerializer.Serialize(session.Engine.Snapshot)==connectionBefore,"Explicit connection test uses a read-only ping without enabling delivery");
            var encoded=ConnectionSetup.Export(connection);
            check(ConnectionSetup.Import(encoded)==connection,"Private connection setup code round-trips without changing the destination");
        }
        finally {
            services.Dispose();
            foreach(var name in new[]{"diagnostics.dat","diagnostics.dat.bak"}) File.Delete(Path.Combine(directory,name));
            if(Directory.Exists(directory))Directory.Delete(directory);
        }
    }
    private sealed class Receiver : HttpMessageHandler
    {
        public bool SupportsAutoSent;
        public List<string> Actions=[];public JsonElement LastWrite;public string WriteError="";public Action? OnPing;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            using var data=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var action=data.RootElement.GetProperty("action").GetString()!;Actions.Add(action);
            if(action=="appendReflection") LastWrite=data.RootElement.Clone(); else OnPing?.Invoke();
            var content=action=="appendReflection" && WriteError.Length>0 ? JsonSerializer.Serialize(new{success=false,code=WriteError})
                : JsonSerializer.Serialize(new{success=true,target="synthetic destination",sheet="QA only",deliveryProtocol=SheetsClient.DeliveryProtocol,supportsCheckIns=true,supportsAutoSent=SupportsAutoSent});
            return new(HttpStatusCode.OK){Content=new StringContent(content)};
        }
    }
    private sealed class SilentAudio : IAlertAudioBackend
    {
        public int Count;
        public Task PlayAsync(string path,AudioLevel level,CancellationToken cancellationToken) { Interlocked.Increment(ref Count);return Task.CompletedTask; }
    }
}
