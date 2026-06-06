using System;
using System.Collections.Generic;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.Parser;
using HMUI;
using static BeatSaberMarkupLanguage.BSMLParser;

namespace BeatSaberMarkupLanguage.TypeHandlers
{
    [ComponentHandler(typeof(Backgroundable))]
    public class BackgroundableHandler : TypeHandler<Backgroundable>
    {
        public override Dictionary<string, string[]> Props => new()
        {
            { "background", new[] { "bg", "background" } },
            { "backgroundColor", new[] { "bg-color", "background-color" } },
            { "backgroundColor0", new[] { "bg-color0", "background-color0" } },
            { "backgroundColor1", new[] { "bg-color1", "background-color1" } },
            { "backgroundGradient", new[] { "background-gradient" } },
            { "backgroundAlpha", new[] { "bg-alpha", "background-alpha" } },
        };

        public override Dictionary<string, Action<Backgroundable, string>> Setters { get; } = new()
        {
            { "background", new Action<Backgroundable, string>((component, value) => component.ApplyBackground(value)) },
            { "backgroundColor", new Action<Backgroundable, string>((component, value) => component.Background.color = Parse.Color(value, component.Background.color.a)) },
            {
                "backgroundColor0", new Action<Backgroundable, string>((component, value) =>
                {
                    if (component.Background is ImageView imageView)
                    {
                        imageView.color0 = Parse.Color(value, imageView.color0.a);
                    }
                })
            },
            {
                "backgroundColor1", new Action<Backgroundable, string>((component, value) =>
                {
                    if (component.Background is ImageView imageView)
                    {
                        imageView.color1 = Parse.Color(value, imageView.color1.a);
                    }
                })
            },
            {
                "backgroundGradient", new Action<Backgroundable, string>((component, value) =>
                {
                    if (component.Background is ImageView imageView)
                    {
                        imageView.gradient = Parse.Bool(value);
                    }
                })
            },
            { "backgroundAlpha", new Action<Backgroundable, string>((component, value) => component.ApplyAlpha(Parse.Float(value))) },
        };

        public override void HandleType(ComponentTypeWithData componentType, BSMLParserParams parserParams)
        {
            if (componentType.Component is not Backgroundable backgroundable)
            {
                return;
            }

            if (componentType.Data.TryGetValue("background", out string background))
            {
                backgroundable.ApplyBackground(background);
            }

            NotifyUpdater updater = null;
            foreach (KeyValuePair<string, string> pair in componentType.Data)
            {
                if (pair.Key == "background" || !Setters.TryGetValue(pair.Key, out Action<Backgroundable, string> action))
                {
                    continue;
                }

                try
                {
                    action.Invoke(backgroundable, pair.Value);
                }
                catch (Exception ex)
                {
                    throw new TypeHandlerException(this, pair.Key, ex);
                }

                if (componentType.ValueMap.TryGetValue(pair.Key, out BSMLValue value))
                {
                    updater = BindValue(componentType, parserParams, value, val => action.Invoke(backgroundable, val.InvariantToString()), updater);
                }
            }
        }
    }
}
