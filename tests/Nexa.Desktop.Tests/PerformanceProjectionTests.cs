using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void LaunchProjectionsDoNotRepeatOnIdleFrames()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        for (int index = 0; index < 5; index++) fixture.Shell.Render(new XsrUiSize(1280, 800));
        int before = fixture.Controller.ProjectionPasses;
        for (int index = 0; index < 100; index++) fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertEqual(before, fixture.Controller.ProjectionPasses);
        AssertTrue(fixture.Service.AddProfile(new Nexa.Services.Accounts.LaunchProfile
        { Username = "Cache wake", Kind = Nexa.Services.Accounts.LaunchProfileKind.Offline }).IsSuccess);
        fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(fixture.Controller.ProjectionPasses > before);
    }
}
