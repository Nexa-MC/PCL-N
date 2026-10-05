using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void LaunchHookSettingsEditorsSendExactCommandsAndWaitPolicy()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents,
            fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        fixture.Shell.Render(new(1000, 650));
        var wrapper = Find("SettingsInput.game.wrapper");
        var command = Find("SettingsHookLine.game.pre-launch.0");
        const string wrapperDraft = "wrapper --label 'two words' \"\"";
        const string commandDraft = "echo 'user text' && echo ${literal}";
        fixture.Shell.Renderer.SetTextInputValue(wrapper, wrapperDraft);
        Emit(fixture.Intents, "ui.settings.edit", Find("SettingsEdit.game.wrapper"));
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return Read("game.wrapper") == wrapperDraft; }, TimeSpan.FromSeconds(5)));
        fixture.Shell.Renderer.SetTextInputValue(command, commandDraft);
        Emit(fixture.Intents, "ui.settings.edit", Find("SettingsEdit.game.pre-launch"));
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return Read("game.pre-launch") == commandDraft; }, TimeSpan.FromSeconds(5)));
        Emit(fixture.Intents, "ui.settings.choice", Find("SettingsOption.game.pre-launch-wait.false"));
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return Read("game.pre-launch-wait") == "false"; }, TimeSpan.FromSeconds(5)));
        AssertEqual(commandDraft, fixture.Shell.Tree.GetComponent<XsrUiTextInput>(command)!.ReadDraft());
        AssertEqual(wrapperDraft, fixture.Shell.Tree.GetComponent<XsrUiTextInput>(wrapper)!.ReadDraft());
        fixture.Shell.Renderer.SetTextInputValue(command, "");
        Emit(fixture.Intents, "ui.settings.edit", Find("SettingsEdit.game.pre-launch"));
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return Read("game.pre-launch") == ""; }, TimeSpan.FromSeconds(5)));

        string? Read(string key) => fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(value => value.Key == key).Value.Value;
        XsrUiEntityId Find(string name)
        {
            XsrUiEntityId result = default;
            fixture.Shell.Tree.Walk(settings.Page, entity => { if (fixture.Shell.Tree.Name(entity) == name) result = entity; return true; });
            AssertTrue(result.IsAssigned); return result;
        }
    }

    private static void LaunchHookLinesPreserveExactSeparatorsTabsDraftsAndPaging()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var policy = fixture.Foundation.Host.SettingsPolicy;
        const string original = "echo first\r\n\tprintf second\n\n echo fourth\r\n";
        AssertTrue(policy.Set(new("game.pre-launch", SettingsLayer.Global, new(SettingsOverrideMode.Custom, original))).IsSuccess);
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents,
            fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return Find("SettingsHookLine.game.pre-launch.4", false).IsAssigned; }, TimeSpan.FromSeconds(5)));
        AssertEqual("\tprintf second", Draft(1)); AssertEqual("", Draft(2)); AssertEqual("", Draft(4));
        Apply(); AssertEqual(original, Read());

        var changed = Find("SettingsHookLine.game.pre-launch.1");
        fixture.Shell.Renderer.Focus(changed);
        fixture.Shell.Renderer.SetTextInputValue(changed, "\tprintf 'edited value'");
        AssertTrue(policy.Set(new("developer.enabled", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "true"))).IsSuccess);
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            fixture.Shell.Render(new(1000, 650));
            return Find("SettingsHookLine.game.pre-launch.1") != changed;
        }, TimeSpan.FromSeconds(5)));
        AssertEqual("\tprintf 'edited value'", Draft(1));
        AssertEqual("SettingsHookLine.game.pre-launch.1", fixture.Shell.Tree.Name(fixture.Shell.Renderer.Focused));
        Apply(); AssertEqual(original.Replace("\tprintf second", "\tprintf 'edited value'", StringComparison.Ordinal), Read());

        string many = string.Join("\r\n", Enumerable.Range(0, 70).Select(index => "echo " + index)) + "\r\n";
        AssertTrue(policy.Set(new("game.pre-launch", SettingsLayer.Global, new(SettingsOverrideMode.Custom, many))).IsSuccess);
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return Find("SettingsHookNext.game.pre-launch", false).IsAssigned; }, TimeSpan.FromSeconds(5)));
        AssertEqual(32, CountLines());
        fixture.Shell.Renderer.SetTextInputValue(Find("SettingsHookLine.game.pre-launch.0"), "echo changed");
        Emit(fixture.Intents, "ui.settings.hook.page", Find("SettingsHookNext.game.pre-launch")); fixture.Shell.Render(new(1000, 650));
        AssertEqual("echo 32", Draft(32)); AssertEqual(32, CountLines());
        Emit(fixture.Intents, "ui.settings.hook.page", Find("SettingsHookPrevious.game.pre-launch")); fixture.Shell.Render(new(1000, 650));
        AssertEqual("echo changed", Draft(0));
        Apply(); AssertEqual("echo changed" + many["echo 0".Length..], Read());

        Emit(fixture.Intents, "ui.settings.hook.add", Find("SettingsHookAdd.game.pre-launch")); fixture.Shell.Render(new(1000, 650));
        AssertEqual("", Draft(71));
        fixture.Shell.Renderer.SetTextInputValue(Find("SettingsHookLine.game.pre-launch.71"), "echo added");
        Emit(fixture.Intents, "ui.settings.hook.remove", Find("SettingsHookRemove.game.pre-launch.70")); fixture.Shell.Render(new(1000, 650));
        Apply(); AssertEqual("echo changed" + many["echo 0".Length..] + "echo added", Read());

        string? Read() => policy.Read(new()).Value!.Values.Single(value => value.Key == "game.pre-launch").Value.Value;
        string Draft(int index) => fixture.Shell.Tree.GetComponent<XsrUiTextInput>(Find("SettingsHookLine.game.pre-launch." + index))!.ReadDraft();
        int CountLines()
        {
            int count = 0;
            fixture.Shell.Tree.Walk(settings.Page, entity => { if (fixture.Shell.Tree.Name(entity).StartsWith("SettingsHookLine.game.pre-launch.", StringComparison.Ordinal)) count++; return true; });
            return count;
        }
        void Apply()
        {
            Emit(fixture.Intents, "ui.settings.edit", Find("SettingsEdit.game.pre-launch"));
            fixture.Shell.Render(new(1000, 650));
            AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return !settings.SettingsWritePending; }, TimeSpan.FromSeconds(5)));
        }
        XsrUiEntityId Find(string name, bool required = true)
        {
            XsrUiEntityId found = default;
            fixture.Shell.Tree.Walk(settings.Page, entity => { if (fixture.Shell.Tree.Name(entity) == name) found = entity; return true; });
            if (required) AssertTrue(found.IsAssigned); return found;
        }
    }

    private static void LaunchWrapperEditorPreservesBoundedLongCommands()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents,
            fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        fixture.Shell.Render(new(1000, 650));
        XsrUiEntityId input = default, apply = default;
        fixture.Shell.Tree.Walk(settings.Page, entity =>
        {
            if (fixture.Shell.Tree.Name(entity) == "SettingsInput.game.wrapper") input = entity;
            if (fixture.Shell.Tree.Name(entity) == "SettingsEdit.game.wrapper") apply = entity;
            return true;
        });
        AssertTrue(input.IsAssigned); AssertTrue(apply.IsAssigned);
        string command = "wrapper '" + new string('x', 32758) + "'";
        AssertEqual(32768, command.Length);
        fixture.Shell.Renderer.Focus(input); fixture.Shell.Renderer.SetTextInputValue(input, command);
        AssertEqual(command, fixture.Shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft());
        Emit(fixture.Intents, "ui.settings.edit", apply);
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            fixture.Shell.Render(new(1000, 650));
            return fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(value => value.Key == "game.wrapper").Value.Value == command;
        }, TimeSpan.FromSeconds(5)));
    }

    private static void LaunchHookSaveFailureKeepsMultilineDraftAndCommittedScript()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        const string committed = "echo committed\r\n\techo untouched\n";
        AssertTrue(fixture.Foundation.Host.SettingsPolicy.Set(new("game.pre-launch", SettingsLayer.Global,
            new(SettingsOverrideMode.Custom, committed))).IsSuccess);
        var commands = new XsrCommandRouterBuilder();
        commands.Register<SettingsMutation>(SettingsPolicyContract.SetCommand, (_, _) => new(XsrResult.Failure(
            new(XsrErrorKind.Rejected, XsrSemanticId.Parse("fixture.settings.persistence_failed"), "Fixture persistence failure."))));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents,
            fixture.Foundation.Queries, commands.Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        fixture.Shell.Render(new(1000, 650));
        fixture.Shell.Renderer.SetTextInputValue(Find("SettingsHookLine.game.pre-launch.0"), "\techo pending");
        Emit(fixture.Intents, "ui.settings.edit", Find("SettingsEdit.game.pre-launch"));
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return !settings.SettingsWritePending; }, TimeSpan.FromSeconds(5)));
        AssertEqual(committed, fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(value => value.Key == "game.pre-launch").Value.Value);
        AssertEqual("\techo pending", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(Find("SettingsHookLine.game.pre-launch.0"))!.ReadDraft());
        AssertEqual("\techo untouched", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(Find("SettingsHookLine.game.pre-launch.1"))!.ReadDraft());
        AssertTrue(fixture.Feedback.Snapshot().Notifications.Any(notification => notification.Message.Contains("设置未保存", StringComparison.Ordinal)));

        XsrUiEntityId Find(string name)
        {
            XsrUiEntityId found = default;
            fixture.Shell.Tree.Walk(settings.Page, entity => { if (fixture.Shell.Tree.Name(entity) == name) found = entity; return true; });
            AssertTrue(found.IsAssigned); return found;
        }
    }

    private static void LaunchHookFailedInheritancePreservesDraftAcrossRevisionsAndCanApply()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        string instance = Path.GetFullPath("hook-inheritance-ui-instance");
        var policy = fixture.Foundation.Host.SettingsPolicy;
        const string inherited = "echo inherited\n\techo global\n";
        const string committed = "echo committed\r\n\techo untouched\n";
        const string draft = "\techo pending\r\n\techo untouched\n";
        AssertTrue(policy.Set(new("game.pre-launch", SettingsLayer.Global,
            new(SettingsOverrideMode.Custom, inherited))).IsSuccess);
        AssertTrue(policy.Set(new("game.pre-launch", SettingsLayer.Instance,
            new(SettingsOverrideMode.Custom, committed), instance)).IsSuccess);
        bool rejectInheritance = true;
        var commands = new XsrCommandRouterBuilder();
        commands.Register<SettingsMutation>(SettingsPolicyContract.SetCommand, (mutation, _) => new(
            rejectInheritance && mutation.Value.Mode == SettingsOverrideMode.Inherit
                ? XsrResult.Failure(new(XsrErrorKind.Rejected, XsrSemanticId.Parse("fixture.settings.persistence_failed"), "Fixture persistence failure."))
                : policy.Set(mutation)));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents,
            fixture.Foundation.Queries, commands.Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback, () => instance);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return Find("SettingsHookLine.game.pre-launch.0", false).IsAssigned; }, TimeSpan.FromSeconds(5)));
        var input = Find("SettingsHookLine.game.pre-launch.0");
        fixture.Shell.Renderer.Focus(input); fixture.Shell.Renderer.SetTextInputValue(input, "\techo pending");
        Emit(fixture.Intents, "ui.settings.inherit", Find("SettingsInherit.game.pre-launch"));
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return !settings.SettingsWritePending; }, TimeSpan.FromSeconds(5)));
        AssertEqual(committed, Read());
        AssertTrue(fixture.Feedback.Snapshot().Notifications.Any(notification => notification.Message.Contains("设置未保存", StringComparison.Ordinal)));

        AssertTrue(policy.Set(new("game.width", SettingsLayer.Global,
            new(SettingsOverrideMode.Custom, "1280"))).IsSuccess);
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            fixture.Shell.Render(new(1000, 650));
            return fixture.Shell.Tree.GetComponent<XsrUiTextInput>(Find("SettingsInput.game.width"))!.ReadDraft() == "1280";
        }, TimeSpan.FromSeconds(5)));
        AssertEqual("\techo pending", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(Find("SettingsHookLine.game.pre-launch.0"))!.ReadDraft());
        AssertEqual("\techo untouched", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(Find("SettingsHookLine.game.pre-launch.1"))!.ReadDraft());
        Emit(fixture.Intents, "ui.settings.edit", Find("SettingsEdit.game.pre-launch"));
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return !settings.SettingsWritePending && Read() == draft; }, TimeSpan.FromSeconds(5)));

        // Successful inheritance still discards the local draft and shows the exact global script.
        rejectInheritance = false;
        Emit(fixture.Intents, "ui.settings.inherit", Find("SettingsInherit.game.pre-launch"));
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            fixture.Shell.Render(new(1000, 650));
            return !settings.SettingsWritePending && Read() == inherited
                && fixture.Shell.Tree.GetComponent<XsrUiTextInput>(Find("SettingsHookLine.game.pre-launch.0"))!.ReadDraft() == "echo inherited";
        }, TimeSpan.FromSeconds(5)));

        string? Read() => policy.Read(new(instance)).Value!.Values.Single(value => value.Key == "game.pre-launch").Value.Value;
        XsrUiEntityId Find(string name, bool required = true)
        {
            XsrUiEntityId found = default;
            fixture.Shell.Tree.Walk(settings.Page, entity => { if (fixture.Shell.Tree.Name(entity) == name) found = entity; return true; });
            if (required) AssertTrue(found.IsAssigned); return found;
        }
    }
}
