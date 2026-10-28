$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$root=Split-Path $PSScriptRoot -Parent
$images=[Collections.Generic.List[byte[]]]::new()
$sizes=@(16,24,32,48,64,128,256)
foreach($size in $sizes){
 $bitmap=[Drawing.Bitmap]::new($size,$size)
 $graphics=[Drawing.Graphics]::FromImage($bitmap)
 $graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
 $graphics.ScaleTransform($size/256.0,$size/256.0)
 function Fill-Rect($hex,$x,$y,$w,$h){$brush=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($hex));$graphics.FillRectangle($brush,$x,$y,$w,$h);$brush.Dispose()}
 Fill-Rect '#F5F5F7' 8 8 240 240
 Fill-Rect '#24262B' 49 65 158 107
 Fill-Rect '#DCE6EB' 61 77 134 83
 $brush=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#A2BDB8'))
 $points=[Drawing.PointF[]]@([Drawing.PointF]::new(61,131),[Drawing.PointF]::new(100,109),[Drawing.PointF]::new(145,126),[Drawing.PointF]::new(195,114),[Drawing.PointF]::new(195,160),[Drawing.PointF]::new(61,160))
 $graphics.FillPolygon($brush,$points);$brush.Dispose()
 foreach($line in @(@('#525B65',128,174,128,193),@('#525B65',101,193,155,193),@('#E7BA79',65,48,191,48),@('#92BDB0',32,81,32,154),@('#8EB5D2',224,81,224,154),@('#D8A68A',65,188,80,188),@('#D8A68A',176,188,191,188))){
  $pen=[Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml($line[0]),11);$pen.StartCap='Round';$pen.EndCap='Round'
  $graphics.DrawLine($pen,[single]$line[1],[single]$line[2],[single]$line[3],[single]$line[4]);$pen.Dispose()
 }
 $stream=[IO.MemoryStream]::new();$bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png);$images.Add($stream.ToArray());$stream.Dispose();$graphics.Dispose();$bitmap.Dispose()
}
$output=[IO.File]::Create((Join-Path $root 'assets/ambilight.ico'));$writer=[IO.BinaryWriter]::new($output)
try{
 $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
 $offset=6+16*$sizes.Count
 for($i=0;$i -lt $sizes.Count;$i++){$dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]};$writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$images[$i].Length);$writer.Write([uint32]$offset);$offset+=$images[$i].Length}
 foreach($bytes in $images){$writer.Write($bytes)}
}finally{$writer.Dispose();$output.Dispose()}
