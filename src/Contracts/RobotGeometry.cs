namespace Contracts;

/// <summary>GLB와 가상장치 공통 좌표: 미터, Y가 높이. 웨이퍼 두께는 크기 설정과 무관하게 1 mm.</summary>
public static class RobotGeometry
{
    public const double PadOffset = .002;
    public const double WaferHalfThickness = .0005;
    public const double AlignerBottom = .120;
    public const double ChuckBottom = .114;
    public static double SlotBottom(int slot) => .024 + (Math.Clamp(slot, 1, 25) - 1) * .006;
}
