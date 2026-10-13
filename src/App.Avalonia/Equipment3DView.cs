using Core.Visualization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;
using Contracts;
namespace App.Avalonia;
public sealed class Equipment3DView : Control
{
    public static readonly StyledProperty<EquipmentSnapshot?> SnapshotProperty=AvaloniaProperty.Register<Equipment3DView,EquipmentSnapshot?>(nameof(Snapshot));
    public static readonly StyledProperty<StepEvent?> ActiveStepProperty=AvaloniaProperty.Register<Equipment3DView,StepEvent?>(nameof(ActiveStep));
    public static readonly StyledProperty<StepEvent?> RobotStepProperty=AvaloniaProperty.Register<Equipment3DView,StepEvent?>(nameof(RobotStep));
    public static readonly StyledProperty<double> WaferDiameterMmProperty=AvaloniaProperty.Register<Equipment3DView,double>(nameof(WaferDiameterMm),300);
    public static readonly StyledProperty<TimeSpan> RobotMoveTimeProperty=AvaloniaProperty.Register<Equipment3DView,TimeSpan>(nameof(RobotMoveTime),TimeSpan.FromSeconds(2.5));
    public static readonly StyledProperty<bool> CutawayProperty=AvaloniaProperty.Register<Equipment3DView,bool>(nameof(Cutaway),true);
    public static readonly StyledProperty<bool> CloseUpProperty=AvaloniaProperty.Register<Equipment3DView,bool>(nameof(CloseUp));
    static Equipment3DView()=>AffectsRender<Equipment3DView>(SnapshotProperty,ActiveStepProperty,RobotStepProperty,WaferDiameterMmProperty,RobotMoveTimeProperty,CutawayProperty,CloseUpProperty);
    public EquipmentSnapshot? Snapshot {get=>GetValue(SnapshotProperty);set=>SetValue(SnapshotProperty,value);}
    public StepEvent? ActiveStep {get=>GetValue(ActiveStepProperty);set=>SetValue(ActiveStepProperty,value);}
    public StepEvent? RobotStep {get=>GetValue(RobotStepProperty);set=>SetValue(RobotStepProperty,value);}
    public double WaferDiameterMm {get=>GetValue(WaferDiameterMmProperty);set=>SetValue(WaferDiameterMmProperty,value);}
    public TimeSpan RobotMoveTime {get=>GetValue(RobotMoveTimeProperty);set=>SetValue(RobotMoveTimeProperty,value);}
    public bool Cutaway {get=>GetValue(CutawayProperty);set=>SetValue(CutawayProperty,value);}
    public bool CloseUp {get=>GetValue(CloseUpProperty);set=>SetValue(CloseUpProperty,value);}
    public Camera3D Camera {get;private set;}=Camera3D.Default;
    private Point? last;
    private readonly DepthRasterizer rasterizer = new();
    private static readonly PrecisionScene precision = PrecisionScene.LoadEmbedded();
    private WriteableBitmap? bitmap;
    private byte[] upload=[];
    public void TransportCamera(){CloseUp=false;Camera=(Camera3D.Default with {Yaw=-.55f,Pitch=.20f,Zoom=1.65f,Target=new System.Numerics.Vector3(-.38f,.10f,.16f)}).Clamp();InvalidateVisual();}
    public void ResetCamera(){Camera=Camera3D.Default;InvalidateVisual();}
    public void Rotate(float yaw,float pitch){Camera=(Camera with {Yaw=Camera.Yaw+yaw,Pitch=Camera.Pitch+pitch}).Clamp();InvalidateVisual();}
    public void Zoom(float factor){Camera=(Camera with {Zoom=Camera.Zoom*factor}).Clamp();InvalidateVisual();}
    protected override void OnPointerPressed(PointerPressedEventArgs e){base.OnPointerPressed(e);last=e.GetPosition(this);e.Pointer.Capture(this);}
    protected override void OnPointerReleased(PointerReleasedEventArgs e){base.OnPointerReleased(e);last=null;e.Pointer.Capture(null);}
    protected override void OnPointerMoved(PointerEventArgs e){base.OnPointerMoved(e);if(last is not { } p)return;var now=e.GetPosition(this);Rotate((float)((now.X-p.X)*.008),(float)((p.Y-now.Y)*.008));last=now;}
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e){base.OnPointerWheelChanged(e);Zoom(e.Delta.Y>0?1.1f:.9f);}
    private static (int Width,int Height) RenderSize(int width,int height)
    {
        width=Math.Max(1,width);height=Math.Max(1,height);
        var scale=Math.Min(1,Math.Sqrt(810000.0/(width*(double)height)));
        return (Math.Max(1,(int)Math.Round(width*scale)),Math.Max(1,(int)Math.Round(height*scale)));
    }
    private static System.Numerics.Vector2? PrecisionPoint(System.Numerics.Vector3 point,Camera3D camera,float width,float height,bool closeUp)
    {
        var c=camera.Clamp();var target=c.Target ?? (closeUp?new System.Numerics.Vector3(.35f,.11f,0):new System.Numerics.Vector3(0,.2f,0));
        var distance=closeUp?.8f:2.1f;
        var eye=new System.Numerics.Vector3(MathF.Sin(c.Yaw)*distance,distance*.43f+MathF.Sin(c.Pitch)*distance*.85f,MathF.Cos(c.Yaw)*distance)+target;
        var view=System.Numerics.Matrix4x4.CreateLookAt(eye,target,System.Numerics.Vector3.UnitY);
        var projection=System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(Math.Clamp(.85f/(c.Zoom*(closeUp?1.65f:1)),.12f,2.3f),Math.Max(.1f,width/Math.Max(1,height)),.01f,100);
        var v=System.Numerics.Vector4.Transform(new System.Numerics.Vector4(point,1),view*projection);
        if(v.W<=.01f)return null;
        return new System.Numerics.Vector2((v.X/v.W+1)*width/2,(1-v.Y/v.W)*height/2);
    }
    private static FormattedText Text(string value,double size)=>new(value,System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface.Default,size,Brushes.White);
    public override void Render(DrawingContext context)
    {
        base.Render(context);context.FillRectangle(new SolidColorBrush(Color.Parse("#101B27")),new Rect(Bounds.Size));
        if(Snapshot is not { } snapshot)return;
        var (width,height)=RenderSize((int)Math.Ceiling(Bounds.Width),(int)Math.Ceiling(Bounds.Height));
        rasterizer.Render(precision.Build(snapshot,Cutaway,CloseUp,WaferDiameterMm,RobotStep,RobotMoveTime),Camera,width,height,CloseUp,true);
        if(bitmap is null || bitmap.PixelSize.Width!=width || bitmap.PixelSize.Height!=height)
        { bitmap?.Dispose();bitmap=new WriteableBitmap(new PixelSize(width,height),new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Opaque); }
        using(var locked=bitmap.Lock())
        {
            if(upload.Length<width*height*4)upload=new byte[width*height*4];rasterizer.CopyBgra(upload);
            for(var y=0;y<height;y++)Marshal.Copy(upload,y*width*4,locked.Address+y*locked.RowBytes,width*4);
        }
        context.DrawImage(bitmap,new Rect(0,0,width,height),new Rect(Bounds.Size));
        var labelBounds=new List<Rect>();
        foreach(var (part,pos) in new[]{("LOAD / CASSETTE",new System.Numerics.Vector3(-.69f,.1f,.28f)),("ROBOT",new System.Numerics.Vector3(-.30f,.10f,-.18f)),("ALIGNER",new System.Numerics.Vector3(-.12f,.11f,.34f)),("CHUCK / XY",new System.Numerics.Vector3(.35f-(float)snapshot.Motion.StageXUm*1e-6f,.12f,(float)snapshot.Motion.StageYUm*1e-6f)),("4-PIN Z",new System.Numerics.Vector3(.35f,.2f+(float)snapshot.Motion.ZUm*.0001f,0))})
        {
            if(CloseUp && part is "LOAD / CASSETTE" or "ROBOT" or "ALIGNER")continue;
            var pt=PrecisionPoint(pos,Camera,(float)Bounds.Width,(float)Bounds.Height,CloseUp);
            if(pt is not { } point)continue;
            var text=Text(part,10);
            foreach(var offset in new[]{0,-18,18,-36,36})
            {
                var rect=new Rect(point.X+5,point.Y+offset,text.Width+4,15);
                if(rect.X<4||rect.Y<4||rect.Right>Bounds.Width-4||rect.Bottom>Bounds.Height-70||labelBounds.Any(r=>r.Intersects(rect)))continue;
                if(offset!=0)context.DrawLine(new Pen(Brushes.Gray,.7),new Point(point.X,point.Y),new Point(rect.X,rect.Y+7));
                context.DrawText(text,rect.TopLeft);labelBounds.Add(rect);break;
            }
        }
        var wait=snapshot.Modules.FirstOrDefault(m=>m.State==ModuleState.Waiting);
        var lines=new[]{"접촉식 4PP 계측기 · 분석 기반 개념 모델 · 가상 반송 상태 연동",
            CloseUp ? ControlText.Probe(snapshot).Replace("\n", " · ") : RobotTransportText.Describe(snapshot.RobotTransport),
            $"진공 {(snapshot.Motion.VacuumConfirmed?"확인":"대기")} · 초점 {(snapshot.Motion.CameraFocused?"확인":"대기")} · {(wait is null?"대기 없음":$"대기 {wait.Module}: {wait.Detail}")}"};
        if (CloseUp) lines = ControlText.Probe(snapshot).Split('\n');
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(225,13,25,37)),new Rect(0,Math.Max(0,Bounds.Height-67),Bounds.Width,67));
        for(var i=0;i<lines.Length;i++)context.DrawText(Text(lines[i],10),new Point(8,Math.Max(0,Bounds.Height-65)+i*21));
    }
}
