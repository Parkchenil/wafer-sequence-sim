using Contracts;

namespace App.WinForms;

public static class Services
{
    public static ISimulator Simulator { get; } = new FakeSimulator();
}
