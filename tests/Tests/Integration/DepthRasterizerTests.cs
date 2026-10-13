using Core.Visualization;
using System.Numerics;
namespace Tests.Integration;
public class DepthRasterizerTests
{
    private static Matrix4x4 CameraInverse()
    {
        var c=Camera3D.Default;
        var target=new Vector3(0,.2f,0);
        var eye=new Vector3(MathF.Sin(c.Yaw)*2.1f,.903f+MathF.Sin(c.Pitch)*1.785f,MathF.Cos(c.Yaw)*2.1f)+target;
        Matrix4x4.Invert(Matrix4x4.CreateLookAt(eye,target,Vector3.UnitY),out var inverse);return inverse;
    }
    private static Face3D Triangle(float z,Rgb color,string name)
    {
        var inverse=CameraInverse();
        return new Face3D([Vector3.Transform(new Vector3(-.2f,-.2f,z),inverse),Vector3.Transform(new Vector3(.2f,-.2f,z),inverse),Vector3.Transform(new Vector3(0,.2f,z),inverse)],color,name);
    }
    [Fact] public void NearFaceWins_AndBgraChannelsRemainCorrect()
    {
        var red=Triangle(-1,new Rgb(255,0,0),"red");var blue=Triangle(-2,new Rgb(0,0,255),"blue");
        var r=new DepthRasterizer();r.Render([blue,red],Camera3D.Default,160,120,precision:true);
        var first=r.Pixels.ToArray();var center=first[60*160+80];
        Assert.True(((center>>16)&255)>(center&255));
        r.Render([red,blue],Camera3D.Default,160,120,precision:true);
        Assert.Equal(first,r.Pixels.ToArray());
        r.Render([blue],Camera3D.Default,160,120,precision:true);
        center=r.Pixels[60*160+80];Assert.True((center&255)>((center>>16)&255));
        var bytes=new byte[160*120*4];r.CopyBgra(bytes);
        Assert.True(bytes[(60*160+80)*4]>bytes[(60*160+80)*4+2]);
    }
    [Fact] public void NearPlaneCrossing_RetainsVisiblePixels_AndRepeatedFrameIsStable()
    {
        var inverse=CameraInverse();
        var face=new Face3D([Vector3.Transform(new Vector3(-.15f,-.15f,-.005f),inverse),Vector3.Transform(new Vector3(.15f,-.15f,-.2f),inverse),Vector3.Transform(new Vector3(0,.15f,-.2f),inverse)],new Rgb(255,255,255),"near");
        var r=new DepthRasterizer();r.Render([face],Camera3D.Default,120,90,precision:true);
        var first=r.Pixels.ToArray();Assert.Contains(first,p=>p!=0xff101b27u);
        r.Render([face],Camera3D.Default,120,90,precision:true);Assert.Equal(first,r.Pixels.ToArray());
    }
    [Fact] public void IntersectingTriangles_ShowDifferentFrontColors_RegardlessOfOrder()
    {
        var inverse=CameraInverse();
        Face3D Make(float leftDepth,float rightDepth,Rgb color,string name)=>new(
            [Vector3.Transform(new Vector3(-.3f,-.25f,leftDepth),inverse),Vector3.Transform(new Vector3(.3f,-.25f,rightDepth),inverse),Vector3.Transform(new Vector3(0,.3f,(leftDepth+rightDepth)/2),inverse)],color,name);
        var red=Make(-1,-2,new Rgb(255,0,0),"red");
        var blue=Make(-2,-1,new Rgb(0,0,255),"blue");
        var raster=new DepthRasterizer();raster.Render([red,blue],Camera3D.Default,180,120,precision:true);
        var first=raster.Pixels.ToArray();
        raster.Render([blue,red],Camera3D.Default,180,120,precision:true);
        Assert.Equal(first,raster.Pixels.ToArray());
        Assert.Contains(first,p=>((p>>16)&255)>(p&255)+20);
        Assert.Contains(first,p=>(p&255)>((p>>16)&255)+20);
    }
}
