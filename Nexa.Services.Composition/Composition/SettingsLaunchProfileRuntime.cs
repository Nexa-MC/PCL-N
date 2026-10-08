using Nexa.Services.Settings;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

public static class SettingsLaunchProfileRuntime
{
    public static void Register(XsrCommandRouterBuilder commands, XsrQueryRouterBuilder queries, SettingsPolicyService policy)
    {
        commands.Register<SettingsLaunchProfileSaveCommand>(SettingsLaunchProfileContract.Save,
            (command, _) => ValueTask.FromResult(policy.SaveLaunchProfile(command)));
        commands.Register<SettingsLaunchProfileDeleteCommand>(SettingsLaunchProfileContract.Delete,
            (command, _) => ValueTask.FromResult(policy.DeleteLaunchProfile(command)));
        commands.Register<SettingsLaunchProfileSelectCommand>(SettingsLaunchProfileContract.Select,
            (command, _) => ValueTask.FromResult(policy.SelectLaunchProfile(command)));
        commands.Register<SettingsTemporaryLaunchBeginCommand>(SettingsLaunchProfileContract.BeginTemporary,
            (command, _) => ValueTask.FromResult(policy.BeginTemporaryLaunch(command)));
        commands.Register<SettingsTemporaryLaunchEndCommand>(SettingsLaunchProfileContract.EndTemporary,
            (command, _) => ValueTask.FromResult(policy.EndTemporaryLaunch(command)));
        queries.Register<SettingsLaunchProfilesQuery, SettingsLaunchProfilesSnapshot>(SettingsLaunchProfileContract.Query,
            (query, _) => ValueTask.FromResult(policy.ReadLaunchProfiles(query)));
    }
}
