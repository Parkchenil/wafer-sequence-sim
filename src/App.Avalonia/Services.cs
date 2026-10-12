using Contracts;
namespace App.Avalonia;
public static class Services { public static ISimulator Simulator { get; } = new FakeSimulator(); }
