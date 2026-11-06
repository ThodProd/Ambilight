using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
namespace Ambilight;
internal static class Program
{
 // Physical pixels keep the on-screen capture preview aligned with DXGI on scaled displays.
 [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);
 static async Task<int> Main(string[] args)
 {
  Console.InputEncoding=new UTF8Encoding(false);Console.OutputEncoding=new UTF8Encoding(false);
  try{SetProcessDpiAwarenessContext(-4);}catch(Exception){}
  if(args.Contains("--cleanup")){await Background.Stop();Startup.Set(false);return 0;}
  if(args.Contains("--background"))return await Background.Serve();
  if(!args.Contains("--direct"))return await Background.Proxy();
  try
  {
   using var app=new Engine();
   string? line;
   while((line=await Console.In.ReadLineAsync())!=null)
   {
    string? id=null;
    try
    {
     if(line.Length>1_000_000)throw new ArgumentException("Запрос слишком большой.");
     using var doc=JsonDocument.Parse(line);var root=doc.RootElement;
     id=root.GetProperty("id").GetString();if(root.GetProperty("jsonrpc").GetString()!="2.0"||id is null)throw new ArgumentException("Некорректный JSON-RPC.");
     var result=await app.Call(root.GetProperty("method").GetString()??"",root.TryGetProperty("params",out var p)?p:default);
     Console.WriteLine(JsonSerializer.Serialize(new{jsonrpc="2.0",id,result},ConfigStore.Json));
    }
    catch(Exception ex){Console.Error.WriteLine(ex.Message);Console.WriteLine(JsonSerializer.Serialize(new{jsonrpc="2.0",id,error=new{code=-32000,message=ex.Message}},ConfigStore.Json));}
   }
   return 0;
  }
  catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}
 }
}
internal static class Startup
{
 const string Key=@"Software\Microsoft\Windows\CurrentVersion\Run";
 const string Name="ThodHUB.Ambilight";
 public static bool Enabled {get{using var k=Registry.CurrentUser.OpenSubKey(Key);return k?.GetValue(Name) is string value&&value.EndsWith(" --launch ambilight",StringComparison.Ordinal);}}
 public static void Set(bool enabled)
 {
  using var key=Registry.CurrentUser.CreateSubKey(Key);
  if(!enabled){if(key.GetValue(Name) is string own&&own.EndsWith(" --launch ambilight",StringComparison.Ordinal))key.DeleteValue(Name,false);return;}
  var launcher=Environment.GetEnvironmentVariable("THODHUB_LAUNCHER");
  if(string.IsNullOrWhiteSpace(launcher)||!File.Exists(launcher)||launcher.Contains('"'))throw new InvalidOperationException("Хаб не передал путь запуска. Откройте Ambilight из установленного ThodHUB.");
  key.SetValue(Name,$"\"{launcher}\" --launch ambilight");
 }
}
internal sealed class Engine : IDisposable
{
 readonly ConfigStore store=new();readonly object gate=new();readonly CancellationTokenSource stop=new();readonly Task loop;readonly Task frameLoop;readonly Task networkLoop;readonly Device device;readonly RegionOverlay overlay=new();readonly SemaphoreSlim frameReady=new(0,1);
 Settings settings;string configError="";string captureError="";string networkError="";string activeMode="screen",deviceVersion="";
 byte[] colors=[];byte[] outgoing=[];int calibration=-1;List<LedPoint>? calibrationPoints;DateTime calibrationUntil;bool calibrationVerify,calibrationSaved;int calibrationUtilityCount,calibrationUtilityBrightness=20;List<UtilityLedConfig> calibrationUtilityLeds=[];bool connected;bool configure=true;bool preview=true;
 Task<List<FoundDevice>>? discovery;List<FoundDevice> found=[];string discoveryError="";
 DateTime whitePreviewUntil;bool idleSleeping;
 long revision;double actualFps;long frames;bool suspended;int effectiveBrightness=65;
 public Engine()
 {
  try{settings=store.Load();}catch(Exception ex){settings=new();configError="Не удалось прочитать конфигурацию: "+ex.Message;}
  device=new(store.DirectoryPath);SystemEvents.PowerModeChanged+=PowerChanged;loop=Task.Run(Run);frameLoop=Task.Run(FrameLoop);networkLoop=Task.Run(NetworkLoop);
 }
 void PowerChanged(object sender,PowerModeChangedEventArgs e){lock(gate){suspended=e.Mode==PowerModes.Suspend;if(e.Mode==PowerModes.Resume){suspended=false;configure=true;}}}
 void EndCalibration(){calibrationPoints=null;calibration=-1;calibrationVerify=false;calibrationSaved=false;configure=true;revision++;}
 Settings ActiveSettings()
 {
  if(calibrationPoints!=null&&DateTime.UtcNow>=calibrationUntil)EndCalibration();
  return calibrationPoints==null?settings:settings with{Points=calibrationPoints,UtilityLedCount=calibrationUtilityCount,UtilityBrightness=calibrationUtilityBrightness,UtilityLeds=calibrationUtilityLeds,Mode="solid",Schedule=false,Brightness=20,Power=true};
 }
 void RequireCalibration(){ActiveSettings();if(calibrationPoints==null)throw new InvalidOperationException("Калибровка завершена или истекло время ожидания. Начните её заново.");}
 static object[] Monitors()=>Screen.AllScreens.Select(s=>(object)new{id=s.DeviceName,name=$"{(s.Primary?"Основной":"Монитор")} · {s.Bounds.Width} × {s.Bounds.Height}",width=s.Bounds.Width,height=s.Bounds.Height,primary=s.Primary}).ToArray();
 public async Task<object> Call(string method,JsonElement p)
 {
  switch(method)
  {
   case "init":lock(gate)return new{settings,monitors=Monitors(),autostart=Startup.Enabled,version=typeof(Program).Assembly.GetName().Version!.ToString(3),configError,paired=device.Paired};
   case "status":
    lock(gate){if(discovery?.IsCompleted==true){if(discovery.IsCompletedSuccessfully)found=discovery.Result;else discoveryError=discovery.Exception?.GetBaseException().Message??"Поиск отменён.";discovery=null;}
     return new{settings,revision,connected,paired=device.Paired,deviceVersion,deviceError=networkError,captureError,configError,mode=activeMode,fps=Math.Round(actualFps,1),frames,brightness=effectiveBrightness,colors=preview?colors.Select(b=>(int)b).ToArray():[],idleSleeping,whitePreview=DateTime.UtcNow<whitePreviewUntil,calibration,calibrationVerify,calibrationActive=calibrationPoints!=null,calibrationCount=calibrationPoints?.Count??0,scanning=discovery!=null,devices=found,discoveryError,monitors=Monitors()};}
   case "settings":
    var updated=p.Deserialize<Settings>(ConfigStore.Json)??throw new ArgumentException("Нет настроек.");updated.NormalizeUtilityLeds();updated.Validate();
    lock(gate){updated.DeviceIp=settings.DeviceIp;updated.DeviceId=settings.DeviceId;store.Save(updated);settings=updated;configError="";configure=true;revision++;return new{settings,revision};}
   case "layout":
    int top=p.GetProperty("top").GetInt32(),side=p.GetProperty("side").GetInt32(),bottom=p.GetProperty("bottom").GetInt32(),offset=p.GetProperty("offset").GetInt32();bool reverse=p.GetProperty("reverse").GetBoolean();
    if(top<1||side<1||bottom<1||top+2*side+bottom>360)throw new ArgumentException("Количество: 4–360 светодиодов.");
    lock(gate){var next=settings with{Top=top,Side=side,Bottom=bottom,Offset=offset,Reverse=reverse,Points=Settings.Layout(top,side,bottom,offset,reverse)};store.Save(next);settings=next;EndCalibration();return settings;}
   case "calibration.begin":lock(gate){
    int count=p.GetProperty("count").GetInt32(),utility=p.TryGetProperty("utilityCount",out var uc)?uc.GetInt32():0,utilityBrightness=p.TryGetProperty("utilityBrightness",out var ub)?ub.GetInt32():20;
    if(count is <4 or >360||utility<0||utility>Math.Min(32,count-4)||utilityBrightness is <0 or >100)throw new ArgumentException("Проверьте общее и служебное количество LED.");
    calibrationUtilityCount=utility;calibrationUtilityBrightness=utilityBrightness;calibrationUtilityLeds=Settings.DefaultUtilityLeds(utility,utilityBrightness);calibrationPoints=CalibrationMap.Uniform(count-utility);calibrationPoints.AddRange(Enumerable.Repeat(new LedPoint(0,0,true),utility));calibration=0;calibrationVerify=false;calibrationSaved=false;calibrationUntil=DateTime.UtcNow.AddSeconds(15);configure=true;revision++;return true;}
   case "calibration.select":lock(gate){RequireCalibration();int index=p.GetProperty("index").GetInt32(),ambient=calibrationPoints!.Count-calibrationUtilityCount;if(index<0||index>=ambient)throw new ArgumentException("Нет такого светодиода на рамке.");calibration=index;calibrationVerify=false;calibrationUntil=DateTime.UtcNow.AddSeconds(15);return true;}
   case "calibration.keepalive":lock(gate){RequireCalibration();calibrationUntil=DateTime.UtcNow.AddSeconds(15);return true;}
   case "calibration.cancel":lock(gate){EndCalibration();return true;}
   case "calibration.preview":lock(gate){
    RequireCalibration();var anchors=p.GetProperty("anchors").Deserialize<List<CalibrationAnchor>>(ConfigStore.Json)??throw new ArgumentException("Нет отмеченных точек.");
    return CalibrationMap.Build(calibrationPoints!.Count-calibrationUtilityCount,anchors);
   }
   case "calibration.save":lock(gate){
    RequireCalibration();var anchors=p.GetProperty("anchors").Deserialize<List<CalibrationAnchor>>(ConfigStore.Json)??throw new ArgumentException("Нет отмеченных точек.");
    var map=CalibrationMap.Build(calibrationPoints!.Count-calibrationUtilityCount,anchors);var points=map.Points.Concat(Enumerable.Repeat(new LedPoint(0,0,true),calibrationUtilityCount)).ToList();
    var next=settings with{Points=points,CalibrationAnchors=anchors,UtilityLedCount=calibrationUtilityCount,UtilityBrightness=calibrationUtilityBrightness,UtilityMode="status",UtilityLeds=calibrationUtilityLeds,Power=true,Mode="screen"};store.Save(next);settings=next;calibrationPoints=points;calibration=-1;calibrationVerify=true;calibrationSaved=true;calibrationUntil=DateTime.UtcNow.AddSeconds(15);configure=true;revision++;return new{settings,revision,map};
   }
   case "calibration.finish":lock(gate){RequireCalibration();if(!calibrationSaved)throw new InvalidOperationException("Сначала сохраните схему и проверьте зелёную змейку.");EndCalibration();return new{settings,revision};
   }
   case "white.preview":lock(gate){whitePreviewUntil=p.GetProperty("enabled").GetBoolean()?DateTime.UtcNow.AddMinutes(2):DateTime.MinValue;return true;}
   case "preview":lock(gate){preview=p.GetProperty("enabled").GetBoolean();return true;}
   case "overlay":
    bool showOverlay=p.GetProperty("enabled").GetBoolean();
    if(!showOverlay){overlay.Hide();return true;}
    double overlayDepth;string overlayMonitor;lock(gate){overlayDepth=settings.Depth;overlayMonitor=settings.Monitor;}
    if(p.TryGetProperty("depth",out var dp))overlayDepth=dp.GetDouble();
    if(p.TryGetProperty("monitor",out var mp))overlayMonitor=mp.GetString()??"";
    if(!double.IsFinite(overlayDepth)||overlayDepth<.01||overlayDepth>.3)throw new ArgumentException("Глубина захвата: 1–30%.");
    overlay.Show(overlayDepth,overlayMonitor);return true;
   case "scan":lock(gate){if(discovery==null){discoveryError="";found=[];discovery=Device.Discover(stop.Token);}return true;}
   case "pair":
    var ip=p.GetProperty("ip").GetString()??"";var code=p.GetProperty("code").GetString()??"";
    await device.Pair(ip,code,stop.Token);var status=await device.Status(ip,stop.Token);
    lock(gate){var next=settings with{DeviceIp=ip,DeviceId=status.GetProperty("id").GetString()??""};store.Save(next);settings=next;configure=true;revision++;return true;}
   case "autostart":Startup.Set(p.GetProperty("enabled").GetBoolean());return Startup.Enabled;
   default:throw new ArgumentException("Неизвестная команда.");
  }
 }
 async Task Run()
 {
  var idle=new IdleDimmer();using var capture=new Capture();var clock=Stopwatch.StartNew();long lastFrames=0;double lastMeasure=0;double lastStamp=0;var nextCapture=DateTime.MinValue;byte[] smooth=[];string lastMode="";int lastBrightness=-1;bool lastPower=false;
  while(!stop.IsCancellationRequested)
  {
   var iteration=Stopwatch.StartNew();Settings s;int cal;bool verify,sleep,online;
   lock(gate){s=ActiveSettings();cal=calibration;verify=calibrationVerify;sleep=suspended;online=connected;if(cal<0&&!verify&&DateTime.UtcNow<whitePreviewUntil)s=s with{Mode="solid",Color="#ffffff",Schedule=false};}
   var mode=Lighting.EffectiveMode(s,DateTime.Now);int brightness=mode=="night"?s.NightBrightness:s.Brightness;bool power=s.Power&&!sleep;
   if(mode!=lastMode||lastBrightness!=brightness||lastPower!=power){lock(gate)configure=true;smooth=[];lastMode=mode;lastBrightness=brightness;lastPower=power;}
   try
   {
    if(mode=="screen"&&cal<0&&power)
    {
     var screens=Screen.AllScreens;var screen=string.IsNullOrEmpty(s.Monitor)?screens.FirstOrDefault(x=>x.Primary):screens.FirstOrDefault(x=>x.DeviceName==s.Monitor);
     if(screen==null)throw new InvalidOperationException("Выбранный монитор отключён. Выберите доступный монитор.");
     if(DateTime.UtcNow>=nextCapture){try{capture.Read(screen.DeviceName);lock(gate)captureError=capture.HasFrame?"":"Ожидание первого кадра рабочего стола…";}catch{nextCapture=DateTime.UtcNow.AddSeconds(1);throw;}}
    }
    else {capture.Dispose();lock(gate)captureError="";}
   }
   catch(Exception ex){capture.Dispose();lock(gate)captureError="Захват временно недоступен. Повторяем подключение. "+ex.Message;}
   var rgb=Lighting.Frame(s,capture.Pixels,mode,clock.Elapsed.TotalSeconds,cal,verify);
   if(cal<0&&!verify)Lighting.Utility(s,rgb,online,brightness,mode);
   if(mode=="screen"&&!capture.HasFrame&&cal<0)Array.Clear(rgb);
   // Time-based smoothing keeps the same feel at 15, 30 and 60 fps; an older
   // frame is never worth a longer pause when the loop falls behind.
   double now=clock.Elapsed.TotalSeconds,keep=Math.Pow(s.Smoothing,Math.Clamp((now-lastStamp)*s.Fps,.05,4));lastStamp=now;
   if(smooth.Length!=rgb.Length||cal>=0||verify)smooth=rgb.ToArray();
   else for(int i=0;i<rgb.Length;i++)smooth[i]=(byte)Math.Round(smooth[i]*keep+rgb[i]*(1-keep));
   // Failure and suspension must never retain stale screen colours.
   if(!power || mode=="screen"&&!capture.HasFrame&&cal<0)Array.Clear(smooth);
   double fade=idle.Update(capture.Pixels,clock.Elapsed.TotalSeconds,s.IdleSleep&&mode=="screen"&&cal<0&&!verify&&power&&capture.HasFrame,s.IdleMinutes);
   lock(gate){idleSleeping=fade<1;outgoing=smooth.Select(v=>(byte)Math.Round(v*fade)).ToArray();colors=new byte[smooth.Length];int ambient=s.Points.Count-s.UtilityLedCount;for(int i=0;i<s.Points.Count;i++){int level=i<ambient?brightness:s.UtilityLeds[i-ambient].Brightness;for(int c=0;c<3;c++)colors[i*3+c]=(byte)Math.Round(outgoing[i*3+c]*level/100.0);}activeMode=mode;effectiveBrightness=brightness;frames++;if(clock.Elapsed.TotalSeconds-lastMeasure>=1){actualFps=(frames-lastFrames)/(clock.Elapsed.TotalSeconds-lastMeasure);lastFrames=frames;lastMeasure=clock.Elapsed.TotalSeconds;}}
   // Hand the newest frame to the network task immediately instead of letting it
   // wait for the next poll. A pending frame is simply replaced by the newer one.
   if(frameReady.CurrentCount==0)frameReady.Release();
   try{await Task.Delay(Math.Max(1,(int)(1000.0/s.Fps-iteration.Elapsed.TotalMilliseconds)),stop.Token);}catch(OperationCanceledException){break;}
  }
 }
 async Task FrameLoop()
 {
  while(!stop.IsCancellationRequested)
  {
   try{await frameReady.WaitAsync(stop.Token);}catch(OperationCanceledException){break;}
   Settings s;byte[] rgb;bool ready;
   lock(gate){s=ActiveSettings();rgb=outgoing;ready=!suspended&&device.Paired&&!string.IsNullOrEmpty(s.DeviceIp);}
   if(!ready||rgb.Length!=s.Points.Count*3)continue;
   try{await device.Send(s.DeviceIp,rgb,stop.Token);}
   catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
   catch(Exception ex){lock(gate){connected=false;networkError="Нет связи с ESP32. "+ex.Message;configure=true;}try{await Task.Delay(250,stop.Token);}catch(OperationCanceledException){break;}}
  }
 }
 async Task NetworkLoop()
 {
  var lastStatus=DateTime.MinValue;var nextDiscover=DateTime.MinValue;bool reconnect=true;
  while(!stop.IsCancellationRequested)
  {
   Settings s;bool sendConfig,sleep,calibrating;long expectedRevision;
   lock(gate){s=ActiveSettings();calibrating=calibrationPoints!=null;sendConfig=configure;sleep=suspended;expectedRevision=revision;}
   if(device.Paired&&!string.IsNullOrEmpty(s.DeviceIp))try
   {
    var mode=Lighting.EffectiveMode(s,DateTime.Now);int brightness=mode=="night"?s.NightBrightness:s.Brightness;
    if((DateTime.UtcNow-lastStatus).TotalSeconds>=1||sendConfig||reconnect)
    {
     var remote=await device.Status(s.DeviceIp,stop.Token);
     lock(gate)deviceVersion=remote.GetProperty("version").GetString()??"";
     if(remote.GetProperty("id").GetString()!=s.DeviceId)throw new InvalidOperationException("Адрес занят другим устройством.");
     if(!remote.GetProperty("rmtReady").GetBoolean())throw new InvalidOperationException("ESP32 не смогла включить выход ленты. Проверьте GPIO прошивки.");
     bool rp=remote.GetProperty("power").GetBoolean();int rb=remote.GetProperty("brightness").GetInt32();int rn=remote.GetProperty("nightBrightness").GetInt32();
     if(rb is <0 or >100||rn is <0 or >100)throw new InvalidDataException("Некорректная яркость устройства.");
     // On reconnection retain the physical OFF state and standalone brightness.
     // During a live connection an explicit UI command wins; ordinary polls mirror hardware.
     if(calibrating&&!sleep&&!rp)
     {
      // Firmware 1.0.7 turns power off after a 3.5 s stream gap. Calibration is
      // explicitly temporary and powered, so recover instead of persisting OFF.
      await device.Configure(s,true,brightness,stop.Token);rp=true;lock(gate)if(revision==expectedRevision)configure=false;
     }
     else if(!calibrating&&(reconnect||!sendConfig)&&!sleep)
     {
      bool live=remote.GetProperty("streaming").GetBoolean();
      var next=s with{Power=rp,NightBrightness=rn};
      if(!reconnect&&live){if(mode=="night")next.NightBrightness=rb;else next.Brightness=rb;}
      if(next.Power!=s.Power||next.NightBrightness!=s.NightBrightness||next.Brightness!=s.Brightness)lock(gate){if(revision==expectedRevision){store.Save(next);settings=next;s=next;revision++;expectedRevision=revision;}}
     }
     brightness=mode=="night"?s.NightBrightness:s.Brightness;
     if(sendConfig||reconnect){await device.Configure(s,s.Power&&!sleep,brightness,stop.Token);lock(gate)if(revision==expectedRevision)configure=false;}
     lock(gate){connected=true;networkError="";}lastStatus=DateTime.UtcNow;reconnect=false;
    }
   }
   catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
   catch(Exception ex)
   {
    lock(gate){connected=false;networkError="Нет связи с ESP32. "+ex.Message;}lastStatus=DateTime.UtcNow;reconnect=true;
    if(DateTime.UtcNow>=nextDiscover)
    {
     nextDiscover=DateTime.UtcNow.AddSeconds(15);
     try{var devices=await Device.Discover(stop.Token);var match=devices.FirstOrDefault(d=>d.Id==s.DeviceId);if(match!=null)lock(gate){var next=settings with{DeviceIp=match.Ip};store.Save(next);settings=next;revision++;}}
     catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
     catch(Exception scanEx){Console.Error.WriteLine(scanEx.Message);}
    }
    try{await Task.Delay(1000,stop.Token);}catch(OperationCanceledException){break;}
   }
   try{await Task.Delay(250,stop.Token);}catch(OperationCanceledException){break;}
  }
 }
 public void Dispose(){SystemEvents.PowerModeChanged-=PowerChanged;stop.Cancel();try{Task.WaitAll([loop,frameLoop,networkLoop],1500);}catch(AggregateException){}overlay.Dispose();device.Dispose();stop.Dispose();}
}
