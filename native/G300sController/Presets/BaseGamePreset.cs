using System;
using System.Collections.Generic;
using G300sController.Models;

namespace G300sController.Presets;

/// <summary>
/// Abstract base class providing common logic and serialization for modular presets.
/// </summary>
public abstract class BaseGamePreset : IPreset
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public virtual string Category => "Oyun";
    public abstract string Icon { get; }
    public abstract string Description { get; }
    public virtual IReadOnlyList<string> ProcessNames => [];

    public virtual int PollingRateHz => 1000;
    public abstract (int R, int G, int B) LedColor { get; }
    public virtual int DefaultDpiStage => 1;
    public abstract IReadOnlyList<int> DpiStages { get; }
    public abstract IReadOnlyDictionary<int, string> Buttons { get; }

    public virtual void ApplyTo(G300sProfile profile)
    {
        profile.PollingRateHz = PollingRateHz;
        profile.DefaultDpiStage = Math.Clamp(DefaultDpiStage, 0, 3);

        for (int i = 0; i < Math.Min(4, DpiStages.Count); i++)
        {
            profile.SetDpiStage(i, DpiStages[i]);
        }

        var (r, g, b) = LedColor;
        profile.SetLed(r, g, b);

        for (int i = 0; i < 9; i++)
        {
            if (Buttons.TryGetValue(i, out var action))
            {
                profile.SetButtonAction(i, action);
            }
        }
    }

    public virtual PresetTemplate ToTemplate()
    {
        var template = new PresetTemplate
        {
            Id = Id,
            Name = Name,
            Category = Category,
            Icon = Icon,
            Description = Description,
            ProcessNames = [.. ProcessNames],
            PollingRateHz = PollingRateHz,
            LedR = LedColor.R,
            LedG = LedColor.G,
            LedB = LedColor.B,
            DefaultDpiStage = DefaultDpiStage,
            DpiStages = [.. DpiStages],
            Buttons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        for (int i = 0; i < 9; i++)
        {
            if (Buttons.TryGetValue(i, out var action))
            {
                template.Buttons[$"G{i + 1}"] = action;
            }
        }

        return template;
    }
}
