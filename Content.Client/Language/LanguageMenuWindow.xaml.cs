using Content.Client.Language.Systems;
using Content.Shared.Language;
using Content.Shared.Language.Systems;
using Robust.Client.AutoGenerated;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Client.UserInterface.XAML;
using Robust.Shared.Console;
using static Content.Shared.Language.Systems.SharedLanguageSystem;

namespace Content.Client.Language;

[GenerateTypedNameReferences]
public sealed partial class LanguageMenuWindow : DefaultWindow
{
    [Dependency] private readonly IConsoleHost _consoleHost = default!;
    private readonly List<(string language, Button button)> _buttons = new();

    public LanguageMenuWindow()
    {
        RobustXamlLoader.Load(this);
        IoCManager.InjectDependencies(this);
    }

    public void UpdateState(LanguageMenuStateMessage state)
    {
        // TODO: this is a placeholder
        CurrentLanguageLabel.Text = "Current language: " + state.CurrentLanguage;

        CurrentLanguageContainer.RemoveAllChildren();
        OptionsContainer.RemoveAllChildren();
        _buttons.Clear();

        foreach (var language in state.Options)
        {
            var entry = MakeLanguageEntry(language);
            OptionsContainer.AddChild(entry);
        }

        foreach (var entry in _buttons)
        {
            // Disable the button for the current language, if any.
            entry.button.Disabled = state.CurrentLanguage == entry.language;
        }
    }

    private BoxContainer MakeLanguageEntry(string language)
    {
        var _language = IoCManager.Resolve<LanguageSystem>();
        var proto = _language.GetLanguage(language);

        var container = new BoxContainer();
        container.Orientation = BoxContainer.LayoutOrientation.Horizontal;
        container.HorizontalExpand = true;
        container.SeparationOverride = 4;

        var name = new Label();
        name.Text = proto?.LocalizedName ?? "<error>";

        var description = new Label();
        description.Text = proto?.LocalizedDescription ?? string.Empty;
        description.HorizontalExpand = true;

        var button = new Button();
        button.Text = "Choose";
        button.OnPressed += _ => OnLanguageChosen(language);

        _buttons.Add((language, button));

        container.AddChild(name);
        container.AddChild(description);
        container.AddChild(button);

        return container;
    }

    private void OnLanguageChosen(string id)
    {
        _consoleHost.ExecuteCommand("lsselectlang " + id);
    }
}
