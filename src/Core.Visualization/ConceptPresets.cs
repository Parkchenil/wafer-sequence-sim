using Contracts;
namespace Core.Visualization;
public static class ConceptPresets
{
    // 모두 공개 시뮬레이터 기본값에서 만든 가상 실험 예시다.
    public static IReadOnlyList<SitePoint> ThirteenSites { get; } = BuildSites();
    private static IReadOnlyList<SitePoint> BuildSites()
    {
        var points=new List<SitePoint>{new(0,0)};
        for(var ring=0;ring<2;ring++)for(var i=0;i<6;i++)
        {var angle=i*Math.PI/3;var r=ring==0?55000:105000;points.Add(new SitePoint(Math.Round(r*Math.Cos(angle)),Math.Round(r*Math.Sin(angle))));}
        return points;
    }
    public static RecipeDto WaitingExample()=>new RecipeDto { Name="분석 기반 13점 · 대기 누적 (가상 예시)",Pattern=SitePattern.Custom,CustomSites=ThirteenSites,VacuumCheckCount=3,ZLiftAfterMeasureUm=600 };
    public static RecipeDto ImprovementExample()=>WaitingExample() with { Name="개선 가정 13점 (가상 예시)",VacuumCheckWait=TimeSpan.FromMilliseconds(600),ZLiftAfterMeasureUm=300,MapInspect=MapInspectMode.FirstSiteOnly,UnloadOverlap=false,Save=SaveMode.Async };
}
