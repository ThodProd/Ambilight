using System.Runtime.InteropServices;
namespace Ambilight;
internal sealed class Capture : IDisposable
{
 [DllImport("Ambilight.Capture.dll",CharSet=CharSet.Unicode,CallingConvention=CallingConvention.Cdecl)] private static extern int capture_open(string name,out nint handle);
 [DllImport("Ambilight.Capture.dll",CallingConvention=CallingConvention.Cdecl)] private static extern int capture_read(nint handle,byte[] rgb,int width,int height);
 [DllImport("Ambilight.Capture.dll",CallingConvention=CallingConvention.Cdecl)] private static extern void capture_close(nint handle);
 private nint handle;
 private string name="";
 public byte[] Pixels {get;}=new byte[192*108*3];
 public bool HasFrame {get;private set;}
 public void Read(string display)
 {
  if(name!=display)Dispose();
  if(handle==0) {var hr=capture_open(display,out handle);if(hr<0)Marshal.ThrowExceptionForHR(hr); name=display;}
  var result=capture_read(handle,Pixels,192,108);
  if(result<0) {Dispose();Marshal.ThrowExceptionForHR(result);}
  if(result==0)HasFrame=true;
 }
 public void Dispose() {if(handle!=0)capture_close(handle);handle=0;name="";HasFrame=false;Array.Clear(Pixels);}
}
internal static class Lighting
{
 public static string EffectiveMode(Settings s,DateTime now)
 {
  if(!s.Schedule)return s.Mode;
  var t=TimeOnly.FromDateTime(now);var day=TimeOnly.ParseExact(s.DayAt,"HH:mm");var night=TimeOnly.ParseExact(s.NightAt,"HH:mm");
  bool isDay=day<night?t>=day&&t<night:t>=day||t<night;
  return isDay?s.Mode:"night";
 }
 public static byte[] Frame(Settings s,byte[] pixels,string mode,double elapsed,int calibration)
 {
  var frame=new byte[s.Points.Count*3];var hex=Convert.FromHexString(s.Color[1..]);
  int left=0,right=191,top=0,bottom=107;
  if(mode=="screen"&&s.BlackBars)
  {
   bool DarkRow(int y) {long sum=0;for(int x=0;x<192;x++)for(int c=0;c<3;c++)sum+=pixels[(y*192+x)*3+c];return sum<192*3*7;}
   bool DarkCol(int x) {long sum=0;for(int y=0;y<108;y++)for(int c=0;c<3;c++)sum+=pixels[(y*192+x)*3+c];return sum<108*3*7;}
   while(top<22&&DarkRow(top))top++;while(bottom>85&&DarkRow(bottom))bottom--;
   while(left<38&&DarkCol(left))left++;while(right>153&&DarkCol(right))right--;
  }
  for(int i=0;i<s.Points.Count;i++)
  {
   var p=s.Points[i];if(p.Skip&&calibration<0)continue;double[] rgb=[0,0,0];
   if(calibration>=0) {if(calibration==i)rgb=[255,255,255];}
   else if(mode=="screen")
   {
    var px=left+p.X*(right-left);var py=top+p.Y*(bottom-top);
    int dx=Math.Max(1,(int)(192*s.Depth)),dy=Math.Max(1,(int)(108*s.Depth));
    // The nearest edge selects inward depth; tangential width follows LED density.
    double[] edges=[p.Y,1-p.X,1-p.Y,p.X];int edge=Array.IndexOf(edges,edges.Min());
    int x0,x1,y0,y1;
    if(edge is 0 or 2) {int half=Math.Max(1,192/Math.Max(2,edge==0?s.Top:s.Bottom)/2);x0=(int)px-half;x1=(int)px+half;y0=edge==0?(int)py:(int)py-dy;y1=edge==0?(int)py+dy:(int)py;}
    else {int half=Math.Max(1,108/Math.Max(2,s.Side)/2);y0=(int)py-half;y1=(int)py+half;x0=edge==3?(int)px:(int)px-dx;x1=edge==3?(int)px+dx:(int)px;}
    int count=0;
    for(int y=Math.Clamp(y0,0,107);y<=Math.Clamp(y1,0,107);y++)for(int x=Math.Clamp(x0,0,191);x<=Math.Clamp(x1,0,191);x++){for(int c=0;c<3;c++)rgb[c]+=pixels[(y*192+x)*3+c];count++;}
    for(int c=0;c<3;c++)rgb[c]/=Math.Max(1,count);
   }
   else if(mode=="night")rgb=[255,143,49];
   else if(mode=="day")rgb=[190,221,255];
   else if(mode=="rainbow") {double h=(elapsed*.05+(double)i/s.Points.Count)%1;rgb=Hsv(h);}
   else {double f=mode=="breath"?.25+.75*(.5+.5*Math.Sin(elapsed*1.3)):1;rgb=[hex[0]*f,hex[1]*f,hex[2]*f];}
   var gains=new[]{s.Red,s.Green,s.Blue};
   for(int c=0;c<3;c++)frame[i*3+c]=(byte)Math.Clamp(Math.Round(Math.Pow(rgb[c]/255,s.Gamma)*255*gains[c]),0,255);
  }
  return frame;
 }
 static double[] Hsv(double h) {double k=h*6,x=255*(1-Math.Abs(k%2-1));return (int)k switch {0=>[255,x,0],1=>[x,255,0],2=>[0,255,x],3=>[0,x,255],4=>[x,0,255],_=>[255,0,x]};}
}
