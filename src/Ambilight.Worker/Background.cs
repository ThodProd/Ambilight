using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
namespace Ambilight;
internal static class Background
{
 static string Pipe => "Ambilight."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("THODHUB_DATA_DIR")??"")))[..20];
 static async Task<NamedPipeClientStream> Connect(int timeout)
 {
  var pipe=new NamedPipeClientStream(".",Pipe,PipeDirection.InOut,PipeOptions.Asynchronous);try{await pipe.ConnectAsync(timeout);return pipe;}catch{pipe.Dispose();throw;}
 }
 public static async Task Stop()
 {
  try{using var p=await Connect(1000);using var w=new StreamWriter(p,new UTF8Encoding(false)){AutoFlush=true};await w.WriteLineAsync("STOP");}catch(TimeoutException){}
 }
 public static async Task<int> Proxy()
 {
  NamedPipeClientStream p;
  try{p=await Connect(500);}catch(TimeoutException){
   // The proxy exits promptly on hub EOF; the capture process has its own lifetime.
   Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--background"){UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden});
   p=await Connect(10000);
  }
  using(p)using(var reader=new StreamReader(p))using(var writer=new StreamWriter(p,new UTF8Encoding(false)){AutoFlush=true})
  {
   string? line;while((line=await Console.In.ReadLineAsync())!=null){await writer.WriteLineAsync(line);var response=await reader.ReadLineAsync();if(response==null)throw new IOException("Фоновый процесс остановлен.");Console.WriteLine(response);}
  }
  return 0;
 }
 public static async Task<int> Serve()
 {
  using var mutex=new Mutex(true,"Local\\"+Pipe,out bool owner);if(!owner)return 0;
  using var engine=new Engine();
  while(true){
   using var p=new NamedPipeServerStream(Pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
   await p.WaitForConnectionAsync();using var reader=new StreamReader(p);using var writer=new StreamWriter(p,new UTF8Encoding(false)){AutoFlush=true};
   try{string? line;while((line=await reader.ReadLineAsync())!=null){if(line=="STOP")return 0;string? id=null;object response;try{using var doc=JsonDocument.Parse(line);var root=doc.RootElement;id=root.GetProperty("id").GetString();var result=await engine.Call(root.GetProperty("method").GetString()!,root.TryGetProperty("params",out var args)?args:default);response=new{jsonrpc="2.0",id,result};}catch(Exception ex){response=new{jsonrpc="2.0",id,error=new{code=-32000,message=ex.Message}};}await writer.WriteLineAsync(JsonSerializer.Serialize(response,ConfigStore.Json));}}
   catch(IOException){}
   await engine.Call("calibration.cancel",default);
  }
 }
}
