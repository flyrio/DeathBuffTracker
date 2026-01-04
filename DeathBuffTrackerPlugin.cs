using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using DeathBuffTracker.Events;
using DeathBuffTracker.Storage;
using DeathBuffTracker.UI;

namespace DeathBuffTracker;

public sealed class DeathBuffTrackerPlugin : IDalamudPlugin {
    private const string CommandName = "/dbt";

    public Configuration Configuration { get; }
    public TrackedStatusStore StatusStore { get; }
    public JsonEventStore EventStore { get; }
    public TrackerEventCapture EventCapture { get; }
    public WindowSystem WindowSystem { get; }
    public MainWindow MainWindow { get; }
    public ConfigWindow ConfigWindow { get; }

    public DeathBuffTrackerPlugin(IDalamudPluginInterface pluginInterface) {
        Service.Initialize(pluginInterface);

        Configuration = Configuration.Get(pluginInterface);
        StatusStore = new TrackedStatusStore(pluginInterface);
        var storedStatuses = StatusStore.Load();
        if (storedStatuses != null) {
            Configuration.TrackedStatuses = storedStatuses;
            Configuration.Save();
        } else if (!StatusStore.FileExists) {
            StatusStore.Save(Configuration.TrackedStatuses);
        }

        EventStore = new JsonEventStore(pluginInterface);
        EventStore.Load();

        EventCapture = new TrackerEventCapture(this);
        WindowSystem = new WindowSystem("DeathBuffTracker");
        MainWindow = new MainWindow(this);
        ConfigWindow = new ConfigWindow(this);

        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ConfigWindow);

        pluginInterface.UiBuilder.Draw += () => WindowSystem.Draw();
        pluginInterface.UiBuilder.OpenMainUi += () => MainWindow.Toggle();
        pluginInterface.UiBuilder.OpenConfigUi += () => ConfigWindow.Toggle();

        var commandInfo = new CommandInfo((_, _) => MainWindow.Toggle()) {
            HelpMessage = "打开/关闭 死亡与状态追踪",
        };
        Service.CommandManager.AddHandler(CommandName, commandInfo);
    }

    public void Dispose() {
        EventCapture.Dispose();
        Service.CommandManager.RemoveHandler(CommandName);
    }
}
