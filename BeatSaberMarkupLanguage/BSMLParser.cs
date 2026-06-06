using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.Macros;
using BeatSaberMarkupLanguage.Parser;
using BeatSaberMarkupLanguage.Tags;
using BeatSaberMarkupLanguage.TypeHandlers;
using BeatSaberMarkupLanguage.Util;
using UnityEngine;
using Zenject;

namespace BeatSaberMarkupLanguage
{
    public class BSMLParser : ZenjectSingleton<BSMLParser>, IInitializable
    {
        internal static readonly string MacroPrefix = "macro.";
        internal static readonly string RetrieveValuePrefix = "~";
        internal static readonly string SubscribeEventActionPrefix = "#";
        private static readonly Dictionary<Type, HostReflectionInfo> HostReflectionCache = new();

        private readonly Dictionary<string, BSMLTag> tags = new();
        private readonly Dictionary<string, BSMLMacro> macros = new();
        private readonly List<TypeHandler> typeHandlers = new();

        private bool isInitialized;

        private BSMLParser(List<BSMLTag> tags, List<BSMLMacro> macros, List<TypeHandler> typeHandlers)
        {
            foreach (BSMLTag tag in tags)
            {
                RegisterTag(tag);
            }

            foreach (BSMLMacro macro in macros)
            {
                RegisterMacro(macro);
            }

            foreach (TypeHandler typeHandler in typeHandlers)
            {
                RegisterTypeHandler(typeHandler);
            }
        }

        public void Initialize()
        {
            foreach (BSMLTag tag in tags.Values.Distinct())
            {
                tag.Initialize();
            }

#if DEBUG
            if (Environment.GetCommandLineArgs().Contains("--bsml-generate-documentation"))
            {
                IEnumerable<Type> typeHandlers = Assembly.GetExecutingAssembly().GetTypes().Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(TypeHandler)));
                new DocumentationDataGenerator(typeHandlers).Generate();
            }
#endif

