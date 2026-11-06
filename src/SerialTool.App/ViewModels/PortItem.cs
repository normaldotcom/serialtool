using SerialTool.Core;
using SerialTool.Serial;

namespace SerialTool.App.ViewModels;

/// <summary>An entry in the port dropdown: a real serial port or a built-in fake generator (dev builds).</summary>
public sealed class PortItem
{
    private PortItem(string name, string display, PortInfo? info, Func<IDataSource>? fakeFactory)
    {
        Name = name;
        Display = display;
        Info = info;
        FakeFactory = fakeFactory;
    }

    public string Name { get; }
    public string Display { get; }
    public PortInfo? Info { get; }
    public Func<IDataSource>? FakeFactory { get; }
    public bool IsFake => FakeFactory is not null;

    public static PortItem FromPort(PortInfo info) => new(info.PortName, info.ToString(), info, null);

    public static PortItem Fake(string name, string description, Func<IDataSource> factory) =>
        new(name, $"{name} — {description}", null, factory);

    public override string ToString() => Display;
}
