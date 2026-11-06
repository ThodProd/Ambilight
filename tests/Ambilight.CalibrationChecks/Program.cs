using Ambilight;

static void Check(bool condition,string message)
{
 if(!condition)throw new InvalidOperationException(message);
}

static CalibrationAnchor A(string kind,int index,double x,double y)=>new(kind,index,x,y);

var clockwise=CalibrationMap.Build(20,
[
 A("first",0,.5,0),A("tl",18,0,0),A("tr",3,1,0),
 A("br",8,1,1),A("bl",13,0,1),A("last",19,.25,0)
]);
Check(clockwise.Clockwise,"Не определён обход по часовой стрелке.");
Check(clockwise.Points.Count==20,"Потеряно количество LED.");
Check(clockwise.Points[3]==new LedPoint(1,0),"Не сохранён верхний правый угол.");
Check(clockwise.Points[13]==new LedPoint(0,1),"Не сохранён нижний левый угол.");

var counterClockwise=CalibrationMap.Build(20,
[
 A("first",0,.5,0),A("tl",3,0,0),A("tr",18,1,0),
 A("br",13,1,1),A("bl",8,0,1),A("last",19,.75,0)
]);
Check(!counterClockwise.Clockwise,"Не определён обход против часовой стрелки.");
Check(counterClockwise.Points[8]==new LedPoint(0,1),"Неверно рассчитан угол при обратном обходе.");
Check(counterClockwise.Points.All(p=>p.X is >=0 and <=1&&p.Y is >=0 and <=1),"Точка вышла за контур монитора.");

try
{
 CalibrationMap.Build(20,
 [
  A("first",0,.5,0),A("tl",3,0,0),A("tr",3,1,0),
  A("br",8,1,1),A("bl",13,0,1),A("last",19,.25,0)
 ]);
 throw new InvalidOperationException("Повтор одного LED на разных углах не отклонён.");
}
catch(ArgumentException){}

var square116=CalibrationMap.Build(116,
[
 A("first",0,0,0),A("tl",0,0,0),A("tr",29,1,0),
 A("br",58,1,1),A("bl",87,0,1),A("last",115,0,0)
]);
Check(square116.Clockwise,"Для рамки 116 LED неверно определено направление.");
Check(square116.Points[0]==new LedPoint(0,0)&&square116.Points[115]==new LedPoint(0,0),"Первый и последний LED рамки должны сходиться в стартовом углу.");

var serviceSettings=new Settings{Points=Settings.Layout(30,18,30,0,false).Concat(Enumerable.Repeat(new LedPoint(0,0,true),4)).ToList(),UtilityLedCount=4};
serviceSettings.NormalizeUtilityLeds();
Check(serviceSettings.UtilityLeds.Select(x=>x.Function).SequenceEqual(["connection","brightness","screen","mode"]),"Не назначены четыре разные функции служебных LED.");
serviceSettings.Validate();

Console.WriteLine("Calibration checks passed.");

var idle=new IdleDimmer();byte[] pixels=[50,50,50];double fade=1;
for(int tick=0;tick<=240;tick++)fade=idle.Update(pixels,tick*.25,true,1);
Check(fade>0&&fade<1,"Сон должен начаться плавно после минуты.");
for(int tick=241;tick<=252;tick++)fade=idle.Update(pixels,tick*.25,true,1);
Check(fade==0,"Неподвижное изображение должно погасить ленту.");
pixels[0]=100;fade=idle.Update(pixels,63.25,true,1);
Check(fade>0&&fade<1,"Изменение изображения должно плавно разбудить ленту.");
for(int tick=254;tick<=260;tick++)fade=idle.Update(pixels,tick*.25,true,1);
Check(fade==1,"После пробуждения яркость должна восстановиться.");
Check(idle.Update(pixels,66,false,1)==1,"Отключённый сон не должен затемнять ленту.");
Console.WriteLine("Idle sleep checks passed.");
