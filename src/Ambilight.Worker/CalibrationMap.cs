namespace Ambilight;

public sealed record CalibrationAnchor(string Kind,int Index,double X,double Y);
public sealed record CalibratedMap(List<LedPoint> Points,bool Clockwise);

public static class CalibrationMap
{
 public static LedPoint At(double t)
 {
  t=(t%4+4)%4;
  return t<1?new(t,0):t<2?new(1,t-1):t<3?new(3-t,1):new(0,4-t);
 }
 public static double Position(double x,double y)
 {
  if(!double.IsFinite(x)||!double.IsFinite(y)||x<0||x>1||y<0||y>1)throw new ArgumentException("Отметьте точку на контуре монитора.");
  if(y==0)return x;if(x==1)return 1+y;if(y==1)return 3-x;if(x==0)return (4-y)%4;
  throw new ArgumentException("Точка должна находиться на краю монитора.");
 }
 static double Distance(double a,double b){double d=Math.Abs(a-b);return Math.Min(d,4-d);}
 static double Travel(double a,double b,int direction)=>(direction*(b-a)%4+4)%4;
 public static List<LedPoint> Uniform(int count)=>Enumerable.Range(0,count).Select(i=>At(4.0*i/count)).ToList();
 public static CalibratedMap Build(int count,IReadOnlyList<CalibrationAnchor> anchors)
 {
  if(count is <4 or >360)throw new ArgumentException("Укажите количество от 4 до 360 LED.");
  string[] kinds=["first","tl","tr","br","bl","last"];
  if(anchors.Count!=6||anchors.Any(a=>a is null)||kinds.Any(k=>anchors.Count(a=>a.Kind==k)!=1))throw new ArgumentException("Отметьте первый LED, четыре угла и последний LED.");
  foreach(var a in anchors){if(a.Index<0||a.Index>=count)throw new ArgumentException("Номер LED выходит за длину ленты.");Position(a.X,a.Y);}
  var first=anchors.Single(a=>a.Kind=="first");var last=anchors.Single(a=>a.Kind=="last");
  if(first.Index!=0||last.Index!=count-1)throw new ArgumentException("Начало и конец должны соответствовать первому и последнему LED.");
  var corners=kinds.Skip(1).Take(4).Select(k=>anchors.Single(a=>a.Kind==k)).ToArray();
  if(corners.Select(a=>a.Index).Distinct().Count()!=4)throw new ArgumentException("Для разных углов выберите разные светодиоды.");
  for(int i=0;i<4;i++)if(Distance(Position(corners[i].X,corners[i].Y),i)>.000001)throw new ArgumentException("Отметьте выделенный угол монитора.");
  var sorted=new List<(int Index,double T)>();
  foreach(var group in anchors.GroupBy(a=>a.Index).OrderBy(g=>g.Key))
  {
   var corner=group.FirstOrDefault(a=>a.Kind is "tl" or "tr" or "br" or "bl");
   double t=Position((corner??group.First()).X,(corner??group.First()).Y);
   if(group.Any(a=>Distance(Position(a.X,a.Y),t)>.06))throw new ArgumentException($"LED {group.Key+1} отмечен в разных местах. Проверьте начало, конец и соответствующий угол.");
   sorted.Add((group.Key,t));
  }
  int direction=0;
  foreach(int candidate in new[]{1,-1})
  {
   double distance=0;bool valid=true;
   for(int i=1;i<sorted.Count;i++){var d=Travel(sorted[i-1].T,sorted[i].T,candidate);if(d<.000001)valid=false;distance+=d;}
   if(valid&&distance<4.000001){if(direction!=0)throw new ArgumentException("Не удалось определить направление. Проверьте LED у четырёх углов.");direction=candidate;}
  }
  if(direction==0)throw new ArgumentException("Номера LED на углах не образуют последовательный обход экрана. Вернитесь к углам и проверьте номера.");
  var points=Enumerable.Repeat(new LedPoint(0,0),count).ToList();
  for(int a=1;a<sorted.Count;a++)
  {
   var left=sorted[a-1];var right=sorted[a];double distance=Travel(left.T,right.T,direction);
   for(int i=left.Index;i<=right.Index;i++)points[i]=At(left.T+direction*distance*(i-left.Index)/(right.Index-left.Index));
  }
  return new(points,direction==1);
 }
}
