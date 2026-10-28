using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Ambilight;
public record FoundDevice(string Id,string Ip,string Name);
public sealed class Device : IDisposable
{
 private readonly HttpClient http=new(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(2)};
 private readonly UdpClient sender=new();
 private readonly string secretPath;
 private string token="";
 private uint seq;
 private readonly uint session=BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
 public Device(string data) {secretPath=Path.Combine(data,"device-secret.dat");if(File.Exists(secretPath))try{token=Secret.Unprotect(File.ReadAllBytes(secretPath));}catch(System.ComponentModel.Win32Exception){Console.Error.WriteLine("Ключ устройства недоступен этой учётной записи Windows. Выполните привязку заново.");}}
 public bool Paired=>token.Length>0;
 public static IPAddress LocalAddress(string ip)
 {
  if(!IPAddress.TryParse(ip,out var address)||address.AddressFamily!=AddressFamily.InterNetwork)throw new ArgumentException("Укажите IPv4-адрес ESP32 в локальной сети.");
  var b=address.GetAddressBytes();if(!(b[0]==10||b[0]==192&&b[1]==168||b[0]==172&&b[1]>=16&&b[1]<=31||b[0]==169&&b[1]==254))throw new ArgumentException("Поддерживаются только адреса локальной сети.");return address;
 }
 public static async Task<List<FoundDevice>> Discover(CancellationToken ct)
 {
  using var udp=new UdpClient(new IPEndPoint(IPAddress.Any,0)){EnableBroadcast=true};
  var nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(8));var request=Encoding.ASCII.GetBytes("ALDISC1 "+nonce);
  var broadcasts=new HashSet<IPAddress>{IPAddress.Broadcast};
  foreach(var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up))foreach(var a in ni.GetIPProperties().UnicastAddresses.Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(a.Address)))
  {var ip=a.Address.GetAddressBytes();var mask=a.IPv4Mask.GetAddressBytes();broadcasts.Add(new IPAddress(ip.Select((b,i)=>(byte)(b|~mask[i])).ToArray()));}
  foreach(var address in broadcasts){try{await udp.SendAsync(request,new IPEndPoint(address,19446),ct);}catch(SocketException){}}
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(3500);var found=new Dictionary<string,FoundDevice>();
  try {while(true){var packet=await udp.ReceiveAsync(timeout.Token);if(packet.Buffer.Length>512)continue;var parts=Encoding.UTF8.GetString(packet.Buffer).Split('|');if(parts.Length==4&&parts[0]=="AL1"&&parts[1]==nonce&&System.Text.RegularExpressions.Regex.IsMatch(parts[2],"^[a-fA-F0-9]{12}$")){var ip=packet.RemoteEndPoint.Address.ToString();LocalAddress(ip);found[parts[2]]=new(parts[2],ip,"Ambilight · "+parts[2][^6..]);}}}
  catch(OperationCanceledException) when(!ct.IsCancellationRequested){}
  return found.Values.ToList();
 }
 public async Task Pair(string ip,string code,CancellationToken ct)
 {
  LocalAddress(ip);if(!System.Text.RegularExpressions.Regex.IsMatch(code,"^[0-9]{6}$"))throw new ArgumentException("Введите шестизначный код со страницы настройки ESP32.");
  using var response=await http.PostAsync($"http://{ip}/pair",new FormUrlEncodedContent(new Dictionary<string,string>{{"code",code}}),ct);
  if(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
   throw new InvalidOperationException("Код привязки неверен, уже использован или сеанс настройки закрыт. Нужен новый шестизначный код со страницы настройки ESP32 — это не пароль Wi-Fi и не номер устройства. Откройте настройку кнопкой на 5 секунд либо командой SETUP через USB. Сохранённая привязка не изменена.");
  if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"ESP32 не выполнила привязку (ответ {(int)response.StatusCode}). Проверьте адрес устройства и повторите настройку. Сохранённая привязка не изменена.");
  var json=await response.Content.ReadFromJsonAsync<JsonElement>(ct);
  var newToken=json.GetProperty("token").GetString()??"";
  if(!System.Text.RegularExpressions.Regex.IsMatch(newToken,"^[a-fA-F0-9]{32}$"))throw new InvalidDataException("Устройство вернуло некорректный ключ.");
  File.WriteAllBytes(secretPath+".new",Secret.Protect(newToken));File.Move(secretPath+".new",secretPath,true);token=newToken;
 }
 public async Task<JsonElement> Status(string ip,CancellationToken ct)
 {
  using var request=new HttpRequestMessage(HttpMethod.Get,$"http://{LocalAddress(ip)}/status");request.Headers.Authorization=new("Bearer",token);
  using var response=await http.SendAsync(request,ct);
  if(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)throw new InvalidOperationException("ESP32 не приняла сохранённый ключ. Выберите «Подключить другую ESP32 / привязать заново» и получите новый код настройки.");
  if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"ESP32 не вернула состояние (ответ {(int)response.StatusCode}). Повторяем соединение автоматически.");
  return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
 }
 public async Task Configure(Settings s,bool power,int brightness,CancellationToken ct)
 {
  var form=new Dictionary<string,string>{{"power",power?"1":"0"},{"brightness",brightness.ToString()},{"nightBrightness",s.NightBrightness.ToString()},{"count",s.Points.Count.ToString()},{"session",session.ToString()}};
  using var request=new HttpRequestMessage(HttpMethod.Post,$"http://{LocalAddress(s.DeviceIp)}/control"){Content=new FormUrlEncodedContent(form)};request.Headers.Authorization=new("Bearer",token);
  using var response=await http.SendAsync(request,ct);response.EnsureSuccessStatusCode();
 }
 public async Task Send(string ip,byte[] colors,CancellationToken ct)
 {
  var data=new byte[16+colors.Length+32];Encoding.ASCII.GetBytes("ALF1",data);BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4),session);BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8),++seq);BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12),(ushort)(colors.Length/3));colors.CopyTo(data,16);HMACSHA256.HashData(Encoding.ASCII.GetBytes(token),data.AsSpan(0,data.Length-32),data.AsSpan(data.Length-32));
  await sender.SendAsync(data,new IPEndPoint(LocalAddress(ip),19447),ct);
 }
 public void Dispose(){http.Dispose();sender.Dispose();}
}
static class Secret
{
 [StructLayout(LayoutKind.Sequential)] struct Blob {public int Length;public nint Data;}
 [DllImport("crypt32",SetLastError=true)] static extern bool CryptProtectData(ref Blob input,string description,nint entropy,nint reserved,nint prompt,int flags,out Blob output);
 [DllImport("crypt32",SetLastError=true)] static extern bool CryptUnprotectData(ref Blob input,nint description,nint entropy,nint reserved,nint prompt,int flags,out Blob output);
 [DllImport("kernel32")] static extern nint LocalFree(nint data);
 public static byte[] Protect(string value)=>Transform(Encoding.UTF8.GetBytes(value),true);
 public static string Unprotect(byte[] value)=>Encoding.UTF8.GetString(Transform(value,false));
 static byte[] Transform(byte[] bytes,bool protect)
 {
  var input=new Blob{Length=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)};Marshal.Copy(bytes,0,input.Data,bytes.Length);
  try {Blob output;bool ok=protect?CryptProtectData(ref input,"Ambilight",0,0,0,1,out output):CryptUnprotectData(ref input,0,0,0,0,1,out output);if(!ok)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());try{var result=new byte[output.Length];Marshal.Copy(output.Data,result,0,result.Length);return result;}finally{LocalFree(output.Data);}}
  finally {Marshal.FreeHGlobal(input.Data);}
 }
}
