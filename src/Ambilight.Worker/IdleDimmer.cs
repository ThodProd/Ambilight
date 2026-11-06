namespace Ambilight;
internal sealed class IdleDimmer
{
 byte[] previous=[];double lastChange,lastCheck=-1,lastTick,level=1;
 public double Update(byte[] pixels,double now,bool enabled,int minutes)
 {
  double dt=Math.Clamp(now-lastTick,0,.25);lastTick=now;
  if(!enabled){lastChange=now;previous=[];level=1;return level;}
  if(now-lastCheck>=.25){lastCheck=now;bool changed=previous.Length!=pixels.Length;
   if(!changed)for(int i=0;i<pixels.Length;i++)if(Math.Abs(pixels[i]-previous[i])>3){changed=true;break;}
   if(changed){lastChange=now;previous=pixels.ToArray();}
  }
  double target=now-lastChange>=minutes*60?0:1;
  if(target>level)level=Math.Min(1,level+dt/1.5);
  else if(target<level)level=Math.Max(0,level-dt/3);
  return level;
 }
}
