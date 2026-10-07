using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using ReflectionTimer.Accessible;
using ReflectionTimer.Core;
using ReflectionTimer.Desktop;

static class CsvTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"ReflectionTimer-Csv-"+Guid.NewGuid().ToString("N"));
        var moment=new DateTimeOffset(2026,10,7,8,52,0,TimeSpan.FromHours(-4));
        OutboxItem Entry(string folder)=>new(){Message="Café, Pokémon \"notes\"\nSecond line",SubmittedAt=moment,DurationSeconds=900,ActualDurationSeconds=621,
            CsvDirectory=folder,CsvStatus=CsvDeliveryStatus.Pending,SheetsRequested=false};
        try {
            var fresh=AppState.CreateDefault();
            check(fresh.Csv.Enabled&&!fresh.ExtensionDisabledConfirmed,"Fresh profiles prefer CSV with Sheets off");
            check(fresh.Csv.ResolvedDirectory==Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Reflection Timer Logs"),"The default CSV folder is Reflection Timer Logs on the actual Desktop");
            var legacy=JsonSerializer.Deserialize<AppState>("{\"ExtensionDisabledConfirmed\":true,\"Outbox\":[{\"Message\":\"Existing pending reflection\"}]}",DataJson.Options)!;
            check(legacy.ExtensionDisabledConfirmed&&legacy.Csv.Enabled&&legacy.Outbox[0].WantsSheets&&legacy.Outbox[0].CsvStatus==CsvDeliveryStatus.NotRequested,"Legacy Sheets preferences and pending entries survive without exporting old history");
            var folder=Path.Combine(root,"new","daily");var entry=Entry(folder);var log=new CsvLog();
            var settingsEngine=new TimerEngine(new MemoryStore(),()=>moment);settingsEngine.SaveCsvSettings(new(){Directory=folder});
            check(!Directory.Exists(folder),"Saving CSV preferences does not create a folder before an entry");
            var reply=log.Write(entry);var rows=Read(reply.File);
            check(reply.Success&&Path.GetFileName(reply.File)=="10-07-2026.csv","The first entry creates the missing folder and daily file with filename-safe Sheets date separators");
            check(rows[0].SequenceEqual(CsvLog.Headers)&&rows.Length==2&&rows[1].Length==11,"Daily CSV has one header row and the Sheets columns plus an entry ID");
            check(rows[1][1]==entry.Message&&rows[1][2]=="10:21"&&rows[1][3]=="15:00"&&rows[1][6]=="timer","CSV round-trips commas, quotes, Unicode and line breaks with timer durations");
            check(File.ReadAllBytes(reply.File).Take(3).SequenceEqual(new byte[]{0xef,0xbb,0xbf}),"A UTF-8 BOM preserves Unicode in Windows spreadsheet apps");
            var simple=entry with{Id=Guid.NewGuid(),Message="testing",Mode=SessionMode.Stopwatch,ActualDurationSeconds=51,CsvDirectory=Path.Combine(root,"readable")};var simpleReply=log.Write(simple);var simpleText=File.ReadAllText(simpleReply.File);
            check(simpleText.StartsWith(string.Join(",",CsvLog.Headers)+"\r\n")&&!simpleText.Contains('"')&&simpleText.Contains("testing,0:51,,,,stop watch"),"Headers, plain text and empty cells avoid unnecessary quotes");
            var quoted=entry with{Id=Guid.NewGuid(),Message="Leading space ",CsvDirectory=simple.CsvDirectory};check(log.Write(quoted).Success&&Read(simpleReply.File).Last()[1]==quoted.Message,"Required quoting preserves whitespace as well as commas, quotes and line breaks");
            var legacyFolder=Path.Combine(root,"legacy-quotes");Directory.CreateDirectory(legacyFolder);var legacyEntry=simple with{CsvDirectory=legacyFolder};
            var legacyRows=Read(simpleReply.File);var oldQuoted=string.Join("\r\n",legacyRows.Select(row=>string.Join(",",row.Select(value=>"\""+value.Replace("\"","\"\"")+"\""))))+"\r\n";File.WriteAllText(CsvLog.FilePath(legacyEntry),oldQuoted,new UTF8Encoding(true));
            var legacyBytes=File.ReadAllBytes(CsvLog.FilePath(legacyEntry));
            check(log.Write(legacyEntry).Success&&legacyBytes.SequenceEqual(File.ReadAllBytes(CsvLog.FilePath(legacyEntry))),"Retrying an older fully quoted CSV retains its existing entry without duplicates");
            check(log.Write(legacyEntry with{Id=Guid.NewGuid(),Message="Another entry"}).Success&&Read(CsvLog.FilePath(legacyEntry)).Length==4&&Read(CsvLog.FilePath(legacyEntry))[2][1]==quoted.Message,"Appending to older CSV files simplifies quoting without changing stored cell contents");
            var before=File.ReadAllBytes(reply.File);
            check(log.Write(entry).Success&&before.SequenceEqual(File.ReadAllBytes(reply.File)),"Retrying an entry leaves one row and does not rewrite the file");
            check(log.Write(entry with{Message="Different contents"}).Error=="csv_conflict"&&before.SequenceEqual(File.ReadAllBytes(reply.File)),"A conflicting ID never overwrites or duplicates an existing row");
            check(log.Write(entry with{Id=Guid.NewGuid(),SubmittedAt=moment.AddDays(1)}).Success&&File.Exists(Path.Combine(folder,"10-08-2026.csv")),"Each local submission day gets its own CSV");
            check(CsvLog.FileName(entry with{SubmittedAt=moment.AddHours(15)})=="10-07-2026.csv","UTC midnight cannot move an entry into the wrong local day");
            var watch=entry with{Id=Guid.NewGuid(),Mode=SessionMode.Stopwatch,ActualDurationSeconds=3661,AutoSent=true,Message="",Pauses=[new(Guid.NewGuid(),moment.AddMinutes(-3).ToUnixTimeMilliseconds(),61000,"Phone, \"call\"\nBack soon"),new(Guid.NewGuid(),moment.AddMinutes(-1).ToUnixTimeMilliseconds(),3000,"")]};
            check(log.Write(watch).Success,"Stopwatch, blank auto-send and pause records export");
            var watchRow=Read(reply.File).Last();
            check(watchRow[1]=="N/A"&&watchRow[2]=="1:01:01"&&watchRow[3]==""&&watchRow[4]=="auto-sent"&&watchRow[6]=="stop watch","Stopwatch and auto-sent values match the Sheets column semantics");
            check(watchRow[7]=="1. 8:49 AM 10/7/2026\n2. 8:51 AM 10/7/2026"&&watchRow[8]=="1. 1:01\n2. 0:03"&&watchRow[9]=="1. Phone, \"call\"\nBack soon\n2. N/A","Pause columns retain time-first local dates, chronological numbering, durations and multiline reasons");
            var dateFirst=watch with{CsvDirectory=Path.Combine(root,"old-pause-order")};Directory.CreateDirectory(dateFirst.CsvDirectory);
            var dateFirstRow=(string[])watchRow.Clone();dateFirstRow[7]="1. 10/7/2026 8:49 AM\n2. 10/7/2026 8:51 AM";
            File.WriteAllText(CsvLog.FilePath(dateFirst),string.Join("\r\n",new[]{CsvLog.Headers.ToArray(),dateFirstRow}.Select(row=>string.Join(",",row.Select(value=>"\""+value.Replace("\"","\"\"")+"\""))))+"\r\n",new UTF8Encoding(true));
            var dateFirstBytes=File.ReadAllBytes(CsvLog.FilePath(dateFirst));
            check(log.Write(dateFirst).Success&&dateFirstBytes.SequenceEqual(File.ReadAllBytes(CsvLog.FilePath(dateFirst))),"Retrying an older date-first pause entry succeeds without duplication or rewriting");
            check(log.Write(dateFirst with{Id=Guid.NewGuid(),Message="Next reflection"}).Success&&Read(CsvLog.FilePath(dateFirst))[1].SequenceEqual(watchRow),"Appending normalizes older pause timestamps to time-first while retaining the other columns");
            log.Write(entry with{Id=Guid.NewGuid(),Message="=SUM(1,2)",EarlyEndReason="@command",EndedEarly=true});
            check(Read(reply.File).Last()[1]=="'=SUM(1,2)"&&Read(reply.File).Last()[5]=="'@command","Spreadsheet formulas stay plain text");
            check(CsvLog.FileName(entry with{IsTest=true})=="test-10-07-2026.csv","Practice reflections have a separate daily file");
            using(var locked=File.Open(reply.File,FileMode.Open,FileAccess.Read,FileShare.None))check(!log.Write(entry with{Id=Guid.NewGuid()}).Success,"A locked CSV reports a recoverable failure");
            check(Read(reply.File).Length==4,"A failed write leaves the original file intact");
            var badFolder=Path.Combine(root,"wrong-format");Directory.CreateDirectory(badFolder);var bad=Entry(badFolder);File.WriteAllText(CsvLog.FilePath(bad),"Unrelated,columns\r\nDo,not overwrite\r\n");var badBytes=File.ReadAllBytes(CsvLog.FilePath(bad));
            check(log.Write(bad).Error=="csv_format"&&badBytes.SequenceEqual(File.ReadAllBytes(CsvLog.FilePath(bad))),"An unrelated CSV is retained unchanged");
            var invalidEncoding=Entry(Path.Combine(root,"invalid-encoding"));Directory.CreateDirectory(invalidEncoding.CsvDirectory);
            var invalidBytes=Encoding.UTF8.GetBytes(string.Join(",",CsvLog.Headers)+"\r\n").Concat(new byte[]{0xff,0xfe,0x41}).ToArray();File.WriteAllBytes(CsvLog.FilePath(invalidEncoding),invalidBytes);
            check(log.Write(invalidEncoding).Error=="csv_format"&&invalidBytes.SequenceEqual(File.ReadAllBytes(CsvLog.FilePath(invalidEncoding))),"Invalid text encoding is reported without replacing the existing CSV bytes");
            using(var released=File.Open(CsvLog.FilePath(invalidEncoding),FileMode.Open,FileAccess.ReadWrite,FileShare.None))check(released.Length==invalidBytes.Length,"A malformed CSV reader releases its file handle for correction");
            var parallel=Enumerable.Range(0,10).Select(_=>Entry(Path.Combine(root,"parallel"))).ToArray();var results=await Task.WhenAll(parallel.Select(item=>Task.Run(()=>new CsvLog().Write(item))));
            check(results.All(r=>r.Success)&&Read(results[0].File).Length==11,"Concurrent writers preserve every row and one header");
            foreach(var csvEnabled in new[]{false,true})foreach(var sheetsEnabled in new[]{false,true}){
                var id=Guid.NewGuid();var memory=new MemoryStore{State=new(){LoggingEnabled=false,ExtensionDisabledConfirmed=sheetsEnabled,Csv=new(){Enabled=csvEnabled,Directory=Path.Combine(root,"destinations")},Prompts=[new(id,moment.ToUnixTimeMilliseconds(),900,0,false)]}};
                var engine=new TimerEngine(memory,()=>moment);engine.QueueReflection(id,"Saved entry");var queued=engine.Snapshot.Outbox.Single();
                check(queued.WantsSheets==sheetsEnabled&&(queued.CsvStatus==CsvDeliveryStatus.Pending)==csvEnabled,$"Queue snapshots independent destinations: CSV={csvEnabled}, Sheets={sheetsEnabled}");
                engine.SaveCsvSettings(new(){Enabled=!csvEnabled,Directory=Path.Combine(root,"other")});engine.SaveSettings(new(),false,false,!sheetsEnabled);
                check(engine.Snapshot.Outbox.Single().WantsSheets==sheetsEnabled&&engine.Snapshot.Outbox.Single().CsvDirectory==queued.CsvDirectory,"Changing preferences does not reroute a queued entry");
            }
            foreach(var offline in new[]{false,true}){
                var id=Guid.NewGuid();var receiver=new Receiver{Offline=offline};var memory=new MemoryStore{State=new(){LoggingEnabled=false,ExtensionDisabledConfirmed=true,Timer=new(){Volume=0},Connection=Connection,
                    Csv=new(){Directory=Path.Combine(root,offline?"offline":"both")},Prompts=[new(id,moment.ToUnixTimeMilliseconds(),900,0,false)]}};
                var engine=new TimerEngine(memory,()=>moment);engine.QueueReflection(id,"Both destinations");using var services=new PreviewServices(engine,root,new SheetsClient(receiver),new SilentAudio(),speech:new SilentVoice());
                await services.Sync();var saved=engine.Snapshot.Outbox.Single();
                check(saved.CsvStatus==CsvDeliveryStatus.Saved&&Read(saved.CsvFile).Length==2,"CSV succeeds independently of Sheets network availability: offline="+offline);
                check(offline?saved.Status==DeliveryStatus.Pending:saved.Status==DeliveryStatus.Sent,"Sheets keeps its retry and success behavior: offline="+offline);
                receiver.Offline=false;await services.Sync(true);check(engine.Snapshot.Outbox.Single().DeliveryComplete&&Read(saved.CsvFile).Length==2,"A later Sheets retry does not append another CSV row");
            }
            var csvId=Guid.NewGuid();var csvStore=new MemoryStore{State=new(){LoggingEnabled=false,Timer=new(){Volume=0},Csv=new(){Directory=Path.Combine(root,"csv-only")},Prompts=[new(csvId,moment.ToUnixTimeMilliseconds(),900,0,false)]}};
            var csvEngine=new TimerEngine(csvStore,()=>moment);csvEngine.QueueReflection(csvId,"CSV only");var noNetwork=new Receiver();using(var services=new PreviewServices(csvEngine,root,new SheetsClient(noNetwork),new SilentAudio(),speech:new SilentVoice()))await services.Sync(true);
            check(noNetwork.Requests==0&&csvEngine.Snapshot.Outbox.Single().DeliveryComplete,"CSV-only delivery needs no Google connection or network request");
            using(var directClient=new SheetsClient(noNetwork))check(!(await directClient.Upload(Connection,csvEngine.Snapshot.Outbox.Single())).Success&&noNetwork.Requests==0,"The Sheets client also refuses entries that selected CSV only");
            var csvOnlyOldDestination=Entry(Path.Combine(root,"account-change")) with{SheetUrl=Connection.SheetUrl,ReceiverUrl=Connection.WebAppUrl};
            var accountEngine=new TimerEngine(new MemoryStore{State=new(){Connection=Connection,Outbox=[csvOnlyOldDestination]}},()=>moment);
            var anotherConnection=Connection with{SheetUrl="https://docs.google.com/spreadsheets/d/ABCDEFGHIJKLMNOPQRSTUVWXYZ/edit"};accountEngine.SaveSettings(anotherConnection,false,false,false);
            check(accountEngine.Snapshot.Connection==anotherConnection&&accountEngine.Snapshot.Outbox.Single()==csvOnlyOldDestination,"A CSV-only pending entry does not block changes to the unrelated Sheets account");
            var blocked=Path.Combine(root,"blocked-folder");File.WriteAllText(blocked,"This is a file, not a folder.");var blockedId=Guid.NewGuid();var blockedReceiver=new Receiver();
            var blockedEngine=new TimerEngine(new MemoryStore{State=new(){LoggingEnabled=false,Timer=new(){Volume=0},ExtensionDisabledConfirmed=true,Connection=Connection,Csv=new(){Directory=blocked},Prompts=[new(blockedId,moment.ToUnixTimeMilliseconds(),900,0,false)]}},()=>moment);
            blockedEngine.QueueReflection(blockedId,"Sheets still works when CSV fails");
            using(var services=new PreviewServices(blockedEngine,root,new SheetsClient(blockedReceiver),new SilentAudio(),speech:new SilentVoice())){
                var notices=new List<string>();services.Announcement+=notices.Add;await services.Sync(true);var failed=blockedEngine.Snapshot.Outbox.Single();
                check(failed.Status==DeliveryStatus.Sent&&failed.CsvStatus!=CsvDeliveryStatus.Saved&&!failed.DeliveryComplete,"CSV failure does not block Sheets or falsely mark all delivery complete");
                check(notices.Last().Contains("CSV delivery remains in Outbox"),"Partial success announces the remaining CSV work");
                var networkCalls=blockedReceiver.Requests;blockedEngine.SaveCsvSettings(new(){Directory=Path.Combine(root,"retry-folder")});blockedEngine.RetryUpload(blockedId);await services.Sync(true);
                check(blockedEngine.Snapshot.Outbox.Single().DeliveryComplete&&blockedReceiver.Requests==networkCalls,"Retry selected uses the new CSV folder without sending Sheets twice");
            }
            var previewItem=Entry(Path.Combine(root,"never-export-preview")) with{LocalOnly=true};var previewEngine=new TimerEngine(new MemoryStore{State=new(){LoggingEnabled=false,Timer=new(){Volume=0},Outbox=[previewItem]}},()=>moment);
            using(var services=new PreviewServices(previewEngine,root,new SheetsClient(new Receiver()),new SilentAudio(),speech:new SilentVoice()))await services.Sync(true);
            check(!Directory.Exists(previewItem.CsvDirectory),"Explicit local preview entries never create CSV files");
            var interruptedStore=new MemoryStore{State=new(){LoggingEnabled=false,Timer=new(){Volume=0},Outbox=[Entry(Path.Combine(root,"commit-recovery"))]}};var recoveredEngine=new TimerEngine(interruptedStore,()=>moment);
            using(var services=new PreviewServices(recoveredEngine,root,new SheetsClient(new Receiver()),new SilentAudio(),speech:new SilentVoice())){
                interruptedStore.Fail=true;await services.Sync(true);interruptedStore.Fail=false;await services.Sync(true);
                var saved=recoveredEngine.Snapshot.Outbox.Single();check(saved.CsvStatus==CsvDeliveryStatus.Saved&&Read(saved.CsvFile).Length==2,"A failed profile commit after CSV replacement recovers without duplicates");
            }
            var sentCsv=entry with{Id=Guid.NewGuid(),CsvEntryId=Guid.NewGuid(),CsvStatus=CsvDeliveryStatus.Saved,Status=DeliveryStatus.NeedsReview,SheetsRequested=true,ErrorKind="write_uncertain"};
            var retryEngine=new TimerEngine(new MemoryStore{State=new(){Outbox=[sentCsv]}},()=>moment);retryEngine.RetryUpload(sentCsv.Id);
            check(retryEngine.Snapshot.Outbox[0].Id!=sentCsv.Id&&retryEngine.Snapshot.Outbox[0].CsvEntryId==sentCsv.CsvEntryId&&retryEngine.Snapshot.Outbox[0].CsvStatus==CsvDeliveryStatus.Saved,"A new Sheets request preserves the already saved CSV identity");
        } finally {
            if(!Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(),"ReflectionTimer-Csv-"),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unexpected test cleanup path.");
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }
    private static string[][] Read(string file){using var parser=new TextFieldParser(file,Encoding.UTF8,true){HasFieldsEnclosedInQuotes=true,TrimWhiteSpace=false};parser.SetDelimiters(",");var rows=new List<string[]>();while(!parser.EndOfData)rows.Add(parser.ReadFields()!);return rows.ToArray();}
    private static readonly ConnectionSettings Connection=new(){SheetUrl="https://docs.google.com/spreadsheets/d/abcdefghijklmnopqrstuvwxyz/edit",WebAppUrl="https://script.google.com/macros/s/syntheticReceiver/exec",ApiToken=new string('a',64)};
    private sealed class Receiver:HttpMessageHandler
    {
        internal bool Offline;internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation){Requests++;if(Offline)throw new HttpRequestException("Synthetic offline receiver");return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{success=true,target="Synthetic",sheet="Synthetic",deliveryProtocol=SheetsClient.DeliveryProtocol,supportsAutoSent=true,supportsCheckIns=true,supportsStopwatch=true,supportsPauses=true}))});}
    }
    private sealed class SilentAudio:IAlertAudioBackend {public Task PlayAsync(string path,AudioLevel level,CancellationToken cancellation)=>Task.CompletedTask;}
    private sealed class SilentVoice:IVoiceOutput {public bool TakeFailure()=>false;public void Speak(string text,int volume){}public void SetVolume(int volume){}public void Stop(){}public void Dispose(){}}
}
