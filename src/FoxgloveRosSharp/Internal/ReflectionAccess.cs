using System;
using System.Linq;
using System.Reflection;

namespace RosSharp.RosBridgeClient.Internal
{
    internal static class ReflectionAccess
    {
        public static object? GetMemberValue(object target, string name)
        {
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null)
                return property.GetValue(target);

            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            return field?.GetValue(target);
        }

        public static Type? GetMemberType(Type targetType, string name)
        {
            PropertyInfo? property = targetType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null)
                return property.PropertyType;

            FieldInfo? field = targetType.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            return field?.FieldType;
        }

        public static object? GetNestedMemberValue(object target, string name)
        {
            foreach (PropertyInfo property in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!typeof(Message).IsAssignableFrom(property.PropertyType))
                    continue;

                object? nested = property.GetValue(target);
                if (nested != null && GetMemberType(nested.GetType(), name) != null)
                    return GetMemberValue(nested, name);
            }

            foreach (FieldInfo field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!typeof(Message).IsAssignableFrom(field.FieldType))
                    continue;

                object? nested = field.GetValue(target);
                if (nested != null && GetMemberType(nested.GetType(), name) != null)
                    return GetMemberValue(nested, name);
            }

            return null;
        }

        public static Type? GetNestedMemberType(Type targetType, string name)
        {
            foreach (PropertyInfo property in targetType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!typeof(Message).IsAssignableFrom(property.PropertyType))
                    continue;

                Type? nestedMemberType = GetMemberType(property.PropertyType, name);
                if (nestedMemberType != null)
                    return nestedMemberType;
            }

            foreach (FieldInfo field in targetType.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!typeof(Message).IsAssignableFrom(field.FieldType))
                    continue;

                Type? nestedMemberType = GetMemberType(field.FieldType, name);
                if (nestedMemberType != null)
                    return nestedMemberType;
            }

            return null;
        }

        public static bool SetNestedMemberValue(object target, string name, object? value)
        {
            foreach (PropertyInfo property in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!typeof(Message).IsAssignableFrom(property.PropertyType) || GetMemberType(property.PropertyType, name) == null)
                    continue;

                object? nested = property.GetValue(target);
                if (nested == null)
                {
                    nested = Activator.CreateInstance(property.PropertyType);
                    property.SetValue(target, nested);
                }

                if (nested != null)
                {
                    SetMemberValue(nested, name, value);
                    return true;
                }
            }

            foreach (FieldInfo field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!typeof(Message).IsAssignableFrom(field.FieldType) || GetMemberType(field.FieldType, name) == null)
                    continue;

                object? nested = field.GetValue(target);
                if (nested == null)
                {
                    nested = Activator.CreateInstance(field.FieldType);
                    field.SetValue(target, nested);
                }

                if (nested != null)
                {
                    SetMemberValue(nested, name, value);
                    return true;
                }
            }

            return false;
        }

        public static void SetMemberValue(object target, string name, object? value)
        {
            Type type = target.GetType();
            PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, ConvertValue(value, property.PropertyType));
                return;
            }

            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            field?.SetValue(target, ConvertValue(value, field.FieldType));
        }

        public static object? ConvertValue(object? value, Type targetType)
        {
            if (value == null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            Type nonNullableTarget = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nonNullableTarget.IsInstanceOfType(value))
                return value;

            if (nonNullableTarget.IsEnum)
                return Enum.ToObject(nonNullableTarget, value);

            return Convert.ChangeType(value, nonNullableTarget);
        }

        public static Array ToTypedArray(object?[] values, Type arrayType)
        {
            Type elementType = arrayType.GetElementType() ?? typeof(object);
            Array array = Array.CreateInstance(elementType, values.Length);
            for (int i = 0; i < values.Length; i++)
                array.SetValue(ConvertValue(values[i], elementType), i);
            return array;
        }

        public static Type FindMessageType(string rosType, Type preferredAssemblyType)
        {
            string simpleName = rosType[(rosType.LastIndexOf('/') + 1)..];
            Type? match = preferredAssemblyType.Assembly.GetTypes()
                .FirstOrDefault(t => typeof(Message).IsAssignableFrom(t) &&
                                     string.Equals(t.Name, simpleName, StringComparison.Ordinal));

            if (match != null)
                return match;

            return typeof(DynamicMessage);
        }
    }

    internal sealed class DynamicMessage : Message { }
}