            isInitialized = true;
        }

        public void RegisterTag(BSMLTag tag)
        {
            foreach (string alias in tag.Aliases)
            {
                tags.Add(alias, tag);
            }
        }

        public void RegisterMacro(BSMLMacro macro)
        {
            foreach (string alias in macro.Aliases)
            {
                macros.Add(MacroPrefix + alias, macro);
            }
        }

        public void RegisterTypeHandler(TypeHandler typeHandler)
        {
            if (typeHandler == null)
            {
                throw new ArgumentNullException(nameof(typeHandler));
            }

            Type typeHandlerType = typeHandler.GetType();
            Type targetType = typeHandlerType.GetCustomAttribute<ComponentHandler>()?.Type;

            if (targetType == null)
            {
                Logger.Log.Warn($"TypeHandler {typeHandlerType.FullName} does not have the [{nameof(ComponentHandler)}] attribute and will be ignored.");
                return;
            }

            foreach (TypeHandler otherTypeHandler in typeHandlers.Where(th => th.GetType().GetCustomAttribute<ComponentHandler>().Type == targetType))
            {
                List<string> conflictingProps = typeHandler.Props.Keys.Intersect(otherTypeHandler.Props.Keys).ToList();

                if (conflictingProps.Count > 0)
                {
                    Logger.Log.Warn($"Registering type handler {typeHandlerType.FullName} for type {targetType.FullName} that has conflicting properties [{string.Join(", ", conflictingProps)}] with {otherTypeHandler.GetType().FullName}! This may lead to unexpected behaviour.");
                }
                else
                {
                    Logger.Log.Warn($"Registering type handler {typeHandlerType.FullName} for type {targetType.FullName} that is already handled by {otherTypeHandler.GetType().FullName}! This may lead to unexpected behaviour in the future if type handlers share attributes with the same name.");
                }
            }

            typeHandlers.Add(typeHandler);
        }

        public BSMLParserParams Parse(string content, GameObject parent, object host = null)
        {
            XDocument document = XDocument.Parse(content, LoadOptions.SetLineInfo);
            return Parse(document, parent, host);
        }

        public BSMLParserParams Parse(XContainer container, GameObject parent, object host = null)
        {
            if (!isInitialized)
            {
                throw new InvalidOperationException($"{nameof(BSMLParser)} has not initialized.");
            }

            BSMLParserParams parserParams = new(host);

            FieldAccessOption fieldAccessOptions = FieldAccessOption.Auto;
            PropertyAccessOption propertyAccessOptions = PropertyAccessOption.Auto;
            MethodAccessOption methodAccessOptions = MethodAccessOption.Auto;
            HostReflectionInfo hostInfo = host != null ? GetHostReflectionInfo(host.GetType()) : null;
            HostOptionsAttribute hostOptions = hostInfo?.HostOptions;
            if (hostOptions != null)
            {
                fieldAccessOptions = hostOptions.FieldAccessOption;
                propertyAccessOptions = hostOptions.PropertyAccessOption;
                methodAccessOptions = hostOptions.MethodAccessOption;
            }

            if (host != null)
            {
                foreach (MethodBinding methodBinding in hostInfo.Methods)
                {
                    MethodInfo methodInfo = methodBinding.MethodInfo;
                    string methodName = methodInfo.Name;
                    if (methodBinding.UIAction != null)
                    {
                        string uiActionName = methodBinding.UIAction.Id;
                        if (parserParams.Actions.TryGetValue(uiActionName, out BSMLAction existing))
                        {
                            if (existing.FromUIAction)
                            {
                                throw new InvalidOperationException($"UIAction '{uiActionName}' is already used by member '{existing.MemberName}'.");
                            }

                            existing.MethodInfo = methodInfo;
                            existing.FromUIAction = true;
                        }
                        else
                        {
                            parserParams.Actions.Add(uiActionName, new BSMLAction(host, methodInfo, true));
                        }

                        if (methodAccessOptions == MethodAccessOption.AllowBoth && methodName != uiActionName && !parserParams.Actions.ContainsKey(methodName))
                        {
                            parserParams.Actions.Add(methodName, new BSMLAction(host, methodInfo, false));
                        }
                    }
                    else if (methodAccessOptions != MethodAccessOption.OptIn)
                    {
                        if (!parserParams.Actions.ContainsKey(methodName))
                        {
                            parserParams.Actions.Add(methodName, new BSMLAction(host, methodInfo));
                        }
                    }
                }

                // TODO: Figure out a way to prioritize [UIValue] attributes across both fields and properties.
                // If a field has the same name as a UIValue on a property, the field will take precedence. This is usually not the expected behavior.
                foreach (FieldBinding fieldBinding in hostInfo.Fields)
                {
                    FieldInfo fieldInfo = fieldBinding.FieldInfo;
                    string fieldName = fieldInfo.Name;
                    if (fieldBinding.UIValue != null)
                    {
                        string uiValueName = fieldBinding.UIValue.Id;
                        if (parserParams.Values.TryGetValue(uiValueName, out BSMLValue existing))
                        {
                            if (existing.FromUIValue)
                            {
                                throw new InvalidOperationException($"UIValue '{uiValueName}' is already used by member '{existing.MemberName}'.");
                            }

                            if (existing is BSMLFieldValue existingField)
                            {
                                existingField.FieldInfo = fieldInfo;
                                existingField.FromUIValue = true;
                            }
                        }
                        else
                        {
                            parserParams.Values.Add(uiValueName, new BSMLFieldValue(host, fieldInfo));
                        }

                        if (fieldAccessOptions == FieldAccessOption.AllowBoth && fieldName != uiValueName && !parserParams.Values.ContainsKey(fieldName))
                        {
                            parserParams.Values.Add(fieldName, new BSMLFieldValue(host, fieldInfo, false));
                        }
                    }
                    else if (fieldAccessOptions != FieldAccessOption.OptIn && !parserParams.Values.ContainsKey(fieldName))
                    {
                        parserParams.Values.Add(fieldName, new BSMLFieldValue(host, fieldInfo, false));
                    }

                    if (fieldBinding.HasUIParams)
                    {
                        fieldInfo.SetValue(host, parserParams);
                    }
                }

                foreach (PropertyBinding propertyBinding in hostInfo.Properties)
                {
                    PropertyInfo propertyInfo = propertyBinding.PropertyInfo;
                    string propName = propertyInfo.Name;
                    if (propertyBinding.UIValue != null)
                    {
                        string uiValueName = propertyBinding.UIValue.Id;
                        if (parserParams.Values.TryGetValue(uiValueName, out BSMLValue existing))
                        {
                            if (existing.FromUIValue)
                            {
                                throw new InvalidOperationException($"UIValue '{uiValueName}' is already used by member '{existing.MemberName}'.");
                            }

                            if (existing is BSMLPropertyValue existingProp)
                            {
                                existingProp.PropertyInfo = propertyInfo;
                                existingProp.FromUIValue = true;
                            }
                        }
                        else
                        {
                            parserParams.Values.Add(uiValueName, new BSMLPropertyValue(host, propertyInfo));
                        }

                        if (propertyAccessOptions == PropertyAccessOption.AllowBoth && propName != uiValueName && !parserParams.Values.ContainsKey(propName))
                        {
                            parserParams.Values.Add(propName, new BSMLPropertyValue(host, propertyInfo, false));
                        }
                    }
                    else if (propertyAccessOptions != PropertyAccessOption.OptIn && !parserParams.Values.ContainsKey(propName))
                    {
                        parserParams.Values.Add(propName, new BSMLPropertyValue(host, propertyInfo, false));
                    }
                }
            }

            IEnumerable<ComponentTypeWithData> componentInfo = Enumerable.Empty<ComponentTypeWithData>();
            foreach (XElement element in container.Elements())
            {
                HandleNode(element, parent, parserParams, out IEnumerable<ComponentTypeWithData> components);
                componentInfo = componentInfo.Concat(components);
            }

            foreach (KeyValuePair<string, BSMLAction> action in parserParams.Actions.Where(x => x.Key.StartsWith(SubscribeEventActionPrefix, StringComparison.Ordinal)))
            {
                parserParams.AddEvent(action.Key.Substring(1), () => action.Value.Invoke());
            }

            foreach (ComponentTypeWithData component in componentInfo)
            {
                try
                {
                    component.TypeHandler.HandleTypeAfterParse(component, parserParams);
                }
                catch (Exception ex)
                {
                    throw new TypeHandlerException(component.TypeHandler, ex);
                }
            }

            parserParams.EmitEvent("post-parse");

            return parserParams;
        }

        public void HandleNode(XElement element, GameObject parent, BSMLParserParams parserParams, out IEnumerable<ComponentTypeWithData> componentInfo)
        {
            try
            {
                if (element.Name.LocalName.StartsWith(MacroPrefix, StringComparison.Ordinal))
                {
                    HandleMacroNode(element, parent, parserParams, out componentInfo);
                }
                else
                {
                    HandleTagNode(element, parent, parserParams, out componentInfo);
                }
            }
            catch (Exception ex) when (ex is not BSMLParserException)
            {
                throw new BSMLParserException(element, ex);
            }
        }

        internal static Component GetExternalComponent(GameObject gameObject, Type type)
        {
            Component component = null;

            if (gameObject.TryGetComponent(out ExternalComponents externalComponents))
            {
                foreach (Component externalComponent in externalComponents.Components)
                {
                    if (type.IsAssignableFrom(externalComponent.GetType()))
                    {
                        component = externalComponent;
                    }
                }
            }

            if (component == null)
            {
                component = gameObject.GetComponent(type);
            }

            return component;
        }

        private static HostReflectionInfo GetHostReflectionInfo(Type hostType)
        {
            if (!HostReflectionCache.TryGetValue(hostType, out HostReflectionInfo hostInfo))
            {
                hostInfo = new HostReflectionInfo(hostType);
                HostReflectionCache.Add(hostType, hostInfo);
            }

            return hostInfo;
        }

        private void HandleTagNode(XElement element, GameObject parent, BSMLParserParams parserParams, out IEnumerable<ComponentTypeWithData> componentInfo)
        {
            if (!this.tags.TryGetValue(element.Name.LocalName, out BSMLTag currentTag))
            {
                throw new TagNotFoundException(element.Name.LocalName);
            }

            GameObject currentNode = currentTag.CreateObject(parent.transform);

            List<ComponentTypeWithData> componentTypes = new();
            foreach (TypeHandler typeHandler in typeHandlers)
            {
                Type type = typeHandler.GetType().GetCustomAttribute<ComponentHandler>(true)?.Type;
                if (type == null)
                {
                    continue;
                }

                Component component = GetExternalComponent(currentNode, type);
                if (component != null)
                {
                    ComponentTypeWithData componentType;
                    componentType.Data = GetParameters(element, typeHandler.CachedProps, parserParams, out Dictionary<string, BSMLValue> valueMap);
                    componentType.ValueMap = valueMap;
                    componentType.TypeHandler = typeHandler;
                    componentType.Component = component;
                    componentTypes.Add(componentType);
                }
            }

            foreach (ComponentTypeWithData componentType in componentTypes)
            {
                try
                {
                    componentType.TypeHandler.HandleType(componentType, parserParams);
                }
                catch (Exception ex) when (ex is not TypeHandlerException)
                {
                    throw new TypeHandlerException(componentType.TypeHandler, ex);
                }
            }

            object host = parserParams.Host;
            XAttribute id = element.Attribute("id");

            if (host != null && id != null)
            {
                HostReflectionInfo hostInfo = GetHostReflectionInfo(host.GetType());
                foreach (FieldIdBinding fieldInfo in hostInfo.ComponentFields)
                {
                    if (fieldInfo.Id == id.Value)
                    {
                        fieldInfo.FieldInfo.SetValue(host, GetExternalComponent(currentNode, fieldInfo.FieldInfo.FieldType));
                    }
                }

                foreach (FieldIdBinding fieldInfo in hostInfo.ObjectFields)
                {
                    if (fieldInfo.Id == id.Value)
                    {
                        fieldInfo.FieldInfo.SetValue(host, currentNode);
                    }
                }

                foreach (PropertyIdBinding propertyInfo in hostInfo.ComponentProperties)
                {
                    if (propertyInfo.Id == id.Value)
                    {
                        propertyInfo.PropertyInfo.SetValue(host, GetExternalComponent(currentNode, propertyInfo.PropertyInfo.PropertyType));
                    }
                }

                foreach (PropertyIdBinding propertyInfo in hostInfo.ObjectProperties)
                {
                    if (propertyInfo.Id == id.Value)
                    {
                        propertyInfo.PropertyInfo.SetValue(host, currentNode);
                    }
                }
            }

            XAttribute tags = element.Attribute("tags");
            if (tags != null)
            {
                parserParams.AddObjectTags(currentNode, tags.Value.Split(','));
            }

            IEnumerable<ComponentTypeWithData> childrenComponents = Enumerable.Empty<ComponentTypeWithData>();

            if (currentTag.AddChildren)
            {
                foreach (XElement childElement in element.Elements())
                {
                    HandleNode(childElement, currentNode, parserParams, out IEnumerable<ComponentTypeWithData> children);
                    childrenComponents = childrenComponents.Concat(children);
                }
            }

            foreach (ComponentTypeWithData componentType in componentTypes)
            {
                try
                {
                    componentType.TypeHandler.HandleTypeAfterChildren(componentType, parserParams);
                }
                catch (Exception ex)
                {
                    throw new TypeHandlerException(componentType.TypeHandler, ex);
                }
            }

            componentInfo = componentTypes.Concat(childrenComponents);
        }

        private void HandleMacroNode(XElement element, GameObject parent, BSMLParserParams parserParams, out IEnumerable<ComponentTypeWithData> components)
        {
            if (!macros.TryGetValue(element.Name.LocalName, out BSMLMacro currentMacro))
            {
                throw new MacroNotFoundException(element.Name.LocalName);
            }

            Dictionary<string, string> properties = GetParameters(element, currentMacro.CachedProps, parserParams, out _);
            currentMacro.Execute(element, parent, properties, parserParams, out components);
        }

        private Dictionary<string, string> GetParameters(XElement element, Dictionary<string, string[]> properties, BSMLParserParams parserParams, out Dictionary<string, BSMLValue> valueMap)
        {
            Dictionary<string, string> parameters = new();
            valueMap = new Dictionary<string, BSMLValue>();
            foreach (KeyValuePair<string, string[]> propertyAliases in properties)
            {
                List<string> aliasList = new(propertyAliases.Value);
                if (!aliasList.Contains(propertyAliases.Key))
                {
                    aliasList.Add(propertyAliases.Key);
                }

                foreach (string alias in aliasList)
                {
                    XAttribute attribute = element.Attribute(alias);
                    if (attribute != null)
                    {
                        string value = attribute.Value;
                        if (value.StartsWith(RetrieveValuePrefix, StringComparison.Ordinal))
                        {
                            string valueID = value.Substring(1);
                            if (!parserParams.Values.TryGetValue(valueID, out BSMLValue uiValue) || uiValue == null)
                            {
                                throw new ValueNotFoundException(valueID, parserParams.Host);
                            }

                            parameters.Add(propertyAliases.Key, uiValue.GetValue()?.InvariantToString());
                            valueMap.Add(propertyAliases.Key, uiValue);
                        }
                        else
                        {
                            parameters.Add(propertyAliases.Key, value);
                        }

                        break;
                    }

                    if (alias == "_children")
                    {
                        using XmlReader reader = element.CreateReader();
                        reader.MoveToContent();
                        parameters.Add(propertyAliases.Key, reader.ReadInnerXml());
                    }
                }
            }

            return parameters;
        }

        public struct ComponentTypeWithData
        {
            public TypeHandler TypeHandler;
            public Component Component;
            public Dictionary<string, string> Data;
            public Dictionary<string, BSMLValue> ValueMap;
        }

        private readonly struct MethodBinding
        {
            internal MethodBinding(MethodInfo methodInfo)
            {
                MethodInfo = methodInfo;
                UIAction = methodInfo.GetCustomAttribute<UIAction>(true);
            }

            internal MethodInfo MethodInfo { get; }

            internal UIAction UIAction { get; }
        }

        private readonly struct FieldBinding
        {
            internal FieldBinding(FieldInfo fieldInfo)
            {
                FieldInfo = fieldInfo;
                UIValue = fieldInfo.GetCustomAttribute<UIValue>(true);
                HasUIParams = fieldInfo.GetCustomAttribute<UIParams>(true) != null;
            }

            internal FieldInfo FieldInfo { get; }

            internal UIValue UIValue { get; }

            internal bool HasUIParams { get; }
        }

        private readonly struct PropertyBinding
        {
            internal PropertyBinding(PropertyInfo propertyInfo)
            {
                PropertyInfo = propertyInfo;
                UIValue = propertyInfo.GetCustomAttribute<UIValue>(true);
            }

            internal PropertyInfo PropertyInfo { get; }

            internal UIValue UIValue { get; }
        }

        private readonly struct FieldIdBinding
        {
            internal FieldIdBinding(FieldInfo fieldInfo, string id)
            {
                FieldInfo = fieldInfo;
                Id = id;
            }

            internal FieldInfo FieldInfo { get; }

            internal string Id { get; }
        }

        private readonly struct PropertyIdBinding
        {
            internal PropertyIdBinding(PropertyInfo propertyInfo, string id)
            {
                PropertyInfo = propertyInfo;
                Id = id;
            }

            internal PropertyInfo PropertyInfo { get; }

            internal string Id { get; }
        }

        private sealed class HostReflectionInfo
        {
            internal HostReflectionInfo(Type hostType)
            {
                HostOptions = hostType.GetCustomAttribute<HostOptionsAttribute>();
                Methods = hostType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(method => new MethodBinding(method))
                    .ToArray();
                Fields = hostType.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(field => new FieldBinding(field))
                    .ToArray();
                Properties = hostType.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(property => new PropertyBinding(property))
                    .ToArray();

                FieldInfo[] instanceFields = hostType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                PropertyInfo[] instanceProperties = hostType.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                ComponentFields = instanceFields
                    .Select(field => new FieldIdBinding(field, field.GetCustomAttribute<UIComponent>(true)?.Id))
                    .Where(binding => binding.Id != null)
                    .ToArray();
                ObjectFields = instanceFields
                    .Select(field => new FieldIdBinding(field, field.GetCustomAttribute<UIObject>(true)?.Id))
                    .Where(binding => binding.Id != null)
                    .ToArray();
                ComponentProperties = instanceProperties
                    .Select(property => new PropertyIdBinding(property, property.GetCustomAttribute<UIComponent>(true)?.Id))
                    .Where(binding => binding.Id != null)
                    .ToArray();
                ObjectProperties = instanceProperties
                    .Select(property => new PropertyIdBinding(property, property.GetCustomAttribute<UIObject>(true)?.Id))
                    .Where(binding => binding.Id != null)
                    .ToArray();
            }

            internal HostOptionsAttribute HostOptions { get; }

            internal MethodBinding[] Methods { get; }

            internal FieldBinding[] Fields { get; }

            internal PropertyBinding[] Properties { get; }

            internal FieldIdBinding[] ComponentFields { get; }

            internal FieldIdBinding[] ObjectFields { get; }

            internal PropertyIdBinding[] ComponentProperties { get; }

            internal PropertyIdBinding[] ObjectProperties { get; }
        }
    }
}
