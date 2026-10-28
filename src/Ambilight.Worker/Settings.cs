using System.Text.Json;

namespace Ambilight;
public record LedPoint(double X, double Y, bool Skip = false);
public sealed record Settings
{
 public bool Power { get; set; } = true;
 public int Brightness { get; set; } = 65;
 public int NightBrightness { get; set; } = 20;
 public string Mode { get; set; } = "screen";
 public string Color { get; set; } = "#ffb35c";
 public string Monitor { get; set; } = "";
 public bool Preview { get; set; } = true;
 public int Fps { get; set; } = 30;
 public double Depth { get; set; } = .06;
 public double Smoothing { get; set; } = .55;
 public bool BlackBars { get; set; } = true;
 public bool Schedule { get; set; }
 public string DayAt { get; set; } = "08:00";
 public string NightAt { get; set; } = "22:00";
 public double Gamma { get; set; } = 1;
 public double Red { get; set; } = 1;
 public double Green { get; set; } = 1;
 public double Blue { get; set; } = 1;
 public int Top { get; set; } = 30;
 public int Side { get; set; } = 18;
 public int Bottom { get; set; } = 30;
 public int Offset { get; set; }
 public bool Reverse { get; set; }
 public List<LedPoint> Points { get; set; } = Layout(30,18,30,0,false);
 public string DeviceIp { get; set; } = "";
 public string DeviceId { get; set; } = "";
 public static List<LedPoint> Layout(int top,int side,int bottom,int offset,bool reverse)
 {
  var p=new List<LedPoint>();
  for(int i=0;i<top;i++) p.Add(new((i+.5)/top,0));
  for(int i=0;i<side;i++) p.Add(new(1,(i+.5)/side));
  for(int i=0;i<bottom;i++) p.Add(new(1-(i+.5)/bottom,1));
  for(int i=0;i<side;i++) p.Add(new(0,1-(i+.5)/side));
  if(reverse)p.Reverse();
  return Enumerable.Range(0,p.Count).Select(i=>p[(i+((offset%p.Count)+p.Count)%p.Count)%p.Count]).ToList();
 }
 public void Validate()
 {
  if(Points is null || Points.Count is < 4 or > 360 || Points.Any(p=>p is null || !double.IsFinite(p.X)||!double.IsFinite(p.Y)||p.X<0||p.X>1||p.Y<0||p.Y>1)) throw new ArgumentException("Лента должна содержать от 4 до 360 корректных точек.");
  if(Top<1||Side<1||Bottom<1||Top+2*Side+Bottom>360)throw new ArgumentException("Проверьте количество светодиодов.");
  if(Brightness is <0 or >100 || NightBrightness is <0 or >100 || Fps is <10 or >60)throw new ArgumentException("Яркость: 0–100%, частота: 10–60 кадров/с.");
  if(!double.IsFinite(Depth)||Depth<.01||Depth>.3||!double.IsFinite(Smoothing)||Smoothing<0||Smoothing>.95||!double.IsFinite(Gamma)||Gamma<.2||Gamma>3 || new[]{Red,Green,Blue}.Any(v=>!double.IsFinite(v)||v<0||v>2))throw new ArgumentException("Недопустимые параметры цвета.");
  if(!new[]{"screen","solid","night","day","rainbow","breath"}.Contains(Mode))throw new ArgumentException("Неизвестный режим.");
  if(Color is null || !System.Text.RegularExpressions.Regex.IsMatch(Color,"^#[0-9a-fA-F]{6}$"))throw new ArgumentException("Некорректный цвет.");
  if(!TimeOnly.TryParseExact(DayAt,"HH:mm",out _)||!TimeOnly.TryParseExact(NightAt,"HH:mm",out _)||DayAt==NightAt)throw new ArgumentException("Начало дня и ночи должны отличаться.");
 }
}
public sealed class ConfigStore
{
 public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
 public string DirectoryPath { get; }
 private readonly string path;
 public ConfigStore() { DirectoryPath=Environment.GetEnvironmentVariable("THODHUB_DATA_DIR") ?? throw new InvalidOperationException("Запустите Ambilight через ThodHUB."); Directory.CreateDirectory(DirectoryPath);path=Path.Combine(DirectoryPath,"ambilight.json"); }
 public Settings Load() { if(!File.Exists(path))return new(); var s=JsonSerializer.Deserialize<Settings>(File.ReadAllText(path),Json)??throw new InvalidDataException("Конфигурация пуста.");s.Validate();return s; }
 public void Save(Settings s) {s.Validate();var tmp=path+".new";File.WriteAllText(tmp,JsonSerializer.Serialize(s,Json),new System.Text.UTF8Encoding(false));File.Move(tmp,path,true);}
}
