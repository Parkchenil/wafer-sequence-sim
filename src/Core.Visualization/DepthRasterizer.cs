using System.Numerics;
namespace Core.Visualization;

public sealed class DepthRasterizer
{
    private static readonly byte[] Srgb=Enumerable.Range(0,8193).Select(i=>{var v=i/4096f;return (byte)Math.Clamp((int)MathF.Round((v<=.0031308f?12.92f*v:1.055f*MathF.Pow(v,1/2.4f)-.055f)*255),0,255);}).ToArray();
    private float[] depth=[];
    private uint[] pixels=[];
    public int Width {get;private set;}
    public int Height {get;private set;}
    public ReadOnlySpan<uint> Pixels=>pixels.AsSpan(0,Width*Height);
    public void CopyBgra(byte[] destination)
    {
        if(destination.Length<Width*Height*4)throw new ArgumentException("픽셀 버퍼 크기 부족",nameof(destination));
        Buffer.BlockCopy(pixels,0,destination,0,Width*Height*4);
    }
    private readonly struct Vertex(Vector3 position,Vector3 normal)
    {
        public readonly Vector3 P=position;
        public readonly Vector3 N=normal;
        public static Vertex Lerp(Vertex a,Vertex b,float t)=>new(Vector3.Lerp(a.P,b.P,t),Vector3.Lerp(a.N,b.N,t));
    }
    private readonly struct Screen(float x,float y,float reciprocalDepth,float shade,float specular)
    {
        public readonly float X=x,Y=y,D=reciprocalDepth;
        public readonly float Shade=shade,Specular=specular;
    }
    public void Render(IReadOnlyList<Face3D> faces,Camera3D camera,int width,int height,bool closeUp=false,bool precision=false)
    {
        Width=Math.Clamp(width,1,4096);Height=Math.Clamp(height,1,4096);
        var count=Width*Height;
        if(pixels.Length<count){pixels=new uint[count];depth=new float[count];}
        Array.Fill(pixels,0xff101b27u,0,count);Array.Fill(depth,float.PositiveInfinity,0,count);
        var c=camera.Clamp();var target=c.Target ?? (precision?(closeUp?new Vector3(.35f,.11f,0):new Vector3(0,.2f,0)):(closeUp?new Vector3(.85f,.65f,0):new Vector3(0,.75f,0)));
        var distance=precision?(closeUp?.8f:2.1f):7f;var eye=new Vector3(MathF.Sin(c.Yaw)*distance,distance*.43f+MathF.Sin(c.Pitch)*distance*.85f,MathF.Cos(c.Yaw)*distance)+target;
        var view=Matrix4x4.CreateLookAt(eye,target,Vector3.UnitY);
        var projection=Matrix4x4.CreatePerspectiveFieldOfView(Math.Clamp(.85f/(c.Zoom*(closeUp?1.65f:1)),.12f,2.3f),(float)Width/Height,.01f,100);
        Span<Vertex> clipped=stackalloc Vertex[16];Span<Vertex> input=stackalloc Vertex[16];Span<Screen> screen=stackalloc Screen[16];
        foreach(var face in faces)
        {
            var vertices=face.Vertices;if(vertices.Length<3)continue;
            for(var fan=1;fan+1<vertices.Length;fan++)
            {
                var a=Vector3.Transform(vertices[0],face.Transform);var b=Vector3.Transform(vertices[fan],face.Transform);var d=Vector3.Transform(vertices[fan+1],face.Transform);
                var flat=Vector3.Cross(b-a,d-a);if(flat.LengthSquared()<1e-18f)continue;flat=Vector3.Normalize(flat);
                input[0]=new Vertex(Vector3.Transform(a,view),face.Normals is {Length:>0}?Vector3.Normalize(Vector3.TransformNormal(face.Normals[0],face.Transform)):flat);
                input[1]=new Vertex(Vector3.Transform(b,view),face.Normals is {Length:>0}?Vector3.Normalize(Vector3.TransformNormal(face.Normals[fan],face.Transform)):flat);
                input[2]=new Vertex(Vector3.Transform(d,view),face.Normals is {Length:>0}?Vector3.Normalize(Vector3.TransformNormal(face.Normals[fan+1],face.Transform)):flat);
                var length=0;var previous=input[2];var wasInside=previous.P.Z<=-.0101f;
                for(var i=0;i<3;i++)
                {
                    var current=input[i];var inside=current.P.Z<=-.0101f;
                    if(inside!=wasInside)clipped[length++]=Vertex.Lerp(previous,current,(-.0101f-previous.P.Z)/(current.P.Z-previous.P.Z));
                    if(inside)clipped[length++]=current;
                    previous=current;wasInside=inside;
                }
                if(length<3)continue;
                for(var i=0;i<length;i++)
                {
                    var p=clipped[i].P;var q=Vector4.Transform(new Vector4(p,1),projection);
                    var normal=clipped[i].N.LengthSquared()>1e-10f?Vector3.Normalize(clipped[i].N):Vector3.UnitY;
                    var light=Vector3.Normalize(new Vector3(.4f,.8f,.5f));
                    var diffuse=MathF.Abs(Vector3.Dot(normal,light));
                    var shininess=4+96*(1-Math.Clamp(face.Roughness,0,1));
                    var specular=MathF.Pow(MathF.Max(0,Vector3.Dot(Vector3.Reflect(-light,normal),Vector3.UnitZ)),shininess);
                    screen[i]=new Screen((q.X/q.W+1)*Width*.5f,(1-q.Y/q.W)*Height*.5f,1/-p.Z,.25f+.7f*diffuse,(.04f+.65f*face.Metallic)*specular);
                }
                for(var i=1;i+1<length;i++)Triangle(screen[0],screen[i],screen[i+1],face);
            }
        }
    }
    private void Triangle(Screen a,Screen b,Screen c,Face3D face)
    {
        if(!float.IsFinite(a.X+a.Y+b.X+b.Y+c.X+c.Y))return;
        var area=Edge(a,b,c.X,c.Y);if(MathF.Abs(area)<1e-8f)return;
        var minX=Math.Clamp((int)MathF.Floor(MathF.Min(a.X,MathF.Min(b.X,c.X))),0,Width-1);
        var maxX=Math.Clamp((int)MathF.Ceiling(MathF.Max(a.X,MathF.Max(b.X,c.X))),0,Width-1);
        var minY=Math.Clamp((int)MathF.Floor(MathF.Min(a.Y,MathF.Min(b.Y,c.Y))),0,Height-1);
        var maxY=Math.Clamp((int)MathF.Ceiling(MathF.Max(a.Y,MathF.Max(b.Y,c.Y))),0,Height-1);
        for(var y=minY;y<=maxY;y++)for(var x=minX;x<=maxX;x++)
        {
            var u=Edge(b,c,x+.5f,y+.5f)/area;var v=Edge(c,a,x+.5f,y+.5f)/area;var w=1-u-v;
            if(u<0||v<0||w<0)continue;
            var reciprocal=u*a.D+v*b.D+w*c.D;var distance=1/reciprocal;var index=y*Width+x;
            if(reciprocal<=0||distance>=depth[index])continue;
            var brightness=(a.Shade*u*a.D+b.Shade*v*b.D+c.Shade*w*c.D)/reciprocal;
            var highlight=(a.Specular*u*a.D+b.Specular*v*b.D+c.Specular*w*c.D)/reciprocal;
            byte Output(byte value)
            {
                var linear=Math.Clamp(value/255f*brightness+highlight,0,2);
                return face.LinearColor?Srgb[(int)(linear*4096)]: (byte)Math.Clamp((int)MathF.Round(linear*255),0,255);
            }
            var r=Output(face.Color.R);var g=Output(face.Color.G);var blue=Output(face.Color.B);
            depth[index]=distance;pixels[index]=0xff000000u|((uint)r<<16)|((uint)g<<8)|blue;
        }
    }
    private static float Edge(Screen a,Screen b,float x,float y)=>(x-a.X)*(b.Y-a.Y)-(y-a.Y)*(b.X-a.X);
}
