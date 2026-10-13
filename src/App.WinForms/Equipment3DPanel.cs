using Core.Visualization;
using Contracts;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
namespace App.WinForms;
internal sealed class Equipment3DPanel : Panel
{
    [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public EquipmentSnapshot? Snapshot {get;set;}
    [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public StepEvent? ActiveStep {get;set;}
    [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public StepEvent? RobotStep {get;set;}
    [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public double WaferDiameterMm {get;set;}=300;
    [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public TimeSpan RobotMoveTime {get;set;}=TimeSpan.FromSeconds(2.5);
    [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Cutaway {get;set;}=true;
    [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool CloseUp {get;set;}
    [Browsable(false),DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Camera3D Camera {get;private set;}=Camera3D.Default;
    private Point? last;
    private readonly DepthRasterizer rasterizer = new();
    private static readonly PrecisionScene precision = PrecisionScene.LoadEmbedded();
    private Bitmap? bitmap;
    private byte[] upload=[];
    public Equipment3DPanel(){DoubleBuffered=true;BackColor=Color.FromArgb(16,27,39);}
    public void ResetCamera(){Camera=Camera3D.Default;Invalidate();}
    public void Rotate(float yaw,float pitch){Camera=(Camera with { Yaw=Camera.Yaw+yaw,Pitch=Camera.Pitch+pitch }).Clamp();Invalidate();}
    public void Zoom(float factor){Camera=(Camera with { Zoom=Camera.Zoom*factor }).Clamp();Invalidate();}
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);last=e.Location;Capture=true;}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);last=null;Capture=false;}
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(last is not { } p)return;Rotate((e.X-p.X)*.008f,(p.Y-e.Y)*.008f);last=e.Location;}
    protected override void OnMouseWheel(MouseEventArgs e){base.OnMouseWheel(e);Zoom(e.Delta>0?1.1f:.9f);}
    private static (int Width,int Height) RenderSize(int width,int height)
    {
        width=Math.Max(1,width);height=Math.Max(1,height);
        var scale=Math.Min(1,Math.Sqrt(810000.0/(width*(double)height)));
        return (Math.Max(1,(int)Math.Round(width*scale)),Math.Max(1,(int)Math.Round(height*scale)));
    }
    private static System.Numerics.Vector2? PrecisionPoint(System.Numerics.Vector3 point,Camera3D camera,float width,float height,bool closeUp)
    {
        var c=camera.Clamp();var target=closeUp?new System.Numerics.Vector3(.35f,.11f,0):new System.Numerics.Vector3(0,.2f,0);
        var distance=closeUp?.8f:2.1f;
        var eye=new System.Numerics.Vector3(MathF.Sin(c.Yaw)*distance,distance*.43f+MathF.Sin(c.Pitch)*distance*.85f,MathF.Cos(c.Yaw)*distance)+target;
        var view=System.Numerics.Matrix4x4.CreateLookAt(eye,target,System.Numerics.Vector3.UnitY);
        var projection=System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(Math.Clamp(.85f/(c.Zoom*(closeUp?1.65f:1)),.12f,2.3f),Math.Max(.1f,width/Math.Max(1,height)),.01f,100);
        var v=System.Numerics.Vector4.Transform(new System.Numerics.Vector4(point,1),view*projection);
        if(v.W<=.01f)return null;
        return new System.Numerics.Vector2((v.X/v.W+1)*width/2,(1-v.Y/v.W)*height/2);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);if(Snapshot is not { } snapshot)return;
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        var (width,height)=RenderSize(Width,Height);
        rasterizer.Render(precision.Build(snapshot,Cutaway,CloseUp,WaferDiameterMm,RobotStep,RobotMoveTime),Camera,width,height,CloseUp,true);
        if(bitmap is null || bitmap.Width!=width || bitmap.Height!=height){bitmap?.Dispose();bitmap=new Bitmap(width,height,PixelFormat.Format32bppArgb);}
        var bits=bitmap.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
        try{if(upload.Length<width*height*4)upload=new byte[width*height*4];rasterizer.CopyBgra(upload);for(var y=0;y<height;y++)Marshal.Copy(upload,y*width*4,bits.Scan0+y*bits.Stride,width*4);}
        finally{bitmap.UnlockBits(bits);}
        e.Graphics.DrawImage(bitmap,new Rectangle(0,0,Width,Height));
        using var font=new Font("Segoe UI",8.5f);
        var labelBounds=new List<RectangleF>();
        foreach(var (label,pos) in new[]{("LOAD / CASSETTE",new System.Numerics.Vector3(-.69f,.1f,.28f)),("ROBOT",new System.Numerics.Vector3(-.30f,.10f,-.18f)),("ALIGNER",new System.Numerics.Vector3(-.12f,.11f,.34f)),("CHUCK / XY",new System.Numerics.Vector3(.35f-(float)snapshot.Motion.StageXUm*1e-6f,.12f,(float)snapshot.Motion.StageYUm*1e-6f)),("4-PIN Z",new System.Numerics.Vector3(.35f,.2f+(float)snapshot.Motion.ZUm*.0001f,0))})
        {
            if(CloseUp && label is "LOAD / CASSETTE" or "ROBOT" or "ALIGNER")continue;
            var point=PrecisionPoint(pos,Camera,Width,Height,CloseUp);
            if(point is not { } pt)continue;
            var size=e.Graphics.MeasureString(label,font);
            foreach(var offset in new[]{0,-18,18,-36,36})
            {
                var rect=new RectangleF(pt.X+5,pt.Y+offset,size.Width+4,Math.Max(15,size.Height));
                if(rect.X<4||rect.Y<4||rect.Right>Width-4||rect.Bottom>Height-70||labelBounds.Any(r=>r.IntersectsWith(rect)))continue;
                if(offset!=0)e.Graphics.DrawLine(Pens.Gray,pt.X,pt.Y,rect.X,rect.Y+7);
                e.Graphics.DrawString(label,font,Brushes.WhiteSmoke,rect.Location);labelBounds.Add(rect);break;
            }
        }
        var wait=snapshot.Modules.FirstOrDefault(m=>m.State==ModuleState.Waiting);
        using var overlay=new SolidBrush(Color.FromArgb(225,13,25,37));e.Graphics.FillRectangle(overlay,0,Math.Max(0,Height-66),Width,66);
        var lines=new[]{"접촉식 4PP 계측기 · 분석 기반 개념 모델 · 내부 배치/로봇 경로 추정",
            $"웨이퍼 {snapshot.CurrentWaferId??"—"} · 단계 {ActiveStep?.Phase.ToString()??"대기"} · Z 표시 ×{Concept3D.ZDisplayMagnification:0}: {snapshot.Motion.ZUm:F0} µm",
            $"진공 {(snapshot.Motion.VacuumConfirmed?"확인":"대기")} · 초점 {(snapshot.Motion.CameraFocused?"확인":"대기")} · {(wait is null?"대기 없음":$"대기 {wait.Module}: {wait.Detail}")}"};
        for(var i=0;i<lines.Length;i++)e.Graphics.DrawString(lines[i],font,Brushes.WhiteSmoke,8,Math.Max(0,Height-65)+i*21);
    }
}
