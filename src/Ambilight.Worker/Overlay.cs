using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ambilight;

// Shows the real capture strips on the selected monitor while the depth slider is
// being adjusted. The window is topmost and click-through, so it never blocks the
// desktop or the hub. Draw it on one screen at a time and keep it out of Alt+Tab.
internal sealed class RegionOverlay : IDisposable
{
 private readonly object gate=new();
 private readonly Thread thread;
 private readonly System.Threading.Timer watchdog;
 private OverlayForm? form;
 private bool disposed,wanted;
 private long touch;
 private double depth=.06;
 private string monitor="";

 public RegionOverlay()
 {
  thread=new Thread(Run){IsBackground=true,Name="Ambilight capture region"};
  thread.SetApartmentState(ApartmentState.STA);
  thread.Start();
  // If the hub disappears without sending enabled:false, never leave a stray
  // topmost window over the desktop.
  watchdog=new System.Threading.Timer(_=>Watchdog(),null,5000,5000);
 }

 private void Watchdog()
 {
  OverlayForm? window;
  lock(gate){if(!wanted||Environment.TickCount64-touch<=20000)return;wanted=false;window=form;}
  Post(window);
 }

 private void Run()
 {
  var window=new OverlayForm();
  _=window.Handle; // Create the handle before the first bridge call can arrive.
  lock(gate){if(disposed){window.Dispose();return;}form=window;}
  window.Apply(Desired());
  Application.Run(new ApplicationContext());
 }

 private (bool Enabled,double Depth,string Monitor) Desired(){lock(gate)return(wanted,depth,monitor);}

 public void Show(double value,string display)
 {
  if(!double.IsFinite(value))return;
  OverlayForm? window;
  lock(gate){wanted=true;touch=Environment.TickCount64;depth=Math.Clamp(value,.01,.3);monitor=display??"";window=form;}
  Post(window);
 }

 public void Hide()
 {
  OverlayForm? window;
  lock(gate){wanted=false;window=form;}
  Post(window);
 }

 private void Post(OverlayForm? window)
 {
  if(window is null||window.IsDisposed)return;
  try{window.BeginInvoke(()=>window.Apply(Desired()));}catch(InvalidOperationException){}
 }

 public void Dispose()
 {
  watchdog.Dispose();
  OverlayForm? window;
  lock(gate){disposed=true;wanted=false;window=form;form=null;}
  if(window is null)return;
  try{window.BeginInvoke(()=>{window.Close();Application.ExitThread();});}catch(InvalidOperationException){}
  thread.Join(800);
 }
}

internal sealed class OverlayForm : Form
{
 const int WS_EX_TRANSPARENT=0x00000020;
 const int WS_EX_TOOLWINDOW=0x00000080;
 const int WS_EX_TOPMOST=0x00000008;
 const int WS_EX_NOACTIVATE=0x08000000;
 const int CORNER_PREFERENCE=33;
 const int DO_NOT_ROUND=1;
 [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint handle,int attribute,ref int value,int size);
 private static readonly Color Key=Color.FromArgb(1,2,3);
 private static readonly Color Band=Color.FromArgb(54,118,232);
 private static readonly Color Edge=Color.FromArgb(21,58,136);
 private double depth=.06;

 public OverlayForm()
 {
  FormBorderStyle=FormBorderStyle.None;
  StartPosition=FormStartPosition.Manual;
  ShowInTaskbar=false;
  TopMost=true;
  MinimizeBox=false;
  MaximizeBox=false;
  AutoScaleMode=AutoScaleMode.None;
  BackColor=Key;
  TransparencyKey=Key;
  Opacity=.36;
  DoubleBuffered=true;
  SetStyle(ControlStyles.ResizeRedraw,true);
  Text="Ambilight";
 }

 protected override bool ShowWithoutActivation=>true;

 protected override CreateParams CreateParams
 {
  get
  {
   var cp=base.CreateParams;
   cp.ExStyle|=WS_EX_TRANSPARENT|WS_EX_TOOLWINDOW|WS_EX_TOPMOST|WS_EX_NOACTIVATE;
   return cp;
  }
 }

 public void Apply((bool Enabled,double Depth,string Monitor) state)
 {
  if(IsDisposed)return;
  depth=state.Depth;
  if(!state.Enabled){if(Visible)Hide();return;}
  var screen=string.IsNullOrEmpty(state.Monitor)?Screen.PrimaryScreen:Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==state.Monitor);
  screen??=Screen.PrimaryScreen;
  if(screen is null)return;
  SuspendLayout();
  Bounds=screen.Bounds;
  ResumeLayout();
  if(!Visible)Show();
  TopMost=true;
  if(Handle!=0)
  {
   int corner=DO_NOT_ROUND;
   try{DwmSetWindowAttribute(Handle,CORNER_PREFERENCE,ref corner,sizeof(int));}catch(DllNotFoundException){}
  }
  Invalidate();
 }

 protected override void OnPaint(PaintEventArgs e)
 {
  var g=e.Graphics;
  g.Clear(Key);
  int width=ClientSize.Width,height=ClientSize.Height;
  if(width<32||height<32)return;
  // Same fractions the capture loop uses: depth of the width for the sides and
  // depth of the height for the top and bottom strips.
  int bandX=Math.Clamp((int)Math.Round(width*depth),1,width/2);
  int bandY=Math.Clamp((int)Math.Round(height*depth),1,height/2);
  using(var brush=new SolidBrush(Band))
  {
   g.FillRectangle(brush,0,0,width,bandY);
   g.FillRectangle(brush,0,height-bandY,width,bandY);
   g.FillRectangle(brush,0,0,bandX,height);
   g.FillRectangle(brush,width-bandX,0,bandX,height);
  }
  using var pen=new Pen(Edge,2);
  g.DrawLine(pen,0,bandY,width,bandY);
  g.DrawLine(pen,0,height-bandY,width,height-bandY);
  g.DrawLine(pen,bandX,0,bandX,height);
  g.DrawLine(pen,width-bandX,0,width-bandX,height);
 }
}
